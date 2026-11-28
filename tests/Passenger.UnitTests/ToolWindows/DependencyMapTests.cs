using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.App.DependencyMap;
using Passenger.Roslyn.Extension;
using Passenger.TestTables;

namespace Passenger.UnitTests.ToolWindows;

/// <summary>The dependency map: each kind of use read by the compiler's binding of a solution of two projects (Roslyn's own
/// workspace), each use counted once at the deepest level open between the deepest nodes shown, each level laid out with
/// what uses above what it uses, and layer rules matched and their broken uses found where they are written.</summary>
public sealed class DependencyMapTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private const string Core = "/shop/Core/Core.csproj";
    private const string App = "/shop/App/App.csproj";

    private const string CoreCode = "namespace Shop.Core;\n\npublic interface IShape\n{\n    int Sides();\n}\n\npublic class Base\n{\n}\n\n"
        + "[System.AttributeUsage(System.AttributeTargets.All)]\npublic sealed class MarkAttribute : System.Attribute\n{\n}\n";

    private const string AppCode = "using Shop.Core;\n\nnamespace Shop.App;\n\n[Mark]\npublic class Square : Base, IShape\n{\n    private Base kept = new();\n\n"
        + "    public int Sides() => Helper.Four(kept);\n\n    public Base Make(IShape shape) => kept;\n}\n\n"
        + "static class Helper\n{\n    public static int Four(Base value) => 4;\n}\n";

    [Fact]
    public Task The_map_reads_each_kind_of_use_counts_each_once_where_it_is_open_and_lays_users_above_what_they_use() => Cases.AllAsync(
        ("each kind of use is read as the compiler binds it, from the project down to the member used", ReadsEachKindAsync),
        ("each use is counted once, at the deepest level open, between the deepest nodes shown", () =>
        {
            CountsEachUseOnce();
            return Task.CompletedTask;
        }),
        ("a level is laid out in layers, what uses above what it uses, a loop broken where it comes round, none of no projects", () =>
        {
            LaysUsersAbove();
            return Task.CompletedTask;
        }),
        ("layer rules hold code by project, namespace and folder, and a use of a layer not let is found where it is written", BreaksLayerRulesAsync));

    private static async Task BreaksLayerRulesAsync()
    {
        LayerRules rules = new("/shop", [
            new Layer("App", ["App"], [], [], ["Data"]),
            new Layer("Data", [], ["Shop.Data.*"], [], ["Core"]),
            new Layer("Core", [], [], ["Core/**"], []),
        ]);
        Assert.Equal(["App", "Data", "Data", "Core", "Core", "-", "-", "-"], new[]
        {
            rules.Of("App", "Shop.Core", "/shop/Core/Square.cs"),
            rules.Of("Lib", "Shop.Data", null),
            rules.Of("Lib", "Shop.Data.Sql", null),
            rules.Of("Lib", "Other", "/shop/Core/Deep/Money.cs"),
            rules.Of("Lib", "Other", "/shop/core/Money.cs"),
            rules.Of("Lib", "Shop.DataSql", "/shop/Lib/Money.cs"),
            rules.Of("Lib", "Other", "/elsewhere/Core/Money.cs"),
            rules.Of("Lib", "Other", "/shop/Money.cs"),
        }.Select(l => l?.Name ?? "-"));
        Assert.True(LayerRules.Breaks(rules.Layers[0], rules.Layers[2]) && !LayerRules.Breaks(rules.Layers[0], rules.Layers[1]) && !LayerRules.Breaks(rules.Layers[2], rules.Layers[2]),
            "App may use Data and its own layer, and not Core");

        using AdhocWorkspace workspace = new();
        ProjectId core = Add(workspace, "Core", Core, [], ("Core.cs", CoreCode));
        Add(workspace, "App", App, [new ProjectReference(core)], ("Square.cs", AppCode));
        Document square = workspace.CurrentSolution.Projects.Single(p => p.Name == "App").Documents.Single();
        LayerRules apart = new("/shop", [new Layer("App", ["App"], [], [], []), new Layer("Core", ["Core"], [], [], [])]);
        IReadOnlyList<LayerBreak> broken = await DependencyMap.BrokenAsync(square, apart, Cancel);
        Assert.Equal(["4:1 MarkAttribute", "5:22 Base", "5:28 IShape", "7:12 Base", "7:24 Base.Base()", "11:11 Base", "11:21 IShape", "16:27 Base"],
            broken.Select(b => $"{b.Start.Line}:{b.Start.Character} {b.Used}"));
        Assert.All(broken, b => Assert.Equal(("App", "Core"), (b.From, b.To)));

        // The map's edges judged by the same rules: App's reference to Core, and each of Shop.App's types' uses of Core's.
        Solution solution = workspace.CurrentSolution;
        Assert.True(Assert.Single((await DependencyMap.LevelAsync(solution, null, new HashSet<ProjectId>(), apart, Cancel)).Edges).Broken, "App's reference to Core marked broken");
        DependenciesFound types = await DependencyMap.LevelAsync(solution, DependencyIds.Namespace(App, "Shop.App"), new HashSet<ProjectId>(), apart, Cancel);
        Assert.All(types.Edges, e => Assert.True(e.Broken == (e.To[0] == DependencyIds.Project(Core)), $"{e.From} -> {e.To[^1]} marked broken {e.Broken}"));
    }

    private static async Task ReadsEachKindAsync()
    {
        using AdhocWorkspace workspace = new();
        ProjectId core = Add(workspace, "Core", Core, [], ("Core.cs", CoreCode));
        Add(workspace, "App", App, [new ProjectReference(core)], ("Square.cs", AppCode));
        Solution solution = workspace.CurrentSolution;

        DependenciesFound projects = await DependencyMap.LevelAsync(solution, null, new HashSet<ProjectId>(), null, Cancel);
        Assert.Equal(["App", "Core"], projects.Nodes.Select(n => n.Name));
        Assert.Equal([$"{DependencyIds.Project(App)} -> {DependencyIds.Project(Core)}: references 1"], projects.Edges.Select(Said));

        DependenciesFound spaces = await DependencyMap.LevelAsync(solution, DependencyIds.Project(App), new HashSet<ProjectId>(), null, Cancel);
        Assert.Equal(["Shop.App"], spaces.Nodes.Select(n => n.Name));
        DependencyEdge across = Assert.Single(spaces.Edges);
        Assert.Equal([DependencyIds.Project(Core), DependencyIds.Namespace(Core, "Shop.Core")], across.To);

        DependenciesFound types = await DependencyMap.LevelAsync(solution, DependencyIds.Namespace(App, "Shop.App"), new HashSet<ProjectId>(), null, Cancel);
        Assert.Equal(["Helper", "Square"], types.Nodes.Select(n => n.Name));
        Assert.Equal(["Helper -> Base: parameterType 1", "Square -> Base: calls 1, fieldType 1, inherits 1, parameterType 1", "Square -> Helper: calls 1",
            "Square -> IShape: implements 1, parameterType 1", "Square -> MarkAttribute: attribute 1"], ByType(types));

        DependenciesFound members = await DependencyMap.LevelAsync(solution, DependencyIds.Type(App, "T:Shop.App.Square"), new HashSet<ProjectId>(), null, Cancel);
        Assert.Equal(["Make(IShape)", "Sides()", "kept"], members.Nodes.Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Contains($"{DependencyIds.Member(App, "M:Shop.App.Square.Sides")} -> {DependencyIds.Member(App, "M:Shop.App.Helper.Four(Shop.Core.Base)")}: calls 1",
            members.Edges.Select(Said));
        Assert.Contains($"{DependencyIds.Type(App, "T:Shop.App.Square")} -> {DependencyIds.Type(Core, "T:Shop.Core.Base")}: inherits 1", members.Edges.Select(Said));

        // Each type's uses of each type, whatever member of it they use, its name the last part of its id.
        static IEnumerable<string> ByType(DependenciesFound level) => level.Edges
            .GroupBy(e => (From: Short(e.From), To: Short(e.To[2])))
            .Select(g => $"{g.Key.From} -> {g.Key.To}: {string.Join(", ", g.SelectMany(e => e.Kinds).GroupBy(k => k.Key).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Sum(c => c.Value)}"))}")
            .Order(StringComparer.Ordinal);

        static string Short(string id) => id[(id.LastIndexOf('.') + 1)..];
    }

    private static ProjectId Add(AdhocWorkspace workspace, string name, string path, IReadOnlyList<ProjectReference> references, (string Name, string Text) file)
    {
        ProjectId id = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp, filePath: path,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            projectReferences: references, metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(id), file.Name, filePath: Path.Combine(Path.GetDirectoryName(path)!, file.Name),
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(file.Text), VersionStamp.Create()))));
        return id;
    }

    private static void CountsEachUseOnce()
    {
        MapGraph graph = new();
        graph.Take(null, Level([("p|A", "A"), ("p|B", "B")], [Edge("p|A", ["p|B"], (DependencyKinds.References, 1))]));
        MapNode a = graph["p|A"]!;
        MapNode b = graph["p|B"]!;
        Assert.Equal(["A -> B: references 1"], Shown(graph));

        graph.Take(a, Level([("n|A|One", "One")], [Edge("n|A|One", ["p|B", "n|B|Two"], (DependencyKinds.Calls, 2))]));
        Assert.Equal(["One -> B: calls 2"], Shown(graph));
        (IReadOnlyList<MapEdge> leaving, IReadOnlyList<MapEdge> coming) = MapGraph.Crossing(a, graph.Shown(_ => true));
        Assert.True(leaving.Count == 1 && coming.Count == 0, "an open project's own count is what leaves the nodes below it");

        graph.Take(b, Level([("n|B|Two", "Two")], []));
        Assert.Equal(["One -> Two: calls 2"], Shown(graph));
        Assert.Empty(graph.Shown(kind => kind != DependencyKinds.Calls));

        graph.SetOpen(a, open: false);
        Assert.Equal(["A -> B: references 1"], Shown(graph));

        static List<string> Shown(MapGraph graph) =>
            [.. graph.Shown(_ => true).Select(e => $"{e.From.Name} -> {e.To.Name}: {string.Join(", ", e.Kinds.Select(k => $"{k.Key} {k.Value}"))}")];
    }

    private static void LaysUsersAbove()
    {
        MapGraph graph = new();
        graph.Take(null, Level([("p|X", "X"), ("p|Y", "Y"), ("p|Z", "Z"), ("p|W", "W")],
            [Edge("p|X", ["p|Y"], (DependencyKinds.References, 1)), Edge("p|Y", ["p|Z"], (DependencyKinds.References, 1)), Edge("p|Z", ["p|X"], (DependencyKinds.References, 1))]));
        IReadOnlyList<MapEdge> edges = graph.Shown(_ => true);
        Assert.Equal(["X W", "Y", "Z"], MapLayout.Layers(graph.Roots, null, edges).Select(l => string.Join(' ', l.Select(n => n.Name))));
        (IReadOnlyDictionary<MapNode, MapBox> boxes, double width, double height) = MapLayout.Of(graph.Roots, edges, _ => (100, 28), 28);
        Assert.True(boxes[graph["p|Y"]!].Y >= boxes[graph["p|X"]!].Bottom + MapLayout.LayerGap && boxes[graph["p|Z"]!].Y >= boxes[graph["p|Y"]!].Bottom + MapLayout.LayerGap,
            "Y stands a layer below X, and Z below Y");
        Assert.True(boxes.Values.All(box => box.Right + MapLayout.Margin <= width + 0.001 && box.Bottom + MapLayout.Margin <= height + 0.001), "every node stands within the map");

        // A solution of no projects is a map of nothing.
        Assert.Empty(MapLayout.Of([], [], _ => (100, 28), 28).Boxes);
    }

    private static DependenciesFound Level(IReadOnlyList<(string Id, string Name)> nodes, IReadOnlyList<DependencyEdge> edges) =>
        new([.. nodes.Select(n => new DependencyNode(n.Id, n.Name, "project", null, null, Expands: true))], edges);

    private static DependencyEdge Edge(string from, IReadOnlyList<string> to, params (string Kind, int Count)[] kinds) =>
        new(from, to, kinds.ToDictionary(k => k.Kind, k => k.Count));

    private static string Said(DependencyEdge edge) => $"{edge.From} -> {edge.To[^1]}: {string.Join(", ", edge.Kinds.Select(k => $"{k.Key} {k.Value}"))}";
}

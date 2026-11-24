using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.Roslyn.Extension;
using Passenger.TestTables;

namespace Passenger.UnitTests.Roslyn;

/// <summary>Test doubles over Roslyn's own workspace, a library and a test project referencing it: a fake of an interface or
/// a class written into the test project, which compiles and is used by a test as its hooks and records promise; and why
/// none is offered of a sealed or static class, or of a type no test stands one in for.</summary>
public sealed class TestDoublesTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private const string Library = "/shop/Shop/Shop.csproj";
    private const string Tests = "/shop/Shop.Tests/Shop.Tests.csproj";

    private const string Code = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Shop;

        public sealed record Order(int Id);

        public interface IPinged
        {
            void Ping();

            int Count { get; }
        }

        public interface IClock : IPinged
        {
            DateTime Now { get; }

            string Zone { get; init; }

            int Parse(string text);

            string Format(int value);

            string Format(int value, string pattern);

            Task SaveAsync(Order order, CancellationToken cancel);

            Task<int> CountAsync();

            T Read<T>(string key) where T : class;

            bool TryGet(string key, out int value);

            IReadOnlyList<string> Names();

            string this[int index] { get; set; }

            event EventHandler<string> Changed;

            new long Count { get; }
        }

        public interface IRepository<T> where T : class
        {
            T? Find(int id);

            void Add(T item);
        }

        public abstract class Store
        {
            private readonly string name;

            protected Store(string name) => this.name = name;

            public string Name => name;

            public abstract Task<Order?> FindAsync(int id);

            public virtual int Total() => 0;

            protected abstract void Log(string message);

            public abstract string Kind { get; }
        }

        public sealed class Clock
        {
        }

        public static class Clocks
        {
        }

        public struct Moment
        {
        }

        public class Oops : Exception
        {
        }
        """;

    [Fact]
    public Task Fakes_compile_answer_as_their_hooks_say_and_are_offered_only_where_one_can_stand_in() => Cases.AllAsync(
        ("an interface's fake implements every member it and its bases ask for, records each call and answers through its hooks", InterfaceAsync),
        ("a class's fake passes its constructor on, overrides its abstract and virtual members and says it keeps the rest its own", ClassAsync),
        ("a generic interface's fake keeps its type parameters and their constraints", GenericAsync),
        ("Create Fake writes the fake into the test project's Fakes folder, and refuses a second", CreateAsync),
        ("a sealed or static class is offered with its seam, and a struct, a record, an exception or an attribute is offered nothing", OfferedAsync));

    private static async Task InterfaceAsync()
    {
        string fake = await FakeAsync("IClock", """
            using System.Threading.Tasks;
            using Shop;
            using Shop.Tests.Fakes;

            static class Uses
            {
                public static async Task<int> Run()
                {
                    FakeClock clock = new() { Zone = "UTC" };
                    clock.OnParse = text => text.Length;
                    clock.OnFormat2 = (value, pattern) => pattern;
                    IClock used = clock;
                    int parsed = used.Parse("four") + await used.CountAsync() + used.Names().Count;
                    used.TryGet("key", out int value);
                    clock.RaiseChanged(null, "now");
                    used[2] = "two";
                    return parsed + clock.ParseCalls.Count + clock.Format2Calls.Count + clock.TryGetCalls.Count + clock.ItemSets[0].index + value;
                }
            }
            """);
        Assert.Contains("internal sealed class FakeClock : IClock", fake, StringComparison.Ordinal);
        Assert.Contains("public Func<string, int>? OnParse { get; set; }", fake, StringComparison.Ordinal);
        Assert.Contains("public List<string> ParseCalls { get; } = [];", fake, StringComparison.Ordinal);
        Assert.Contains("public List<(int value, string pattern)> Format2Calls { get; } = [];", fake, StringComparison.Ordinal);
        Assert.Contains("public Task SaveAsync(Order order, CancellationToken cancel)", fake, StringComparison.Ordinal);
        Assert.Contains("return OnCountAsync != null ? OnCountAsync() : Task.FromResult<int>(default);", fake, StringComparison.Ordinal);
        Assert.Contains("int IPinged.Count", fake, StringComparison.Ordinal);
    }

    private static async Task ClassAsync()
    {
        string fake = await FakeAsync("Store", """
            using System.Threading.Tasks;
            using Shop;
            using Shop.Tests.Fakes;

            static class Uses
            {
                public static async Task<int> Run()
                {
                    FakeStore store = new("north") { OnTotal = () => 3 };
                    store.OnFindAsync = id => Task.FromResult<Order?>(new Order(id));
                    Order? found = await store.FindAsync(7);
                    return store.Total() + (found?.Id ?? 0) + store.FindAsyncCalls[0] + store.Name.Length;
                }
            }
            """);
        Assert.Contains("public FakeStore(string name) : base(name)", fake, StringComparison.Ordinal);
        Assert.Contains("return OnTotal != null ? OnTotal() : base.Total();", fake, StringComparison.Ordinal);
        Assert.Contains("protected override void Log(string message)", fake, StringComparison.Ordinal);
        DoublesOffered offered = await OfferAsync("Store");
        Assert.True(offered.KeepsOwn, "Store's Name, which is not virtual, said to stay Store's own in the fake");
    }

    private static async Task GenericAsync()
    {
        string fake = await FakeAsync("IRepository", """
            using Shop;
            using Shop.Tests.Fakes;

            static class Uses
            {
                public static int Run()
                {
                    FakeRepository<Order> orders = new() { OnFind = id => new Order(id) };
                    orders.Add(new Order(1));
                    return orders.Find(2)!.Id + orders.AddCalls.Count;
                }
            }
            """);
        Assert.Contains("internal sealed class FakeRepository<T> : IRepository<T> where T : class", fake, StringComparison.Ordinal);
    }

    private static async Task CreateAsync()
    {
        using AdhocWorkspace workspace = Shop();
        Document code = Source(workspace);
        Refactored made = await TestDoubles.CreateAsync(code, new FakeAsked(PlaceOf("interface IClock"), Tests), Cancel);
        Assert.Null(made.Problem);
        FileChange file = Assert.Single(made.Changes);
        Assert.True(file.Created && file.Path == "/shop/Shop.Tests/Fakes/FakeClock.cs", $"the fake made at {file.Path}");
        Assert.StartsWith("using System;", file.Edits[0].Text, StringComparison.Ordinal);
        Assert.Contains("namespace Shop.Tests.Fakes;", file.Edits[0].Text, StringComparison.Ordinal);

        Solution with = workspace.CurrentSolution.AddDocument(DocumentId.CreateNewId(Project(workspace, "Shop.Tests").Id), "FakeClock.cs", file.Edits[0].Text,
            ["Fakes"], file.Path);
        Assert.True(workspace.TryApplyChanges(with));
        Refactored again = await TestDoubles.CreateAsync(Source(workspace), new FakeAsked(PlaceOf("interface IClock"), Tests), Cancel);
        Assert.Equal("FakeClock.cs is already in Shop.Tests", again.Problem);
    }

    private static async Task OfferedAsync()
    {
        DoublesOffered clock = await OfferAsync("sealed class Clock");
        DoublesOffered clocks = await OfferAsync("static class Clocks");
        Assert.Equal([("Clock", FakeSeams.Sealed), ("Clocks", FakeSeams.Static)], [(clock.Type, clock.Seam), (clocks.Type, clocks.Seam)]);
        Assert.Equal("/shop/Shop.Tests/Fakes/FakeClock.cs", Assert.Single((await OfferAsync("interface IClock")).Fakes).File);
        foreach (string none in (string[])["struct Moment", "record Order", "class Oops"])
        {
            Assert.Null((await OfferAsync(none)).Type);
        }
    }

    /// <summary>The fake of a type of the library, as Create Fake writes it into the test project, which compiles with a
    /// test's code beside it with no error.</summary>
    private static async Task<string> FakeAsync(string type, string uses)
    {
        using AdhocWorkspace workspace = Shop();
        Document code = Source(workspace);
        INamedTypeSymbol named = (await TestDoubles.TypeNamedAsync(code, (await code.GetTextAsync(Cancel)).ToString().IndexOf(type, StringComparison.Ordinal) + 1, Cancel))!;
        Project tests = Project(workspace, "Shop.Tests");
        Compilation compilation = (await tests.GetCompilationAsync(Cancel))!;
        Document fake = await TestDoubles.FakeAsync(tests, compilation, named, TestDoubles.FakeFile(tests, named), Cancel);
        Project used = fake.Project.AddDocument("Uses.cs", uses, filePath: "/shop/Shop.Tests/Uses.cs").Project;
        string text = (await fake.GetTextAsync(Cancel)).ToString();
        Diagnostic[] errors = [.. (await used.GetCompilationAsync(Cancel))!.GetDiagnostics(Cancel).Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"the fake of {type} did not compile: {string.Join("; ", errors.Select(e => e.ToString()))}\n{text}");
        return text;
    }

    /// <summary>What is offered at a type's name where a declaration ("sealed class Clock") declares it.</summary>
    private static async Task<DoublesOffered> OfferAsync(string declared)
    {
        using AdhocWorkspace workspace = Shop();
        return await TestDoubles.OfferedAsync(Source(workspace), new DoublesAsked(PlaceOf(declared), [Tests]), Cancel);
    }

    /// <summary>The place of the last word of a declaration in the library's code, a letter into it.</summary>
    private static Place PlaceOf(string declared)
    {
        int at = Code.IndexOf(declared, StringComparison.Ordinal) + declared.LastIndexOf(' ') + 2;
        LinePosition position = SourceText.From(Code).Lines.GetLinePosition(at);
        return new Place(position.Line, position.Character);
    }

    private static Document Source(AdhocWorkspace workspace) => Project(workspace, "Shop").Documents.Single();

    private static Project Project(AdhocWorkspace workspace, string name) => workspace.CurrentSolution.Projects.Single(p => p.Name == name);

    private static AdhocWorkspace Shop()
    {
        AdhocWorkspace workspace = new();
        ProjectId shop = Add(workspace, "Shop", Library, []);
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(shop), "Code.cs", filePath: "/shop/Shop/Code.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Code), VersionStamp.Create()))));
        Add(workspace, "Shop.Tests", Tests, [new ProjectReference(shop)]);
        return workspace;
    }

    private static ProjectId Add(AdhocWorkspace workspace, string name, string path, IReadOnlyList<ProjectReference> references)
    {
        ProjectId id = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp, filePath: path,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest), projectReferences: references,
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]).WithDefaultNamespace(name));
        return id;
    }
}

using System.Text;
using Passenger.Core;
using Passenger.Xaml.Dialects;

namespace Passenger.Tests;

/// <summary>Passenger's reading of a MAUI page's C# expressions held to MAUI's own source generator over random expressions,
/// seeded: operator words in any letter case, XML's entities, single-quoted strings with their escapes, interpolations
/// whose holes hold conditionals, and <c>{=</c>. Each is written into a page of a project on MAUI's controls (from the
/// package cache tests/MauiGraphs fills), built, and the code the generator writes of it, the data type's members it
/// qualifies (<c>__source.Name</c>) taken back to their names, held to the code <see cref="MauiExpressionCode"/> reads of
/// it. A difference is shrunk to its smallest expression and said with its seed.</summary>
[Collection(Beside.Name)]
public sealed class MauiExpressionsDifferentialTests
{
    /// <summary>The run's seed, said with a difference: set here, it replays that run.</summary>
    private const int Seed = 2095;

    private const int Expressions = 80;

    private const int Depth = 3;

    // Kept out on purpose: a one-character string a char parameter takes (Passenger's char only where it binds the
    // method, MauiExpressionDocument), so no method is called; a value naming no member, which is no binding; an operator
    // word or an escaped quote in an interpolation's hole, which the generator writes as it stands; and a name of MAUI's
    // types (Size, a markup extension to MAUI's reader: which value is an expression is MauiDialect.IsExpression's
    // question) or of the page's (Title).

    private static readonly string[] Ints = ["Count", "Total"];

    private static readonly string[] Bools = ["A", "B"];

    private static readonly string[] Strings = ["Name", "Note"];

    [Fact]
    public async Task Passengers_reading_of_maui_expressions_agrees_with_mauis_source_generator_over_random_expressions()
    {
        Random random = new(Seed);
        List<Node> made = [.. Enumerable.Range(0, Expressions).Select(i => Named(Made(random, i % 3 == 0 ? Kind.Bool : Kind.Text, Depth, hole: false)))];
        string folder = await PreparedAsync();
        try
        {
            IReadOnlyList<string> generated = await GeneratedAsync(folder, made);
            for (int i = 0; i < made.Count; i++)
            {
                if (Difference(made[i], generated[i]) is { } difference)
                {
                    Node smallest = await ShrunkAsync(folder, made[i]);
                    Assert.Fail($"Passenger and MAUI's source generator read expression {i} of seed {Seed} otherwise: {difference}\n"
                        + $"The smallest expression found that still differs: {Difference(smallest, (await GeneratedAsync(folder, [smallest]))[0])}");
                }
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private enum Kind
    {
        Bool,
        Int,
        Text,
    }

    /// <summary>An expression's part: its text with each of its parts' places marked <c>§0</c>, <c>§1</c>, <c>§2</c>.</summary>
    private sealed record Node(Kind Kind, string Shape, IReadOnlyList<Node> Parts)
    {
        public string Written()
        {
            string written = Shape;
            for (int i = Parts.Count - 1; i >= 0; i--)
            {
                written = written.Replace("§" + i, Parts[i].Written(), StringComparison.Ordinal);
            }
            return written;
        }

        /// <summary>Expressions of its kind a step smaller: a part of its kind in its place, a part made smaller, a name.</summary>
        public IEnumerable<Node> Smaller()
        {
            for (int i = 0; i < Parts.Count; i++)
            {
                if (Parts[i].Kind == Kind)
                {
                    yield return Parts[i];
                }
                foreach (Node part in Parts[i].Smaller())
                {
                    yield return this with { Parts = [.. Parts.Select((p, at) => at == i ? part : p)] };
                }
            }
            if (Parts.Count > 0)
            {
                yield return Leaf(Kind, Kind switch { Kind.Bool => Bools[0], Kind.Int => Ints[0], _ => Strings[0] });
            }
        }
    }

    private static Node Leaf(Kind kind, string text) => new(kind, text, []);

    private static Node Of(Kind kind, string shape, params Node[] parts) => new(kind, shape, parts);

    /// <summary>A random expression of a kind; in an interpolation's <paramref name="hole"/>, with no operator word and no
    /// escaped quote, which MAUI's generator writes there as they stand, so C# refuses them.</summary>
    private static Node Made(Random random, Kind kind, int depth, bool hole)
    {
        if (depth == 0 || random.Next(4) == 0)
        {
            return kind switch
            {
                Kind.Bool => Leaf(kind, Pick(random, Bools)),
                Kind.Int => random.Next(3) == 0 ? Leaf(kind, random.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture)) : Leaf(kind, Pick(random, Ints)),
                _ => random.Next(2) == 0 ? Leaf(kind, Pick(random, Strings)) : Leaf(kind, Quoted(random, hole)),
            };
        }
        Node Next(Kind of) => Made(random, of, depth - 1, hole);
        Node InHole(Kind of) => Made(random, of, depth - 1, hole: true);
        return kind switch
        {
            Kind.Bool => random.Next(6) switch
            {
                0 or 1 => Of(kind, "§0" + Pick(random, hole ? Symbols : [.. Symbols, " GT ", " gt ", " Gt ", " GTE ", " LT ", " lt ", " LTE ", " lte "]) + "§1", Next(Kind.Int), Next(Kind.Int)),
                2 or 3 => Of(kind, "§0" + Pick(random, hole ? [" &amp;&amp; ", " || "] : [" AND ", " and ", " And ", " &amp;&amp; ", " OR ", " or ", " || "]) + "§1", Next(Kind.Bool), Next(Kind.Bool)),
                4 => Of(kind, "!(§0)", Next(Kind.Bool)),
                _ => Of(kind, "(§0)", Next(Kind.Bool)),
            },
            Kind.Int => random.Next(4) switch
            {
                0 => Of(kind, "§0 + §1", Next(Kind.Int), Next(Kind.Int)),
                1 => Of(kind, "§0 - §1", Next(Kind.Int), Next(Kind.Int)),
                2 => Of(kind, "§0 * §1", Next(Kind.Int), Next(Kind.Int)),
                _ => Of(kind, "(§0)", Next(Kind.Int)),
            },
            _ => random.Next(5) switch
            {
                0 or 1 => Of(kind, "§0 + §1", Next(Kind.Text), Next(Kind.Text)),
                2 => Of(kind, "$'" + Plain(random) + "{§0}" + Plain(random) + "{§1}'", InHole(Kind.Text), InHole(Kind.Int)),
                3 => Of(kind, "$'" + Plain(random) + "{§0 ? §1 : §2}'", InHole(Kind.Bool), InHole(Kind.Text), InHole(Kind.Text)),
                _ => Of(kind, "(§0 ? §1 : §2)", Next(Kind.Bool), Next(Kind.Text), Next(Kind.Text)),
            },
        };
    }

    /// <summary>An expression that names a member of the data type, as every binding does: one that names none, with one.</summary>
    private static Node Named(Node node) =>
        NamesAMember(node) ? node
        : node.Kind == Kind.Bool ? Of(Kind.Bool, "§0 AND §1", node, Leaf(Kind.Bool, "A")) : Of(Kind.Text, "§0 + §1", node, Leaf(Kind.Text, "Name"));

    private static bool NamesAMember(Node node) =>
        node.Parts.Count == 0 ? Ints.Contains(node.Shape) || Bools.Contains(node.Shape) || Strings.Contains(node.Shape) : node.Parts.Any(NamesAMember);

    private static string Pick(Random random, string[] from) => from[random.Next(from.Length)];

    /// <summary>A single-quoted string, now and then with an escaped quote (none in an interpolation's hole) or an entity.</summary>
    private static string Quoted(Random random, bool hole) =>
        "'" + Plain(random) + (random.Next(4) switch { 0 when !hole => "\\'s", 1 => " &amp; ", _ => string.Empty }) + Plain(random) + "'";

    /// <summary>The comparisons written as C#'s own operators, XML's entities standing for '&lt;'.</summary>
    private static readonly string[] Symbols = [" > ", " >= ", " &lt; ", " &lt;= ", " == ", " != "];

    private static string Plain(Random random) => Pick(random, ["", "a", "hi ", "x y", "Tom"]);

    /// <summary>What Passenger reads of an expression otherwise than the generator writes it, or null where they agree.</summary>
    private static string? Difference(Node node, string generated)
    {
        string written = Value(node);
        string? ours = MauiExpressionCode.Of(written)?.Code;
        string theirs = generated.Replace("__source.", string.Empty, StringComparison.Ordinal);
        return ours == theirs ? null : $"{written} reads as {ours ?? "no expression"}, where the generator writes {theirs}";
    }

    /// <summary>An expression as a page's attribute writes it, now and then opened with '='.</summary>
    private static string Value(Node node) => "{" + (node.Written().Length % 5 == 0 ? "=" : string.Empty) + node.Written() + "}";

    /// <summary>The smallest expression a step at a time that still differs, each round's candidates built on one page.</summary>
    private static async Task<Node> ShrunkAsync(string folder, Node node)
    {
        while (true)
        {
            List<Node> smaller = [.. node.Smaller().Select(Named).Where(n => n.Written().Length < node.Written().Length).DistinctBy(n => n.Written())];
            if (smaller.Count == 0)
            {
                return node;
            }
            IReadOnlyList<string> generated = await GeneratedAsync(folder, smaller);
            int differing = Enumerable.Range(0, smaller.Count).FirstOrDefault(i => Difference(smaller[i], generated[i]) is not null, -1);
            if (differing < 0)
            {
                return node;
            }
            node = smaller[differing];
        }
    }

    /// <summary>A project on MAUI's controls with a page whose data type has the members the expressions name.</summary>
    private static async Task<string> PreparedAsync()
    {
        string folder = PathNames.Canonical(TestFolders.Create("maui-differential-"));
        string version = AvaloniaApp.Metadata("MauiVersion");
        await File.WriteAllTextAsync(Path.Combine(folder, "Gen.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <RootNamespace>Gen</RootNamespace>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="{version}" />
              </ItemGroup>
            </Project>

            """);
        await File.WriteAllTextAsync(Path.Combine(folder, "Model.cs"),
            "namespace Gen;\n\npublic class Model\n{\n    public int Count { get; set; }\n    public int Total { get; set; }\n    public bool A { get; set; }\n"
            + "    public bool B { get; set; }\n    public string Name { get; set; } = \"\";\n    public string Note { get; set; } = \"\";\n}\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "Page.xaml.cs"), "namespace Gen;\n\npublic partial class Page : ContentPage\n{\n    public Page()\n    {\n        InitializeComponent();\n    }\n}\n");
        return folder;
    }

    /// <summary>The code the generator writes of each expression, in order, from one build of a page holding them all.</summary>
    private static async Task<IReadOnlyList<string>> GeneratedAsync(string folder, IReadOnlyList<Node> expressions)
    {
        StringBuilder page = new("<ContentPage x:Class=\"Gen.Page\" xmlns:local=\"clr-namespace:Gen\" x:DataType=\"local:Model\">\n    <VerticalStackLayout>\n");
        foreach (Node expression in expressions)
        {
            page.Append("        <Label ").Append(expression.Kind == Kind.Bool ? "IsVisible" : "Text").Append("=\"").Append(Value(expression)).Append("\" />\n");
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "Page.xaml"), page.Append("    </VerticalStackLayout>\n</ContentPage>\n").ToString());
        ChildResult built = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", Path.Combine(folder, "Gen.csproj"), "-nologo", "-v:q"], folder),
            limit: Restore.Limit.Scaled(), cancel: TestContext.Current.CancellationToken);
        Assert.True(built.Succeeded, $"The page of expressions did not build, so MAUI's generator refused one the test wrote:\n{built.Output}{built.Error}\n{page}");
        string code = await File.ReadAllTextAsync(Path.Combine(folder, "obj", "Debug", "net11.0", "generated", "Microsoft.Maui.Controls.SourceGen",
            "Microsoft.Maui.Controls.SourceGen.XamlGenerator", "Page.xaml.xsg.cs"));
        const string Opened = "__source => (";
        const string Closed = ", true),";
        List<string> found = [.. code.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Opened, StringComparison.Ordinal) && l.EndsWith(Closed, StringComparison.Ordinal))
            .Select(l => l[Opened.Length..^Closed.Length])];
        Assert.True(found.Count == expressions.Count, $"The generator wrote {found.Count} bindings of the page's {expressions.Count} expressions:\n{code}");
        return found;
    }
}

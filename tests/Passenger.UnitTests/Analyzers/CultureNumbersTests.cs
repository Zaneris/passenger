extern alias Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Analyzers::Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG0011 (<see cref="CultureNumbers"/>) over code compiled in memory: a number written in the machine's culture,
/// as a hole, a ToString, a join or a write, is flagged wherever Numbers can be reached but inside it, and one whose digits
/// no culture changes, or whose provider the code chose, is passed over.</summary>
public class CultureNumbersTests
{
    [Fact]
    public Task A_number_written_in_the_machines_culture_is_flagged_where_it_is_written() => Cases.EachAsync<(string code, string written)>(
    [
        ("string s = $\"{d} ms\";", "{d}"),
        ("string s = $\"{n:N0} objects\";", "{n:N0}"),
        ("string s = $\"{n,7:#,0}\";", "{n,7:#,0}"),
        ("string s = $\"{maybe}\";", "{maybe}"),
        ("string s = d.ToString();", "d.ToString()"),
        ("string s = n.ToString(\"N0\");", "n.ToString(\"N0\")"),
        ("string s = d.ToString(\"0.0\", null);", "d.ToString(\"0.0\", null)"),
        ("string s = n.ToString(CultureInfo.CurrentCulture);", "n.ToString(CultureInfo.CurrentCulture)"),
        ("string s = string.Create(CultureInfo.CurrentCulture, $\"{n}\");", "{n}"),
        ("string s = \"took \" + d;", "\"took \" + d"),
        ("string s = \"\"; s += d;", "s += d"),
        ("new StringBuilder().Append(d);", "new StringBuilder().Append(d)"),
        ("new StringBuilder().Append($\"{d}\");", "{d}"),
        ("Console.WriteLine(d);", "Console.WriteLine(d)"),
        ("string s = string.Create(CultureInfo.InvariantCulture, $\"{(n > 0 ? $\"{d}\" : \"\")}\");", "{d}"),
    ], async row =>
    {
        (string code, string written) = row;
        List<string> flagged = await FlaggedAsync(Home + Within("Writer", code));

        Assert.True(flagged.SequenceEqual(["Writer: " + written]), $"PSG0011 flagged [{string.Join(", ", flagged)}] in\n{code}\nwhere [{written}] "
            + "is written in the machine's culture, so a German one reads 1,5 for 1.5");
    });

    [Fact]
    public async Task A_number_no_culture_changes_or_one_written_with_a_chosen_provider_is_passed_over()
    {
        const string Code = """
            string a = $"{n} {n:X8} {n:D3} {n,5}";
            string b = string.Create(CultureInfo.InvariantCulture, $"{d:N1} {n:N0}");
            string c = FormattableString.Invariant($"{d}");
            string e = d.ToString(CultureInfo.InvariantCulture) + n.ToString("N0", culture);
            new StringBuilder().Append(CultureInfo.InvariantCulture, $"{d}").Append(n);
            string f = "line " + n + Convert.ToString(d, CultureInfo.InvariantCulture);
            FormattableString later = $"{d}";
            """;
        List<string> flagged = await FlaggedAsync(Home + Within("Writer", Code));
        Assert.True(flagged.Count == 0, $"PSG0011 flagged [{string.Join(", ", flagged)}]: a whole number with no format reads alike in every culture, "
            + "and a provider the code chose, or one left to whoever writes a FormattableString out, is the code's own choice");

        List<string> home = await FlaggedAsync(Home.Replace("public static string Said(long count) => \"\";", "public static string Said(double value) => value.ToString();"));
        Assert.True(home.Count == 0, $"PSG0011 flagged [{string.Join(", ", home)}] inside Passenger.Core.Numbers, which writes the IDE's digits itself");

        List<string> unreached = await FlaggedAsync(Within("Elsewhere", "string s = d.ToString();"));
        Assert.True(unreached.Count == 0, $"PSG0011 flagged [{string.Join(", ", unreached)}] in a program that cannot reach Numbers");
    }

    /// <summary>A stand-in for Core's Numbers, the rule's home.</summary>
    private const string Home = "namespace Passenger.Core { public static class Numbers { public static string Said(long count) => \"\"; } } ";

    /// <summary>A class of that name whose method holds the code, given a double d, an int n, a double? maybe and a culture.</summary>
    private static string Within(string type, string code) =>
        $"public static class {type} {{ public static void Write(double d, int n, double? maybe, IFormatProvider culture) {{ {code} }} }}";

    /// <summary>Each place PSG0011 flags, as its class and its text; the code compiled with no error first.</summary>
    private static async Task<List<string>> FlaggedAsync(string code)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("using System;\nusing System.Globalization;\nusing System.Text;\n" + code,
            new CSharpParseOptions(LanguageVersion.Latest));
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Writes", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"The case does not compile:\n{code}\n{string.Join('\n', errors.Select(e => e.ToString()))}");
        ImmutableArray<Diagnostic> found = await compilation.WithAnalyzers([new CultureNumbers()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. found.Where(d => d.Id == "PSG0011").OrderBy(d => d.Location.SourceSpan.Start).Select(d =>
        {
            SyntaxNode node = tree.GetRoot().FindNode(d.Location.SourceSpan);
            string text = d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan);
            return $"{node.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First().Identifier.Text}: {text}";
        })];
    }
}

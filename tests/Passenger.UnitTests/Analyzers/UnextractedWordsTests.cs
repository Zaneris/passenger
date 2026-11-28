extern alias Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Analyzers::Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG0012 (<see cref="UnextractedWords"/>) over code compiled in memory, turned on as a folder's .editorconfig turns
/// it on: words written in the code and made Words or given to a control's words are flagged, and so are Words made a
/// string or said in one language and made Words again; a name, words with no letter and Words given whole pass. And PSG0013
/// (<see cref="WordsComparisons"/>), on everywhere: Words compared with a string are flagged, their English passing.</summary>
public class UnextractedWordsTests
{
    [Fact]
    public Task Words_a_person_reads_written_in_the_code_or_compared_with_a_string_are_flagged_and_names_and_resources_pass() => Cases.EachAsync<(string Code, string? Flagged)>(
    [
        ("Words w = \"Save all\";", "\"Save all\" is made Words"),
        ("Words w = $\"Saved {name}\";", "\"Saved {}\" is made Words"),
        ("TextBlock t = new() { Text = \"Saved\" };", "\"Saved\" is a control's Text"),
        ("Button b = new(); b.Content = \"Save\";", "\"Save\" is a control's Content"),
        ("ToolTip.SetTip(new Button(), \"Save every file\");", "\"Save every file\" is a control's Tip"),
        ("string s = $\"{Words.Verbatim(name)}!\";", "Words are joined into a string"),
        ("string s = \"in \" + Words.Verbatim(name);", "Words are joined into a string"),
        ("string s = Words.Verbatim(name).ToString();", "Words are made a string"),
        ("Words w = Words.Verbatim(name).English;", "Words said in one language (English) are made Words again"),
        ("Words w = name;", null),
        ("Words w = \"…\";", null),
        ("TextBlock t = new() { Text = name };", null),
        ("ShownWords.Set(new TextBlock(), Words.Verbatim(name));", null),
        ("Assert.Equal(name, Words.Verbatim(name));", "Equal compares Words with a string"),
        ("Assert.Equal<object>(\"Saved\", Words.Verbatim(name));", "Equal compares Words with a string"),
        ("Assert.Equal([name], new List<Words>());", "Equal compares Words with a string"),
        ("Assert.Contains(name, new List<Words>());", "Contains compares Words with a string"),
        ("bool same = Words.Verbatim(name).Equals(name);", "Equals compares Words with a string"),
        ("Assert.Equal(\"Saved\", Words.Verbatim(name).English);", null),
        ("Assert.Equal(Words.Verbatim(name), Words.Verbatim(name));", null),
    ], async row =>
    {
        string[] found = await FlaggedAsync(row.Code);
        string[] expected = row.Flagged is null ? [] : [row.Flagged];
        Assert.True(found.SequenceEqual(expected), $"PSG0012 and PSG0013 said [{string.Join("; ", found)}] of {row.Code}, not [{string.Join("; ", expected)}]");
    }, row => row.Code);

    /// <summary>Stand-ins for Core's Words, Avalonia's controls and the door.</summary>
    private const string Home = """
        namespace Passenger.Core
        {
            public sealed class Words : System.IEquatable<Words>
            {
                public static Words Verbatim(string s) => null!;
                public static implicit operator Words(string s) => null!;
                public string Shown => null!;
                public string English => null!;
                public bool Equals(Words? other) => false;
            }
        }
        namespace Avalonia { public class AvaloniaObject { } }
        namespace Avalonia.Controls
        {
            public class TextBlock : Avalonia.AvaloniaObject { public string? Text { get; set; } }
            public class Button : Avalonia.AvaloniaObject { public object? Content { get; set; } }
            public static class ToolTip { public static void SetTip(Avalonia.AvaloniaObject control, object? tip) { } }
        }
        namespace Passenger.Editor { public static class ShownWords { public static void Set(Avalonia.AvaloniaObject control, Passenger.Core.Words words) { } } }
        """;

    /// <summary>The messages PSG0012 and PSG0013 give over the code, the part naming what it is before the advice.</summary>
    private static async Task<string[]> FlaggedAsync(string code)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Home + "\nnamespace Area { using System.Collections.Generic; using Xunit; using Passenger.Core; using Passenger.Editor; using Avalonia.Controls;\n"
            + $"public static class Shows {{ public static void Show(string name) {{ {code} }} }} }}", new CSharpParseOptions(LanguageVersion.Latest));
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Append(typeof(Assert).Assembly.Location).Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Shows", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic> { ["PSG0012"] = ReportDiagnostic.Warn, ["PSG0013"] = ReportDiagnostic.Warn }, nullableContextOptions: NullableContextOptions.Enable));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"The case does not compile:\n{code}\n{string.Join('\n', errors.Select(e => e.ToString()))}");
        ImmutableArray<Diagnostic> found = await compilation.WithAnalyzers([new UnextractedWords(), new WordsComparisons()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. found.Where(d => d.Id is "PSG0012" or "PSG0013").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Split(':')[0])];
    }
}

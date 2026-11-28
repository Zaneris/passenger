extern alias Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Analyzers::Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG0012 (<see cref="UnextractedWords"/>) over code compiled in memory, turned on as a folder's .editorconfig turns
/// it on: words written in the code and made Words or given to a control's words are flagged, and so are Words made a
/// string; a name, words with no letter and Words given whole pass.</summary>
public class UnextractedWordsTests
{
    [Fact]
    public Task Words_a_person_reads_written_in_the_code_are_flagged_and_names_and_resources_pass() => Cases.EachAsync<(string Code, string? Flagged)>(
    [
        ("Words w = \"Save all\";", "\"Save all\" is made Words"),
        ("Words w = $\"Saved {name}\";", "\"Saved {}\" is made Words"),
        ("TextBlock t = new() { Text = \"Saved\" };", "\"Saved\" is a control's Text"),
        ("Button b = new(); b.Content = \"Save\";", "\"Save\" is a control's Content"),
        ("ToolTip.SetTip(new Button(), \"Save every file\");", "\"Save every file\" is a control's Tip"),
        ("string s = $\"{Words.Verbatim(name)}!\";", "Words are joined into a string"),
        ("string s = \"in \" + Words.Verbatim(name);", "Words are joined into a string"),
        ("string s = Words.Verbatim(name).ToString();", "Words are made a string"),
        ("Words w = name;", null),
        ("Words w = \"…\";", null),
        ("TextBlock t = new() { Text = name };", null),
        ("ShownWords.Set(new TextBlock(), Words.Verbatim(name));", null),
    ], async row =>
    {
        string[] found = await FlaggedAsync(row.Code);
        string[] expected = row.Flagged is null ? [] : [row.Flagged];
        Assert.True(found.SequenceEqual(expected), $"PSG0012 said [{string.Join("; ", found)}] of {row.Code}, not [{string.Join("; ", expected)}]");
    }, row => row.Code);

    /// <summary>Stand-ins for Core's Words, Avalonia's controls and the door.</summary>
    private const string Home = """
        namespace Passenger.Core { public sealed class Words { public static Words Verbatim(string s) => null!; public static implicit operator Words(string s) => null!; } }
        namespace Avalonia { public class AvaloniaObject { } }
        namespace Avalonia.Controls
        {
            public class TextBlock : Avalonia.AvaloniaObject { public string? Text { get; set; } }
            public class Button : Avalonia.AvaloniaObject { public object? Content { get; set; } }
            public static class ToolTip { public static void SetTip(Avalonia.AvaloniaObject control, object? tip) { } }
        }
        namespace Passenger.Editor { public static class ShownWords { public static void Set(Avalonia.AvaloniaObject control, Passenger.Core.Words words) { } } }
        """;

    /// <summary>The messages PSG0012 gives over the code, the part naming what it is before the advice.</summary>
    private static async Task<string[]> FlaggedAsync(string code)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Home + "\nnamespace Area { using Passenger.Core; using Passenger.Editor; using Avalonia.Controls;\n"
            + $"public static class Shows {{ public static void Show(string name) {{ {code} }} }} }}", new CSharpParseOptions(LanguageVersion.Latest));
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Shows", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic> { ["PSG0012"] = ReportDiagnostic.Warn }, nullableContextOptions: NullableContextOptions.Enable));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"The case does not compile:\n{code}\n{string.Join('\n', errors.Select(e => e.ToString()))}");
        ImmutableArray<Diagnostic> found = await compilation.WithAnalyzers([new UnextractedWords()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. found.Where(d => d.Id == "PSG0012").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Split(':')[0])];
    }
}

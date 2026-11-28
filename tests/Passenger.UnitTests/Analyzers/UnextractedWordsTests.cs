extern alias Analyzers;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Analyzers::Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG0012 (<see cref="UnextractedWords"/>) over code compiled in memory, turned on as a folder's .editorconfig turns
/// it on: words written in the code and made Words or given to a control's words are flagged, and so are Words made a
/// string or said in one language and made Words again; a name, words with no letter and Words given whole pass, but where a
/// sweep asks for an exception's message or any string made Words. PSG0014 (<see cref="AgentsWords"/>), turned on in its
/// place where agents read the words: Words made a string, given as an object or read as Shown are flagged, their English and
/// words written in the code passing. And PSG0013 (<see cref="WordsComparisons"/>), on everywhere: Words compared with a
/// string are flagged, their English passing.</summary>
public class UnextractedWordsTests
{
    [Fact]
    public Task Words_a_person_reads_written_in_the_code_or_compared_with_a_string_are_flagged_and_names_and_resources_pass_but_where_a_sweep_asks() => Cases.EachAsync<(string Code, string? Flagged, string? Sweep)>(
    [
        ("Words w = \"Save all\";", "\"Save all\" is made Words", null),
        ("Words w = $\"Saved {name}\";", "\"Saved {}\" is made Words", null),
        ("TextBlock t = new() { Text = \"Saved\" };", "\"Saved\" is a control's Text", null),
        ("Button b = new(); b.Content = \"Save\";", "\"Save\" is a control's Content", null),
        ("ToolTip.SetTip(new Button(), \"Save every file\");", "\"Save every file\" is a control's Tip", null),
        ("string s = $\"{Words.Verbatim(name)}!\";", "Words are joined into a string", null),
        ("string s = \"in \" + Words.Verbatim(name);", "Words are joined into a string", null),
        ("string s = Words.Verbatim(name).ToString();", "Words are made a string", null),
        ("Words w = Words.Verbatim(name).English;", "Words said in one language (English) are made Words again", null),
        ("Words w = name.Length > 0 ? name : \"Unnamed\";", "\"Unnamed\" is made Words", null),
        ("Words w = name;", null, null),
        ("Words w = \"…\";", null, null),
        ("TextBlock t = new() { Text = name };", null, null),
        ("ShownWords.Set(new TextBlock(), Words.Verbatim(name));", null, null),
        ("Assert.Equal(name, Words.Verbatim(name));", "Equal compares Words with a string", null),
        ("Assert.Equal<object>(\"Saved\", Words.Verbatim(name));", "Equal compares Words with a string", null),
        ("Assert.Equal([name], new List<Words>());", "Equal compares Words with a string", null),
        ("Assert.Contains(name, new List<Words>());", "Contains compares Words with a string", null),
        ("bool same = Words.Verbatim(name).Equals(name);", "Equals compares Words with a string", null),
        ("bool same = Words.Verbatim(name) == name;", "== compares Words with a string", null),
        ("bool same = Words.Verbatim(name) != Words.Verbatim(name);", null, null),
        ("Assert.Equal(\"Saved\", Words.Verbatim(name).English);", null, null),
        ("Assert.Equal(Words.Verbatim(name), Words.Verbatim(name));", null, null),
        ("Words w = new System.Exception().Message;", "new System.Exception().Message, an exception's message, is made Words", "messages"),
        ("Words w = new System.Exception().Message;", null, "verbatim"),
        ("Words w = name.Trim();", "name.Trim() is made Words as it is", "messages, verbatim"),
        ("Words w = name;", null, "messages, verbatim"),
        ("Words w = Words.Verbatim(name);", null, "messages, verbatim"),
        ("string s = $\"{Words.Verbatim(name)}!\";", "Words are joined into a string in the person's language", AgentsReading),
        ("string s = \"in \" + Words.Verbatim(name);", "Words are joined into a string in the person's language", AgentsReading),
        ("string s = string.Join(\", \", new List<Words>());", "Words are joined into a string in the person's language", AgentsReading),
        ("string s = Words.Verbatim(name).ToString();", "Words are made a string in the person's language", AgentsReading),
        ("string s = Words.Verbatim(name).Shown;", "Words are read in the person's language (Shown)", AgentsReading),
        ("new System.Text.StringBuilder().Append(Words.Verbatim(name));", "Words are given as an object, said in the person's language", AgentsReading),
        ("string s = $\"{Words.Verbatim(name).English}!\";", null, AgentsReading),
        ("Words w = \"Save all\";", null, AgentsReading),
    ], async row =>
    {
        string[] found = await FlaggedAsync(row.Code, row.Sweep);
        string[] expected = row.Flagged is null ? [] : [row.Flagged];
        Assert.True(found.SequenceEqual(expected), $"PSG0012, PSG0013 and PSG0014 said [{string.Join("; ", found)}] of {row.Code}{(row.Sweep is null ? string.Empty : $" swept for {row.Sweep}")}, not [{string.Join("; ", expected)}]");
    }, row => row.Sweep is null ? row.Code : $"{row.Code} swept for {row.Sweep}");

    /// <summary>What a row's sweep says where PSG0014 is on in PSG0012's place, as for the code answering agents.</summary>
    private const string AgentsReading = "agents";

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
                public static bool operator ==(Words? left, Words? right) => false;
                public static bool operator !=(Words? left, Words? right) => false;
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

    /// <summary>The messages PSG0012 (or PSG0014 in its place where agents read) and PSG0013 give over the code, the part
    /// naming what it is before the advice, the folder's .editorconfig asking PSG0012 for the sweep given.</summary>
    private static async Task<string[]> FlaggedAsync(string code, string? sweep)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Home + "\nnamespace Area { using System.Collections.Generic; using Xunit; using Passenger.Core; using Passenger.Editor; using Avalonia.Controls;\n"
            + $"public static class Shows {{ public static void Show(string name) {{ {code} }} }} }}", new CSharpParseOptions(LanguageVersion.Latest));
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Append(typeof(Assert).Assembly.Location).Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Shows", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>
            {
                ["PSG0012"] = sweep == AgentsReading ? ReportDiagnostic.Suppress : ReportDiagnostic.Warn,
                ["PSG0013"] = ReportDiagnostic.Warn,
                ["PSG0014"] = sweep == AgentsReading ? ReportDiagnostic.Warn : ReportDiagnostic.Suppress,
            }, nullableContextOptions: NullableContextOptions.Enable));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"The case does not compile:\n{code}\n{string.Join('\n', errors.Select(e => e.ToString()))}");
        ImmutableArray<Diagnostic> found = await compilation.WithAnalyzers([new UnextractedWords(), new WordsComparisons(), new AgentsWords()],
            new AnalyzerOptions([], new Swept(sweep == AgentsReading ? null : sweep))).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. found.Where(d => d.Id is "PSG0012" or "PSG0013" or "PSG0014").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Split(':')[0])];
    }

    /// <summary>A folder's .editorconfig as the analyzers read it, its only option the sweep it asks for.</summary>
    private sealed class Swept(string? sweep) : AnalyzerConfigOptionsProvider
    {
        private readonly Options options = new(sweep);

        public override AnalyzerConfigOptions GlobalOptions => options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => options;

        private sealed class Options(string? sweep) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
            {
                value = key == UnextractedWords.SweepOption ? sweep : null;
                return value is not null;
            }
        }
    }
}

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Why an action cannot run reads the same wherever it is said, as <see cref="Refusals"/> words it: no string the
/// IDE's code writes is a refusal's words again, whole, with words added, or as one of Refusals' methods makes them.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class RefusalsTests
{
    /// <summary>The words Refusals' methods make, alone or after what the refusal stopped ("Reformat: "), each hole ({})
    /// where it takes a name or a verb.</summary>
    private static readonly Regex[] Made =
    [
        new(@"(?:^|: )(?:The file|\S+) is read-only$"),
        new(@"(?:^|: )Nothing to .+: no (?:folder is open|executable project)$"),
        new(@"(?:^|: )The AppHost is no longer owned by .+$"),
        new(@"(?:^|: )\{\} is not one of the (?:folder|solution)'s projects(?:$|\W)"),
        new(@"(?:^|: )\{\} is not a project(?:$|[^\w ])"),
        new(@"(?:^|: )\{\}(?: of \{\})? is not running(?:$|\W)"),
        new(@"(?:^|: |; )(?i:t)he tests in \{\} \(\{\}\) could not be found(?:$|\W)"),
        new(@"(?:^|: )No test of \{\} was found(?:$|\W)"),
        new(@"(?:^|: )\{\} tracks no remote's branch to update from$"),
        new(@"(?:^|: )\{\} was not renamed: "),
        new(@"(?:^|: )\{\} changed as it was read$"),
        new(@"(?i:a) (?:file|folder) of the package \{\}, as NuGet restored it"),
        new(@"(?:\b(?:it was|they were)|\{\} was|,) edited meanwhile(?:$|,)"),
        new(@"(?:^|: )Debugging \{\} has ended: "),
        new(@"(?:^|: )\{\} is no address(?:$|\W)"),
        new(@"(?:^|: )The debugger could not read memory at "),
    ];

    [Fact]
    public void A_refusals_words_are_written_only_in_Refusals()
    {
        // Each refusal that takes nothing, in English: Refusals.resx's words and those its members give whole.
        HashSet<string> words = [.. typeof(Refusals).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(Words)).Select(property => ((Words?)property.GetValue(null))?.English).OfType<string>()];
        // A constant's words, more after them or not, alone or after what it stopped, its first letter in either case.
        Regex[] said = [.. words.Select(word => new Regex($@"(?:^|: )(?i:{Regex.Escape(word[..1])}){Regex.Escape(word[1..])}(?:$|\W)"))];
        List<string> found = [];
        foreach ((string relative, SyntaxTree tree) in Sources().Where(s => s.Relative != "src/Passenger.App/Refusals.cs"))
        {
            foreach (SyntaxNode node in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            {
                string? written = node switch
                {
                    LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) => plain.Token.ValueText,
                    InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(part => part is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "{}")),
                    _ => null,
                };
                if (written is not null && Made.Concat(said).Any(made => made.IsMatch(written)))
                {
                    found.Add($"{relative}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: \"{written}\"");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} strings write a refusal's words outside Refusals, so the same want reads differently where an action greys, "
            + $"the status bar says it and an agent is answered, and drifts as one copy changes: say each through Refusals:\n{string.Join("\n", found)}");
    }

    /// <summary>A file's refusal as read-only is the file's own (<see cref="Passenger.App.Workspace.OpenDocument.ReadOnlyWhy"/>,
    /// <see cref="Passenger.App.Workspace.ReadOnlyFiles.Refused"/>), which words one read-only by where it lies as every
    /// refusal of such a file does, a package's naming the package: no code says a file plainly read-only by its name.</summary>
    [Fact]
    public void A_files_read_only_refusal_is_worded_by_the_file_never_by_its_name_alone()
    {
        List<string> found = [];
        foreach ((string relative, SyntaxTree tree) in Sources())
        {
            foreach (InvocationExpressionSyntax call in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                bool readOnly = call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ReadOnly", Expression: ExpressionSyntax of } && of.ToString().EndsWith("Refusals", StringComparison.Ordinal)
                    || relative == "src/Passenger.App/Refusals.cs" && call.Expression is IdentifierNameSyntax { Identifier.ValueText: "ReadOnly" };
                if (readOnly && call.ArgumentList.Arguments is [{ Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Name" } }])
                {
                    found.Add($"{relative}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {call}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} refusals say a file is read-only by its name, so a package's file is refused without naming its package, "
            + $"unlike every other refusal of it: say the file's own ReadOnlyWhy:\n{string.Join("\n", found)}");
    }

    /// <summary>A regex stopped at its time limit is said in one wording wherever it cuts an answer short, Passenger.Text's
    /// (<see cref="Passenger.Text.TextSearch.RegexStopped"/>, which Refusals gives the App): no string of any project's code
    /// words it again or another way.</summary>
    [Fact]
    public void A_regex_stopped_at_its_time_limit_is_said_in_one_wording_across_the_code()
    {
        Regex other = new(@"(?i)took too long and stopped|the pattern given up on|expression ran past");
        List<string> found = [];
        foreach ((string relative, SyntaxTree tree) in Sources("src").Where(s => s.Relative != "src/Passenger.Text/TextSearch.cs"))
        {
            foreach (SyntaxNode node in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            {
                string? written = node switch
                {
                    LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) => plain.Token.ValueText,
                    InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(part => part is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "{}")),
                    _ => null,
                };
                if (written is not null && other.IsMatch(written))
                {
                    found.Add($"{relative}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: \"{written}\"");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} strings word a regex stopped at its time limit apart from TextSearch.RegexStopped, so one stop reads "
            + $"differently where a search, a check or a declaration says it: say it through TextSearch.RegexStopped (Refusals.RegexStopped in the App):\n{string.Join("\n", found)}");
    }

    /// <summary>The App's source files, by their paths from the repository's root, each read as C#.</summary>
    private static IEnumerable<(string Relative, SyntaxTree Tree)> Sources() => Sources(Path.Combine("src", "Passenger.App"));

    /// <summary>The source files under a folder of the repository, by their paths from its root, each read as C#.</summary>
    private static IEnumerable<(string Relative, SyntaxTree Tree)> Sources(string under)
    {
        string root = Debugging.Helpers.Debuggees.Root;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, under), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.Contains("/obj/", StringComparison.Ordinal) && !relative.Contains("/bin/", StringComparison.Ordinal))
            {
                yield return (relative, CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken));
            }
        }
    }
}

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>Why an action cannot run reads the same wherever it is said, as <see cref="Refusals"/> words it: no string the
/// IDE's code writes is a refusal's words again, whole, with words added, or as one of Refusals' methods makes them.</summary>
[Collection(Beside.Name)]
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
    ];

    [Fact]
    public void A_refusals_words_are_written_only_in_Refusals()
    {
        HashSet<string> words = [.. typeof(Refusals).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!)];
        // A constant's words, more after them or not, alone or after what it stopped, its first letter in either case.
        Regex[] said = [.. words.Select(word => new Regex($@"(?:^|: )(?i:{Regex.Escape(word[..1])}){Regex.Escape(word[1..])}(?:$|\W)"))];
        string root = Debugging.Helpers.Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Passenger.App"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "src/Passenger.App/Refusals.cs" || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
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
}

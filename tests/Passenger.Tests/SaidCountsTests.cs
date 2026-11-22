using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A count the window says beside words reads alike wherever it is said, in the IDE's digits ("1,234 files"), as
/// <see cref="Numbers"/> writes it: no string of the window's own writes one as plain digits. Whether a whole number is a
/// count, a line, a port or an id its type cannot say, nor whether a string reaches a person or a program, so no analyzer
/// tells them apart surely; this reads the window's strings for what surely holds a count: a collection's Count or
/// Length, a Count() or a value named as a count, beside words.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed partial class SaidCountsTests
{
    [Fact]
    public void A_count_the_window_says_beside_words_is_written_through_Numbers()
    {
        string root = Debugging.Helpers.Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Passenger.App"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
            foreach (InterpolatedStringExpressionSyntax text in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
            {
                for (int i = 0; i < text.Contents.Count; i++)
                {
                    if (text.Contents[i] is InterpolationSyntax { FormatClause: null, AlignmentClause: null } hole && Counts(hole.Expression)
                        && (WordBefore().IsMatch(Words(text, i - 1)) || WordAfter().IsMatch(Words(text, i + 1))))
                    {
                        found.Add($"{relative}:{hole.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {text}");
                    }
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} counts the window says beside words are written as plain digits, so 1234 reads there where "
            + $"{nameof(Numbers)} says 1,234 everywhere else: say each through {nameof(Numbers)}.{nameof(Numbers.Said)} (or {nameof(Plural)}), or write "
            + $"one a program reads in the invariant culture:\n{string.Join("\n", found)}");
    }

    /// <summary>Whether an expression surely holds a count: a Count, Length or LongLength, a Count() or LongCount(), or a value
    /// named count or ending in Count.</summary>
    private static bool Counts(ExpressionSyntax value) => value switch
    {
        ParenthesizedExpressionSyntax inner => Counts(inner.Expression),
        MemberAccessExpressionSyntax member => Counted(member.Name.Identifier.ValueText),
        ConditionalAccessExpressionSyntax { WhenNotNull: MemberBindingExpressionSyntax bound } => Counted(bound.Name.Identifier.ValueText),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Count" or "LongCount" } } => true,
        IdentifierNameSyntax name => Counted(name.Identifier.ValueText),
        _ => false,
    };

    private static bool Counted(string name) => name is "count" or "Length" or "LongLength" || name.EndsWith("Count", StringComparison.Ordinal);

    /// <summary>The words written beside a hole, at <paramref name="at"/> among the string's parts; none where a hole is.</summary>
    private static string Words(InterpolatedStringExpressionSyntax text, int at) =>
        at >= 0 && at < text.Contents.Count && text.Contents[at] is InterpolatedStringTextSyntax words ? words.TextToken.ValueText : string.Empty;

    /// <summary>A word, then a space or an opening bracket, just before the count: "Replaced 3", "Keep All (3)".</summary>
    [GeneratedRegex(@"\p{L}[.:,;]?\s+\(?$|\p{L}\s*\($")]
    private static partial Regex WordBefore();

    /// <summary>A space, then a word, just after the count: "3 files", "(3) more".</summary>
    [GeneratedRegex(@"^\)?[.,:;]?\s+\p{L}")]
    private static partial Regex WordAfter();
}

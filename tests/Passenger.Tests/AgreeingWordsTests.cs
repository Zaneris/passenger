using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A word agreeing with a count reads alike wherever it is said, as <see cref="Plural"/> chooses it ("1 file is",
/// "0 files are"): no code of Passenger's picks a verb's, pronoun's or noun's form by comparing a count with one of its own.</summary>
[Collection(Beside.Name)]
public sealed class AgreeingWordsTests
{
    /// <summary>Words whose forms for one and for several differ by more than an s.</summary>
    private static readonly HashSet<(string One, string Many)> Forms =
        [("is", "are"), ("was", "were"), ("has", "have"), ("does", "do"), ("it", "they"), ("it", "them"), ("its", "their"), ("this", "these"), ("that", "those")];

    [Fact]
    public void A_word_agreeing_with_a_count_is_chosen_only_through_Plural()
    {
        string root = Debugging.Helpers.Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
            foreach (ConditionalExpressionSyntax choice in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<ConditionalExpressionSyntax>())
            {
                if (ByCount(choice.Condition) && Written(choice.WhenTrue) is { } one && Written(choice.WhenFalse) is { } other && Agree(one, other))
                {
                    found.Add($"{relative}:{choice.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {choice}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} choices pick a word agreeing with a count of their own, so a count reads one way here and another "
            + $"there (\"0 files is\", \"1 files\"): choose it through {nameof(Plural)}.{nameof(Plural.Agreeing)} or {nameof(Plural.Noun)}:\n{string.Join("\n", found)}");
    }

    /// <summary>Whether a condition compares a count with one or two (a list's rest past its first).</summary>
    private static bool ByCount(ExpressionSyntax condition) =>
        condition.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken) && token.Value is 1 or 2);

    private static string? Written(ExpressionSyntax branch) => branch switch
    {
        ParenthesizedExpressionSyntax inner => Written(inner.Expression),
        LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) => plain.Token.ValueText,
        _ => null,
    };

    /// <summary>Whether two words or phrases are one's forms for one and for several: an ending s or es by itself, or a word
    /// in the same place of each that is.</summary>
    private static bool Agree(string one, string other) => one.Length == 0 || other.Length == 0 ? one + other is "s" or "es"
        : one.ToLowerInvariant().Split(' ').Zip(other.ToLowerInvariant().Split(' ')).Any(words => FormsOf(words.First, words.Second));

    private static bool FormsOf(string one, string other) =>
        Forms.Contains((one, other)) || Forms.Contains((other, one))
        || (one.Length < other.Length ? (one, other) : (other, one)) is var (shorter, longer) && shorter.Length >= 2
            && (longer == shorter + "s" || longer == shorter + "es");
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.LiveTree;
using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.Tests;

/// <summary>Names listed in a sentence read alike wherever they are said, as <see cref="Plural.Listed"/> lists them ("Api,
/// Web and Worker") and <see cref="Plural.Either"/> offers a choice of them ("Api, Web or Worker"): no code of Passenger's
/// joins them with " and " or " or " of its own, which says "Api and Web and Worker".</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class ListedNamesTests
{
    [Fact]
    public void Names_in_a_sentence_are_listed_and_offered_as_a_choice_only_through_Plural() => Cases.All(
        ("names in a sentence are listed only through Plural Listed", Names_in_a_sentence_are_listed_only_through_Plural_Listed),
        ("a choice of names in a sentence is offered only through Plural Either", A_choice_of_names_in_a_sentence_is_offered_only_through_Plural_Either));

    private void Names_in_a_sentence_are_listed_only_through_Plural_Listed()
    {
        IReadOnlyList<string> found = Joins("and");
        Assert.True(found.Count == 0, $"{found.Count} joins list names with \" and \" of their own, which says three or more as \"A and B and C\" where "
            + $"every other list says \"A, B and C\": list them through {nameof(Plural)}.{nameof(Plural.Listed)}:\n{string.Join("\n", found)}");
    }

    private void A_choice_of_names_in_a_sentence_is_offered_only_through_Plural_Either()
    {
        IReadOnlyList<string> found = Joins("or");
        Assert.True(found.Count == 0, $"{found.Count} joins offer a choice of names with \" or \" of their own, which says three or more as \"A or B or C\" "
            + $"where every other choice says \"A, B or C\": offer them through {nameof(Plural)}.{nameof(Plural.Either)}:\n{string.Join("\n", found)}");
    }

    /// <summary>The calls of Passenger's source that join names with a word alone (" and ", " or "), each with its file and
    /// line.</summary>
    private static IReadOnlyList<string> Joins(string word)
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
            foreach (InvocationExpressionSyntax call in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Join" } join && join.Expression.ToString() is "string" or "String"
                    && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.ValueText: { } separator }
                    && separator.Trim() == word)
                {
                    found.Add($"{relative}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {call}");
                }
            }
        }
        return found;
    }
}

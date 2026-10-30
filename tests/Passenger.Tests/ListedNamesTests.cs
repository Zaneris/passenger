using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Names listed in a sentence read alike wherever they are said, as <see cref="Plural.Listed"/> lists them ("Api,
/// Web and Worker"): no code of Passenger's joins them with " and " of its own, which says "Api and Web and Worker".</summary>
[Collection(Beside.Name)]
public sealed class ListedNamesTests
{
    [Fact]
    public void Names_in_a_sentence_are_listed_only_through_Plural_Listed()
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
                    && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.ValueText: { } separator } && separator.Trim() == "and")
                {
                    found.Add($"{relative}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {call}");
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} joins list names with \" and \" of their own, which says three or more as \"A and B and C\" where "
            + $"every other list says \"A, B and C\": list them through {nameof(Plural)}.{nameof(Plural.Listed)}:\n{string.Join("\n", found)}");
    }
}

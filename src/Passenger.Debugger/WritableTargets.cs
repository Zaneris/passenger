using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Debugger;

/// <summary>An assignment's target that C# keeps readonly (an in parameter, a ref readonly local, a member of either), in
/// forms that write where it refers to: each of the variables it is reached through, the first first, made writable by
/// Unsafe.AsRef, which the evaluator runs as that very location (iv.X as Unsafe.AsRef(in iv).X, then as
/// Unsafe.AsRef(in iv.X)).</summary>
internal static class WritableTargets
{
    public static IEnumerable<string> Of(string target)
    {
        ExpressionSyntax whole = SyntaxFactory.ParseExpression(target);
        if (whole.ContainsDiagnostics)
        {
            yield break;
        }
        List<ExpressionSyntax> reached = [];
        for (ExpressionSyntax? at = whole; at is not null; at = Receiver(at))
        {
            reached.Add(at);
        }
        string text = whole.ToString();
        for (int at = reached.Count - 1; at >= 0; at--)
        {
            yield return $"System.Runtime.CompilerServices.Unsafe.AsRef(in {reached[at]}){text[(reached[at].Span.End - whole.Span.Start)..]}";
        }
    }

    /// <summary>What a member or an element is reached through, which its expression begins with; null for any other.</summary>
    private static ExpressionSyntax? Receiver(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } member => member.Expression,
        ElementAccessExpressionSyntax element => element.Expression,
        _ => null,
    };
}

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Debugger;

/// <summary>An assignment's target that C# keeps readonly (an in parameter, a ref readonly local, a member of either) or
/// reaches through a copy (a nullable's Value), in forms that write where it refers to: a nullable's Value it is reached
/// through as the reference to the value the nullable holds (Nullable.GetValueRefOrDefaultRef), then each of the variables
/// it is reached through, the first first, made writable by Unsafe.AsRef, which the evaluator runs as that very location
/// (iv.X as Unsafe.AsRef(in iv).X, then as Unsafe.AsRef(in iv.X)).</summary>
internal static class WritableTargets
{
    /// <summary>The forms, <paramref name="isNullable"/> saying of an expression reaching a member named Value whether it is
    /// a nullable.</summary>
    public static IEnumerable<string> Of(string target, Func<string, bool> isNullable)
    {
        ExpressionSyntax whole = SyntaxFactory.ParseExpression(target);
        if (whole.ContainsDiagnostics)
        {
            yield break;
        }
        string through = Through(whole, isNullable);
        ExpressionSyntax rewritten = SyntaxFactory.ParseExpression(through);
        if (rewritten.ContainsDiagnostics)
        {
            yield break;
        }
        List<ExpressionSyntax> reached = Reached(rewritten);
        string text = rewritten.ToString();
        for (int at = reached.Count - 1; at >= 0; at--)
        {
            yield return $"System.Runtime.CompilerServices.Unsafe.AsRef(in {reached[at]}){text[(reached[at].Span.End - rewritten.Span.Start)..]}";
        }
    }

    /// <summary>The target with each nullable's Value it is reached through (not the target itself) as the reference to the
    /// value the nullable holds.</summary>
    private static string Through(ExpressionSyntax whole, Func<string, bool> isNullable)
    {
        List<ExpressionSyntax> reached = Reached(whole);
        string text = whole.ToString();
        string through = reached[^1].ToString();
        for (int at = reached.Count - 2; at >= 0; at--)
        {
            ExpressionSyntax receiver = reached[at + 1];
            through = at > 0 && reached[at] is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } && isNullable(receiver.ToString())
                ? $"System.Nullable.GetValueRefOrDefaultRef(in {through})"
                : through + text[(receiver.Span.End - whole.Span.Start)..(reached[at].Span.End - whole.Span.Start)];
        }
        return through;
    }

    /// <summary>The expressions a target is reached through, itself first: each member's or element's receiver, which its
    /// expression begins with.</summary>
    private static List<ExpressionSyntax> Reached(ExpressionSyntax whole)
    {
        List<ExpressionSyntax> reached = [];
        for (ExpressionSyntax? at = whole; at is not null; at = Receiver(at))
        {
            reached.Add(at);
        }
        return reached;
    }

    private static ExpressionSyntax? Receiver(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } member => member.Expression,
        ElementAccessExpressionSyntax element => element.Expression,
        _ => null,
    };
}

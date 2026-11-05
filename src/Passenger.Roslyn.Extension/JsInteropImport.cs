using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>What an "import" call of JS interop names, by the parameters of the overload it calls, which may put a
/// cancellation token or a timeout between the identifier and what JavaScript is given.</summary>
internal static class JsInteropImport
{
    /// <summary>The path a call gives, its expression and value: the first of what it gives the method's args, listed or as
    /// an array of its own; neither where that is no constant.</summary>
    public static (ExpressionSyntax? Given, string? Path) ModulePath(InvocationExpressionSyntax call, IMethodSymbol method, SemanticModel model, CancellationToken cancel)
    {
        ExpressionSyntax? given = First(Argument(call, method, "args"));
        return Constant(given, model, cancel) is { } path ? (given, path) : (null, null);
    }

    /// <summary>The first element of an array written where it is given, else what is given: the first of those listed.</summary>
    private static ExpressionSyntax? First(ExpressionSyntax? args) => args switch
    {
        ArrayCreationExpressionSyntax { Initializer.Expressions: [var first, ..] } => first,
        ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: [var first, ..] } => first,
        CollectionExpressionSyntax { Elements: [ExpressionElementSyntax first, ..] } => first.Expression,
        _ => args,
    };
}

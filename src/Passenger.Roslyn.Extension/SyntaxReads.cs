using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.Roslyn.Extension;

/// <summary>What the extension's readers of code (routes, configuration, localization, Aspire's code help, Godot's) read of a
/// call and its text the same way: a place in a document, whether two paths name one file, a string constant, a string
/// written as its characters, the name a call is called by, its receiver and the argument it gives a parameter.</summary>
internal static class SyntaxReads
{
    /// <summary>An offset in a document as the protocol's line and character.</summary>
    public static Place PlaceAt(SourceText text, int offset)
    {
        LinePosition position = text.Lines.GetLinePosition(offset);
        return new Place(position.Line, position.Character);
    }

    /// <summary>Whether two paths name one file, compared as <see cref="PathNames"/> compares them, each made canonical when
    /// their names differ.</summary>
    public static bool SamePath(string? left, string? right) => left is not null && right is not null
        && (PathNames.Same(left, right) || PathNames.Same(PathNames.Canonical(left), PathNames.Canonical(right)));

    /// <summary>The string an expression is, when the compiler knows it as a constant.</summary>
    public static string? Constant(ExpressionSyntax? expression, SemanticModel model, CancellationToken cancel) =>
        expression is not null && model.GetConstantValue(expression, cancel) is { HasValue: true, Value: string value } ? value : null;

    /// <summary>Whether a string literal is written as its characters: a plain one, no escape in it.</summary>
    public static bool Exact(SyntaxToken token) => token.Text.Length == token.ValueText.Length + 2 && token.Text[0] == '"';

    /// <summary>The name a call's expression calls by ("MapGet" of <c>app.MapGet</c>), or null for one named by neither a
    /// member access nor a name.</summary>
    public static string? NameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    /// <summary>What a method is called on: the expression before its dot, or an extension method's first argument when it
    /// is called as a static method; null for a static method's type.</summary>
    public static ExpressionSyntax? Receiver(InvocationExpressionSyntax call, IMethodSymbol method)
    {
        if (call.Expression is MemberAccessExpressionSyntax member && (method.MethodKind == MethodKind.ReducedExtension || !method.IsStatic))
        {
            return member.Expression;
        }
        return method.IsExtensionMethod && call.ArgumentList.Arguments.FirstOrDefault() is { } first ? first.Expression : null;
    }

    /// <summary>The expression a call gives a parameter of the method's own, by its name or its place; never an extension's
    /// receiver (<see cref="Receiver"/>), which a call of it as a static method gives first.</summary>
    public static ExpressionSyntax? Argument(InvocationExpressionSyntax call, IMethodSymbol method, string parameter)
    {
        SeparatedSyntaxList<ArgumentSyntax> arguments = call.ArgumentList.Arguments;
        if (arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter) is { } named)
        {
            return named.Expression;
        }
        // A reduced extension's parameters leave out its receiver, as its arguments do; one called as a static method has both.
        int index = method.Parameters.FirstOrDefault(p => p.Name == parameter)?.Ordinal ?? -1;
        bool receiver = index == 0 && method.IsExtensionMethod && method.MethodKind != MethodKind.ReducedExtension;
        return index >= 0 && !receiver && index < arguments.Count && arguments[index].NameColon is null ? arguments[index].Expression : null;
    }
}

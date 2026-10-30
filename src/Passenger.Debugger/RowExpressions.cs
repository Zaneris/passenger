using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Debugger;

/// <summary>The C# that reaches a row's value from the frame, made from the expression of the value it belongs to (its
/// owner): each answers null where no expression reaches the owner.</summary>
internal static class RowExpressions
{
    /// <summary>A member: through a cast to <paramref name="declaring"/> where only one reaches it (a member a derived type
    /// hides, or an interface's explicit implementation).</summary>
    public static string? Member(string? owner, string member, string? declaring = null) =>
        owner is null ? null
            : declaring is null ? $"{Operand(owner)}.{Name(member)}"
            : $"(({declaring}){Operand(owner)}).{Name(member)}";

    /// <summary>A variable's or member's name as an expression names it: a keyword's after an @ (@class).</summary>
    public static string Name(string name) => SyntaxFacts.GetKeywordKind(name) is SyntaxKind.None ? name : $"@{name}";

    /// <summary>An element of an array (or a list), by its whole index.</summary>
    public static string? Element(string? owner, IReadOnlyList<uint> indices) =>
        owner is null ? null : $"{Operand(owner)}[{string.Join(", ", indices)}]";

    /// <summary>What a pointer points at.</summary>
    public static string? Pointed(string? owner) => owner is null ? null : $"*{Operand(owner)}";

    /// <summary>A value as the type named reads it: an enum's underlying number.</summary>
    public static string? Cast(string? owner, string type) => owner is null ? null : $"(({type}){Operand(owner)})";

    /// <summary>The owner as the type named, which reaches what the type it is declared as does not (an indexer, a debugger
    /// view's constructor).</summary>
    public static string? As(string? owner, string type) => owner is null ? null : $"({type}){Operand(owner)}";

    /// <summary>The debugger view a type names for the owner, made over it in the program.</summary>
    public static string? Viewed(string? owner, string view) => owner is null ? null : $"new {view}({owner})";

    /// <summary>An explicit implementation's interface and member, named in metadata as its interface with the declaring
    /// type's own type parameters (<paramref name="typeParameters"/>) then the member
    /// ("System.Collections.Generic.ICollection&lt;T&gt;.IsReadOnly"): the interface as the value's type arguments
    /// (<paramref name="typeArguments"/>) close it, which a cast reaches the member through. Null where the name says none.</summary>
    public static (string Interface, string Member)? Explicit(string name, IReadOnlyList<string> typeParameters, IReadOnlyList<string> typeArguments)
    {
        int depth = 0;
        int dot = -1;
        for (int at = 0; at < name.Length; at++)
        {
            depth += name[at] switch { '<' => 1, '>' => -1, _ => 0 };
            dot = name[at] == '.' && depth == 0 ? at : dot;
        }
        if (dot <= 0 || SyntaxFactory.ParseTypeName(name[..dot]) is not { ContainsDiagnostics: false } type)
        {
            return null;
        }
        Dictionary<string, string> closed = typeParameters.Zip(typeArguments).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        return (new Closing(closed).Visit(type).ToString(), name[(dot + 1)..]);
    }

    /// <summary>An expression as the operand of a member's dot, an index, a cast or a star: in parentheses unless it is
    /// already a primary expression (a name, a type's for its static members, the debugger's $exception, a member, an
    /// element, a call), so *p's member is (*p).x.</summary>
    public static string Operand(string expression) =>
        expression.StartsWith('$') && SyntaxFacts.IsValidIdentifier(expression[1..])
        || SyntaxFactory.ParseExpression(expression) is { ContainsDiagnostics: false } and (SimpleNameSyntax or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax or InvocationExpressionSyntax or ParenthesizedExpressionSyntax or ThisExpressionSyntax)
            ? expression
            : $"({expression})";

    /// <summary>A type's own type parameters, by name, as the arguments a value's type gives them.</summary>
    private sealed class Closing(IReadOnlyDictionary<string, string> closed) : CSharpSyntaxRewriter
    {
        public override Microsoft.CodeAnalysis.SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            node.Parent is not QualifiedNameSyntax && closed.TryGetValue(node.Identifier.ValueText, out string? argument) ? SyntaxFactory.ParseTypeName(argument) : node;
    }
}

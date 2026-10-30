using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Languages.CSharp;

/// <summary>Where in C# a live template may go: a statement's place, a member's (in a type's braces), a type's (in a
/// namespace or at the top of a file).</summary>
[Flags]
public enum TemplateScope
{
    None = 0,
    Statement = 1,
    Member = 2,
    Type = 4,
}

/// <summary>Reads the scope at a place from the file's syntax, and the type a member would go in.</summary>
public static class TemplateScopes
{
    /// <summary>The scopes a word ending at an offset stands in: a file of top-level statements takes statements and
    /// types both; in a comment, a string or an expression's middle, none.</summary>
    public static (TemplateScope Scope, string? TypeName) At(ParsedText syntax, int offset)
    {
        SyntaxNode root = syntax.Root;
        offset = Math.Clamp(offset, 0, root.FullSpan.End);
        SyntaxToken token = root.FindToken(Math.Max(0, offset - 1));
        if (root.FindTrivia(Math.Max(0, offset - 1)) is { RawKind: not 0 } trivia && trivia.Span.Contains(Math.Max(0, offset - 1))
            && trivia.Kind() is not SyntaxKind.WhitespaceTrivia and not SyntaxKind.EndOfLineTrivia)
        {
            return (TemplateScope.None, null);
        }
        if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
        {
            return (TemplateScope.None, null);
        }
        string? typeName = token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case ExpressionStatementSyntax or LocalDeclarationStatementSyntax or BlockSyntax or SwitchSectionSyntax
                    or ForStatementSyntax or ForEachStatementSyntax or IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                    or LockStatementSyntax or UsingStatementSyntax or TryStatementSyntax or SwitchStatementSyntax:
                    // A word alone where a statement goes; a word inside an argument list or an initializer is not one.
                    return Statementlike(token) ? (TemplateScope.Statement, typeName) : (TemplateScope.None, null);
                case GlobalStatementSyntax:
                    return (TemplateScope.Statement | TemplateScope.Type, typeName);
                case MemberDeclarationSyntax member when member is not BaseNamespaceDeclarationSyntax && InHeader(member, offset)
                    && !StartsDeclaration(token, offset):
                    // After a modifier, a type or a name, a template would write its own again.
                    return (TemplateScope.None, null);
                case IncompleteMemberSyntax or FieldDeclarationSyntax when node.Parent is BaseTypeDeclarationSyntax:
                    return (TemplateScope.Member, typeName);
                case IncompleteMemberSyntax or FieldDeclarationSyntax when node.Parent is CompilationUnitSyntax unit:
                    return (unit.Members.OfType<GlobalStatementSyntax>().Any() || !unit.Members.OfType<BaseNamespaceDeclarationSyntax>().Any() ? TemplateScope.Statement | TemplateScope.Type : TemplateScope.Type, typeName);
                case IncompleteMemberSyntax or FieldDeclarationSyntax:
                    return (TemplateScope.Type, typeName);
                case BaseTypeDeclarationSyntax type when offset > type.OpenBraceToken.SpanStart:
                    return (TemplateScope.Member, typeName);
                case BaseNamespaceDeclarationSyntax:
                    return (TemplateScope.Type, typeName);
                case CompilationUnitSyntax:
                    return (TemplateScope.Statement | TemplateScope.Type, typeName);
                case ArgumentListSyntax or InitializerExpressionSyntax or ParameterListSyntax or AttributeListSyntax:
                    return (TemplateScope.None, null);
            }
        }
        return (TemplateScope.None, null);
    }

    /// <summary>Whether <paramref name="offset"/> is in a declaration's own words rather than a type's body.</summary>
    private static bool InHeader(MemberDeclarationSyntax member, int offset) =>
        member is not BaseTypeDeclarationSyntax type || type.OpenBraceToken.Span.IsEmpty || offset <= type.OpenBraceToken.SpanStart;

    /// <summary>Whether a word ending at <paramref name="offset"/> (or none, after <paramref name="token"/>) starts its
    /// declaration: nothing but attributes before it since the declaration began.</summary>
    private static bool StartsDeclaration(SyntaxToken token, int offset)
    {
        bool word = token.Span.End == offset && (token.IsKind(SyntaxKind.IdentifierToken) || SyntaxFacts.IsKeywordKind(token.Kind()));
        SyntaxToken previous = word ? token.GetPreviousToken() : token;
        return previous.RawKind == 0 || previous.Kind() is SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken or SyntaxKind.SemicolonToken
            || (previous.IsKind(SyntaxKind.CloseBracketToken) && previous.Parent is AttributeListSyntax);
    }

    /// <summary>Whether a word stands where a statement starts: nothing but the word since the statement began.</summary>
    private static bool Statementlike(SyntaxToken token)
    {
        SyntaxToken previous = token.GetPreviousToken();
        return previous.RawKind == 0 || previous.Kind() is SyntaxKind.SemicolonToken or SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken
            or SyntaxKind.ColonToken or SyntaxKind.CloseParenToken or SyntaxKind.ElseKeyword or SyntaxKind.DoKeyword;
    }
}

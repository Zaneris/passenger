using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Languages.CSharp;

/// <summary>Where a character just typed in C# is one to format around, read from the file's syntax: a ';' that is the
/// code's own and ends a statement or a declaration, never one of a for loop's header, or a '}' that is the code's own and
/// closes a block, a type or an initializer, never an interpolation's; one in a string, a comment or a directive is
/// none.</summary>
public static class TypedFormatting
{
    /// <summary>Whether the character before <paramref name="caret"/>, just typed as <paramref name="typed"/>, is one to
    /// format around.</summary>
    public static bool Formats(ParsedText syntax, int caret, char typed)
    {
        int at = caret - 1;
        if (typed is not (';' or '}') || at < 0 || caret > syntax.Root.FullSpan.End || syntax.Text[at] != typed)
        {
            return false;
        }
        SyntaxToken token = syntax.Root.FindToken(at);
        if (token.IsMissing || token.SpanStart != at || token.Span.Length != 1)
        {
            return false;
        }
        return typed == ';'
            ? token.IsKind(SyntaxKind.SemicolonToken) && token.Parent is not ForStatementSyntax
            : token.IsKind(SyntaxKind.CloseBraceToken) && token.Parent is not InterpolationSyntax;
    }
}

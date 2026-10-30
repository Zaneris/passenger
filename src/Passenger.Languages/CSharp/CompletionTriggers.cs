using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Languages.CSharp;

/// <summary>Places in C# where completion opens on a space, read from the file's syntax.</summary>
public static class CompletionTriggers
{
    /// <summary>Whether a space typed before <paramref name="offset"/> follows a member's <c>override</c>, after which the
    /// members it may override are what can go there.</summary>
    public static bool AfterOverride(ParsedText syntax, int offset)
    {
        if (offset < 2 || offset > syntax.Root.FullSpan.End || syntax.Text[offset - 1] != ' ')
        {
            return false;
        }
        SyntaxToken token = syntax.Root.FindToken(offset - 2);
        return token.IsKind(SyntaxKind.OverrideKeyword) && token.Span.End == offset - 1
            && token.Parent is MemberDeclarationSyntax { Parent: TypeDeclarationSyntax };
    }
}

using System.Buffers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;

namespace Passenger.Languages.CSharp;

/// <summary>Whether a C# text's brackets go past what a parser is given, its strings, comments and the branches its
/// conditions leave out apart: a parser recurses only through what it reads as code. The text as it stands is read first
/// (<see cref="Brackets"/>), which real code passes at the cost of a search; only what that finds deep is read by the lexer,
/// the parser's own, so nothing a parser reads as code passes for a string. Compiled into each assembly that parses.</summary>
internal static class CodeBrackets
{
    /// <summary>Whether a text is too deep, asked on a stack of its characters' (<see cref="DeepText.Read"/>): the lexer
    /// recurses through an interpolation's brackets, so a text past what a stack <see cref="DeepText.Holds"/> stays as deep
    /// as it reads.</summary>
    public static bool TooDeep(SourceText text, CSharpParseOptions? options)
    {
        char[] stretch = ArrayPool<char>.Shared.Rent(4096);
        Brackets brackets = new();
        for (int at = 0; at < text.Length && !brackets.TooDeepToLex; at += stretch.Length)
        {
            int count = Math.Min(stretch.Length, text.Length - at);
            text.CopyTo(at, stretch, 0, count);
            brackets.Read(stretch.AsSpan(0, count));
        }
        ArrayPool<char>.Shared.Return(stretch);
        return brackets.TooDeep && (brackets.TooDeepToLex || !DeepText.Holds(text.Length) || TokensTooDeep(text.ToString(), options));
    }

    /// <summary>A text's brackets as its tokens have them. An interpolated string is a token to the lexer and code to the
    /// parser, as a documentation comment is trivia to one and read by the other, so each is read as it stands, on its own, the
    /// text around it left as it was: what a template leaves open doesn't add up over a file.</summary>
    private static bool TokensTooDeep(string text, CSharpParseOptions? options)
    {
        Brackets brackets = new();
        foreach (SyntaxToken token in SyntaxFactory.ParseTokens(text, options: options))
        {
            if (DeepIn(token.LeadingTrivia, ref brackets) || DeepIn(token.TrailingTrivia, ref brackets))
            {
                return true;
            }
            switch (token.Kind())
            {
                case SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.CloseBracketToken
                    or SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken or SyntaxKind.SemicolonToken:
                    brackets.Read(token.Text);
                    break;
                case SyntaxKind.InterpolatedStringToken when DeepOnItsOwn(token.Text, brackets):
                    return true;
            }
            if (brackets.TooDeep)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reads the conditions among trivia into what's counted; whether a documentation comment goes too deep.</summary>
    private static bool DeepIn(SyntaxTriviaList trivia, ref Brackets brackets)
    {
        foreach (SyntaxTrivia one in trivia)
        {
            switch (one.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia or SyntaxKind.ElifDirectiveTrivia or SyntaxKind.EndIfDirectiveTrivia:
                    brackets.Read(one.ToFullString());
                    break;
                case SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia when DeepOnItsOwn(one.ToFullString(), brackets):
                    return true;
            }
        }
        return false;
    }

    /// <summary>Whether a text goes too deep from where the brackets stand, which a copy leaves as they were.</summary>
    private static bool DeepOnItsOwn(string text, Brackets brackets)
    {
        brackets.Read(text);
        return brackets.TooDeep;
    }
}

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
    /// <summary>Where a text first goes too deep, null for one that doesn't, asked on a stack of its characters' (<see
    /// cref="DeepText.Read"/>): the lexer recurses through an interpolation's brackets, so a text past what a stack <see
    /// cref="DeepText.Holds"/> stays as deep as it reads.</summary>
    public static int? DeepAt(SourceText text, CSharpParseOptions? options)
    {
        char[] stretch = ArrayPool<char>.Shared.Rent(4096);
        Brackets brackets = new() { CSharp = true };
        for (int at = 0; at < text.Length && !brackets.TooDeepToLex; at += stretch.Length)
        {
            int count = Math.Min(stretch.Length, text.Length - at);
            text.CopyTo(at, stretch, 0, count);
            brackets.Read(stretch.AsSpan(0, count));
        }
        ArrayPool<char>.Shared.Return(stretch);
        if (!brackets.TooDeep)
        {
            return null;
        }
        return brackets.TooDeepToLex || !DeepText.Holds(text.Length) ? brackets.DeepAt : TokensDeepAt(text.ToString(), options);
    }

    /// <summary>A text's brackets as its tokens have them. An interpolated string is a token to the lexer and code to the
    /// parser, as a documentation comment is trivia to one and read by the other, so each is read on its own, the text around it
    /// left as it was: what a template leaves open doesn't add up over a file, and a string's text closes nothing in its holes. A <c>?</c> is a link only before
    /// a <c>.</c> or a <c>[</c>, where it is a conditional access, and <c>not</c> only as written, never escaped. A token after
    /// a closing brace that no expression goes on with ends the conditional accesses read ahead through at its level.</summary>
    private static int? TokensDeepAt(string text, CSharpParseOptions? options)
    {
        Brackets brackets = new() { CSharp = true, Tokens = true };
        SyntaxKind before = SyntaxKind.None;
        foreach (SyntaxToken token in SyntaxFactory.ParseTokens(text, options: options))
        {
            if ((DeepIn(token.LeadingTrivia, ref brackets) ?? DeepIn(token.TrailingTrivia, ref brackets)) is int inTrivia)
            {
                return inTrivia;
            }
            if (before == SyntaxKind.CloseBraceToken && !GoesOn(token))
            {
                brackets.Ended();
            }
            switch (token.Kind())
            {
                case SyntaxKind.DotToken or SyntaxKind.OpenBracketToken when before == SyntaxKind.QuestionToken:
                    brackets.Read(token.IsKind(SyntaxKind.DotToken) ? "?." : "?[");
                    break;
                case SyntaxKind.OpenParenToken or SyntaxKind.CloseParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.CloseBracketToken
                    or SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken or SyntaxKind.SemicolonToken
                    or SyntaxKind.LessThanToken or SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanEqualsToken or SyntaxKind.EqualsGreaterThanToken:
                    brackets.Read(token.Text);
                    break;
                case SyntaxKind.IdentifierToken when token.Text == "not":
                    brackets.Read(token.Text);
                    break;
                case SyntaxKind.InterpolatedStringToken when DeepOnItsOwn(token.Text, brackets with { Tokens = false }):
                    return token.SpanStart;
            }
            if (brackets.TooDeep)
            {
                return token.SpanStart;
            }
            before = token.Kind();
        }
        return null;
    }

    /// <summary>Whether an expression may go on with a token after a closing brace: an operator, a bracket, <c>switch</c>,
    /// <c>with</c>, a comma and a pattern's or a query's words may, where any other word, a semicolon or a closer ends it.</summary>
    private static bool GoesOn(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.IdentifierToken => token.Text is "with" or "and" or "or" or "not" or "when" or "select" or "where" or "group" or "by"
            or "into" or "orderby" or "join" or "let" or "on" or "equals" or "ascending" or "descending" or "from",
        SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.CloseParenToken
            or SyntaxKind.CloseBracketToken or SyntaxKind.EndOfFileToken => false,
        SyntaxKind kind when SyntaxFacts.IsKeywordKind(kind) => kind is SyntaxKind.SwitchKeyword or SyntaxKind.IsKeyword or SyntaxKind.AsKeyword or SyntaxKind.InKeyword,
        _ => true,
    };

    /// <summary>Reads the conditions among trivia into what's counted: where they or a documentation comment go too deep.</summary>
    private static int? DeepIn(SyntaxTriviaList trivia, ref Brackets brackets)
    {
        foreach (SyntaxTrivia one in trivia)
        {
            switch (one.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia or SyntaxKind.ElifDirectiveTrivia or SyntaxKind.EndIfDirectiveTrivia:
                    brackets.Read(one.ToFullString());
                    if (brackets.TooDeep)
                    {
                        return one.SpanStart;
                    }
                    break;
                case SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
                    when DeepOnItsOwn(one.ToFullString(), brackets) || ElementsTooDeep(one.GetStructure(), brackets):
                    return one.SpanStart;
            }
        }
        return null;
    }

    /// <summary>Whether a documentation comment's elements, each a level of the parser that reads it, nest too deep from where
    /// the brackets stand, a copy leaving them as they were. An element's tags, each closed, are no mark of how deep it is.</summary>
    private static bool ElementsTooDeep(SyntaxNode? comment, Brackets brackets)
    {
        brackets.Nest(ElementDepth(comment));
        return brackets.TooDeep;
    }

    /// <summary>How many elements a documentation comment's deepest one sits within, itself among them, read with a stack of its
    /// own rather than the thread's.</summary>
    private static int ElementDepth(SyntaxNode? comment)
    {
        int deepest = 0;
        Stack<(SyntaxNode Node, int Depth)> nodes = new();
        if (comment is not null)
        {
            nodes.Push((comment, 0));
        }
        while (nodes.TryPop(out (SyntaxNode Node, int Depth) at))
        {
            int depth = at.Depth + (at.Node.IsKind(SyntaxKind.XmlElement) ? 1 : 0);
            deepest = Math.Max(deepest, depth);
            foreach (SyntaxNode child in at.Node.ChildNodes())
            {
                nodes.Push((child, depth));
            }
        }
        return deepest;
    }

    /// <summary>Whether a text goes too deep from where the brackets stand, which a copy leaves as they were.</summary>
    private static bool DeepOnItsOwn(string text, Brackets brackets)
    {
        brackets.Read(text);
        return brackets.TooDeep;
    }
}

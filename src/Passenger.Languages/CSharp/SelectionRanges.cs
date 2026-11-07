using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Extend Selection: the ranges around a selection, smallest first, each holding the one before. In C# they
/// come from the syntax tree: the word, a string's text inside its quotes, then every enclosing node, with a block's
/// statements, a list's items and a type's members before the braces or brackets around them, and a member with its doc
/// comment after the member alone. In any other text: the word, the line's text, the line, the whole.</summary>
public static class SelectionRanges
{
    /// <summary>The next range out from a selection, or null when it already holds everything; from the file's syntax
    /// when it is C#, else from its words and lines.</summary>
    public static TextRange? Extend(TextSnapshot text, ParsedText? csharp, TextRange selection) =>
        (csharp is not null ? InCSharp(csharp, selection) : InText(text, selection))
            .Where(r => r.Start <= selection.Start && r.End >= selection.End && r.Length > selection.Length)
            .OrderBy(r => r.Length)
            .Cast<TextRange?>()
            .FirstOrDefault();

    private static IEnumerable<TextRange> InCSharp(ParsedText syntax, TextRange selection)
    {
        TextSnapshot snapshot = syntax.Text;
        SyntaxNode root = syntax.Root;
        SyntaxToken token = root.FindToken(Math.Min(selection.Start, Math.Max(0, root.FullSpan.End - 1)));
        // A caret just after a word takes that word, as one before a word takes the one it is before.
        if (selection.Length == 0 && selection.Start <= token.SpanStart && token.GetPreviousToken() is { } previous
            && previous.Span.End == selection.Start && IsWordLike(previous))
        {
            token = previous;
        }
        if (token.IsKind(SyntaxKind.StringLiteralToken) && token.Span.Length >= 2)
        {
            int quote = token.Text.StartsWith('@') ? 2 : 1;
            yield return TextRange.FromBounds(token.SpanStart + quote, token.Span.End - 1);
        }
        yield return Range(token.Span);
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            // What a node gives lies in its full span, read at once, where its span walks down to its first and last tokens.
            if (node.FullSpan.Length <= selection.Length || !node.FullSpan.Contains(new TextSpan(selection.Start, selection.Length)))
            {
                continue;
            }
            if (Inside(node) is { } inside)
            {
                yield return Range(inside);
            }
            yield return Range(node.Span);
            if (node is MemberDeclarationSyntax && node.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) is { RawKind: not 0 } doc)
            {
                yield return TextRange.FromBounds(doc.SpanStart, node.Span.End);
            }
            // Every range further out holds this node, so none is nearer: a chain 100,000 long is walked no further.
            if (node.Span.Length > selection.Length && node.Span.Contains(new TextSpan(selection.Start, selection.Length)))
            {
                break;
            }
        }
        yield return new TextRange(0, snapshot.Length);
    }

    /// <summary>What a node holds between its braces or brackets: a block's statements, a list's items, a type's members.</summary>
    private static TextSpan? Inside(SyntaxNode node) => node switch
    {
        BlockSyntax { Statements: [var first, .., var last] } => TextSpan.FromBounds(first.SpanStart, last.Span.End),
        BlockSyntax { Statements: [var only] } => only.Span,
        BaseArgumentListSyntax { Arguments: [var first, ..] } list => TextSpan.FromBounds(first.SpanStart, list.Arguments[^1].Span.End),
        BaseParameterListSyntax { Parameters: [var first, ..] } list => TextSpan.FromBounds(first.SpanStart, list.Parameters[^1].Span.End),
        TypeArgumentListSyntax { Arguments: [var first, ..] } list => TextSpan.FromBounds(first.SpanStart, list.Arguments[^1].Span.End),
        TypeDeclarationSyntax { Members: [var first, ..] } type => TextSpan.FromBounds(first.SpanStart, type.Members[^1].Span.End),
        InitializerExpressionSyntax { Expressions: [var first, ..] } list => TextSpan.FromBounds(first.SpanStart, list.Expressions[^1].Span.End),
        CollectionExpressionSyntax { Elements: [var first, ..] } list => TextSpan.FromBounds(first.SpanStart, list.Elements[^1].Span.End),
        _ => null,
    };

    private static bool IsWordLike(SyntaxToken token) =>
        token.IsKind(SyntaxKind.IdentifierToken) || token.IsKeyword() || token.IsKind(SyntaxKind.NumericLiteralToken);

    private static TextRange Range(TextSpan span) => new(span.Start, span.Length);

    private static IEnumerable<TextRange> InText(TextSnapshot text, TextRange selection)
    {
        int line = text.GetLineFromOffset(selection.Start);
        LineSpan span = text.GetLine(line);
        string content = text.GetText(span.Start, span.Length);
        int at = selection.Start - span.Start;
        int start = at;
        int end = at;
        while (start > 0 && IsWordChar(content[start - 1]))
        {
            start--;
        }
        while (end < content.Length && IsWordChar(content[end]))
        {
            end++;
        }
        if (end > start)
        {
            yield return TextRange.FromBounds(span.Start + start, span.Start + end);
        }
        int indent = content.Length - content.TrimStart().Length;
        yield return TextRange.FromBounds(span.Start + indent, span.Start + content.TrimEnd().Length);
        if (text.GetLineFromOffset(selection.End) == line || selection.End == span.EndIncludingBreak)
        {
            yield return TextRange.FromBounds(span.Start, span.EndIncludingBreak);
        }
        yield return new TextRange(0, text.Length);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

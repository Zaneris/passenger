using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Where Surround With offers a template: around whole statements of one block (if, try), an expression
/// (cast), whole lines (#region, #if), types where a namespace may go, or anywhere in code (parentheses, braces).</summary>
[Flags]
public enum SurroundScope
{
    None = 0,
    Statements = 1,
    Expression = 2,
    Lines = 4,
    Types = 8,
    Everywhere = 16,
}

/// <summary>What Surround With wraps: the code chosen without the whitespace around it, and where that code stands.</summary>
public readonly record struct SurroundTarget(TextRange Range, SurroundScope Scope);

/// <summary>Reads what a selection is from the file's syntax, for Surround With.</summary>
public static class SurroundScopes
{
    /// <summary>The code a selection holds, or the caret's line with none chosen, and the scopes it stands in; none in a
    /// comment or a string, or with nothing but whitespace chosen.</summary>
    public static SurroundTarget Of(ParsedText syntax, TextRange selection)
    {
        TextSnapshot text = syntax.Text;
        if (selection.Length == 0)
        {
            LineSpan line = text.GetLine(text.GetLineFromOffset(Math.Min(selection.Start, text.Length)));
            selection = new TextRange(line.Start, line.Length);
        }
        int start = selection.Start;
        int end = Math.Min(selection.End, text.Length);
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        TextRange range = TextRange.FromBounds(start, end);
        SyntaxNode root = syntax.Root;
        if (range.Length == 0 || Inside(root, start) || Inside(root, end))
        {
            return new SurroundTarget(range, SurroundScope.None);
        }
        SurroundScope scope = SurroundScope.Everywhere;
        SyntaxToken first = root.FindToken(start);
        SyntaxToken last = root.FindToken(end - 1);
        if (WholeLines(text, start, end))
        {
            scope |= SurroundScope.Lines;
        }
        if (Statements(first, last, start, end))
        {
            scope |= SurroundScope.Statements;
        }
        if (first.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault(e => e.Span.Start == start && e.Span.End == end) is { } expression
            && !SyntaxFacts.IsInNamespaceOrTypeContext(expression))
        {
            scope |= SurroundScope.Expression;
        }
        if (Types(first, last, start, end))
        {
            scope |= SurroundScope.Types;
        }
        return new SurroundTarget(range, scope);
    }

    /// <summary>The lines of a range after its first that begin inside a string's text (a verbatim, raw or interpolated
    /// string's), counted from its first: they hold the string's value, which indenting them would change.</summary>
    public static IReadOnlySet<int> InStrings(ParsedText syntax, TextRange range)
    {
        TextSnapshot text = syntax.Text;
        SyntaxNode root = syntax.Root;
        int first = text.GetLineFromOffset(range.Start);
        HashSet<int> inside = [];
        for (int line = first + 1; line <= text.GetLineFromOffset(range.End); line++)
        {
            if (InString(root, text.GetLineStart(line)))
            {
                inside.Add(line - first);
            }
        }
        return inside;
    }

    /// <summary>Whether an offset falls inside a comment or a string (a template there would be text, not code); one at
    /// a comment's start or just after its end is outside it.</summary>
    private static bool Inside(SyntaxNode root, int offset)
    {
        if (offset <= 0 || offset >= root.FullSpan.End)
        {
            return false;
        }
        SyntaxTrivia trivia = root.FindTrivia(offset);
        return (trivia.Span.Start < offset && offset < trivia.Span.End && trivia.Kind() is not SyntaxKind.WhitespaceTrivia and not SyntaxKind.EndOfLineTrivia)
            || InString(root, offset);
    }

    /// <summary>Whether an offset falls inside a string's text, past its opening quote and before its closing one, but
    /// not in the code of an interpolation.</summary>
    private static bool InString(SyntaxNode root, int offset)
    {
        if (offset <= 0 || offset >= root.FullSpan.End)
        {
            return false;
        }
        SyntaxToken token = root.FindToken(offset);
        if (token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken && token.Span.Start < offset && offset < token.Span.End)
        {
            return true;
        }
        // An interpolated string's text, though not the code of its interpolations.
        return token.Parent?.AncestorsAndSelf().OfType<InterpolatedStringExpressionSyntax>().FirstOrDefault() is { } interpolated
            && interpolated.Span.Start < offset && offset < interpolated.Span.End
            && !interpolated.Contents.OfType<InterpolationSyntax>().Any(i => i.Expression.Span.Start <= offset && offset <= i.Expression.Span.End);
    }

    /// <summary>Whether the code chosen begins its first line and ends its last, with only indentation and trailing
    /// whitespace beside it.</summary>
    private static bool WholeLines(TextSnapshot text, int start, int end)
    {
        int first = text.GetLineStart(text.GetLineFromOffset(start));
        LineSpan last = text.GetLine(text.GetLineFromOffset(end));
        for (int i = first; i < start; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }
        for (int i = end; i < last.End; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the code chosen is a run of whole statements in one block, switch section or file of top-level
    /// statements, a comment before the first or after the last taken with it.</summary>
    private static bool Statements(SyntaxToken first, SyntaxToken last, int start, int end)
    {
        List<StatementSyntax> opening = [.. Ancestors<StatementSyntax>(first).Where(s => Begins(s, start) && ListOf(s) is not null)];
        List<StatementSyntax> closing = [.. Ancestors<StatementSyntax>(last).Where(s => Ends(s, end) && ListOf(s) is not null)];
        return opening.Any(a => closing.Any(b => ListOf(a) == ListOf(b) && a.SpanStart <= b.SpanStart));
    }

    /// <summary>Whether the code chosen is a run of whole types (or namespaces in braces) in one namespace in braces or a
    /// file without a file-scoped namespace, where a namespace in braces may go around them.</summary>
    private static bool Types(SyntaxToken first, SyntaxToken last, int start, int end)
    {
        static SyntaxNode? Holder(MemberDeclarationSyntax member) =>
            member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or NamespaceDeclarationSyntax
            && member.Parent is NamespaceDeclarationSyntax or CompilationUnitSyntax
            && !member.Parent.ChildNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any()
                ? member.Parent
                : null;
        List<MemberDeclarationSyntax> opening = [.. Ancestors<MemberDeclarationSyntax>(first).Where(m => Begins(m, start) && Holder(m) is not null)];
        List<MemberDeclarationSyntax> closing = [.. Ancestors<MemberDeclarationSyntax>(last).Where(m => Ends(m, end) && Holder(m) is not null)];
        return opening.Any(a => closing.Any(b => Holder(a) == Holder(b) && a.SpanStart <= b.SpanStart));
    }

    private static IEnumerable<T> Ancestors<T>(SyntaxToken token) where T : SyntaxNode =>
        token.Parent?.AncestorsAndSelf().OfType<T>() ?? [];

    /// <summary>The list a statement is one of: its block's, its switch section's, or the file's top-level statements.</summary>
    private static SyntaxNode? ListOf(StatementSyntax statement) => statement.Parent switch
    {
        BlockSyntax or SwitchSectionSyntax => statement.Parent,
        GlobalStatementSyntax global => global.Parent,
        _ => null,
    };

    /// <summary>Whether a node begins at an offset, itself or the comment before it.</summary>
    private static bool Begins(SyntaxNode node, int offset) =>
        node.SpanStart == offset || node.GetLeadingTrivia().Any(t => t.SpanStart == offset && !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia));

    /// <summary>Whether a node ends at an offset, itself or the comment after it on its line.</summary>
    private static bool Ends(SyntaxNode node, int offset) =>
        node.Span.End == offset || node.GetTrailingTrivia().Any(t => t.Span.End == offset && !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia));
}

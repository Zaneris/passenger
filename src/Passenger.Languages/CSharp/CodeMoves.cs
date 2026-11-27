using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Edits by a C# file's structure, from its one parse: the statements or members a selection covers moved past the
/// one beside them in their block, type, namespace or file (Move Statement); an element of a list moved past the one
/// beside it (Move Element: an argument, a parameter, a type argument, an attribute, an enum member, a collection's
/// element, a base type, a declarator, an arm); and the body of a statement holding others taken out of it (Unwrap).</summary>
public static class CodeMoves
{
    /// <summary>The lines of the statements or members around a selection, and of the one beside them above or below in
    /// their block, switch section, type, namespace or file, each with the comments right above it; null where none stands
    /// beside them there, or one shares a line with code outside it.</summary>
    public static ((int First, int Last) Moving, (int First, int Last) Beside)? StatementLines(ParsedText csharp, TextRange selection, bool up)
    {
        SyntaxNode root = csharp.Root;
        if (UnitAt(root, selection.Start) is not { } first)
        {
            return null;
        }
        SyntaxNode last = selection.Length == 0 ? first : UnitAt(root, selection.End - 1) ?? first;
        // The two ends taken out to the innermost units of one list.
        if (Outward(first).SelectMany(a => Outward(last).Where(b => b.Parent == a.Parent).Select(b => (a, b))).FirstOrDefault() is not ({ } inner, { } outer))
        {
            return null;
        }
        (first, last) = (inner, outer);
        List<SyntaxNode> siblings = [.. first.Parent!.ChildNodes().Where(IsUnit)];
        int from = siblings.IndexOf(first);
        int to = siblings.IndexOf(last);
        if (from > to)
        {
            (from, to) = (to, from);
        }
        int beside = up ? from - 1 : to + 1;
        if (beside < 0 || beside >= siblings.Count || Lines(csharp.Text, siblings[from], siblings[to]) is not { } moving
            || Lines(csharp.Text, siblings[beside], siblings[beside]) is not { } other)
        {
            return null;
        }
        return (moving, other);
    }

    /// <summary>The element of a list at a caret and the one beside it on the left or right, innermost first, each without the
    /// separators and white space around it; null where the innermost element at the caret has none beside it that way.</summary>
    public static (TextRange Moving, TextRange Beside)? ElementSpans(ParsedText csharp, int caret, bool right)
    {
        SyntaxNode root = csharp.Root;
        SyntaxToken token = root.FindToken(Math.Clamp(caret, 0, Math.Max(0, root.FullSpan.End - 1)));
        // At an element's end, before its comma or the list's closer, the caret is the element's.
        if (caret > 0 && caret == token.SpanStart && (token.IsKind(SyntaxKind.CommaToken) || IsCloser(token)))
        {
            token = root.FindToken(caret - 1);
        }
        for (SyntaxNode? node = token.Parent; node?.Parent is { } parent; node = parent)
        {
            ChildSyntaxList children = parent.ChildNodesAndTokens();
            int at = IndexOf(children, node);
            bool before = at > 0 && children[at - 1].IsKind(SyntaxKind.CommaToken);
            bool after = at + 1 < children.Count && children[at + 1].IsKind(SyntaxKind.CommaToken);
            if (!before && !after)
            {
                continue;
            }
            int other = right ? at + 2 : at - 2;
            return (right ? after : before) && other >= 0 && other < children.Count && children[other].AsNode() is { } beside
                ? (TextRange.FromBounds(node.SpanStart, node.Span.End), TextRange.FromBounds(beside.SpanStart, beside.Span.End))
                : null;
        }
        return null;
    }

    private static bool IsCloser(SyntaxToken token) => token.Kind() is SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken
        or SyntaxKind.CloseBraceToken or SyntaxKind.GreaterThanToken;

    private static int IndexOf(ChildSyntaxList children, SyntaxNode node)
    {
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].AsNode() == node)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Unwrap at a caret: the innermost statement around it holding others (an if and its else branches, a loop, a
    /// using, lock, fixed, checked or unsafe statement, a try and its catches and finally, a block in a block) with the part
    /// the caret is in: the statement's lines, and the lines of what that part holds, which take its place; null where the
    /// caret is in none, or one of them shares a line with other code.</summary>
    public static ((int First, int Last) Whole, (int First, int Last)? Kept, string Name)? UnwrapLines(ParsedText csharp, int caret)
    {
        SyntaxNode root = csharp.Root;
        SyntaxToken token = root.FindToken(Math.Clamp(caret, 0, Math.Max(0, root.FullSpan.End - 1)));
        // Inside a lambda, a local function or a member, what holds it there is no statement to take out.
        for (SyntaxNode? node = token.Parent; node is not null and not (MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
            node = node.Parent)
        {
            if (Wrapped(node, caret) is not ({ } whole, { } kept, { } name))
            {
                continue;
            }
            if (Lines(csharp.Text, whole, whole) is not { } around)
            {
                return null;
            }
            if (kept is BlockSyntax block && Inside(csharp.Text, block) is { } between)
            {
                return (around, between.First <= between.Last ? between : null, name);
            }
            if (kept is BlockSyntax { Statements.Count: 0 })
            {
                return (around, null, name);
            }
            (SyntaxNode first, SyntaxNode last) = kept is BlockSyntax statements ? (statements.Statements[0], statements.Statements[^1]) : (kept, kept);
            return Lines(csharp.Text, first, last) is { } inside ? (around, inside, name) : null;
        }
        return null;
    }

    /// <summary>A statement a caret is in that Unwrap takes out (the whole if chain for an if or else), the statement whose
    /// body takes its place, and what it is called; null for any other node.</summary>
    private static (SyntaxNode Whole, StatementSyntax Kept, string Name)? Wrapped(SyntaxNode node, int caret)
    {
        switch (node)
        {
            case IfStatementSyntax when node.Parent is ElseClauseSyntax:
                return null;
            case IfStatementSyntax chain:
                for (IfStatementSyntax at = chain; ; at = (IfStatementSyntax)at.Else!.Statement)
                {
                    if (Within(at.Statement, caret) || at.Else is null)
                    {
                        return (chain, at.Statement, at == chain ? "if" : "else if");
                    }
                    if (at.Else.Statement is not IfStatementSyntax)
                    {
                        return Within(at.Else, caret) ? (chain, at.Else.Statement, "else") : (chain, chain.Statement, "if");
                    }
                }
            case TryStatementSyntax attempt:
                return attempt.Catches.FirstOrDefault(c => Within(c, caret)) is { } caught ? (attempt, caught.Block, "catch")
                    : attempt.Finally is { } last && Within(last, caret) ? (attempt, last.Block, "finally")
                    : (attempt, attempt.Block, "try");
            case WhileStatementSyntax loop:
                return (loop, loop.Statement, "while");
            case DoStatementSyntax loop:
                return (loop, loop.Statement, "do");
            case ForStatementSyntax loop:
                return (loop, loop.Statement, "for");
            case CommonForEachStatementSyntax loop:
                return (loop, loop.Statement, "foreach");
            case UsingStatementSyntax used:
                return (used, used.Statement, "using");
            case LockStatementSyntax locked:
                return (locked, locked.Statement, "lock");
            case FixedStatementSyntax fixedOne:
                return (fixedOne, fixedOne.Statement, "fixed");
            case CheckedStatementSyntax check:
                return (check, check.Block, check.Keyword.Text);
            case UnsafeStatementSyntax unsafeOne:
                return (unsafeOne, unsafeOne.Block, "unsafe");
            case BlockSyntax block when block.Parent is BlockSyntax or SwitchSectionSyntax:
                return (block, block, "block");
            default:
                return null;
        }
    }

    /// <summary>The lines between a block's braces, each on a line of its own, its comments and blank lines with them; null
    /// where a brace shares its line with code.</summary>
    private static (int First, int Last)? Inside(TextSnapshot text, BlockSyntax block)
    {
        int open = text.GetLineFromOffset(block.OpenBraceToken.SpanStart);
        int close = text.GetLineFromOffset(block.CloseBraceToken.SpanStart);
        LineSpan opening = text.GetLine(open);
        LineSpan closing = text.GetLine(close);
        bool alone = close > open && text.GetText(block.OpenBraceToken.Span.End, opening.End - block.OpenBraceToken.Span.End).Trim().Length == 0
            && text.GetText(closing.Start, block.CloseBraceToken.SpanStart - closing.Start).Trim().Length == 0;
        if (!alone)
        {
            return null;
        }
        int first = open + 1;
        int last = close - 1;
        while (first <= last && text.GetText(text.GetLine(first).Start, text.GetLine(first).Length).Trim().Length == 0)
        {
            first++;
        }
        while (last >= first && text.GetText(text.GetLine(last).Start, text.GetLine(last).Length).Trim().Length == 0)
        {
            last--;
        }
        return (first, last);
    }

    private static bool Within(SyntaxNode node, int caret) => node.FullSpan.Start <= caret && caret <= node.Span.End;

    /// <summary>A unit and each unit around it, innermost first.</summary>
    private static IEnumerable<SyntaxNode> Outward(SyntaxNode unit) => unit.AncestorsAndSelf().Where(IsUnit);

    /// <summary>The innermost statement or member of a list around an offset.</summary>
    private static SyntaxNode? UnitAt(SyntaxNode root, int offset) =>
        root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1))).Parent?.AncestorsAndSelf().FirstOrDefault(IsUnit);

    /// <summary>Whether a node stands in a list of its kind that Move Statement moves it in.</summary>
    private static bool IsUnit(SyntaxNode node) => node.Parent switch
    {
        BlockSyntax or SwitchSectionSyntax => node is StatementSyntax,
        TypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or CompilationUnitSyntax => node is MemberDeclarationSyntax,
        SwitchStatementSyntax => node is SwitchSectionSyntax,
        AccessorListSyntax => node is AccessorDeclarationSyntax,
        _ => false,
    };

    /// <summary>The whole lines from one node's first, with the comments right above it, to another's last; null where code
    /// outside them shares one of those lines.</summary>
    private static (int First, int Last)? Lines(TextSnapshot text, SyntaxNode first, SyntaxNode last)
    {
        int start = first.SpanStart;
        // Comments right above it go with it, up to a blank line or a directive.
        SyntaxTriviaList leading = first.GetLeadingTrivia();
        int breaks = 0;
        for (int i = leading.Count - 1; i >= 0; i--)
        {
            SyntaxTrivia trivia = leading[i];
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (++breaks > 1)
                {
                    break;
                }
            }
            else if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                start = trivia.SpanStart;
                breaks = 0;
            }
            else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                break;
            }
        }
        int end = last.Span.End;
        int firstLine = text.GetLineFromOffset(start);
        int lastLine = text.GetLineFromOffset(end);
        LineSpan top = text.GetLine(firstLine);
        LineSpan bottom = text.GetLine(lastLine);
        string after = text.GetText(end, bottom.End - end).Trim();
        if (text.GetText(top.Start, start - top.Start).Trim().Length > 0 || (after.Length > 0 && !after.StartsWith("//", StringComparison.Ordinal)))
        {
            return null;
        }
        return (firstLine, lastLine);
    }
}

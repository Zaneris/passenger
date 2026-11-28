using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Where Move Statement takes lines (<see cref="CodeMoves.StatementMoveOf"/>): past the lines beside them, or, with
/// none beside, before another line, each moved to an indentation but those beginning inside a string (<see cref="StringLines"/>).</summary>
public sealed record StatementMove((int First, int Last) Moving, (int First, int Last)? Beside, int Before, string? Indent, IReadOnlySet<int> Strings);

/// <summary>A statement Unwrap may take out (<see cref="CodeMoves.UnwrapChoices"/>): its lines, the lines of the part the
/// caret is in that take its place (none for an empty one), what it is called, its first line's code, and its lines beginning
/// inside a string, which keep their indentation (<see cref="StringLines"/>).</summary>
public sealed record Unwrapping((int First, int Last) Whole, (int First, int Last)? Kept, string Name, string Header, IReadOnlySet<int> Strings);

/// <summary>Edits by a C# file's structure, from its one parse: the statements or members a selection covers moved past the
/// one beside them in their block, type, namespace or file, statements into and out of blocks (Move Statement); an element of a list moved past the one
/// beside it (Move Element: an argument, a parameter, a type argument, an attribute, an enum member, a collection's
/// element, a base type, a declarator, an arm); and the body of a statement holding others taken out of it, the person
/// choosing which where several hold the caret (Unwrap).</summary>
public static class CodeMoves
{
    /// <summary>Where Move Statement takes the statements or members around a selection: past the one beside them above or
    /// below in their block, switch section, type, namespace or file, each with the comments right above it; statements into
    /// the block of the statement beside them (its first branch's start going down, its last's end going up), and out of the
    /// block they begin or end, into the next or previous branch of its if or try, else past the statement holding it. Null
    /// where none of these is there, or one shares a line with code outside it.</summary>
    public static StatementMove? StatementMoveOf(ParsedText csharp, TextRange selection, bool up)
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
        SyntaxNode list = first.Parent!;
        List<SyntaxNode> siblings = [.. list.ChildNodes().Where(IsUnit)];
        int from = siblings.IndexOf(first);
        int to = siblings.IndexOf(last);
        if (from > to)
        {
            (from, to) = (to, from);
        }
        TextSnapshot text = csharp.Text;
        if (Lines(text, siblings[from], siblings[to]) is not { } moving)
        {
            return null;
        }
        int beside = up ? from - 1 : to + 1;
        bool statements = list is BlockSyntax or SwitchSectionSyntax;
        if (beside >= 0 && beside < siblings.Count)
        {
            if (statements && Entered(siblings[beside], up) is { } block && Inside(text, block) is not null)
            {
                return Into(text, moving, block, up, Deeper(text, siblings[from], list), Strings(text, siblings[from..(to + 1)]));
            }
            return Lines(text, siblings[beside], siblings[beside]) is { } other ? new StatementMove(moving, other, 0, null, new HashSet<int>()) : null;
        }
        if (list is not BlockSyntax ending)
        {
            return null;
        }
        if (Branch(ending, up) is { } next && Inside(text, next) is not null && Inside(text, ending) is not null)
        {
            return Into(text, moving, next, up, IndentAt(text, siblings[from].SpanStart), Strings(text, siblings[from..(to + 1)]));
        }
        if (Holder(ending) is not { Parent: BlockSyntax or SwitchSectionSyntax } holder || Lines(text, holder, holder) is not { } around)
        {
            return null;
        }
        return new StatementMove(moving, null, up ? around.First : around.Last + 1, IndentAt(text, holder.SpanStart), Strings(text, siblings[from..(to + 1)]));
    }

    /// <summary>The lines beginning inside the strings of some statements or members.</summary>
    private static HashSet<int> Strings(TextSnapshot text, IEnumerable<SyntaxNode> units) => [.. units.SelectMany(u => StringLines.Of(u, text))];

    /// <summary>Statements put into a block: at its start going down, before its first statement's indentation, or at its end
    /// going up; an empty one's taking <paramref name="deeper"/>.</summary>
    private static StatementMove Into(TextSnapshot text, (int First, int Last) moving, BlockSyntax block, bool up, string deeper, IReadOnlySet<int> strings)
    {
        int open = text.GetLineFromOffset(block.OpenBraceToken.SpanStart);
        int close = text.GetLineFromOffset(block.CloseBraceToken.SpanStart);
        SyntaxNode? beside = up ? block.Statements.LastOrDefault() : block.Statements.FirstOrDefault();
        return new StatementMove(moving, null, up ? close : open + 1, beside is null ? deeper : IndentAt(text, beside.SpanStart), strings);
    }

    /// <summary>The block a statement beside those moving holds that they go into: its first branch's going down, its last's
    /// going up; null for one that holds no block.</summary>
    private static BlockSyntax? Entered(SyntaxNode statement, bool up) => statement switch
    {
        BlockSyntax block => block,
        IfStatementSyntax chain when !up => chain.Statement as BlockSyntax,
        IfStatementSyntax chain => LastBranch(chain),
        TryStatementSyntax attempt => up ? attempt.Finally?.Block ?? attempt.Catches.LastOrDefault()?.Block ?? attempt.Block : attempt.Block,
        WhileStatementSyntax loop => loop.Statement as BlockSyntax,
        DoStatementSyntax loop => loop.Statement as BlockSyntax,
        ForStatementSyntax loop => loop.Statement as BlockSyntax,
        CommonForEachStatementSyntax loop => loop.Statement as BlockSyntax,
        UsingStatementSyntax used => used.Statement as BlockSyntax,
        LockStatementSyntax locked => locked.Statement as BlockSyntax,
        FixedStatementSyntax fixedOne => fixedOne.Statement as BlockSyntax,
        CheckedStatementSyntax check => check.Block,
        UnsafeStatementSyntax unsafeOne => unsafeOne.Block,
        _ => null,
    };

    private static BlockSyntax? LastBranch(IfStatementSyntax chain)
    {
        IfStatementSyntax at = chain;
        while (at.Else?.Statement is IfStatementSyntax next)
        {
            at = next;
        }
        return (at.Else?.Statement ?? at.Statement) as BlockSyntax;
    }

    /// <summary>The block of an if's or a try's next branch after a block (going down) or the one before it (going up); null
    /// where the block is no branch of one, or the first or last.</summary>
    private static BlockSyntax? Branch(BlockSyntax block, bool up)
    {
        List<BlockSyntax?> branches = Holder(block) switch
        {
            IfStatementSyntax chain => [.. Branches(chain)],
            TryStatementSyntax attempt => [attempt.Block, .. attempt.Catches.Select(c => c.Block), attempt.Finally?.Block],
            _ => [],
        };
        int at = branches.IndexOf(block);
        int next = up ? at - 1 : at + 1;
        return at >= 0 && next >= 0 && next < branches.Count ? branches[next] : null;

        static IEnumerable<BlockSyntax?> Branches(IfStatementSyntax chain)
        {
            for (IfStatementSyntax? at = chain; at is not null; at = at.Else?.Statement as IfStatementSyntax)
            {
                yield return at.Statement as BlockSyntax;
                if (at.Else is { Statement: not IfStatementSyntax } last)
                {
                    yield return last.Statement as BlockSyntax;
                }
            }
        }
    }

    /// <summary>The statement whose block a block is, its whole if chain for an if's or else's, its try for a try's, catch's
    /// or finally's, itself for a block in a block; null for a member's, a lambda's or a local function's body.</summary>
    private static StatementSyntax? Holder(BlockSyntax block)
    {
        SyntaxNode? owner = block.Parent switch
        {
            CatchClauseSyntax or FinallyClauseSyntax => block.Parent.Parent,
            ElseClauseSyntax => block.Parent.Parent,
            BlockSyntax or SwitchSectionSyntax => block,
            StatementSyntax statement and not LocalFunctionStatementSyntax => statement,
            _ => null,
        };
        while (owner is IfStatementSyntax { Parent: ElseClauseSyntax { Parent: IfStatementSyntax above } })
        {
            owner = above;
        }
        return owner as StatementSyntax;
    }

    /// <summary>The indentation of a statement a level deeper than one in a list: the list's own step past its brace or label.</summary>
    private static string Deeper(TextSnapshot text, SyntaxNode statement, SyntaxNode list)
    {
        string own = IndentAt(text, statement.SpanStart);
        string outer = IndentAt(text, list is BlockSyntax block ? block.OpenBraceToken.SpanStart : list.SpanStart);
        return own + (own.Length > outer.Length && own.StartsWith(outer, StringComparison.Ordinal) ? own[outer.Length..] : "    ");
    }

    private static string IndentAt(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        return text.GetText(line.Start, TextNavigation.IndentEnd(text, line) - line.Start);
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

    /// <summary>What Unwrap may take out at a caret: each statement around it holding others (an if and its else branches, a
    /// loop, a using, lock, fixed, checked or unsafe statement, a try and its catches and finally, a block in a block),
    /// innermost first, up to the member, lambda or local function holding it, with the part the caret is in: the
    /// statement's lines, the lines of what that part holds, which take its place, what it is called and its first line's
    /// code. One that shares a line with other code is passed over.</summary>
    public static IReadOnlyList<Unwrapping> UnwrapChoices(ParsedText csharp, int caret)
    {
        SyntaxNode root = csharp.Root;
        SyntaxToken token = root.FindToken(Math.Clamp(caret, 0, Math.Max(0, root.FullSpan.End - 1)));
        List<Unwrapping> found = [];
        for (SyntaxNode? node = token.Parent; node is not null and not (MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
            node = node.Parent)
        {
            if (Wrapped(node, caret) is not ({ } whole, { } kept, { } name) || Lines(csharp.Text, whole, whole) is not { } around)
            {
                continue;
            }
            string header = csharp.Text.GetLineText(csharp.Text.GetLineFromOffset(whole.SpanStart)).Trim();
            IReadOnlySet<int> strings = StringLines.Of(kept, csharp.Text);
            if (kept is BlockSyntax block && Inside(csharp.Text, block) is { } between)
            {
                found.Add(new Unwrapping(around, between.First <= between.Last ? between : null, name, header, strings));
            }
            else if (kept is BlockSyntax { Statements.Count: 0 })
            {
                found.Add(new Unwrapping(around, null, name, header, strings));
            }
            else if (Lines(csharp.Text, kept is BlockSyntax statements ? statements.Statements[0] : kept, kept is BlockSyntax many ? many.Statements[^1] : kept) is { } inside)
            {
                found.Add(new Unwrapping(around, inside, name, header, strings));
            }
        }
        return found;
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>What folds in one parse of a C# file: the comments it begins with (its header), the usings, a namespace's,
/// a type's, a member's, an accessor's and a multi-line lambda's braces, #region to #endregion, and doc comments and
/// block comments over several lines, each of its kind. A braced region starts at the end of the line before its brace
/// when the brace stands alone, so folded it reads <c>void Run() {...}</c>. Lines are the editor's. Found from the
/// parse before as well: a subtree the incremental parse took whole from the tree before keeps the regions it had
/// there, moved, so a key reads the nodes it changed and the lists of their neighbours, never the whole file (whose
/// every node a walk of the new tree would build anew).</summary>
public sealed class FoldRegions
{
    private FoldRegions(SyntaxNode root, Made[] owned, FoldRegion[] comments, IReadOnlyList<FoldRegion> all)
    {
        Root = root;
        Owned = owned;
        Comments = comments;
        All = all;
    }

    /// <summary>Every region, in order of where it starts.</summary>
    public IReadOnlyList<FoldRegion> All { get; }

    /// <summary>The tree these are of, which the next parse is compared with.</summary>
    private SyntaxNode Root { get; }

    /// <summary>The regions of nodes' braces and of a namespace's usings, each with the node that made it, by where
    /// they start (in the order of a walk of the tree where they start together); the comments' by where they start.</summary>
    private Made[] Owned { get; }

    private FoldRegion[] Comments { get; }

    /// <summary>The regions of a parse, read from the subtrees it does not share with <paramref name="before"/>, the
    /// regions of the tree it was parsed incrementally from (null for a parse of the whole text).</summary>
    public static FoldRegions Of(ParsedText parse, FoldRegions? before)
    {
        SyntaxNode root = parse.Root;
        TextSnapshot text = parse.Text;
        // Where the text changed since the tree before, in this one's offsets, where children are paired from both ends.
        TextSpan changed = default;
        if (before is not null)
        {
            TextChangeRange change = TextChangeRange.Collapse(root.SyntaxTree.GetText().GetChangeRanges(before.Root.SyntaxTree.GetText()));
            changed = new TextSpan(change.Span.Start, change.NewLength);
        }
        Walk walk = new(text, before, changed);
        walk.Run(root, before?.Root);
        List<Made> owned = walk.Owned;
        if (!InOrder(owned))
        {
            owned = [.. owned.OrderBy(m => m.Region.Start)];
        }
        List<FoldRegion> usings = [];
        Header(text, root, usings);
        Usings(text, root, usings);
        List<FoldRegion> regions = Regions(text, root);
        Made[] made = [.. owned];
        FoldRegion[] comments = [.. walk.Comments];
        // Regions starting together in this order: the file's header and usings, nodes, #regions, comments.
        return new FoldRegions(root, made, comments, Merge([usings, new Unmade(made), [.. regions.OrderBy(r => r.Start)], comments]));
    }

    /// <summary>A pair of braces over several lines: from the end of the line before an opening brace on its own line,
    /// or the end of the brace's line, to the closing brace.</summary>
    private static void Braced(TextSnapshot text, SyntaxToken open, SyntaxToken close, List<FoldRegion> found, FoldKind kind)
    {
        if (open.IsMissing || close.IsMissing)
        {
            return;
        }
        int braceLine = text.GetLineFromOffset(open.SpanStart);
        bool alone = braceLine > 0 && Blank(text, text.GetLineStart(braceLine), open.SpanStart);
        int header = alone ? braceLine - 1 : braceLine;
        if (text.GetLineFromOffset(close.SpanStart) <= header)
        {
            return;
        }
        found.Add(new FoldRegion(text.GetLine(header).End, close.Span.End, alone ? "{...}" : "...}", Kind: kind));
    }

    private static bool Blank(TextSnapshot text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Two or more usings in a row, folded after the first.</summary>
    private static void Usings(TextSnapshot text, SyntaxNode scope, List<FoldRegion> found)
    {
        SyntaxList<UsingDirectiveSyntax> usings = scope switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax space => space.Usings,
            _ => default,
        };
        if (usings.Count < 2)
        {
            return;
        }
        int first = text.GetLineFromOffset(usings[0].SpanStart);
        if (text.GetLineFromOffset(usings[^1].Span.End) > first)
        {
            found.Add(new FoldRegion(text.GetLine(first).End, usings[^1].Span.End, "...", Kind: FoldKind.Imports));
        }
    }

    /// <summary>The regions a node's own braces and usings make, its descendants' apart.</summary>
    private static void Own(TextSnapshot text, SyntaxNode node, List<FoldRegion> found)
    {
        switch (node)
        {
            case NamespaceDeclarationSyntax block:
                Braced(text, block.OpenBraceToken, block.CloseBraceToken, found, FoldKind.Code);
                Usings(text, block, found);
                break;
            case FileScopedNamespaceDeclarationSyntax space:
                Usings(text, space, found);
                break;
            case BaseTypeDeclarationSyntax type when !type.OpenBraceToken.IsMissing:
                Braced(text, type.OpenBraceToken, type.CloseBraceToken, found, FoldKind.Code);
                break;
            case BaseMethodDeclarationSyntax { Body: { } body }:
                Braced(text, body.OpenBraceToken, body.CloseBraceToken, found, FoldKind.Body);
                break;
            case LocalFunctionStatementSyntax { Body: { } body }:
                Braced(text, body.OpenBraceToken, body.CloseBraceToken, found, FoldKind.Body);
                break;
            case BasePropertyDeclarationSyntax { AccessorList: { } accessors }:
                Braced(text, accessors.OpenBraceToken, accessors.CloseBraceToken, found, FoldKind.Body);
                break;
            case AccessorDeclarationSyntax { Body: { } body }:
                Braced(text, body.OpenBraceToken, body.CloseBraceToken, found, FoldKind.Body);
                break;
            case AnonymousFunctionExpressionSyntax { Body: BlockSyntax body }:
                Braced(text, body.OpenBraceToken, body.CloseBraceToken, found, FoldKind.Code);
                break;
        }
    }

    /// <summary>Doc comments and block comments over several lines in a token's trivia, folded after their first.</summary>
    private static void Commented(TextSnapshot text, SyntaxToken token, List<FoldRegion> found)
    {
        foreach (SyntaxTrivia trivia in token.LeadingTrivia)
        {
            Comment(text, trivia, found);
        }
        foreach (SyntaxTrivia trivia in token.TrailingTrivia)
        {
            Comment(text, trivia, found);
        }
    }

    private static void Comment(TextSnapshot text, SyntaxTrivia trivia, List<FoldRegion> found)
    {
        if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
        {
            return;
        }
        // A /// comment's trivia ends after its last line's break; its last line is the one before.
        int end = trivia.Span.End;
        while (end > trivia.SpanStart && text[end - 1] is '\n' or '\r')
        {
            end--;
        }
        int first = text.GetLineFromOffset(trivia.SpanStart);
        if (text.GetLineFromOffset(end) > first)
        {
            found.Add(new FoldRegion(text.GetLine(first).End, end, "...", Kind: trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ? FoldKind.Comment : FoldKind.DocComment));
        }
    }

    /// <summary>The comments the file begins with over several lines, a licence's or a copyright's, folded after their first
    /// line: read from the first token's trivia alone, so a key costs nothing here.</summary>
    private static void Header(TextSnapshot text, SyntaxNode root, List<FoldRegion> found)
    {
        int start = -1;
        int end = -1;
        foreach (SyntaxTrivia trivia in root.GetFirstToken(includeZeroWidth: true).LeadingTrivia)
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                start = start < 0 ? trivia.SpanStart : start;
                end = trivia.Span.End;
            }
            else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                break;
            }
        }
        int first = start < 0 ? -1 : text.GetLineFromOffset(start);
        if (first >= 0 && text.GetLineFromOffset(end) > first)
        {
            found.Add(new FoldRegion(text.GetLine(first).End, end, "...", Kind: FoldKind.FileHeader));
        }
    }

    /// <summary>Each #region to its #endregion, the region's name left showing: found through the nodes that hold a
    /// directive, which Roslyn marks, so a file without one is not walked for them.</summary>
    private static List<FoldRegion> Regions(TextSnapshot text, SyntaxNode root)
    {
        List<FoldRegion> found = [];
        if (!root.ContainsDirectives)
        {
            return found;
        }
        Stack<int> open = [];
        ChildSyntaxList.Enumerator[] stack = new ChildSyntaxList.Enumerator[8];
        int depth = 0;
        stack[depth++] = root.ChildNodesAndTokens().GetEnumerator();
        while (depth > 0)
        {
            if (!stack[depth - 1].MoveNext())
            {
                depth--;
                continue;
            }
            SyntaxNodeOrToken child = stack[depth - 1].Current;
            if (!child.ContainsDirectives)
            {
                continue;
            }
            if (child.AsNode() is { } node)
            {
                if (depth == stack.Length)
                {
                    Array.Resize(ref stack, depth * 2);
                }
                stack[depth++] = node.ChildNodesAndTokens().GetEnumerator();
                continue;
            }
            SyntaxToken token = child.AsToken();
            Directives(token.LeadingTrivia);
            Directives(token.TrailingTrivia);
        }
        return found;

        void Directives(SyntaxTriviaList trivia)
        {
            foreach (SyntaxTrivia directive in trivia)
            {
                if (!directive.IsDirective)
                {
                    continue;
                }
                if (directive.GetStructure() is RegionDirectiveTriviaSyntax region)
                {
                    open.Push(region.SpanStart);
                }
                else if (directive.GetStructure() is EndRegionDirectiveTriviaSyntax end && open.Count > 0)
                {
                    int first = text.GetLineFromOffset(open.Pop());
                    int last = text.GetLineFromOffset(end.SpanStart);
                    if (last > first)
                    {
                        found.Add(new FoldRegion(text.GetLine(first).End, text.GetLine(last).End, "...", Kind: FoldKind.Region));
                    }
                }
            }
        }
    }

    private static bool InOrder(List<Made> regions)
    {
        for (int i = 1; i < regions.Count; i++)
        {
            if (regions[i].Region.Start < regions[i - 1].Region.Start)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lists each in order of where their regions start as one, a region of an earlier list first where two
    /// start together; of two that fold the same text (a header that is one block comment), the earlier list's alone.</summary>
    private static FoldRegion[] Merge(IReadOnlyList<IReadOnlyList<FoldRegion>> lists)
    {
        FoldRegion[] merged = new FoldRegion[lists.Sum(l => l.Count)];
        int[] next = new int[lists.Count];
        int count = 0;
        for (int k = 0; k < merged.Length; k++)
        {
            int best = -1;
            for (int l = 0; l < lists.Count; l++)
            {
                if (next[l] < lists[l].Count && (best < 0 || lists[l][next[l]].Start < lists[best][next[best]].Start))
                {
                    best = l;
                }
            }
            FoldRegion region = lists[best][next[best]++];
            if (count == 0 || merged[count - 1].Start != region.Start || merged[count - 1].End != region.End)
            {
                merged[count++] = region;
            }
        }
        return count == merged.Length ? merged : merged[..count];
    }

    /// <summary>A walk of the tree in document order that, beside a node of the tree before, takes the regions of each
    /// child the incremental parse took from it whole rather than walk that child; it holds a stack of its own, since a
    /// long chain of operators nests deeper than a thread's stack would allow.</summary>
    private sealed class Walk(TextSnapshot text, FoldRegions? before, TextSpan changed)
    {
        private Frame[] frames = new Frame[16];
        private int depth;

        private readonly List<FoldRegion> own = [];

        public List<Made> Owned { get; } = new(before?.Owned.Length ?? 0);

        public List<FoldRegion> Comments { get; } = new(before?.Comments.Length ?? 0);

        public void Run(SyntaxNode root, SyntaxNode? old)
        {
            Enter(root, old);
            while (depth > 0)
            {
                if (frames[depth - 1].Next(out SyntaxNodeOrToken child, out SyntaxNode? was))
                {
                    Enter(child, was);
                }
                else
                {
                    frames[--depth] = default;
                }
            }
        }

        private void Enter(SyntaxNodeOrToken item, SyntaxNode? old)
        {
            if (item.AsNode() is not { } node)
            {
                Commented(text, item.AsToken(), Comments);
                return;
            }
            if (old is not null && before is not null && node.IsIncrementallyIdenticalTo(old))
            {
                Take(before, old.FullSpan, node.FullSpan.Start - old.FullSpan.Start, depth);
                return;
            }
            Own(text, node, own);
            foreach (FoldRegion region in own)
            {
                Owned.Add(new Made(region, node.FullSpan.Start, node.FullSpan.End, depth));
            }
            own.Clear();
            if (depth == frames.Length)
            {
                Array.Resize(ref frames, depth * 2);
            }
            if (old is null)
            {
                frames[depth++] = Frame.Walking(node);
            }
            else if (node.FullSpan.Start <= changed.End && node.FullSpan.End >= changed.Start)
            {
                frames[depth++] = Frame.Pairing(node, old);
            }
            else
            {
                frames[depth++] = node.RawKind == old.RawKind && node.FullSpan.Length == old.FullSpan.Length ? Frame.InStep(node, old) : Frame.Walking(node);
            }
        }

        /// <summary>The regions a subtree of the tree before made, moved to where it is now: not its ancestors', though a
        /// parent's from the braces of its body or accessors lie inside that child (and may span the same text, its other
        /// children empty), and a reused child may be another kind of node's now. The subtree is as deep in both trees,
        /// both walks going down from their roots by pairs.</summary>
        private void Take(FoldRegions from, TextSpan span, int moved, int deep)
        {
            Made[] owned = from.Owned;
            for (int i = Sorted.FirstAtOrAfter(owned, static m => m.Region.Start, span.Start); i < owned.Length && owned[i].Region.Start < span.End; i++)
            {
                Made made = owned[i];
                if (made.Region.End <= span.End && made.OwnerStart >= span.Start && made.OwnerEnd <= span.End && made.OwnerDepth >= deep)
                {
                    Owned.Add(made with { Region = made.Region with { Start = made.Region.Start + moved, End = made.Region.End + moved }, OwnerStart = made.OwnerStart + moved, OwnerEnd = made.OwnerEnd + moved });
                }
            }
            FoldRegion[] comments = from.Comments;
            for (int i = Sorted.FirstAtOrAfter(comments, static r => r.Start, span.Start); i < comments.Length && comments[i].Start < span.End; i++)
            {
                if (comments[i].End <= span.End)
                {
                    Comments.Add(comments[i] with { Start = comments[i].Start + moved, End = comments[i].End + moved });
                }
            }
        }
    }

    /// <summary>A region, and the full span of the node that made it and how deep in the tree that node is.</summary>
    private readonly record struct Made(FoldRegion Region, int OwnerStart, int OwnerEnd, int OwnerDepth);

    /// <summary>The regions alone, read in place.</summary>
    private sealed class Unmade(Made[] made) : IReadOnlyList<FoldRegion>
    {
        public int Count => made.Length;

        public FoldRegion this[int index] => made[index].Region;

        public IEnumerator<FoldRegion> GetEnumerator() => made.Select(m => m.Region).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A node whose children are being walked: in order, or beside the tree before's node in its place, its
    /// children paired with that node's: where the text changed, the same from the front and from the back and the rest
    /// in turn; elsewhere, one for one.</summary>
    private struct Frame
    {
        private ChildSyntaxList.Enumerator children;
        private ChildSyntaxList.Enumerator oldChildren;
        private bool inStep;
        private SyntaxNodeOrToken[]? paired;
        private SyntaxNode?[]? olds;
        private int index;

        public static Frame Walking(SyntaxNode node) => new() { children = node.ChildNodesAndTokens().GetEnumerator() };

        /// <summary>A node the parse made anew away from the text that changed, beside one of the same kind and length
        /// in its place, child for child with no list made: an incremental parse makes a namespace and the nodes that
        /// end just before an edit anew, though it reuses what they hold.</summary>
        public static Frame InStep(SyntaxNode node, SyntaxNode old) =>
            new() { children = node.ChildNodesAndTokens().GetEnumerator(), oldChildren = old.ChildNodesAndTokens().GetEnumerator(), inStep = true };

        public static Frame Pairing(SyntaxNode node, SyntaxNode old)
        {
            SyntaxNodeOrToken[] now = Children(node);
            SyntaxNodeOrToken[] was = Children(old);
            int n = now.Length;
            int m = was.Length;
            int front = 0;
            while (front < n && front < m && Same(now[front], was[front]))
            {
                front++;
            }
            int back = 0;
            while (back < n - front && back < m - front && Same(now[n - 1 - back], was[m - 1 - back]))
            {
                back++;
            }
            SyntaxNode?[] olds = new SyntaxNode?[n];
            for (int i = 0; i < n; i++)
            {
                int j = i < n - back ? i : i - n + m;
                olds[i] = i < front || i >= n - back || j < m - back ? was[j].AsNode() : null;
            }
            return new Frame { paired = now, olds = olds };
        }

        public bool Next(out SyntaxNodeOrToken child, out SyntaxNode? old)
        {
            if (paired is null)
            {
                bool more = children.MoveNext();
                child = more ? children.Current : default;
                old = more && inStep && oldChildren.MoveNext() ? oldChildren.Current.AsNode() : null;
                return more;
            }
            if (index < paired.Length)
            {
                child = paired[index];
                old = olds![index];
                index++;
                return true;
            }
            child = default;
            old = null;
            return false;
        }

        private static SyntaxNodeOrToken[] Children(SyntaxNode node)
        {
            ChildSyntaxList list = node.ChildNodesAndTokens();
            SyntaxNodeOrToken[] all = new SyntaxNodeOrToken[list.Count];
            int i = 0;
            foreach (SyntaxNodeOrToken child in list)
            {
                all[i++] = child;
            }
            return all;
        }

        private static bool Same(SyntaxNodeOrToken now, SyntaxNodeOrToken was) => now.IsIncrementallyIdenticalTo(was);
    }
}

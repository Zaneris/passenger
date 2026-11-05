using System.Text;

namespace Passenger.Text;

/// <summary>An immutable B-tree of text. Leaves hold up to <see cref="MaxLeaf"/> characters and
/// branches up to <see cref="MaxChildren"/> children, each node caching its length and its count of
/// '\n', so finding an offset or a line and replacing a range cost O(log n). An edit copies only the
/// path it touches, so every earlier version stays whole and readable from any thread.</summary>
internal abstract class RopeNode
{
    public const int MaxLeaf = 1024;
    public const int MaxChildren = 16;

    public abstract int Length { get; }
    public abstract int Breaks { get; }
    public abstract int Height { get; }
}

internal sealed class RopeLeaf : RopeNode
{
    public static readonly RopeLeaf Empty = new(string.Empty);

    public RopeLeaf(string text)
    {
        Text = text;
        Breaks = text.AsSpan().Count('\n');
    }

    public string Text { get; }
    public override int Length => Text.Length;
    public override int Breaks { get; }
    public override int Height => 0;
}

internal sealed class RopeBranch : RopeNode
{
    public RopeBranch(RopeNode[] children)
    {
        Children = children;
        int length = 0;
        int breaks = 0;
        foreach (RopeNode child in children)
        {
            length += child.Length;
            breaks += child.Breaks;
        }
        Length = length;
        Breaks = breaks;
        Height = children[0].Height + 1;
    }

    public RopeNode[] Children { get; }
    public override int Length { get; }
    public override int Breaks { get; }
    public override int Height { get; }
}

internal static class Rope
{
    private const int MaxLeaf = RopeNode.MaxLeaf;
    private const int MaxChildren = RopeNode.MaxChildren;

    public static RopeNode Build(string text)
    {
        if (text.Length == 0)
        {
            return RopeLeaf.Empty;
        }
        List<RopeNode> leaves = [];
        AddLeaves(text.AsSpan(), leaves);
        return Join(leaves);
    }

    /// <summary>The rope with <paramref name="length"/> characters at <paramref name="start"/>
    /// replaced by <paramref name="text"/>.</summary>
    public static RopeNode Replace(RopeNode root, int start, int length, ReadOnlySpan<char> text)
    {
        List<RopeNode> nodes = [];
        Replace(root, start, start + length, text, nodes);
        return Join(nodes);
    }

    public static char CharAt(RopeNode node, int offset)
    {
        while (node is RopeBranch branch)
        {
            foreach (RopeNode child in branch.Children)
            {
                if (offset < child.Length)
                {
                    node = child;
                    break;
                }
                offset -= child.Length;
            }
        }
        return ((RopeLeaf)node).Text[offset];
    }

    public static void CopyTo(RopeNode node, int start, Span<char> destination)
    {
        if (destination.Length == 0)
        {
            return;
        }
        if (node is RopeLeaf leaf)
        {
            leaf.Text.AsSpan(start, destination.Length).CopyTo(destination);
            return;
        }
        int pos = 0;
        int end = start + destination.Length;
        foreach (RopeNode child in ((RopeBranch)node).Children)
        {
            int childEnd = pos + child.Length;
            if (childEnd > start && pos < end)
            {
                int from = Math.Max(start, pos);
                int to = Math.Min(end, childEnd);
                CopyTo(child, from - pos, destination.Slice(from - start, to - from));
            }
            if (childEnd >= end)
            {
                break;
            }
            pos = childEnd;
        }
    }

    /// <summary>Calls <paramref name="visit"/> with each leaf's slice of the range, in order, so a
    /// reader can stream the text without building one string.</summary>
    public static void Visit(RopeNode node, int start, int length, Action<ReadOnlyMemory<char>> visit)
    {
        if (length == 0)
        {
            return;
        }
        if (node is RopeLeaf leaf)
        {
            visit(leaf.Text.AsMemory(start, length));
            return;
        }
        int pos = 0;
        int end = start + length;
        foreach (RopeNode child in ((RopeBranch)node).Children)
        {
            int childEnd = pos + child.Length;
            if (childEnd > start && pos < end)
            {
                int from = Math.Max(start, pos);
                int to = Math.Min(end, childEnd);
                Visit(child, from - pos, to - from, visit);
            }
            if (childEnd >= end)
            {
                break;
            }
            pos = childEnd;
        }
    }

    /// <summary>The offset just past the <paramref name="breaks"/>-th '\n' (counting from 1), which
    /// is where that many breaks into the text a line starts.</summary>
    public static int OffsetAfterBreaks(RopeNode node, int breaks)
    {
        if (breaks == 0)
        {
            return 0;
        }
        int offset = 0;
        while (node is RopeBranch branch)
        {
            foreach (RopeNode child in branch.Children)
            {
                if (child.Breaks >= breaks)
                {
                    node = child;
                    break;
                }
                breaks -= child.Breaks;
                offset += child.Length;
            }
        }
        ReadOnlySpan<char> text = ((RopeLeaf)node).Text;
        int at = -1;
        for (int i = 0; i < breaks; i++)
        {
            at += 1 + text[(at + 1)..].IndexOf('\n');
        }
        return offset + at + 1;
    }

    /// <summary>How many '\n' come before <paramref name="offset"/>: the line it is on.</summary>
    public static int BreaksBefore(RopeNode node, int offset)
    {
        int breaks = 0;
        while (node is RopeBranch branch)
        {
            RopeNode? next = null;
            foreach (RopeNode child in branch.Children)
            {
                if (offset < child.Length)
                {
                    next = child;
                    break;
                }
                offset -= child.Length;
                breaks += child.Breaks;
            }
            if (next is null)
            {
                return breaks;
            }
            node = next;
        }
        return breaks + ((RopeLeaf)node).Text.AsSpan(0, offset).Count('\n');
    }

    public static void AppendTo(RopeNode node, StringBuilder builder)
    {
        if (node is RopeLeaf leaf)
        {
            builder.Append(leaf.Text);
            return;
        }
        foreach (RopeNode child in ((RopeBranch)node).Children)
        {
            AppendTo(child, builder);
        }
    }

    /// <summary>Replaces [start, end) within <paramref name="node"/> and adds what stands in its place
    /// to <paramref name="output"/>: zero or more nodes, all of the node's own height.</summary>
    private static void Replace(RopeNode node, int start, int end, ReadOnlySpan<char> text, List<RopeNode> output)
    {
        if (node is RopeLeaf leaf)
        {
            ReadOnlySpan<char> old = leaf.Text;
            int total = start + text.Length + (old.Length - end);
            if (total == 0)
            {
                return;
            }
            if (total <= MaxLeaf)
            {
                output.Add(new RopeLeaf(string.Concat(old[..start], text, old[end..])));
                return;
            }
            AddLeaves(old[..start], text, old[end..], output);
            return;
        }

        RopeBranch branch = (RopeBranch)node;
        List<RopeNode> children = new(branch.Children.Length + 2);
        int pos = 0;
        bool placed = false;
        foreach (RopeNode child in branch.Children)
        {
            int childEnd = pos + child.Length;
            if (!placed && start <= childEnd)
            {
                placed = true;
                Replace(child, start - pos, Math.Min(end, childEnd) - pos, text, children);
            }
            else if (start <= pos && childEnd <= end && placed)
            {
                // Wholly inside the removed range: dropped without a visit.
            }
            else if (start < childEnd && end > pos)
            {
                Replace(child, Math.Max(start, pos) - pos, Math.Min(end, childEnd) - pos, ReadOnlySpan<char>.Empty, children);
            }
            else
            {
                children.Add(child);
            }
            pos = childEnd;
        }
        MergeSmall(children);
        Group(children, output);
    }

    /// <summary>Joins neighbours that have shrunk, so deletions do not leave the tree full of
    /// slivers. Only a small node merges, and only when the result still fits.</summary>
    private static void MergeSmall(List<RopeNode> nodes)
    {
        for (int i = 0; i + 1 < nodes.Count;)
        {
            RopeNode a = nodes[i];
            RopeNode b = nodes[i + 1];
            if (a is RopeLeaf left && b is RopeLeaf right)
            {
                bool small = left.Length < MaxLeaf / 4 || right.Length < MaxLeaf / 4;
                if (small && left.Length + right.Length <= MaxLeaf)
                {
                    nodes[i] = new RopeLeaf(left.Text + right.Text);
                    nodes.RemoveAt(i + 1);
                    continue;
                }
            }
            else if (a is RopeBranch x && b is RopeBranch y)
            {
                bool small = x.Children.Length < MaxChildren / 4 || y.Children.Length < MaxChildren / 4;
                if (small && x.Children.Length + y.Children.Length <= MaxChildren)
                {
                    nodes[i] = new RopeBranch([.. x.Children, .. y.Children]);
                    nodes.RemoveAt(i + 1);
                    continue;
                }
            }
            i++;
        }
    }

    /// <summary>Puts same-height nodes under as few branches as fit, sized evenly.</summary>
    private static void Group(List<RopeNode> nodes, List<RopeNode> output)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        int groups = (nodes.Count + MaxChildren - 1) / MaxChildren;
        int index = 0;
        for (int g = 0; g < groups; g++)
        {
            int take = (nodes.Count - index) / (groups - g);
            output.Add(new RopeBranch(nodes.GetRange(index, take).ToArray()));
            index += take;
        }
    }

    /// <summary>One root over same-height nodes: grouped level by level, and a root left with a
    /// single child replaced by that child.</summary>
    private static RopeNode Join(List<RopeNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return RopeLeaf.Empty;
        }
        while (nodes.Count > 1)
        {
            List<RopeNode> parents = [];
            Group(nodes, parents);
            nodes = parents;
        }
        RopeNode root = nodes[0];
        while (root is RopeBranch { Children.Length: 1 } single)
        {
            root = single.Children[0];
        }
        return root;
    }

    /// <summary>Cuts text into leaves of even size, never between the halves of a surrogate pair.</summary>
    private static void AddLeaves(ReadOnlySpan<char> text, List<RopeNode> output) => AddLeaves(text, [], [], output);

    /// <summary>Cuts three pieces of text, one after another, into leaves of even size as if they were one, never between
    /// the halves of a surrogate pair: each character is copied once, into its leaf, as a console appends whole batches to
    /// its last.</summary>
    private static void AddLeaves(ReadOnlySpan<char> a, ReadOnlySpan<char> b, ReadOnlySpan<char> c, List<RopeNode> output)
    {
        int total = a.Length + b.Length + c.Length;
        int count = (total + MaxLeaf - 1) / MaxLeaf;
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            int end = i == count - 1 ? total : index + (total - index) / (count - i);
            if (end < total && end > index && char.IsHighSurrogate(At(end - 1, a, b, c)))
            {
                end++;
            }
            output.Add(new RopeLeaf(string.Concat(Within(a, 0, index, end), Within(b, a.Length, index, end), Within(c, a.Length + b.Length, index, end))));
            index = end;
        }
    }

    /// <summary>The character at a place of three pieces one after another.</summary>
    private static char At(int at, ReadOnlySpan<char> a, ReadOnlySpan<char> b, ReadOnlySpan<char> c) =>
        at < a.Length ? a[at] : at < a.Length + b.Length ? b[at - a.Length] : c[at - a.Length - b.Length];

    /// <summary>What of a piece, starting at <paramref name="at"/> among the pieces, lies from one place to another of them.</summary>
    private static ReadOnlySpan<char> Within(ReadOnlySpan<char> piece, int at, int from, int to) =>
        piece[Math.Clamp(from - at, 0, piece.Length)..Math.Clamp(to - at, 0, piece.Length)];
}

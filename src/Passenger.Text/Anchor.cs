namespace Passenger.Text;

/// <summary>Where an anchor goes when text is inserted at its very place: <see cref="Before"/> stays before the text,
/// <see cref="After"/> goes after it.</summary>
public enum AnchorGravity
{
    Before,
    After,
}

/// <summary>A place in a document's text, carried through each change before anyone hears of it
/// (<see cref="TextDocument.AnchorAt"/>). At the start of a replaced stretch it stays; at its end it goes after what replaced
/// it; inside it, to the start, or after what replaced it with <see cref="AnchorGravity.After"/>. Text deleted around it
/// never takes it away: it collapses where the deletion was. Read and moved on the thread that changes its document.</summary>
public sealed class Anchor
{
    internal Anchor? left;
    internal Anchor? right;
    internal Anchor? parent;

    /// <summary>Its offset, less the shifts the anchors above it still hold for those below them.</summary>
    internal int key;

    /// <summary>What every anchor below it is still to be shifted by.</summary>
    internal int shift;

    internal uint priority;

    /// <summary>Pressed to the start of the edit just applied from inside it, so text the next edit inserts there goes
    /// before it, whatever its gravity.</summary>
    internal bool pressed;

    internal AnchoredRange? range;
    internal AnchorTree? tree;

    /// <summary>Its offset as it was taken out of its document.</summary>
    private int removedAt;

    internal Anchor(AnchorGravity gravity) => Gravity = gravity;

    public AnchorGravity Gravity { get; internal set; }

    /// <summary>Where it is in its document's text now; once taken out, where it was then.</summary>
    public int Offset
    {
        get
        {
            if (tree is null)
            {
                return removedAt;
            }
            int offset = key;
            for (Anchor? above = parent; above is not null; above = above.parent)
            {
                offset += above.shift;
            }
            return offset;
        }
    }

    /// <summary>Whether its document still carries it.</summary>
    public bool IsLive => tree is not null;

    /// <summary>Puts it elsewhere in its document's text, as if made there; nothing once it is taken out.</summary>
    public void MoveTo(int offset)
    {
        if (tree is { } carried)
        {
            carried.Remove(this);
            carried.Add(this, offset);
        }
    }

    /// <summary>Takes it out of its document, which no longer carries it; its offset stays.</summary>
    public void Remove()
    {
        if (tree is { } from)
        {
            removedAt = Offset;
            from.Remove(this);
        }
    }
}

/// <summary>A stretch of a document's text between two anchors (<see cref="TextDocument.RangeAt"/>), its end never before its
/// start. One given a gravity for when it is empty has both ends take it once the text between them is gone (as a squiggle
/// that typing at its place no longer widens), and keeps it.</summary>
public sealed class AnchoredRange
{
    internal AnchoredRange(Anchor start, Anchor end, AnchorGravity? empty)
    {
        StartAnchor = start;
        EndAnchor = end;
        Empty = empty;
        start.range = this;
        end.range = this;
        Collapse();
    }

    public Anchor StartAnchor { get; }

    public Anchor EndAnchor { get; }

    public int Start => StartAnchor.Offset;

    public int End => Math.Max(StartAnchor.Offset, EndAnchor.Offset);

    public TextRange Range
    {
        get
        {
            int start = StartAnchor.Offset;
            return TextRange.FromBounds(start, Math.Max(start, EndAnchor.Offset));
        }
    }

    /// <summary>The gravity both ends take once it is empty, or null to keep their own.</summary>
    internal AnchorGravity? Empty { get; }

    public bool IsLive => StartAnchor.IsLive;

    /// <summary>Takes both ends out of the document, which no longer carries them; the range stays where it was.</summary>
    public void Remove()
    {
        StartAnchor.Remove();
        EndAnchor.Remove();
    }

    /// <summary>Gives both ends the empty gravity once nothing is between them.</summary>
    internal void Collapse()
    {
        if (Empty is { } gravity && EndAnchor.Offset <= StartAnchor.Offset)
        {
            StartAnchor.Gravity = gravity;
            EndAnchor.Gravity = gravity;
        }
    }
}

/// <summary>A document's anchors in a treap ordered by offset, each holding its offset less the shifts its ancestors hold
/// for their subtrees. An edit splits out the anchors at or inside what it replaced, places them, and shifts every anchor
/// past it at once through the subtree's root: a change costs the logarithm of how many anchors there are and what it
/// reaches, and allocates nothing.</summary>
internal sealed class AnchorTree
{
    private readonly List<Anchor> reached = [];
    private readonly List<AnchoredRange> emptied = [];
    private List<Anchor> pressed = [];
    private List<Anchor> pressing = [];
    private readonly List<Anchor> above = [];
    private Anchor? root;
    private uint seed = 0x9E3779B9;

    public int Count { get; private set; }

    public void Add(Anchor anchor, int offset)
    {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        anchor.priority = seed;
        anchor.tree = this;
        Reset(anchor, offset);
        Split(root, offset, out Anchor? before, out Anchor? rest);
        root = Rooted(Merge(Merge(before, anchor), rest));
        Count++;
    }

    public void Remove(Anchor anchor)
    {
        // Every shift above it pushed down first, so what takes its place holds its own offsets.
        above.Clear();
        for (Anchor? up = anchor.parent; up is not null; up = up.parent)
        {
            above.Add(up);
        }
        for (int i = above.Count - 1; i >= 0; i--)
        {
            Push(above[i]);
        }
        above.Clear();
        Push(anchor);
        Anchor? joined = Merge(anchor.left, anchor.right);
        Anchor? parent = anchor.parent;
        if (joined is not null)
        {
            joined.parent = parent;
        }
        if (parent is null)
        {
            root = joined;
        }
        else if (parent.left == anchor)
        {
            parent.left = joined;
        }
        else
        {
            parent.right = joined;
        }
        anchor.left = null;
        anchor.right = null;
        anchor.parent = null;
        anchor.tree = null;
        Count--;
    }

    /// <summary>Carries every anchor through a change's edits, sorted and apart in the old text's offsets: the last first,
    /// so each is in the offsets of the text the one after it left.</summary>
    public void Apply(IReadOnlyList<TextEdit> edits)
    {
        if (root is null)
        {
            return;
        }
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            Apply(edits[i]);
        }
        Press();
        foreach (AnchoredRange range in emptied)
        {
            range.Collapse();
        }
        emptied.Clear();
    }

    private void Apply(TextEdit edit)
    {
        int start = edit.Start;
        int end = edit.Start + edit.Length;
        int inserted = edit.Text.Length;
        Split(root, start, out Anchor? before, out Anchor? rest);
        Split(Rooted(rest), end + 1, out Anchor? at, out Anchor? after);
        if (Rooted(after) is { } past)
        {
            past.key += inserted - edit.Length;
            past.shift += inserted - edit.Length;
        }
        Anchor? placed = null;
        if (Rooted(at) is { } inside)
        {
            reached.Clear();
            Collect(inside);
            Anchor? atStart = null;
            Anchor? atEnd = null;
            foreach (Anchor anchor in reached)
            {
                int offset = anchor.key;
                bool goesAfter = anchor.pressed
                    || (edit.Length == 0 || (offset > start && offset < end) ? anchor.Gravity == AnchorGravity.After : offset == end);
                Reset(anchor, goesAfter ? start + inserted : start);
                if (offset > start && anchor.key == start)
                {
                    pressing.Add(anchor);
                }
                if (goesAfter)
                {
                    atEnd = Merge(atEnd, anchor);
                }
                else
                {
                    atStart = Merge(atStart, anchor);
                }
                if (anchor.range is { Empty: not null } range)
                {
                    emptied.Add(range);
                }
            }
            reached.Clear();
            placed = Merge(Rooted(atStart), Rooted(atEnd));
        }
        root = Rooted(Merge(Merge(Rooted(before), Rooted(placed)), Rooted(after)));
        Press();
    }

    /// <summary>Those the edit just applied pressed to its start are pressed for the next, and those before are not: the
    /// edits are apart, so only an insertion at that start, next in the list, reaches them.</summary>
    private void Press()
    {
        foreach (Anchor anchor in pressed)
        {
            anchor.pressed = false;
        }
        pressed.Clear();
        foreach (Anchor anchor in pressing)
        {
            anchor.pressed = true;
        }
        (pressed, pressing) = (pressing, pressed);
    }

    /// <summary>The anchors of a subtree in order, each holding its offset.</summary>
    private void Collect(Anchor node)
    {
        Push(node);
        if (node.left is { } less)
        {
            Collect(less);
        }
        reached.Add(node);
        if (node.right is { } more)
        {
            Collect(more);
        }
    }

    private static void Reset(Anchor anchor, int offset)
    {
        anchor.left = null;
        anchor.right = null;
        anchor.parent = null;
        anchor.key = offset;
        anchor.shift = 0;
    }

    private static Anchor? Rooted(Anchor? node)
    {
        if (node is not null)
        {
            node.parent = null;
        }
        return node;
    }

    private static void Push(Anchor node)
    {
        if (node.shift == 0)
        {
            return;
        }
        if (node.left is { } less)
        {
            less.key += node.shift;
            less.shift += node.shift;
        }
        if (node.right is { } more)
        {
            more.key += node.shift;
            more.shift += node.shift;
        }
        node.shift = 0;
    }

    /// <summary>A subtree as those before <paramref name="offset"/> and those at or after it.</summary>
    private static void Split(Anchor? node, int offset, out Anchor? before, out Anchor? rest)
    {
        if (node is null)
        {
            before = null;
            rest = null;
            return;
        }
        Push(node);
        if (node.key < offset)
        {
            Split(node.right, offset, out Anchor? kept, out rest);
            node.right = kept;
            if (kept is not null)
            {
                kept.parent = node;
            }
            before = node;
        }
        else
        {
            Split(node.left, offset, out before, out Anchor? kept);
            node.left = kept;
            if (kept is not null)
            {
                kept.parent = node;
            }
            rest = node;
        }
    }

    /// <summary>Two subtrees as one, every anchor of <paramref name="before"/> at or before every one of
    /// <paramref name="rest"/>.</summary>
    private static Anchor? Merge(Anchor? before, Anchor? rest)
    {
        if (before is null)
        {
            return rest;
        }
        if (rest is null)
        {
            return before;
        }
        if (before.priority > rest.priority)
        {
            Push(before);
            Anchor joined = Merge(before.right, rest)!;
            before.right = joined;
            joined.parent = before;
            return before;
        }
        Push(rest);
        Anchor joinedLeft = Merge(before, rest.left)!;
        rest.left = joinedLeft;
        joinedLeft.parent = rest;
        return rest;
    }
}

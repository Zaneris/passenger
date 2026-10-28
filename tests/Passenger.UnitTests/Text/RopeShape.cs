using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>What every rope holds after any edit: leaves no longer than a leaf may be (but by the half of a surrogate pair the cut
/// would split) and never empty but in the empty rope,
/// branches of at most their children's count, every leaf at one depth, each node's length and count of '\n' its own, and no
/// leaf ending between the halves of a surrogate pair.</summary>
internal static class RopeShape
{
    public static void Check(RopeNode root)
    {
        if (root is RopeBranch top)
        {
            Assert.True(top.Children.Length >= 2, $"The rope's root is a branch of {top.Children.Length} child: a root of one is its child");
        }
        Check(root, root.Length == 0);
        char? last = null;
        Leaves(root, leaf =>
        {
            Assert.False(last is { } before && char.IsHighSurrogate(before) && char.IsLowSurrogate(leaf.Text[0]),
                "A leaf ends between the halves of a surrogate pair, so a reader of its chunks sees half a character");
            last = leaf.Text.Length > 0 ? leaf.Text[^1] : last;
        });
    }

    /// <summary>No deeper than two levels past a rope of full leaves and branches holding its text, so a read or an edit stays
    /// the logarithm of its length.</summary>
    public static void Balanced(RopeNode root)
    {
        int least = 0;
        for (long held = RopeNode.MaxLeaf; held < root.Length; held *= RopeNode.MaxChildren)
        {
            least++;
        }
        Assert.True(root.Height <= least + 2, $"A rope of {root.Length} characters is {root.Height} levels deep, where {least} would hold it");
    }

    public static int LeafCount(RopeNode node)
    {
        int count = 0;
        Leaves(node, _ => count++);
        return count;
    }

    private static void Check(RopeNode node, bool emptyRope)
    {
        if (node is RopeLeaf leaf)
        {
            // One past the size where a leaf cut there would split the surrogate pair it ends with.
            bool pairKept = leaf.Length == RopeNode.MaxLeaf + 1 && char.IsLowSurrogate(leaf.Text[^1]) && char.IsHighSurrogate(leaf.Text[^2]);
            Assert.True(leaf.Length <= RopeNode.MaxLeaf || pairKept, $"A leaf holds {leaf.Length} characters, past the {RopeNode.MaxLeaf} a leaf may");
            Assert.True(leaf.Length > 0 || emptyRope, "A rope holding text has an empty leaf");
            Assert.Equal(leaf.Text.AsSpan().Count('\n'), leaf.Breaks);
            Assert.Equal(0, leaf.Height);
            return;
        }
        RopeBranch branch = (RopeBranch)node;
        Assert.InRange(branch.Children.Length, 1, RopeNode.MaxChildren);
        int length = 0;
        int breaks = 0;
        foreach (RopeNode child in branch.Children)
        {
            Assert.True(child.Height == branch.Height - 1, $"A branch of height {branch.Height} has a child of height {child.Height}: its leaves are not all at one depth");
            Check(child, emptyRope);
            length += child.Length;
            breaks += child.Breaks;
        }
        Assert.Equal(length, branch.Length);
        Assert.Equal(breaks, branch.Breaks);
    }

    private static void Leaves(RopeNode node, Action<RopeLeaf> visit)
    {
        if (node is RopeLeaf leaf)
        {
            visit(leaf);
            return;
        }
        foreach (RopeNode child in ((RopeBranch)node).Children)
        {
            Leaves(child, visit);
        }
    }
}

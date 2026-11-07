using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>Where a flame graph's bars go, worked out with lists of its own rather than a stack's recursion: a call tree is as
/// deep as its trace's stacks, which a trace file may make any depth. The lists are the caller's, kept from frame to frame.</summary>
internal static class FlameBars
{
    /// <summary>How many rows a node and its callees take.</summary>
    public static int Rows(CallNode node, Stack<(CallNode Node, int Row)> left)
    {
        int deepest = 0;
        left.Clear();
        left.Push((node, 1));
        while (left.TryPop(out (CallNode Node, int Row) at))
        {
            deepest = Math.Max(deepest, at.Row);
            IReadOnlyList<CallNode> callees = at.Node.Ordered;
            for (int i = 0; i < callees.Count; i++)
            {
                left.Push((callees[i], at.Row + 1));
            }
        }
        return deepest;
    }

    /// <summary>The bars of a node and its callees from the row at <paramref name="y"/> down, into <paramref name="laid"/>:
    /// the node's first, then each callee's and its own before the next callee's, each as wide as its share of its caller's
    /// samples; none narrower than a pixel or below <paramref name="bottom"/>, nor any under it.</summary>
    public static void Lay(CallNode node, double width, double y, double bottom, double rowHeight, List<(CallNode Node, double X, double Width, double Y)> laid,
        Stack<(CallNode Node, double X, double Width, double Y)> left, List<(CallNode Node, double X, double Width, double Y)> row)
    {
        laid.Clear();
        left.Clear();
        left.Push((node, 0, width, y));
        while (left.TryPop(out (CallNode Node, double X, double Width, double Y) at))
        {
            if (at.Width < 1 || at.Y > bottom)
            {
                continue;
            }
            laid.Add(at);
            row.Clear();
            double x = at.X;
            IReadOnlyList<CallNode> callees = at.Node.Ordered;
            for (int i = 0; i < callees.Count; i++)
            {
                double share = at.Width * callees[i].Total / at.Node.Total;
                row.Add((callees[i], x, share, at.Y + rowHeight));
                x += share;
            }
            // The last pushed first, so the first comes out next.
            for (int i = row.Count - 1; i >= 0; i--)
            {
                left.Push(row[i]);
            }
        }
    }
}

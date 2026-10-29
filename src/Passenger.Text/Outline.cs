using System.Runtime.InteropServices;

namespace Passenger.Text;

/// <summary>A tree as rows in reading order, each after its parent and before its children, with the lines each spans:
/// a file's outline as a list shows it. Made in one pass however deep the tree, from any thread. The innermost row holding
/// a line is found by halving, then up through its parents. Made again over an earlier outline, it keeps each row the
/// earlier one had in its place where the two are equal, and is the earlier one whole where nothing changed.</summary>
public sealed class Outline<T>
    where T : class
{
    public static readonly Outline<T> Empty = new([], [], ordered: true);

    private readonly T[] rows;
    private readonly Place[] places;

    /// <summary>Whether no row starts on a line before the row above it, as a server lists a file's symbols: only then do
    /// the rows starting by a line come first, for halving to find.</summary>
    private readonly bool ordered;

    private Outline(T[] rows, Place[] places, bool ordered)
    {
        this.rows = rows;
        this.places = places;
        this.ordered = ordered;
    }

    public IReadOnlyList<T> Rows => rows;

    /// <summary>The rows of a tree, <paramref name="row"/> making each node's from it and its depth (0 at the top) with the
    /// first and last lines it spans. A row equal to the one <paramref name="before"/> has in its place is that one.</summary>
    public static Outline<T> Of<TNode>(IReadOnlyList<TNode> roots, Func<TNode, IReadOnlyList<TNode>?> children,
        Func<TNode, int, (T Row, int First, int Last)> row, Outline<T>? before = null, CancellationToken cancel = default)
    {
        List<T> made = [];
        List<Place> placed = [];
        bool ordered = true;
        int kept = 0;
        Stack<(IReadOnlyList<TNode> Nodes, int Next, int Parent)> levels = new();
        levels.Push((roots, 0, -1));
        while (levels.TryPop(out (IReadOnlyList<TNode> Nodes, int Next, int Parent) level))
        {
            if (level.Next == level.Nodes.Count)
            {
                continue;
            }
            levels.Push(level with { Next = level.Next + 1 });
            TNode node = level.Nodes[level.Next];
            (T one, int first, int last) = row(node, levels.Count - 1);
            int index = made.Count;
            if ((index & 4095) == 4095)
            {
                cancel.ThrowIfCancellationRequested();
            }
            if (before is not null && index < before.rows.Length && EqualityComparer<T>.Default.Equals(before.rows[index], one))
            {
                one = before.rows[index];
                kept++;
            }
            ordered &= index == 0 || placed[index - 1].First <= first;
            made.Add(one);
            placed.Add(new Place(level.Parent, first, last));
            if (children(node) is { Count: > 0 } under)
            {
                levels.Push((under, 0, index));
            }
        }
        if (before is not null && kept == before.rows.Length && kept == made.Count && CollectionsMarshal.AsSpan(placed).SequenceEqual(before.places))
        {
            return before;
        }
        return new Outline<T>([.. made], [.. placed], ordered);
    }

    /// <summary>The index of the innermost row whose lines hold a line, the last in reading order of those that do; -1 for
    /// none.</summary>
    public int InnermostAt(int line)
    {
        if (!ordered)
        {
            for (int i = rows.Length - 1; i >= 0; i--)
            {
                if (places[i].First <= line && line <= places[i].Last)
                {
                    return i;
                }
            }
            return -1;
        }
        int low = 0;
        int high = places.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (places[middle].First <= line)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        // Where rows nest, the innermost holding the line is the last to start by it or one of that row's parents.
        for (int i = low - 1; i >= 0; i = places[i].Parent)
        {
            if (line <= places[i].Last)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A row's parent's index (-1 at the top) and the lines it spans.</summary>
    private readonly record struct Place(int Parent, int First, int Last);
}

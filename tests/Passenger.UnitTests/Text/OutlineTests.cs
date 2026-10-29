using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's outline as the Structure window and File Structure list it: rows in reading order with their
/// depths, made in one pass however wide or deep the tree, the caret's member found as the last row spanning its line,
/// and an outline made again keeping what did not change.</summary>
public class OutlineTests
{
    [Fact]
    public void Rows_come_in_reading_order_each_after_its_parent_and_before_its_children_at_its_depth()
    {
        Node tree = new("Program", 0, 20,
        [
            new Node("Main", 2, 8, [new Node("local", 4, 6, [])]),
            new Node("Helper", 10, 18, []),
        ]);
        Outline<Row> outline = Make([tree, new Node("Other", 22, 30, [])]);
        Assert.Equal(["0 Program", "1 Main", "2 local", "1 Helper", "0 Other"], outline.Rows.Select(r => $"{r.Depth} {r.Name}"));
    }

    /// <summary>Counted by what a symbol's row costs to make in bytes, the same on every run, rather than by a time: a
    /// row copied into each list of rows above it, or a tree walked by recursion, costs more per symbol as the outline
    /// grows (a JSON file of three megabytes has a hundred thousand).</summary>
    [Fact]
    public void An_outline_of_a_hundred_thousand_symbols_costs_each_what_one_of_ten_thousand_does_wide_or_deep()
    {
        Measured(Wide(1000));
        double wideFew = Measured(Wide(10_000)) / 10_000.0;
        double wideMany = Measured(Wide(100_000)) / 100_000.0;
        double deepFew = Measured(Deep(10_000)) / 10_000.0;
        double deepMany = Measured(Deep(100_000)) / 100_000.0;
        Assert.True(wideMany <= wideFew * 1.25 && deepMany <= deepFew * 1.25,
            $"A symbol's row cost {wideMany:0} bytes in an outline of 100,000 symbols four to a parent against {wideFew:0} in one of 10,000, "
            + $"and {deepMany:0} against {deepFew:0} in a chain of each nested in the last: a large file's outline costs more than its size");
        Outline<Node> deep = Outline<Node>.Of(Deep(100_000), node => node.Children, (node, depth) => (node, node.First, node.Last),
            cancel: TestContext.Current.CancellationToken);
        Assert.Equal(100_000, deep.Rows.Count);
        Assert.Equal(99_999, deep.InnermostAt(99_999));
    }

    /// <summary>Halving to the last row starting by the line and going up through its parents finds what reading every row
    /// from the end does, over seeded random outlines whose rows nest, some starting on their parent's line or on one
    /// another's, and over one whose rows a server listed out of order.</summary>
    [Fact]
    public void The_innermost_row_at_a_line_is_the_last_in_reading_order_whose_lines_hold_it()
    {
        Random random = new(716);
        for (int round = 0; round < 200; round++)
        {
            int line = 0;
            IReadOnlyList<Node> tree = Nested(random, ref line, 0);
            Outline<Row> outline = Make(tree);
            for (int at = -1; at <= line + 1; at++)
            {
                Assert.Equal(LastSpanning(outline.Rows, at), outline.InnermostAt(at));
            }
        }
        Outline<Row> unordered = Make([new Node("Later", 10, 20, [new Node("Inner", 12, 14, [])]), new Node("Earlier", 0, 5, [])]);
        Assert.Equal([2, -1, 0, 1, 0, -1], new[] { 3, 7, 11, 13, 20, 21 }.Select(unordered.InnermostAt));
    }

    [Fact]
    public void An_outline_made_again_keeps_each_unchanged_row_and_is_the_earlier_one_where_nothing_changed()
    {
        Node[] tree = [new Node("Program", 0, 20, [new Node("Main", 2, 8, []), new Node("Helper", 10, 18, [])])];
        Outline<Row> first = Make(tree);
        Assert.Same(first, Make(tree, first));
        Outline<Row> renamed = Make([new Node("Program", 0, 20, [new Node("Main", 2, 8, []), new Node("Helped", 10, 18, [])])], first);
        Assert.NotSame(first, renamed);
        Assert.Same(first.Rows[0], renamed.Rows[0]);
        Assert.Same(first.Rows[1], renamed.Rows[1]);
        Assert.Equal("Helped", renamed.Rows[2].Name);
        Outline<Row> moved = Make([new Node("Program", 0, 20, [new Node("Main", 2, 8, []), new Node("Helper", 10, 19, [])])], first);
        Assert.NotSame(first, moved);
        Assert.Equal(19, moved.Rows[2].Last);
    }

    private sealed record Node(string Name, int First, int Last, IReadOnlyList<Node> Children);

    private sealed record Row(string Name, int Depth, int First, int Last);

    private static Outline<Row> Make(IReadOnlyList<Node> tree, Outline<Row>? before = null) =>
        Outline<Row>.Of(tree, node => node.Children, (node, depth) => (new Row(node.Name, depth, node.First, node.Last), node.First, node.Last), before,
            TestContext.Current.CancellationToken);

    /// <summary>The bytes this thread allocates making an outline whose rows are the nodes themselves.</summary>
    private static long Measured(IReadOnlyList<Node> tree)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Outline<Node> outline = Outline<Node>.Of(tree, node => node.Children, (node, depth) => (node, node.First, node.Last), cancel: TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(outline);
        return allocated;
    }

    /// <summary>A JSON array's shape: objects of four keys each, a fifth of the symbols at the top.</summary>
    private static Node[] Wide(int symbols)
    {
        Node[] top = new Node[symbols / 5];
        for (int i = 0; i < top.Length; i++)
        {
            int first = i * 6;
            top[i] = new Node("item", first, first + 5, [.. Enumerable.Range(1, 4).Select(k => new Node("key", first + k, first + k, []))]);
        }
        return top;
    }

    /// <summary>A chain of symbols, each nested in the one before, each a line below it.</summary>
    private static Node[] Deep(int symbols)
    {
        Node node = new("leaf", symbols - 1, symbols * 2, []);
        for (int i = symbols - 2; i >= 0; i--)
        {
            node = new Node("level", i, (symbols * 2) + (symbols - i), [node]);
        }
        return [node];
    }

    /// <summary>Up to four siblings from a line on, each a few lines long or one, holding up to two levels more.</summary>
    private static Node[] Nested(Random random, ref int line, int depth)
    {
        List<Node> level = [];
        int count = random.Next(depth == 0 ? 1 : 0, 4);
        for (int i = 0; i < count; i++)
        {
            int first = line + random.Next(0, 2);
            line = first + random.Next(0, 2);
            Node[] children = depth < 2 ? Nested(random, ref line, depth + 1) : [];
            line += random.Next(0, 2);
            level.Add(new Node($"n{depth}.{i}", first, line, children));
        }
        return [.. level];
    }

    private static int LastSpanning(IReadOnlyList<Row> rows, int line)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].First <= line && line <= rows[i].Last)
            {
                return i;
            }
        }
        return -1;
    }
}

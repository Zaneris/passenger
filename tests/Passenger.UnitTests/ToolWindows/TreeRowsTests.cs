using System.Diagnostics;
using Passenger.App;

namespace Passenger.UnitTests.ToolWindows;

/// <summary>The rows a tree shows (Find, Hierarchy, Variables, the profiler's): each open row followed by its own, those
/// of a closed row left out however open they were, listed with no frame of the thread's per level open, so a tree opened
/// deep costs its rows alone.</summary>
public sealed class TreeRowsTests
{
    [Fact]
    public void Each_open_row_is_followed_by_its_own_rows_and_a_closed_one_by_none()
    {
        Row inner = new("inner", 2, [], open: true);
        Row shut = new("shut", 1, [new Row("hidden", 2, [], open: true)], open: false);
        Row first = new("first", 0, [new Row("a", 1, [inner], open: true), shut], open: true);
        Row second = new("second", 0, [new Row("b", 1, [], open: false)], open: true);

        Assert.Equal(["first", "a", "inner", "shut", "second", "b"], TreeRows<Row>.Shown([first, second], r => r.Below).Select(r => r.Name));
    }

    [Fact]
    public void A_row_closed_as_the_rows_are_listed_shows_none_of_its_own()
    {
        Row top = new("top", 0, [new Row("under", 1, [], open: false)], open: true);
        List<string> listed = [];

        foreach (Row row in TreeRows<Row>.Shown([top], r => r.Below))
        {
            listed.Add(row.Name);
            row.IsExpanded = false;
        }

        Assert.Equal(["top"], listed);
    }

    [Fact]
    public void A_tree_open_thousands_of_levels_deep_is_listed_with_no_frame_per_level()
    {
        const int Levels = 2_000;
        Row top = Chain(Levels);
        int? shallow = null;
        int deepest = 0;

        int listed = TreeRows<Row>.Shown([top], row =>
        {
            int frames = new StackTrace(false).FrameCount;
            shallow ??= frames;
            deepest = Math.Max(deepest, frames);
            return row.Below;
        }).Count();

        Assert.Equal(Levels + 1, listed);
        // A few frames either way are the runtime's own, as it compiles the walk again on its way.
        Assert.True(deepest - shallow <= 4, $"Listing a tree {Levels} levels open took {deepest - shallow} more frames at its deepest level than at its first: "
            + "a tree opened deep enough would end the IDE");
    }

    [Fact]
    public void A_tree_open_a_hundred_thousand_levels_deep_is_listed_on_a_small_stack()
    {
        const int Levels = 100_000;
        Row top = Chain(Levels);
        int listed = 0;

        SmallStack.Run(() => listed = TreeRows<Row>.Shown([top], r => r.Below).Count());

        Assert.Equal(Levels + 1, listed);
    }

    /// <summary>A row with a chain of rows under it, each open, one under the next.</summary>
    private static Row Chain(int levels)
    {
        Row bottom = new("0", levels, [], open: true);
        for (int depth = levels - 1; depth >= 0; depth--)
        {
            bottom = new Row(depth.ToString(System.Globalization.CultureInfo.InvariantCulture), depth, [bottom], open: true);
        }
        return bottom;
    }

    private sealed class Row(string name, int depth, List<Row> below, bool open) : ITreeRow
    {
        public string Name { get; } = name;

        public int Depth { get; } = depth;

        public List<Row> Below { get; } = below;

        public bool Expands => Below.Count > 0;

        public bool IsExpanded { get; set; } = open;
    }
}

using Passenger.App.Profiling;
using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>The call tree and hot spots tell methods apart by what they are, not by how they are shown: overloads whose
/// parameters' types differ only in their namespaces, and one name in two modules, are rows of their own, each opening
/// its own code; one method caught again and again is one row.</summary>
public sealed class CallTreeTests
{
    private static readonly MethodCode TextA = new("/app/App.dll", 0x06000002);
    private static readonly MethodCode TextB = new("/app/App.dll", 0x06000003);
    private static readonly MethodCode Caller = new("/app/App.dll", 0x06000001);

    [Fact]
    public void Overloads_that_read_alike_under_one_caller_are_two_rows_each_with_its_own_samples_and_code()
    {
        Snapshot snapshot = Snapshot.Of(
        [
            ([("Program.Parse(class A.Text)", "App", TextA), ("Program.Caller()", "App", Caller)], 3),
            ([("Program.Parse(class B.Text)", "App", TextB), ("Program.Caller()", "App", Caller)], 5),
            ([("Program.Parse(class A.Text)", "App", TextA), ("Program.Caller()", "App", Caller)], 2),
        ]);

        CallNode caller = Assert.Single(Assert.Single(snapshot.Threads).Ordered);
        Assert.Equal(("Program.Caller()", 10), (caller.Name, caller.Total));
        Assert.Equal([("Program.Parse(Text)", 5, TextA), ("Program.Parse(Text)", 5, TextB)],
            caller.Ordered.Select(c => (c.Name, c.Total, c.Code!.Value)));
        Assert.Equal([("Program.Parse(Text)", 5, TextA), ("Program.Parse(Text)", 5, TextB)],
            snapshot.HotSpots.Where(h => h.Name.StartsWith("Program.Parse", StringComparison.Ordinal)).Select(h => (h.Name, h.Self, h.Code!.Value)).OrderBy(h => h.Item3.Token));
    }

    [Fact]
    public void One_name_in_two_modules_is_two_rows()
    {
        MethodCode inLibrary = new("/app/Library.dll", 0x06000002);
        Snapshot snapshot = Snapshot.Of(
        [
            ([("Shared.Parse(class System.String)", "App", TextA), ("Program.Caller()", "App", Caller)], 4),
            ([("Shared.Parse(class System.String)", "Library", inLibrary), ("Program.Caller()", "App", Caller)], 6),
        ]);

        CallNode caller = Assert.Single(Assert.Single(snapshot.Threads).Ordered);
        Assert.Equal([("Shared.Parse(String)", "Library", 6), ("Shared.Parse(String)", "App", 4)], caller.Ordered.Select(c => (c.Name, c.Module, c.Total)));
    }

    /// <summary>A trace file's stacks may be any depth: a flame graph of one 100,000 frames deep counts its rows and lays its
    /// bars out on a small stack, one a row, each as wide as its caller's.</summary>
    [Fact]
    public void A_flame_graph_of_a_stack_deeper_than_a_stack_holds_counts_its_rows_and_lays_out_a_bar_for_each()
    {
        const int Deep = 100_000;
        Snapshot snapshot = Snapshot.Of([([.. Enumerable.Range(0, Deep).Select(i => ($"Program.Recurse{i % 3}()", "App", (MethodCode?)null))], 7)]);
        CallNode thread = Assert.Single(snapshot.Threads);
        List<(CallNode Node, double X, double Width, double Y)> laid = [];
        int rows = 0;
        SmallStack.Run(() =>
        {
            rows = FlameBars.Rows(thread, new Stack<(CallNode, int)>());
            FlameBars.Lay(thread, 800, 0, (Deep + 1) * 20, 20, laid, new Stack<(CallNode, double, double, double)>(), []);
        });
        Assert.Equal(Deep + 1, rows);
        Assert.Equal(Deep + 1, laid.Count);
        Assert.All(laid, bar => Assert.Equal((0d, 800d), (bar.X, bar.Width)));
        Assert.Equal(Deep * 20d, laid[^1].Y);
    }
}

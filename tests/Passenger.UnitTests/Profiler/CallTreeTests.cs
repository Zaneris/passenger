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
}

using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>The rows a stop's values list in PassengerCases' rows case: what a value opens to and what a frame lists as its
/// variables.</summary>
[Collection(DebugSession.Collection)]
public class VariablesRowsTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>An indexer is no row: a class with one, a list's and a dictionary's raw views and a tuple list none, and no
    /// getter is called in the program to show one; the members beside it are listed as before.</summary>
    [Fact]
    public async Task An_indexer_is_no_row_of_its_object_and_nothing_is_evaluated_for_one()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"rows {shelf[0]}");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        List<DapVariable> locals = await host.LocalsAsync(stop);

        List<(string Owner, DapVariable Row)> rows = [];
        foreach (string name in (string[])["shelf", "names", "ages", "pair"])
        {
            List<DapVariable> members = await host.VariablesAsync(Assert.Single(locals, v => v.Name == name).VariablesReference);
            if (members.FirstOrDefault(m => m.Name == "Raw View") is { } raw)
            {
                members.AddRange(await host.VariablesAsync(raw.VariablesReference));
            }
            rows.AddRange(members.Select(member => (name, member)));
        }
        string[] indexers = [.. rows.Where(r => r.Row.Name == "Item" || r.Row.Name.EndsWith(".Item", StringComparison.Ordinal)).Select(r => $"{r.Owner}: {r.Row.Name} = {r.Row.Value}")];
        Assert.True(indexers.Length == 0, $"An indexer is listed as a row, its getter called with no index: {string.Join("; ", indexers)}");
        Assert.True(rows.Any(r => r is ("shelf", { Name: "Count", Value: "2" })) && rows.Any(r => r is ("names", { Name: "Count", Value: "2" })),
            $"The members beside the indexers are not listed as before: {string.Join("; ", rows.Select(r => $"{r.Owner}: {r.Row.Name} = {r.Row.Value}"))}");
        Assert.DoesNotContain("TargetParameterCountException", host.EngineLog(), StringComparison.Ordinal);
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("0", (await host.EvaluateAsync(frame, "Shelf.Reads")).Result);
    }

    /// <summary>A local only a local function captures is listed under its own name, with its live value, in the frame that
    /// declares it and in the local function's, with no row of the closure holding it nor that closure in the local
    /// function's frame name; an iterator's frame lists its own variables and none of its enumerator's properties.</summary>
    [Fact]
    public async Task A_local_a_local_function_captures_is_listed_in_both_frames_and_an_iterators_frame_lists_no_current()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"called {Call(1)}\");", "return called;", "yield return taken;");

        StoppedEvent declaring = await host.WaitForStoppedEvent();
        List<DapVariable> outer = await host.LocalsAsync(declaring);
        Assert.True(outer.SingleOrDefault(v => v.Name == "captive")?.Value == "5" && !outer.Any(Generated),
            $"Run's frame lists {Listed(outer)}, where captive = 5 is to be listed by its name and no closure");

        StoppedEvent called = await host.ContinueAsync().WaitForStoppedEvent();
        DapStackFrame local = await host.TopFrameAsync(called.ThreadId!.Value);
        List<DapVariable> inner = await host.LocalsAsync(called);
        Assert.True(inner.SingleOrDefault(v => v.Name == "captive")?.Value == "5" && inner.SingleOrDefault(v => v.Name == "called")?.Value == "6" && !inner.Any(Generated),
            $"Call's frame lists {Listed(inner)}, where x = 1, called = 6 and captive = 5 are to be listed by their names and no closure");
        Assert.DoesNotContain("DisplayClass", local.Name, StringComparison.Ordinal);

        StoppedEvent iterating = await host.ContinueAsync().WaitForStoppedEvent();
        List<DapVariable> iterator = await host.LocalsAsync(iterating);
        Assert.True(iterator.SingleOrDefault(v => v.Name == "count")?.Value == "2" && iterator.SingleOrDefault(v => v.Name == "taken")?.Value == "0"
            && !iterator.Any(v => v.Name.Contains("Current", StringComparison.Ordinal)),
            $"The iterator's frame lists {Listed(iterator)}, where count = 2 and taken = 0 are to be listed and no Current");

        static bool Generated(DapVariable row) => row.Name.Contains('<') || row.Name.Contains('$') || row.Name.StartsWith("arg_", StringComparison.Ordinal)
            || row.Type?.Contains("DisplayClass", StringComparison.Ordinal) == true;

        static string Listed(List<DapVariable> rows) => string.Join(", ", rows.Select(v => $"{v.Name} = {v.Value}"));
    }

    /// <summary>PassengerCases launched on its rows case, stopping at the lines with this code on them.</summary>
    private static async Task Launch(DebugSession host, params string[] codes)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["rows"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([.. codes.Select(code => Debuggees.Line("PassengerCases", code))], Debuggees.Source("PassengerCases", "Program.cs"))
            .ConfigurationDoneAsync();
    }
}

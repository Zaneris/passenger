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

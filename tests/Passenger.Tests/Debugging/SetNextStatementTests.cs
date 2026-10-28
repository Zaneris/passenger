using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Set Next Statement through the adapter's gotoTargets and goto: a paused thread goes on from another line of
/// its method, skipping what lies between or doing it again.</summary>
[Collection(DebugSession.Collection)]
public class SetNextStatementTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task The_next_statement_skips_ahead_and_goes_back_within_the_method()
    {
        int first = Debuggees.Line("PassengerCases", "jumps += 1;");
        int second = Debuggees.Line("PassengerCases", "jumps += 10;");
        int third = Debuggees.Line("PassengerCases", "jumps += 100;");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", first, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        Assert.Equal(first, stop.ReadStopInfo().line);

        // Past the first two additions, so only the hundred is added.
        await GoTo(host, thread, third);
        await host.StepOverAsync(thread);
        await host.WaitForStoppedEvent();
        Assert.Equal("100", await Evaluate(host, thread, "jumps"));

        // Back to the second, which then adds the ten and the hundred again.
        await GoTo(host, thread, second);
        await host.StepOverAsync(thread);
        await host.WaitForStoppedEvent();
        await host.StepOverAsync(thread);
        await host.WaitForStoppedEvent();
        Assert.Equal("210", await Evaluate(host, thread, "jumps"));

        GotoTargetsResponse elsewhere = await host.RequestAsync<GotoTargetsResponse>(DapCommands.GotoTargets,
            new GotoTargetsArguments(new DapSource { Path = Debuggees.Source("PassengerCases", "Program.cs") }, Debuggees.Line("PassengerCases", "Console.WriteLine(\"interpreter\");"), null));
        Assert.Empty(elsewhere.Targets);
    }

    private static async Task GoTo(DebugSession host, int thread, int line)
    {
        GotoTargetsResponse targets = await host.RequestAsync<GotoTargetsResponse>(DapCommands.GotoTargets,
            new GotoTargetsArguments(new DapSource { Path = Debuggees.Source("PassengerCases", "Program.cs") }, line, null));
        GotoTarget target = Assert.Single(targets.Targets);
        Assert.Equal(line, target.Line);
        await host.RequestAsync(DapCommands.Goto, new GotoArguments(thread, target.Id));
        StoppedEvent moved = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Goto, moved.Reason);
        Assert.Equal(line, moved.ReadStopInfo().line);
    }

    private static async Task<string> Evaluate(DebugSession host, int thread, string expression)
    {
        EvaluateResponse response = await host.EvaluateAsync((await host.TopFrameAsync(thread)).Id, expression);
        return response.Result;
    }
}

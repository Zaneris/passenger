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

    /// <summary>A line holding a lambda after its statement begins is a target in the method paused in, at the statement:
    /// the thread goes back to it and on to the next line from it, never into the lambda.</summary>
    [Fact]
    public async Task The_next_statement_goes_to_a_line_holding_a_lambda_at_its_statement_in_the_method()
    {
        const string Stepping = "SteppingConsoleApp";
        int counted = Debuggees.Line(Stepping, "int counted = numbers.Count(n => n > 2);");
        int unread = Debuggees.Line(Stepping, "IEnumerable<int> unread = numbers.Where(n => n > 3);");
        int said = Debuggees.Line(Stepping, "Console.WriteLine($\"counted {counted}\");");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, said, []);
        int thread = (await host.WaitForStoppedEvent()).ThreadId!.Value;

        foreach ((int line, int next) in new[] { (unread, said), (counted, unread) })
        {
            StoppedEvent moved = await GoTo(host, thread, line, Stepping);
            Assert.Equal(9, moved.ReadStopInfo().column);
            Assert.EndsWith("!Queries.Run()", (await host.TopFrameAsync(thread)).Name, StringComparison.Ordinal);
            StoppedEvent stepped = await host.StepOverAsync(thread).WaitForStoppedEvent();
            Assert.Equal((StopReasons.Step, next), (stepped.Reason, stepped.ReadStopInfo().line));
        }
        Assert.Equal("3", await Evaluate(host, thread, "counted"));
    }

    private static async Task<StoppedEvent> GoTo(DebugSession host, int thread, int line, string debuggee = "PassengerCases")
    {
        GotoTargetsResponse targets = await host.RequestAsync<GotoTargetsResponse>(DapCommands.GotoTargets,
            new GotoTargetsArguments(new DapSource { Path = Debuggees.Source(debuggee, "Program.cs") }, line, null));
        GotoTarget target = Assert.Single(targets.Targets);
        Assert.Equal(line, target.Line);
        await host.RequestAsync(DapCommands.Goto, new GotoArguments(thread, target.Id));
        StoppedEvent moved = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Goto, moved.Reason);
        Assert.Equal(line, moved.ReadStopInfo().line);
        return moved;
    }

    private static async Task<string> Evaluate(DebugSession host, int thread, string expression)
    {
        EvaluateResponse response = await host.EvaluateAsync((await host.TopFrameAsync(thread)).Id, expression);
        return response.Result;
    }
}

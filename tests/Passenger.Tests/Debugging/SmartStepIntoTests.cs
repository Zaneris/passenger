using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Smart Step Into: the calls of the line a thread stopped at, in the order they run, each placed where it is
/// written, and a step straight into the one chosen, lambdas and awaited async methods included.</summary>
[Collection(DebugSession.Collection)]
public class SmartStepIntoTests(ITestOutputHelper testOutputHelper)
{
    private const string Stepping = "SteppingConsoleApp";

    private static int Line(string code) => Debuggees.Line(Stepping, code);

    [Fact]
    public async Task The_calls_of_a_line_are_listed_in_the_order_they_run_and_the_one_chosen_is_stepped_into()
    {
        const string code = "int nested = Outer(Inner());";
        int line = Line(code);
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        IReadOnlyList<StepInTarget> targets = await Targets(host, thread);
        Assert.Equal(["Inner()", "Outer(int)"], targets.Select(t => t.Label));
        string text = File.ReadAllLines(Debuggees.Source(Stepping, "Program.cs"))[line - 1];
        Assert.Equal((line, text.IndexOf("Inner", StringComparison.Ordinal) + 1, line, text.IndexOf("Inner", StringComparison.Ordinal) + 6),
            (targets[0].Line, targets[0].Column, targets[0].EndLine, targets[0].EndColumn));
        Assert.Equal(text.IndexOf("Outer", StringComparison.Ordinal) + 1, targets[1].Column);

        // Inner has run by then, so Outer is given what it returned.
        stop = await host.Step(thread, targets[1]);
        Assert.Equal((StopReasons.Step, Line("static int Outer(int value)") + 1), (stop.Reason, stop.ReadStopInfo().line));
        Assert.Contains("Outer", (await host.TopFrameAsync(thread)).Name, StringComparison.Ordinal);
        Assert.Equal("20", Assert.Single(await host.LocalsAsync(stop), v => v.Name == "value").Value);
    }

    [Fact]
    public async Task Of_two_calls_of_the_same_method_the_second_chosen_is_the_one_stepped_into()
    {
        int line = Line("int sum = Add(Add(1, 2), 3);");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        IReadOnlyList<StepInTarget> targets = await Targets(host, thread);
        Assert.Equal(["Add(int, int)", "Add(int, int)"], targets.Select(t => t.Label));
        Assert.True(targets[0].Column > targets[1].Column, "The inner call, which runs first, was not listed first");
        stop = await host.Step(thread, targets[1]);
        List<DapVariable> locals = await host.LocalsAsync(stop);
        Assert.Equal(("3", "3"), (Assert.Single(locals, v => v.Name == "a").Value, Assert.Single(locals, v => v.Name == "b").Value));
    }

    /// <summary>Of ok ? Pick(1) : Pick(2) with ok false, Pick(2) chosen is stepped into from its own call, though Pick(1)
    /// never ran; Pick(1) chosen is never entered from its call, so the step ends at the next line as a step over does.</summary>
    [Fact]
    public async Task A_call_chosen_after_one_the_condition_skips_is_stepped_into_and_the_skipped_one_never_is()
    {
        const string code = "int chosen = ok ? Pick(1) : Pick(2);";
        int line = Line(code);
        string text = File.ReadAllLines(Debuggees.Source(Stepping, "Program.cs"))[line - 1];
        int first = text.IndexOf("Pick(1)", StringComparison.Ordinal) + 1;
        int second = text.IndexOf("Pick(2)", StringComparison.Ordinal) + 1;
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        IReadOnlyList<StepInTarget> targets = await Targets(host, thread);
        Assert.Equal(["Pick(int)", "Pick(int)"], targets.Select(t => t.Label));
        Assert.Equal([first, second], targets.Select(t => t.Column ?? 0).Order());
        stop = await host.Step(thread, Assert.Single(targets, t => t.Column == second));
        Assert.True(stop.ReadStopInfo().line == Line("private static int Pick(int value)") + 1,
            $"Smart Step Into Pick(2) stopped at line {stop.ReadStopInfo().line} ({stop.Reason}), not in Pick: it waited for an entry from Pick(1), which ok skipped");
        Assert.Equal("2", Assert.Single(await host.LocalsAsync(stop), v => v.Name == "value").Value);

        stop = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal((StopReasons.Breakpoint, line), (stop.Reason, stop.ReadStopInfo().line));
        stop = await host.Step(thread, Assert.Single(await Targets(host, thread), t => t.Column == first));
        Assert.True((stop.Reason, stop.ReadStopInfo().line) == (StopReasons.Step, Line("Console.WriteLine($\"chosen {chosen}\");")),
            $"Smart Step Into Pick(1), which ok skips, stopped at line {stop.ReadStopInfo().line} ({stop.Reason}), where it was to end at the next line");
    }

    [Fact]
    public async Task A_lambda_on_the_line_is_stepped_into_when_what_it_is_handed_to_runs_it()
    {
        const string code = "int[] doubled = [.. Numbers().Select(n => Twice(n))];";
        int line = Line(code);
        int column = File.ReadAllLines(Debuggees.Source(Stepping, "Program.cs"))[line - 1].IndexOf(code, StringComparison.Ordinal) + 1;
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal((line, column), (stop.ReadStopInfo().line, stop.ReadStopInfo().column));
        int thread = stop.ThreadId!.Value;

        // Select is the framework's, so only the lambda's body is offered.
        IReadOnlyList<StepInTarget> targets = await Targets(host, thread);
        Assert.Equal(["Numbers()", "n => Twice(n)"], targets.Select(t => t.Label));
        stop = await host.Step(thread, targets[1]);
        Assert.Equal((StopReasons.Step, line), (stop.Reason, stop.ReadStopInfo().line));
        Assert.Contains(">b__", (await host.TopFrameAsync(thread)).Name, StringComparison.Ordinal);
        Assert.Equal("1", Assert.Single(await host.LocalsAsync(stop), v => v.Name == "n").Value);
    }

    [Fact]
    public async Task An_awaited_async_method_is_stepped_into_at_its_first_line()
    {
        int line = Line("int computed = await ComputeAsync(Seed());");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        IReadOnlyList<StepInTarget> targets = await Targets(host, thread);
        Assert.Equal(["Seed()", "ComputeAsync(int)"], targets.Select(t => t.Label));
        stop = await host.Step(thread, targets[1]);
        Assert.Equal((StopReasons.Step, Line("static async Task<int> ComputeAsync(int value)") + 1), (stop.Reason, stop.ReadStopInfo().line));
        Assert.Equal("4", Assert.Single(await host.LocalsAsync(stop), v => v.Name == "value").Value);
    }

    private static async Task<IReadOnlyList<StepInTarget>> Targets(DebugSession host, int thread) =>
        (await host.RequestAsync<StepInTargetsResponse>(DapCommands.StepInTargets, new StepInTargetsArguments((await host.TopFrameAsync(thread)).Id))).Targets;
}

internal static class SmartStepIntoRequests
{
    /// <summary>Smart Step Into's step: straight into a target, and the stop it comes to.</summary>
    public static async Task<StoppedEvent> Step(this DebugSession host, int thread, StepInTarget target)
    {
        await host.RequestAsync(DapCommands.StepIn, new StepInArguments(thread, target.Id));
        return await host.WaitForStoppedEvent();
    }
}

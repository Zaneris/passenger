using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Pause: a running program stops wherever it is, on a thread showing its code, and a step from there goes on
/// from the loop. While it is paused, an evaluation runs its own thread alone, and what the other threads were about to
/// stop for (a breakpoint, an exception) is still seen once it is over.</summary>
[Collection(DebugSession.Collection)]
public class PauseTests(ITestOutputHelper testOutputHelper)
{
    private const string Looping = "LoopingConsoleApp";

    [Fact]
    public async Task Pause_stops_a_looping_program_in_its_loop_and_a_second_pause_after_continuing_does_too()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, sleeps: false);

        StoppedEvent stopped = await Pause(host);
        Assert.Equal(StopReasons.Pause, stopped.Reason);
        AssertInLoop(await host.TopFrameAsync(stopped.ThreadId!.Value));

        await host.StepOverAsync(stopped.ThreadId.Value);
        StoppedEvent stepped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Step, stepped.Reason);
        AssertInLoop(await host.TopFrameAsync(stepped.ThreadId!.Value));

        await host.ContinueAsync();
        Assert.Equal(StopReasons.Pause, (await Pause(host)).Reason);
    }

    /// <summary>A pause that caught the thread in the runtime's own code (its sleep), where none of the program's can run: a
    /// value its ToString would show is shown by its type, its fields there to open, never as an error; and a step from
    /// there comes back to the loop.</summary>
    [Fact]
    public async Task A_value_paused_in_a_sleep_shows_its_type_and_fields_where_its_ToString_cannot_run_and_a_step_comes_back_to_the_loop()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, sleeps: true);

        StoppedEvent stopped = await Pause(host);
        Assert.Equal(StopReasons.Pause, stopped.Reason);
        StackTraceResponse stack = await host.StackTraceAsync(stopped.ThreadId!.Value, levels: null);
        DapStackFrame main = stack.StackFrames.First(f => f.Source?.Path?.EndsWith("Program.cs", StringComparison.Ordinal) == true);
        ScopesResponse scopes = await host.ScopesAsync(main.Id);
        List<DapVariable> locals = await host.VariablesAsync(scopes.Scopes.Single().VariablesReference);
        DapVariable nap = Assert.Single(locals, v => v.Name == "nap");
        Assert.True(nap.Value is "00:00:00.0200000" or "{System.TimeSpan}" && nap.PresentationHint?.Attributes?.Contains("failedEvaluation") != true,
            $"A TimeSpan read where the pause caught its thread shows as {nap.Value}");
        List<DapVariable> fields = await host.VariablesAsync(nap.VariablesReference);
        Assert.Equal("200000", Assert.Single(fields, f => f.Name == "_ticks").Value);

        await host.StepOverAsync(stopped.ThreadId!.Value);
        StoppedEvent stepped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Step, stepped.Reason);
        AssertInLoop(await host.TopFrameAsync(stepped.ThreadId!.Value));
    }

    /// <summary>Stopped while another thread counts: listing an object's properties, whose getter takes a while, leaves that
    /// thread where it was; and an evaluation that waits on it is stopped saying why, the next one running as ever.</summary>
    [Fact]
    public async Task Listing_an_objects_properties_leaves_the_other_threads_where_they_were_and_an_evaluation_waiting_on_one_is_stopped_saying_why()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Start(host, ["count"], host => host.BreakpointsAsync(Debuggees.Line(Looping, "stop while counting"), Debuggees.Source(Looping, "Program.cs")));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stopped.ThreadId!.Value)).Id;

        async Task<string> Count()
        {
            EvaluateResponse counted = await host.EvaluateAsync(frame, "Counter.Value");
            return counted.Result;
        }
        string before = await Count();
        Assert.NotEqual("0", before);
        // Its getter takes a while, through which the counting thread would count on if it ran.
        DapVariable slow = Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "slow");
        List<DapVariable> properties = await host.VariablesAsync(slow.VariablesReference);
        Assert.Equal("\"slow\"", Assert.Single(properties, p => p.Name == "Value").Value);
        string after = await Count();
        Assert.True(after == before, $"The paused program's other thread counted on from {before} to {after} while its properties were listed");

        EvaluateResponse waited = await host.EvaluateAsync(frame, "Counter.Next");
        Assert.Contains("another thread", waited.Result, StringComparison.Ordinal);
        EvaluateResponse sum = await host.EvaluateAsync(frame, "1 + 1");
        Assert.Equal("2", sum.Result);
    }

    [Fact]
    public async Task A_condition_that_calls_a_method_stops_a_parallel_loop_on_its_matching_iteration()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Start(host, ["parallel"], host => host.BreakpointsAsync(Debuggees.Source(Looping, "Program.cs"),
            [new DapSourceBreakpoint(Debuggees.Line(Looping, "each iteration"), Condition: "i.ToString() == \"377\"")]));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Breakpoint, stopped.Reason);
        Assert.Equal("377", Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "i").Value);
    }

    [Fact]
    public async Task An_exception_in_a_parallel_loop_whose_iterations_test_a_condition_stops_where_it_was_thrown()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Start(host, ["parallel", "throws"], async host =>
        {
            await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([DapExceptionFilters.All]));
            // Never true, but each iteration's test runs code in the program while the other threads' iterations wait.
            await host.BreakpointsAsync(Debuggees.Source(Looping, "Program.cs"),
                [new DapSourceBreakpoint(Debuggees.Line(Looping, "each iteration"), Condition: "i.ToString() == \"none\"")]);
        });
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Exception, stopped.Reason);
        Assert.Equal("200", Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "i").Value);
        ExceptionInfoResponse thrown = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stopped.ThreadId!.Value));
        Assert.Equal("iteration 200", thrown.Details?.Message);
    }

    private static async Task Launch(DebugSession host, bool sleeps)
    {
        await Start(host, sleeps ? ["sleep"] : []);
        // The program says when it is in its loop.
        await host.WaitForEvent<OutputEvent>(output => output.Output.Contains("looping", StringComparison.Ordinal));
    }

    private static async Task Start(DebugSession host, List<string> arguments, Func<DebugSession, Task>? configure = null)
    {
        string program = Debuggees.Program(Looping);
        await host.InitializeAsync().RequestAsync(DapCommands.Launch, new LaunchArguments
        {
            Program = program,
            Args = arguments,
            Cwd = Path.GetDirectoryName(program),
            Console = "internalConsole",
            JustMyCode = true,
            StopAtEntry = false,
        });
        await host.WaitForInitializedEvent();
        if (configure is not null)
        {
            await configure(host);
        }
        await host.ConfigurationDoneAsync();
    }

    private static async Task<StoppedEvent> Pause(DebugSession host)
    {
        await host.RequestAsync(DapCommands.Pause, new ThreadArguments(0));
        return await host.WaitForStoppedEvent();
    }

    private static void AssertInLoop(DapStackFrame top)
    {
        Assert.EndsWith("Program.cs", top.Source?.Path);
        Assert.InRange(top.Line, Debuggees.Line(Looping, "turns++") - 2, Debuggees.Line(Looping, "Thread.Sleep(nap)") + 2);
    }
}

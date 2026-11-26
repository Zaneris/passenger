using System.Globalization;
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

    /// <summary>The gate's pauses as one flow: a looping program paused in its loop and again after it went on, a value
    /// paused in a sleep shown though its ToString cannot run and a step back to the loop, and a parallel loop stopped
    /// on the iteration its condition matches or where its exception was thrown.</summary>
    [Gate]
    [Fact]
    public async Task The_gates_pauses_hold_as_one_flow()
    {
        await Flow.StepApartAsync("Pause stops a looping program in its loop and a second pause after continuing does too", () => new PauseTests(testOutputHelper), t => t.Pause_stops_a_looping_program_in_its_loop_and_a_second_pause_after_continuing_does_too());
        await Flow.StepApartAsync("a value paused in a sleep shows its type and fields where its ToString cannot run and a step comes back to the loop", () => new PauseTests(testOutputHelper), t => t.A_value_paused_in_a_sleep_shows_its_type_and_fields_where_its_ToString_cannot_run_and_a_step_comes_back_to_the_loop());
        await Flow.StepApartAsync("a condition that calls a method stops a parallel loop on its matching iteration", () => new PauseTests(testOutputHelper), t => t.A_condition_that_calls_a_method_stops_a_parallel_loop_on_its_matching_iteration());
        await Flow.StepApartAsync("an exception in a parallel loop whose iterations test a condition stops where it was thrown", () => new PauseTests(testOutputHelper), t => t.An_exception_in_a_parallel_loop_whose_iterations_test_a_condition_stops_where_it_was_thrown());
    }

    private async Task Pause_stops_a_looping_program_in_its_loop_and_a_second_pause_after_continuing_does_too()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, sleeps: false);

        StoppedEvent stopped = await Pause(host);
        Assert.Equal(StopReasons.Pause, stopped.Reason);
        await AssertInLoop(host, stopped.ThreadId!.Value, "The pause");

        await host.StepOverAsync(stopped.ThreadId.Value);
        StoppedEvent stepped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Step, stepped.Reason);
        await AssertInLoop(host, stepped.ThreadId!.Value, "The step over from the pause");

        await host.ContinueAsync();
        Assert.Equal(StopReasons.Pause, (await Pause(host)).Reason);
    }

    /// <summary>A pause that caught the thread in the runtime's own code (its sleep), where none of the program's can run: a
    /// value its ToString would show is shown by its type, its fields there to open, never as an error; and a step from
    /// there comes back to the loop.</summary>
    private async Task A_value_paused_in_a_sleep_shows_its_type_and_fields_where_its_ToString_cannot_run_and_a_step_comes_back_to_the_loop()
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
        Assert.True(nap.Value is "00:00:00.0200000" or "{System.TimeSpan}" && nap.PresentationHint?.Attributes?.Contains(DapAttributes.FailedEvaluation) != true,
            $"A TimeSpan read where the pause caught its thread shows as {nap.Value}");
        List<DapVariable> fields = await host.VariablesAsync(nap.VariablesReference);
        Assert.Equal("200000", Assert.Single(fields, f => f.Name == "_ticks").Value);

        await host.StepOverAsync(stopped.ThreadId!.Value);
        StoppedEvent stepped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Step, stepped.Reason);
        await AssertInLoop(host, stepped.ThreadId!.Value, "The step over from the pause in the sleep");
    }

    /// <summary>A step from a pause in a sleep comes back as the sleep returns; listing an object's properties leaves
    /// the other threads where they were and an evaluation waiting on one is stopped saying why; one waiting on a lock
    /// another thread holds runs again with every thread let go; a parallel loop's condition stops on its matching
    /// iteration; a thread started or missed while a condition is evaluated stops at its breakpoint's first pass; a
    /// breakpoint or an event whose handling fails stops or lets the program go on, saying why.</summary>
    [Fact]
    public async Task A_pause_or_an_evaluation_holds_each_thread_where_it_stands_and_lets_it_go_whole()
    {
        await Flow.StepApartAsync<PauseTests>("a step over from a pause in a sleep comes back as the sleep returns, with Just My Code", () => new PauseTests(testOutputHelper), t => t.A_step_from_a_pause_in_a_sleep_comes_back_as_the_sleep_returns_on_the_turn_it_was_paused_on(DapCommands.Next, true));
        await Flow.StepApartAsync<PauseTests>("a step into from a pause in a sleep comes back as the sleep returns, without Just My Code", () => new PauseTests(testOutputHelper), t => t.A_step_from_a_pause_in_a_sleep_comes_back_as_the_sleep_returns_on_the_turn_it_was_paused_on(DapCommands.StepIn, false));
        await Flow.StepApartAsync<PauseTests>("listing an object's properties leaves the other threads where they were and an evaluation waiting on one is stopped saying why", () => new PauseTests(testOutputHelper), t => t.Listing_an_objects_properties_leaves_the_other_threads_where_they_were_and_an_evaluation_waiting_on_one_is_stopped_saying_why());
        await Flow.StepApartAsync<PauseTests>("an evaluation waiting on a lock another thread holds runs again with every thread let go", () => new PauseTests(testOutputHelper), t => t.An_evaluation_waiting_on_a_lock_another_thread_holds_runs_again_with_every_thread_let_go());
        await Flow.StepApartAsync<PauseTests>("a parallel loop's condition whose evaluations hold no other thread still stops on its matching iteration", () => new PauseTests(testOutputHelper), t => t.A_parallel_loops_condition_whose_evaluations_hold_no_other_thread_still_stops_on_its_matching_iteration());
        await Flow.StepApartAsync<PauseTests>("a thread started while a condition is evaluated waits with the others and stops at its breakpoint's first pass", () => new PauseTests(testOutputHelper), t => t.A_thread_started_while_a_condition_is_evaluated_waits_with_the_others_and_stops_at_its_breakpoints_first_pass());
        await Flow.StepApartAsync<PauseTests>("a thread an evaluation's hold misses is held where its breakpoint is put off and stops on that pass", () => new PauseTests(testOutputHelper), t => t.A_thread_an_evaluations_hold_misses_is_held_where_its_breakpoint_is_put_off_and_stops_on_that_pass());
        await Flow.StepApartAsync<PauseTests>("a breakpoint whose handling fails stops where its thread stands saying why and so does one met during an evaluation", () => new PauseTests(testOutputHelper), t => t.A_breakpoint_whose_handling_fails_stops_where_its_thread_stands_saying_why_and_so_does_one_met_during_an_evaluation());
        await Flow.StepApartAsync<PauseTests>("an event that stops nothing whose handling fails lets the program go on saying why", () => new PauseTests(testOutputHelper), t => t.An_event_that_stops_nothing_whose_handling_fails_lets_the_program_go_on_saying_why());
    }

    /// <summary>A step from a pause in the sleep, the thread in the library's code calling native code, comes back as the
    /// sleep returns, on the turn of the loop it was paused on, in the loop, or without Just My Code where the library's frame
    /// returns: one that ended only when the thread next passed through that code came back a turn later, and never once the
    /// runtime had compiled that code again meanwhile.</summary>
    private async Task A_step_from_a_pause_in_a_sleep_comes_back_as_the_sleep_returns_on_the_turn_it_was_paused_on(string step, bool justMyCode)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, sleeps: true, justMyCode);

        StoppedEvent stopped = await Pause(host);
        // A pause between two sleeps finds the loop's own line, from which a step may pass the count.
        for (int again = 0; again < 5 && InProgram(await host.TopFrameAsync(stopped.ThreadId!.Value)); again++)
        {
            await host.ContinueAsync();
            stopped = await Pause(host);
        }
        int thread = stopped.ThreadId!.Value;
        DapStackFrame paused = await host.TopFrameAsync(thread);
        Assert.False(InProgram(paused), $"Six pauses all found the loop's own line, never its sleep: {paused.Name}");
        List<DapStackFrame> pausedStack = [.. (await host.StackTraceAsync(thread, levels: null)).StackFrames];
        string pausedIn = string.Join(" < ", pausedStack.Take(4).Select(f => f.Name));
        long before = await Turns(host, thread);

        await host.RequestAsync(step, new ThreadArguments(thread));
        StoppedEvent stepped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Step, stepped.Reason);
        DapStackFrame came = await host.TopFrameAsync(thread);
        long after = await Turns(host, thread);
        string log = host.EngineLog();
        // Without Just My Code the step may come back in a library frame the sleep returns to, as the runtime compiled them.
        bool back = InProgram(came) || !justMyCode && pausedStack.Skip(1).Any(f => f.Name == came.Name);
        Assert.True(back && after == before,
            $"A {step} from a pause in {pausedIn}{(justMyCode ? string.Empty : ", without Just My Code,")} stopped in {came.Name}, line {came.Line}, on turn {after} of the "
            + $"loop, where it was paused on turn {before}: one that waits for the thread to pass through the sleep's code again never comes back where the program's "
            + $"code is compiled again meanwhile. The engine's log from the pause:\n{log[Math.Max(0, log.LastIndexOf("] Pause", StringComparison.Ordinal))..]}");
        if (InProgram(came))
        {
            await AssertInLoop(host, thread, $"The {step} from the pause in the sleep");
        }

        static bool InProgram(DapStackFrame frame) => frame.Source?.Path?.EndsWith("Program.cs", StringComparison.Ordinal) == true;

        static async Task<long> Turns(DebugSession host, int thread)
        {
            StackTraceResponse stack = await host.StackTraceAsync(thread, levels: null);
            ScopesResponse scopes = await host.ScopesAsync(stack.StackFrames.First(InProgram).Id);
            return long.Parse(Assert.Single(await host.VariablesAsync(scopes.Scopes.Single().VariablesReference), v => v.Name == "turns").Value, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Stopped while another thread counts: listing an object's properties, whose getter takes a while, leaves that
    /// thread where it was; and an evaluation that waits on it is stopped saying why, the next one running as ever.</summary>
    private async Task Listing_an_objects_properties_leaves_the_other_threads_where_they_were_and_an_evaluation_waiting_on_one_is_stopped_saying_why()
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

        EvaluateResponse waited = await host.EvaluateAsync(frame, "Counter.Next", mayStand: true);
        string log = host.EngineLog();
        Assert.True(waited.Result.Contains("another thread", StringComparison.Ordinal),
            $"An evaluation spinning until the paused thread counts was answered \"{waited.Result}\", where it was to be stopped saying it may wait on "
            + $"another thread; the engine's log from it:\n{log[Math.Max(0, log.LastIndexOf("Evaluate: Counter.Next", StringComparison.Ordinal))..]}");
        EvaluateResponse sum = await host.EvaluateAsync(frame, "1 + 1");
        Assert.Equal("2", sum.Result);
    }

    /// <summary>Stopped while a second thread holds a lock: a property read under it never ends with that thread held, and
    /// says it probably needs another thread, as does the next evaluation on the thread while it runs on, nothing having let
    /// that thread run; run again with every thread let go, the one left running returns first and the property gives its
    /// value, after which the thread evaluates as ever.</summary>
    private async Task An_evaluation_waiting_on_a_lock_another_thread_holds_runs_again_with_every_thread_let_go()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Start(host, ["locked"], host => host.BreakpointsAsync(Debuggees.Line(Looping, "stop while the lock is held"), Debuggees.Source(Looping, "Program.cs")));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stopped.ThreadId!.Value)).Id;

        EvaluateResponse stuck = await host.EvaluateAsync(frame, "locked.Value", mayStand: true);
        Assert.True(NeedsThreads(stuck), $"A property read under a lock a held thread holds was answered \"{stuck.Result}\", not marked as needing another thread to run");
        // Held unfinished, the first shows that nothing let the thread holding the lock run.
        EvaluateResponse refused = await host.EvaluateAsync(frame, "locked.Value");
        Assert.True(NeedsThreads(refused), $"An evaluation on the thread while the first is held unfinished was answered \"{refused.Result}\", not marked as needing another thread to run");
        Assert.Contains("is held unfinished on this thread", refused.Result, StringComparison.Ordinal);

        EvaluateResponse ran = await host.EvaluateAsync(frame, "locked.Value", letThreadsRun: true);
        Assert.True(ran.Result == "\"read under the lock\"", $"Run with every thread let go, the property was answered \"{ran.Result}\"; the engine's log:\n{host.EngineLog()}");
        Assert.Equal("true", (await host.EvaluateAsync(frame, "Locked.LetGo")).Result);
        DapStackFrame top = await host.TopFrameAsync(stopped.ThreadId!.Value);
        Assert.True(top.Line == stopped.Line, $"With every thread let go, the stopped thread itself ran on from line {stopped.Line} to {top.Line}");
        EvaluateResponse again = await host.EvaluateAsync(frame, "locked.Value");
        Assert.True(again.Result == "\"read under the lock\"", $"After the run with every thread let go, the thread's next evaluation was answered \"{again.Result}\"");

        static bool NeedsThreads(EvaluateResponse answer) => answer.PresentationHint?.Attributes is { } attributes && attributes.Contains(DapAttributes.FailedEvaluation)
            && attributes.Contains(DapAttributes.NeedsThreads) && answer.Result.Contains("needs another thread", StringComparison.Ordinal);
    }

    private async Task A_condition_that_calls_a_method_stops_a_parallel_loop_on_its_matching_iteration()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StopsOnMatchingIteration(host);
    }

    /// <summary>A parallel loop's condition whose evaluations hold no other thread, as the runtime's hold once missed a
    /// worker: each worker meeting the breakpoint meanwhile is held where its breakpoint came, so the stop is still on the
    /// matching iteration, never one tried where a worker had run on to.</summary>
    private async Task A_parallel_loops_condition_whose_evaluations_hold_no_other_thread_still_stops_on_its_matching_iteration()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper,
            environment: new Dictionary<string, string> { [AdapterEnvironment.HoldNoneVariable] = "1" });
        await StopsOnMatchingIteration(host);
    }

    private static async Task StopsOnMatchingIteration(DebugSession host)
    {
        await Start(host, ["parallel"], host => host.BreakpointsAsync(Debuggees.Source(Looping, "Program.cs"),
            [new DapSourceBreakpoint(Debuggees.Line(Looping, "each iteration"), Condition: "i.ToString() == \"377\"")]));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Breakpoint, stopped.Reason);
        Assert.Equal("377", Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "i").Value);
    }

    /// <summary>A thread made while a breakpoint's condition is evaluated, as a parallel loop's next worker can be, waits with
    /// the other threads until the evaluation is over: its own breakpoint then stops it on its first pass, where the stop
    /// shows it, never turns further on, past hits put off unseen.</summary>
    private async Task A_thread_started_while_a_condition_is_evaluated_waits_with_the_others_and_stops_at_its_breakpoints_first_pass()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int turning = Debuggees.Line(Looping, "the started thread's turn");
        await Start(host, ["starts"], host => host.BreakpointsAsync(Debuggees.Source(Looping, "Program.cs"),
        [
            new DapSourceBreakpoint(Debuggees.Line(Looping, "stop where a condition starts a thread"), Condition: "Starter.Begin()"),
            new DapSourceBreakpoint(turning),
        ]));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(turning, stopped.Line);
        string turn = Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "turn").Value;
        Assert.True(turn == "0", $"The started thread's breakpoint showed it at turn {turn}, where it had run on unseen from its first pass while the condition was evaluated");
    }

    /// <summary>An evaluation whose hold reaches no other thread, as the runtime's once missed a parallel loop's worker: the
    /// thread started while the condition is evaluated runs until its breakpoint comes, and is held there by its event's
    /// putting off, so the stop shows that first pass, never a later turn or wherever it had run on to.</summary>
    private async Task A_thread_an_evaluations_hold_misses_is_held_where_its_breakpoint_is_put_off_and_stops_on_that_pass()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper,
            environment: new Dictionary<string, string> { [AdapterEnvironment.HoldNoneVariable] = "1" });
        int turning = Debuggees.Line(Looping, "the started thread's turn");
        await Start(host, ["starts"], host => host.BreakpointsAsync(Debuggees.Source(Looping, "Program.cs"),
        [
            new DapSourceBreakpoint(Debuggees.Line(Looping, "stop where a condition starts a thread"), Condition: "Starter.Begin()"),
            new DapSourceBreakpoint(turning),
        ]));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        string log = host.EngineLog();
        Assert.True(log.Contains("Held none: every other thread is left running", StringComparison.Ordinal),
            $"The evaluation held the other threads despite the test's hold, so nothing was left for its put-off event to hold; the engine's log:\n{log}");
        Assert.True(stopped.Line == turning, $"The program stopped at line {stopped.Line}, not at the started thread's turn, line {turning}; the engine's log:\n{log}");
        string turn = Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "turn").Value;
        Assert.True(turn == "0", $"The started thread's breakpoint, put off while the condition was evaluated, showed it at turn {turn}: "
            + $"unheld, it ran on from its first pass, the event tried where it then stood; the engine's log:\n{log}");
    }

    /// <summary>A breakpoint whose handling fails still stops the program where its thread stands, saying why in the Debug
    /// Console and the stop, never letting the program run on unseen; and one met, as an evaluation runs, by a thread its
    /// hold missed stops it the same way once the evaluation is over and the program goes on, its thread held meanwhile
    /// where the breakpoint came, so the stop shows that first pass (#2029).</summary>
    private async Task A_breakpoint_whose_handling_fails_stops_where_its_thread_stands_saying_why_and_so_does_one_met_during_an_evaluation()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: new Dictionary<string, string>
        {
            [AdapterEnvironment.HoldNoneVariable] = "1",
            [AdapterEnvironment.FailHandlingVariable] = "Breakpoint",
        });
        int starting = Debuggees.Line(Looping, "stop where a condition starts a thread");
        int turning = Debuggees.Line(Looping, "the started thread's turn");
        await Start(host, ["starts"], host => host.BreakpointsAsync(Debuggees.Source(Looping, "Program.cs"), [new DapSourceBreakpoint(starting), new DapSourceBreakpoint(turning)]));
        const string failed = "the debugger could not handle a Breakpoint event";
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.True((stopped.Reason, stopped.Line) == (StopReasons.Breakpoint, starting) && stopped.Text?.Contains(failed, StringComparison.Ordinal) == true,
            $"The breakpoint whose handling failed stopped for {stopped.Reason} at line {stopped.Line} ({stopped.Text}), not at its line, {starting}, saying why");

        EvaluateResponse began = await host.EvaluateAsync((await host.TopFrameAsync(stopped.ThreadId!.Value)).Id, "Starter.Begin()");
        Assert.Equal("false", began.Result);
        string log = host.EngineLog();
        Assert.True(log.Contains("Put off: HandlingFailed", StringComparison.Ordinal),
            $"The started thread's breakpoint, whose handling failed during the evaluation, was not put off to stop once it was over; the engine's log:\n{log}");
        await host.ContinueAsync().WaitForEvent<OutputEvent>(output => output.Output.Contains($"The program stopped where its thread stands, as {failed}", StringComparison.Ordinal));
        stopped = await host.WaitForStoppedEvent();
        Assert.True((stopped.Reason, stopped.Line) == (StopReasons.Breakpoint, turning) && stopped.Text?.Contains(failed, StringComparison.Ordinal) == true,
            $"Let go after the evaluation, the program stopped for {stopped.Reason} at line {stopped.Line} ({stopped.Text}), not at the started thread's breakpoint whose handling failed, line {turning}, saying why");
        string turn = Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "turn").Value;
        Assert.True(turn == "0", $"The started thread's breakpoint, whose handling failed during the evaluation, showed it at turn {turn}, where it had run on unseen from its first pass");
    }

    /// <summary>An event that stops nothing of itself, a thread's start, whose handling fails lets the program go on, saying
    /// why in the Debug Console, never holding it there nor letting it go unsaid (#2029).</summary>
    private async Task An_event_that_stops_nothing_whose_handling_fails_lets_the_program_go_on_saying_why()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: new Dictionary<string, string> { [AdapterEnvironment.FailHandlingVariable] = "CreateThread" });
        await host.InitializeAsync().LaunchAsync(Debuggees.Program("SteppingConsoleApp"), stopAtEntry: false, justMyCode: true).WaitForInitializedEvent();
        await host.ConfigurationDoneAsync().WaitForEvent<OutputEvent>(output => output.Output.Contains("The program went on, as the debugger could not handle a CreateThread event", StringComparison.Ordinal));
        (string output, int? exitCode) = await host.WaitForEnd();
        Assert.True(output.Contains("count 42 0.75 Spot { X = 2, Y = 3 }", StringComparison.Ordinal) && exitCode == 0,
            $"With a thread's start it failed to handle, the program did not run on to its end as it would have (exit code {exitCode}):\n{output}");
    }

    private async Task An_exception_in_a_parallel_loop_whose_iterations_test_a_condition_stops_where_it_was_thrown()
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
        Assert.True(stopped.Reason == StopReasons.Exception,
            $"The parallel loop stopped for {stopped.Reason} at line {stopped.Line} ({stopped.Text}), not at iteration 200's exception, its condition never true; "
            + $"the engine's log ends:\n{string.Join('\n', host.EngineLog().Split('\n').TakeLast(80))}");
        Assert.Equal("200", Assert.Single(await host.LocalsAsync(stopped), v => v.Name == "i").Value);
        ExceptionInfoResponse thrown = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stopped.ThreadId!.Value));
        Assert.Equal("iteration 200", thrown.Details?.Message);
    }

    private static async Task Launch(DebugSession host, bool sleeps, bool justMyCode = true)
    {
        await Start(host, sleeps ? ["sleep"] : [], justMyCode: justMyCode);
        // The program says when it is in its loop.
        await host.WaitForEvent<OutputEvent>(output => output.Output.Contains("looping", StringComparison.Ordinal));
    }

    private static async Task Start(DebugSession host, List<string> arguments, Func<DebugSession, Task>? configure = null, bool justMyCode = true)
    {
        string program = Debuggees.Program(Looping);
        await host.InitializeAsync().RequestAsync(DapCommands.Launch, new LaunchArguments
        {
            Program = program,
            Args = arguments,
            Cwd = Path.GetDirectoryName(program),
            Console = ConsoleKinds.InternalConsole,
            JustMyCode = justMyCode,
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

    /// <summary>The thread stands in the loop, its top frame on one of the loop's lines; a failure names the frames it
    /// stood in.</summary>
    private static async Task AssertInLoop(DebugSession host, int thread, string stop)
    {
        IReadOnlyList<DapStackFrame> frames = (await host.StackTraceAsync(thread, levels: null)).StackFrames;
        int from = Debuggees.Line(Looping, "turns++") - 2;
        int to = Debuggees.Line(Looping, "Thread.Sleep(nap)") + 2;
        Assert.True(frames is [{ Source.Path: { } path } top, ..] && path.EndsWith("Program.cs", StringComparison.Ordinal) && top.Line >= from && top.Line <= to,
            $"{stop} stood outside the loop (Program.cs, lines {from} to {to}), in "
            + (frames.Count == 0 ? "no frame" : string.Join(" < ", frames.Take(8).Select(f => $"{f.Name} {(f.Source?.Path is { } file ? $"at {Path.GetFileName(file)}:{f.Line}" : "(no source)")}"))));
    }
}

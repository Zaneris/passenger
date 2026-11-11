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

    [Gate]
    [Fact]
    public async Task Pause_stops_a_looping_program_in_its_loop_and_a_second_pause_after_continuing_does_too()
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
    [Gate]
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
        await AssertInLoop(host, stepped.ThreadId!.Value, "The step over from the pause in the sleep");
    }

    /// <summary>A step from a pause in the sleep, the thread in the library's code calling native code, comes back as the
    /// sleep returns, on the turn of the loop it was paused on, in the loop, or without Just My Code where the library's frame
    /// returns: one that ended only when the thread next passed through that code came back a turn later, and never once the
    /// runtime had compiled that code again meanwhile.</summary>
    [Theory]
    [InlineData(DapCommands.Next, true)]
    [InlineData(DapCommands.StepIn, true)]
    [InlineData(DapCommands.Next, false)]
    [InlineData(DapCommands.StepIn, false)]
    public async Task A_step_from_a_pause_in_a_sleep_comes_back_as_the_sleep_returns_on_the_turn_it_was_paused_on(string step, bool justMyCode)
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

    /// <summary>Paused in its sleep, where none of the program's code can run, an interpolated number is written by the
    /// debugger as the program writes it in its own culture (fr-FR), and a value only its own ToString writes is refused,
    /// saying why.</summary>
    [Fact]
    public async Task An_interpolation_paused_in_a_sleep_writes_numbers_in_the_programs_culture_and_refuses_a_value_only_its_ToString_writes()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Start(host, ["asleep"]);
        OutputEvent written = await host.WaitForEvent<OutputEvent>(output => output.Output.Contains("asleep", StringComparison.Ordinal));

        StoppedEvent stopped = await Pause(host);
        StackTraceResponse stack = await host.StackTraceAsync(stopped.ThreadId!.Value, levels: null);
        DapStackFrame main = stack.StackFrames.First(f => f.Source?.Path?.EndsWith("Program.cs", StringComparison.Ordinal) == true);
        EvaluateResponse count = await host.EvaluateAsync(main.Id, "$\"{count}\"");
        EvaluateResponse ratio = await host.EvaluateAsync(main.Id, "$\"{ratio:N1}\"");
        Assert.True($"asleep {Unquoted(count)} {Unquoted(ratio)} dreaming" == written.Output.TrimEnd(),
            $"Paused in its sleep, $\"{{count}}\" answered {count.Result} and $\"{{ratio:N1}}\" {ratio.Result}, where the program wrote {written.Output.TrimEnd()}");

        EvaluateResponse dream = await host.EvaluateAsync(main.Id, "$\"{someObject}\"");
        Assert.True(dream.PresentationHint?.Attributes?.Contains("failedEvaluation") == true && dream.Result.Contains("ToString", StringComparison.Ordinal),
            $"Paused in its sleep, $\"{{someObject}}\", which only its own ToString writes, answered {dream.Result}, where it was to be refused saying why");

        static string Unquoted(EvaluateResponse answer) => answer.Result is ['"', .. string text, '"'] ? text : $"(not a string: {answer.Result})";
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
    [Fact]
    public async Task An_evaluation_waiting_on_a_lock_another_thread_holds_runs_again_with_every_thread_let_go()
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

        static bool NeedsThreads(EvaluateResponse answer) => answer.PresentationHint?.Attributes is { } attributes && attributes.Contains("failedEvaluation")
            && attributes.Contains(DapAttributes.NeedsThreads) && answer.Result.Contains("needs another thread", StringComparison.Ordinal);
    }

    [Gate]
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

    [Gate]
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
            Console = "internalConsole",
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

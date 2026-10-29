using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>An evaluation that cannot be stopped (a getter blocked reading input, outside the runtime), or that a continue
/// leaves behind: the debugger takes the program back, so it is paused as the IDE shows it, and it continues as from any
/// stop.</summary>
[Collection(Alone.Name)]
public class StuckEvaluationTests(ITestOutputHelper testOutputHelper)
{
    private const string Stop = "Console.WriteLine($\"before {before}\");";

    private static string Source => Debuggees.Source("PassengerCases", "Program.cs");

    private static int Line(string code) => Debuggees.Line("PassengerCases", code);

    [Fact]
    public async Task A_continue_runs_every_thread_of_the_program_and_the_evaluations_late_end_is_let_go()
    {
        string worker = "Console.WriteLine(\"worker ran\");";
        await using DebugSession host = await LaunchToBreakpoints(Stop, worker);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        EvaluateResponse stuck = await host.EvaluateAsync((await host.TopFrameAsync(thread)).Id, "Reads.Input");
        Assert.Contains("could not be stopped", stuck.Result, StringComparison.Ordinal);

        await host.ContinueAsync();
        StoppedEvent workerStop = await host.WaitForStoppedEvent();
        Assert.True(workerStop.Reason == StopReasons.Breakpoint && workerStop.ReadStopInfo().line == Line(worker),
            $"The continue left the program's other threads suspended: the worker's breakpoint did not stop it ({workerStop.Reason} at line {workerStop.Line})");
        Assert.NotEqual(thread, workerStop.ThreadId);

        // The read the evaluation waits on has its line, and the program's own the end of its input.
        await host.RequestAsync(DapCommands.Input, new InputArguments("typed\n", Close: true));
        await host.ContinueAsync();
        (string output, int? exitCode) = await host.WaitForEnd();
        Assert.Contains("before 1", output, StringComparison.Ordinal);
        Assert.Contains("read none", output, StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task An_exception_stop_is_still_described_once_an_evaluation_there_could_not_be_stopped()
    {
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", Line("Console.WriteLine(\"end\");"), [DapExceptionFilters.All]);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(StopReasons.Exception, stop.Reason);
        int thread = stop.ThreadId!.Value;
        EvaluateResponse stuck = await host.EvaluateAsync((await host.TopFrameAsync(thread)).Id, "Reads.Input");
        Assert.Contains("could not be stopped", stuck.Result, StringComparison.Ordinal);

        // No code runs on the thread while the evaluation does.
        ExceptionInfoResponse info = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(thread));
        Assert.Equal(("System.InvalidOperationException", "thrown in user code"), (info.Details?.FullTypeName, info.Details?.Message));
        Assert.Contains("Implicit evaluation is disabled", info.Description, StringComparison.Ordinal);
        Assert.Single(await host.LocalsAsync(stop), v => v.Name == "$exception");
    }

    /// <summary>A continue sent while the stop's values are still being read (here by an evaluation that never returns) goes
    /// ahead of them: the evaluation under way is left to the program before its time is up, which goes on with it and has
    /// it aborted once its time is up; a read still waiting finds the stop gone and runs nothing.</summary>
    [Fact]
    public async Task A_continue_goes_ahead_of_the_stops_reads_and_leaves_the_evaluation_under_way_to_the_program()
    {
        await using DebugSession host = await LaunchCase("leaves", "Console.WriteLine($\"turn {turn}\");");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        int frame = (await host.TopFrameAsync(thread)).Id;
        Task<EvaluateResponse> endless = host.Send<EvaluateResponse>(DapCommands.Evaluate, new EvaluateArguments("Leaves.Endless", frame, "watch"));
        await host.WaitForEvent<OutputEvent>(o => o.Output.Contains("evaluating", StringComparison.Ordinal));
        Task<EvaluateResponse> counted = host.Send<EvaluateResponse>(DapCommands.Evaluate, new EvaluateArguments("Leaves.Count", frame, "watch"));

        await host.Send<ContinueResponse>(DapCommands.Continue, new ThreadArguments(thread)).WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);
        // Said only of an evaluation the continue left before its time was up: one it waited out says it took too long.
        Assert.Contains("The program went on before the evaluation ended", (await endless).Result, StringComparison.Ordinal);
        DapException dropped = await Assert.ThrowsAsync<DapException>(() => counted);
        Assert.Contains("earlier stop", dropped.Reason, StringComparison.Ordinal);

        // The getter that counts never ran.
        (string output, int? exitCode) = await host.WaitForEnd();
        Assert.Contains("counted 0", output, StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }

    /// <summary>A program attached to and let go (Detach) a moment after a read of its stop began an evaluation: the detach
    /// waits for that evaluation's end alone, never for its whole time, here ten minutes, so it is answered once the
    /// evaluation returns, and the program runs on to its own end.</summary>
    [Fact]
    public async Task A_detach_just_after_an_evaluation_began_waits_for_its_end_alone_and_the_program_runs_on()
    {
        ProcessStartInfo start = Passenger.Core.ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["lets go"]);
        start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        start.RedirectStandardOutput = true;
        using Process program = Passenger.Core.ChildProcesses.Start(start);
        TaskCompletionSource evaluating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Text.StringBuilder written = new();
        Task heard = Passenger.Core.ChildProcesses.Hear(program, line =>
        {
            lock (written)
            {
                written.AppendLine(line);
            }
            if (line == "evaluating")
            {
                evaluating.TrySetResult();
            }
        });
        Passenger.App.IdeErrors.Observe(heard);
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, program,
            new Dictionary<string, string> { [AdapterEnvironment.EvaluationTimeoutVariable] = TimeSpan.FromMinutes(10).TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        await host
            .InitializeAsync()
            .AttachAsync(program.Id)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Line("Thread.Sleep(10); // each turn of the program let go")], Source).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Task<EvaluateResponse> slow = host.Send<EvaluateResponse>(DapCommands.Evaluate, new EvaluateArguments("LetsGo.Slow", frame, "watch"));
        await evaluating.Task.WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);

        Assert.True(await host.DisconnectAsync(DebugSession.RequestLimit), "The detach was not answered: it waited out the evaluation's ten minutes, not its half-second end");
        Assert.Contains("went on", (await slow).Result, StringComparison.Ordinal);
        Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30).Scaled()), "The program let go did not run on to its end");
        Assert.True(await Waiting.Within(heard, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "The program's output was not read to its end after it ended");
        lock (written)
        {
            Assert.Contains("ran on", written.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, program.ExitCode);
    }

    /// <summary>A Detach after an evaluation that could not be stopped, which has had its time and its abort's window
    /// already, never waits it out again, nor for its thread to leave its stop: it is answered.</summary>
    [Fact]
    public async Task A_detach_after_an_evaluation_that_could_not_be_stopped_is_answered()
    {
        ProcessStartInfo start = Passenger.Core.ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["reads"]);
        start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        // An input kept open with nothing in it: the evaluation's read waits on it for good.
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        using Process program = Passenger.Core.ChildProcesses.Start(start);
        Passenger.App.IdeErrors.Observe(Passenger.Core.ChildProcesses.Hear(program, _ => { }));
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, program);
        await host
            .InitializeAsync()
            .AttachAsync(program.Id)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Line(Stop)], Source).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        EvaluateResponse stuck = await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, "Reads.Input");
        Assert.Contains("could not be stopped", stuck.Result, StringComparison.Ordinal);

        Assert.True(await host.DisconnectAsync(DebugSession.RequestLimit), "The detach was not answered: it waited for an evaluation that had had its time, which never returns");
    }

    /// <summary>An evaluation at a stop that takes a lock another thread holds there waits on it until the program goes on,
    /// and is never aborted there, which would leave the lock held for good. Once the program goes on the evaluation runs to
    /// its end, and the lock is every thread's again: another thread's evaluation takes it, and so does that thread.</summary>
    [Fact]
    public async Task An_evaluation_waiting_on_a_lock_a_paused_thread_holds_leaves_the_lock_free_once_the_program_goes_on()
    {
        await using DebugSession host = await LaunchCase("locks", Held, Later);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        EvaluateResponse waits = await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, "Locks.Taken");
        Assert.Contains("could not be stopped", waits.Result, StringComparison.Ordinal);

        await host.ContinueAsync();
        await TheLockIsFreeOnceTheFirstEvaluationRanToItsEnd(host);
    }

    /// <summary>As above, with the evaluation left to the program by a continue before its time was up: past its time it
    /// waits still, never aborted, and runs to its end once the holder lets the lock go.</summary>
    [Fact]
    public async Task An_evaluation_a_continue_leaves_waiting_on_a_lock_past_its_time_runs_to_its_end_once_the_lock_is_free()
    {
        await using DebugSession host = await LaunchCase("locks", Held, Later);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        Task<EvaluateResponse> waits = host.Send<EvaluateResponse>(DapCommands.Evaluate, new EvaluateArguments("Locks.Taken", (await host.TopFrameAsync(thread)).Id, "watch"));
        await host.WaitForEvent<OutputEvent>(o => o.Output.Contains("taking the lock", StringComparison.Ordinal));

        await host.Send<ContinueResponse>(DapCommands.Continue, new ThreadArguments(thread)).WaitAsync(DebugSession.RequestLimit, TestContext.Current.CancellationToken);
        Assert.Contains("went on", (await waits).Result, StringComparison.Ordinal);
        await TheLockIsFreeOnceTheFirstEvaluationRanToItsEnd(host);
    }

    /// <summary>An evaluation that spins, or sleeps, in a finally block past its time is not aborted there and is said to
    /// run the block, wherever within it the debugger finds it: it runs to its end once another thread lets it out, past
    /// the code after the block.</summary>
    [Theory]
    [InlineData("Finally.Spins")]
    [InlineData("Finally.Sleeps")]
    public async Task An_evaluation_spinning_or_sleeping_in_a_finally_block_is_said_to_run_it_and_runs_to_its_end_once_let_out(string getter)
    {
        string after = "Console.WriteLine($\"let out, {Turns} turns\");";
        await using DebugSession host = await LaunchCase("finally", "Console.WriteLine(\"a finally next\");", after);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        EvaluateResponse held = await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, getter);
        Assert.True(held.Result.Contains("could not be stopped, as it runs a catch or finally block", StringComparison.Ordinal),
            $"The evaluation held in its finally block was given another reason it could not be stopped: {held.Result}");

        await host.ContinueAsync();
        StoppedEvent letOut = await host.WaitForStoppedEvent();
        Assert.True(letOut.Reason == StopReasons.Breakpoint && letOut.ReadStopInfo().line == Line(after),
            $"The program did not go on past the evaluation's end ({letOut.Reason} at line {letOut.Line})");
        EvaluateResponse turns = await host.EvaluateAsync((await host.TopFrameAsync(letOut.ThreadId!.Value)).Id, "Finally.Turns");
        Assert.True(turns.Result == "1", $"The evaluation let out of its finally block counted {turns.Result} turns: an abort left on it took as it left the block");
        await host.ContinueAsync();
        (string output, int? exitCode) = await host.WaitForEnd();
        Assert.Contains("let out, 1 turns", output, StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }

    /// <summary>PassengerCases' "locks" case: the stop while another thread holds the lock, and the later thread's.</summary>
    private const string Held = "Console.WriteLine(\"the lock is held\");";
    private const string Later = "Console.WriteLine(\"a later thread\");";

    /// <summary>The program, gone on from the stop where an evaluation waited on the lock, stops on its later thread, whose
    /// evaluation takes the lock (the first evaluation having taken the first turn, this one takes the second), and the
    /// later thread takes it too.</summary>
    private static async Task TheLockIsFreeOnceTheFirstEvaluationRanToItsEnd(DebugSession host)
    {
        StoppedEvent laterStop = await host.WaitForStoppedEvent();
        Assert.True(laterStop.Reason == StopReasons.Breakpoint && laterStop.ReadStopInfo().line == Line(Later),
            $"The program did not go on to the later thread's breakpoint ({laterStop.Reason} at line {laterStop.Line})");
        EvaluateResponse taken = await host.EvaluateAsync((await host.TopFrameAsync(laterStop.ThreadId!.Value)).Id, "Locks.Taken");
        Assert.True(taken.Result == "2",
            $"Another thread's evaluation of a getter that takes the lock gave {taken.Result}: the first evaluation never let the lock go, or was aborted before its end");

        await host.ContinueAsync();
        (string output, int? exitCode) = await host.WaitForEnd();
        Assert.True(output.Contains("taken again, 3 turns", StringComparison.Ordinal), $"The later thread never had the lock: the program said {output}");
        Assert.Equal(0, exitCode);
    }

    /// <summary>PassengerCases' "reads" case launched with breakpoints on the lines that hold the code given.</summary>
    private Task<DebugSession> LaunchToBreakpoints(params string[] code) => LaunchCase("reads", code);

    /// <summary>A case of PassengerCases launched with breakpoints on the lines that hold the code given.</summary>
    private async Task<DebugSession> LaunchCase(string run, params string[] code)
    {
        DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: [run])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([.. code.Select(Line)], Source).ConfigurationDoneAsync();
        return host;
    }
}

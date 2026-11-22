using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class AsyncStepTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>MyAsyncClass.MyMethodAsync's first line, its awaits on the next four, the async void call on the fifth.</summary>
    private static readonly int Body = Debuggees.Line("DebuggableConsoleApp", "MyAsyncClass.cs", "MyMethodAsync(int myParam)") + 2;

    /// <summary>The opening braces of AnotherClass's async methods, where a step into one stops first.</summary>
    private static readonly int AnotherMethodOpened = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "AnotherMethodAsync()") + 1;
    private static readonly int AsyncVoidOpened = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "AsyncVoidMethod()") + 1;

    /// <summary>RuntimeAsyncConsoleApp's program compiled with state machines, a class each in its Debug build.</summary>
    private const string StateMachines = "StateMachineAsyncConsoleApp";
    private static readonly int InnerAwaited = StateMachinesLine("sum += await InnerAsync(i);");
    private static readonly int BeforeSet = StateMachinesLine("int before = i;");
    /// <summary>ThrowsLaterAsync's first line, an await that always yields, and its next.</summary>
    private static readonly int YieldAwaited = StateMachinesLine("static async Task ThrowsLaterAsync(int depth)") + 2;
    private static readonly int Deeper = YieldAwaited + 1;

    /// <summary>Steps through an async method stop at each of its lines in turn; a step out whose ask of its builder,
    /// or a step over an await whose question of the run that went on, is held unfinished stops in the caller or at
    /// the next line once that evaluation returns.</summary>
    [Fact]
    public async Task Steps_through_an_async_method_stop_at_each_line_and_one_held_unfinished_goes_on_once_it_returns()
    {
        await Flow.StepApartAsync<AsyncStepTests>("steps through an async method stop at each of its lines in turn", () => new AsyncStepTests(testOutputHelper), t => t.Steps_through_an_async_method_stop_at_each_of_its_lines_in_turn());
        await Flow.StepApartAsync<AsyncStepTests>("a step out whose ask of its builder is held unfinished stops in the caller once that evaluation returns", () => new AsyncStepTests(testOutputHelper), t => t.A_step_out_whose_ask_of_its_builder_is_held_unfinished_stops_in_the_caller_once_that_evaluation_returns());
        await Flow.StepApartAsync<AsyncStepTests>("a step over an await whose question of the run that went on is held unfinished stops at the next line once it returns", () => new AsyncStepTests(testOutputHelper), t => t.A_step_over_an_await_whose_question_of_the_run_that_went_on_is_held_unfinished_stops_at_the_next_line_once_it_returns());
    }

    private async Task Steps_through_an_async_method_stop_at_each_of_its_lines_in_turn()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync(Body, Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo.filePath);
        Assert.Equal(Body, stopInfo.line);

        await debugProtocolHost.ClearBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"));

        StoppedEvent stoppedEvent2 = await debugProtocolHost.StepOverAsync(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo2.filePath);
        Assert.Equal(Body + 1, stopInfo2.line);

        StoppedEvent stoppedEvent3 = await debugProtocolHost.StepOverAsync(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo3.filePath);
        Assert.Equal(Body + 2, stopInfo3.line);

        StoppedEvent stoppedEvent4 = await debugProtocolHost.StepOverAsync(stoppedEvent3.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo4 = stoppedEvent4.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo4.filePath);
        Assert.Equal(Body + 3, stopInfo4.line);

        // stoppedEvent4's thread, since the thread may have changed after the await.
        StoppedEvent stoppedEvent5 = await debugProtocolHost.StepOverAsync(stoppedEvent4.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo5 = stoppedEvent5.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo5.filePath);
        Assert.Equal(Body + 4, stopInfo5.line);

        StoppedEvent stoppedEvent6 = await debugProtocolHost.StepInAsync(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo6 = stoppedEvent6.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo6.filePath);
        Assert.Equal(AnotherMethodOpened, stopInfo6.line);

        StoppedEvent stoppedEvent7 = await debugProtocolHost.StepInAsync(stoppedEvent6.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo7 = stoppedEvent7.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo7.filePath);
        Assert.Equal(AnotherMethodOpened + 1, stopInfo7.line);

        StoppedEvent stoppedEvent8 = await debugProtocolHost.StepOutAsync(stoppedEvent7.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo8 = stoppedEvent8.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo8.filePath);
        Assert.Equal(Body + 5, stopInfo8.line);

        StoppedEvent stoppedEvent10 = await debugProtocolHost.StepInAsync(stoppedEvent8.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo10 = stoppedEvent10.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo10.filePath);
        Assert.Equal(AsyncVoidOpened, stopInfo10.line);

        StoppedEvent stoppedEvent11 = await debugProtocolHost.StepOutAsync(stoppedEvent10.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo11 = stoppedEvent11.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo11.filePath);
        Assert.Equal(Body + 5, stopInfo11.line);
    }

    /// <summary>A step out of an async method whose ask of its builder, to tell of the method's end, is held unfinished, as
    /// one waiting on what a held thread has is: the step waits for that evaluation and, once it returns, goes on as a plain
    /// step out, stopping in the caller where the method returned to it, never left to the evaluation (#2029).</summary>
    private async Task A_step_out_whose_ask_of_its_builder_is_held_unfinished_stops_in_the_caller_once_that_evaluation_returns()
    {
        await using DebugSession host = await LaunchHoldingAsyncSteps(BeforeSet);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        await host.ClearBreakpointsAsync(Debuggees.Source("RuntimeAsyncConsoleApp", "Program.cs"));
        Task<(StoppedEvent? Stop, string Output)> stepped = host.StepOutAsync(stop.ThreadId!.Value).WaitForStopOrEnd();
        await WaitsForTheEvaluation(host, "the step out's ask of its builder");
        await host.RequestAsync(DapCommands.Input, new InputArguments("x", Close: true));
        (StoppedEvent? after, string written) = await stepped;
        Assert.True(after is { Reason: StopReasons.Step } && after.Line == InnerAwaited,
            $"Once its builder's held ask returned, the step out of InnerAsync {(after is null ? $"never stopped, the program running to its end:\n{written}" : $"stopped for {after.Reason} at line {after.Line}")}, "
            + $"not in its caller at line {InnerAwaited}; the engine's log:\n{host.EngineLog()}");
        await RunsToItsEnd(host);
    }

    /// <summary>A step over an await whose question of which run went on after it is held unfinished on the thread the run
    /// goes on on: the step waits for that evaluation and, once it returns, goes on there to the next line, never left to
    /// the evaluation (#2029). The await is Task.Yield's, which always leaves the method: a delay may end before its await
    /// looks, the method then going on with no run to tell (#2078).</summary>
    private async Task A_step_over_an_await_whose_question_of_the_run_that_went_on_is_held_unfinished_stops_at_the_next_line_once_it_returns()
    {
        await using DebugSession host = await LaunchHoldingAsyncSteps(YieldAwaited, ["throws"]);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        await host.ClearBreakpointsAsync(Debuggees.Source("RuntimeAsyncConsoleApp", "Program.cs"));
        Task<(StoppedEvent? Stop, string Output)> stepped = host.StepOverAsync(stop.ThreadId!.Value).WaitForStopOrEnd();
        await WaitsForTheEvaluation(host, "the question of which run resumed");
        await host.RequestAsync(DapCommands.Input, new InputArguments("x", Close: true));
        (StoppedEvent? after, string written) = await stepped;
        Assert.True(after is { Reason: StopReasons.Step } && after.Line == Deeper,
            $"Once the held question returned, the step over ThrowsLaterAsync's await {(after is null ? $"never stopped, the program running to its end:\n{written}" : $"stopped for {after.Reason} at line {after.Line}")}, "
            + $"not at its next line, {Deeper}; the engine's log:\n{host.EngineLog()}");
        Assert.Equal("2", Assert.Single(await host.LocalsAsync(after!), v => v.Name == "depth").Value);
        await RunsToItsEnd(host, "caught InvalidOperationException");
    }

    /// <summary>StateMachineAsyncConsoleApp launched to a breakpoint at a line, the async stepper's questions of the program
    /// each waiting for its input first (<see cref="AdapterEnvironment.HoldAsyncStepVariable"/>).</summary>
    private async Task<DebugSession> LaunchHoldingAsyncSteps(int line, IReadOnlyList<string>? arguments = null)
    {
        DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: new Dictionary<string, string> { [AdapterEnvironment.HoldAsyncStepVariable] = "1" });
        try
        {
            await host.InitializeAsync().LaunchAsync(Debuggees.Program(StateMachines), stopAtEntry: false, justMyCode: true, arguments).WaitForInitializedEvent();
            await host.BreakpointsAsync(line, Debuggees.Source("RuntimeAsyncConsoleApp", "Program.cs")).ConfigurationDoneAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>Waits until the async stepper's held question (<paramref name="held"/>) is left running in the program.</summary>
    private static async Task WaitsForTheEvaluation(DebugSession host, string held)
    {
        Assert.True(await Waiting.UntilAsync(() => host.EngineLog() is var log && log.Contains($"Held: {held}", StringComparison.Ordinal) && log.Contains("is left to run on", StringComparison.Ordinal),
            TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100)),
            $"{held} was never held unfinished:\n{host.EngineLog()}");
    }

    /// <summary>The program let go runs on to its end, every value it computed as it would have, writing what it ends with.</summary>
    private static async Task RunsToItsEnd(DebugSession host, string ending = "total 10")
    {
        (string output, int? exitCode) = await host.ContinueAsync().WaitForEnd();
        Assert.True(output.Contains(ending, StringComparison.Ordinal) && exitCode == 0,
            $"Let go, the program did not run on to its end as it would have (exit code {exitCode}):\n{output}");
    }

    private static int StateMachinesLine(string code) => Debuggees.Line("RuntimeAsyncConsoleApp", code);
}

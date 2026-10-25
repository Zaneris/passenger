using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class StepTests(ITestOutputHelper testOutputHelper)
{
    private const string Looping = "LoopingConsoleApp";
    private static readonly int LogWritten = Debuggees.Line("DebuggableConsoleApp", "MyClass.cs", "Console.WriteLine($\"Log_MyMethod\");");
    private static readonly int StaticCalled = Debuggees.Line("DebuggableConsoleApp", "MyClass.cs", "AnotherClass.MyStaticMethod();");
    private static readonly int AnotherVarSet = Debuggees.Line("DebuggableConsoleApp", "MyClass.cs", "string anotherVar = \"asdf\";");
    private static readonly int StaticOpened = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "public static List<int> MyStaticMethod()") + 1;
    private static readonly int TestSet = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "int test = 4;");
    private static readonly int ListMade = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "List<int> myList = [1, 4, 8, 11];");
    private static readonly int ListCopied = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "List<int> myResult2 = myList.ToList();");
    private static readonly int Returned = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "return myResult;");
    private static readonly int MyMethodCalled = Debuggees.Line("DebuggableConsoleApp", "myClass.MyMethod(13, 6);");
    private static readonly int NoMembersCalled = Debuggees.Line("DebuggableConsoleApp", "myClassNoMembers.MyMethod(42);");
    private static readonly int HitConditionCalled = Debuggees.Line("DebuggableConsoleApp", "hitConditionClass.Test();");
    private static readonly int Operated = Debuggees.Line("DebuggableConsoleApp", "MultilineSwitchInMethodCall.cs", "Result result = DoOperation();");
    private static readonly int Switched = Debuggees.Line("DebuggableConsoleApp", "MultilineSwitchInMethodCall.cs", "HandleOperationResult(result switch");

    /// <summary>Just My Code on, stopped in MyClass.MyMethod: a step of a thread the program does not have is refused and
    /// the thread that stopped steps on; a frame or value from before a step is refused; Step Into, Out and Over land where
    /// they should on the thread that stopped, a breakpoint inside a call stepped over stopping there, and a Step Into
    /// landing in code without source going on to the next user code.</summary>
    [Fact]
    public async Task Step_Into_Over_and_Out_each_stop_where_the_code_goes_next()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(StaticCalled)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent first = await debugProtocolHost.WaitForStoppedEvent();
        DapException refused = Assert.Throws<DapException>(() => debugProtocolHost.WithStepOverRequest(999_999));
        Assert.Contains("no thread 999999", refused.Reason, StringComparison.Ordinal);
        DapException refusedOut = Assert.Throws<DapException>(() => debugProtocolHost.WithStepOutRequest(999_999));
        Assert.Contains("no thread 999999", refusedOut.Reason, StringComparison.Ordinal);
        StoppedEvent steppedOn = await debugProtocolHost.WithStepOverRequest(first.ThreadId!.Value).WaitForStoppedEvent();
        Assert.Equal((StopReasons.Step, AnotherVarSet), (steppedOn.Reason, steppedOn.Line));

        StoppedEvent stoppedEvent = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(StaticCalled, stopInfo.line);
        int thread = stoppedEvent.ThreadId!.Value;
        DapStackFrame before = debugProtocolHost.GetTopStackFrame(thread);
        debugProtocolHost.WithScopesRequest(before.Id, out ScopesResponse scopes);
        int locals = scopes.Scopes.Single().VariablesReference;
        debugProtocolHost.WithVariablesRequest(locals, out List<DapVariable> variables);
        int structVar = variables.Single(v => v.Name == "structVar").VariablesReference;
        Assert.True(structVar > 0, "structVar has no members to open");

        StoppedEvent stoppedEvent2 = await debugProtocolHost
            .WithStepInRequest(stoppedEvent.ThreadId!.Value)
            .WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo2.filePath);
        Assert.Equal(StaticOpened, stopInfo2.line);

        // The frame, scope and values from before the step are refused.
        DapStackFrame after = debugProtocolHost.GetTopStackFrame(thread);
        debugProtocolHost.WithScopesRequest(after.Id, out ScopesResponse afterScopes);
        debugProtocolHost.WithVariablesRequest(afterScopes.Scopes.Single().VariablesReference, out List<DapVariable> now);
        Assert.Contains(now, v => v.Name == "myList");
        Assert.NotEqual(before.Id, after.Id);
        foreach (Action stale in new Action[]
        {
            () => debugProtocolHost.WithVariablesRequest(locals, out _),
            () => debugProtocolHost.WithVariablesRequest(structVar, out _),
            () => debugProtocolHost.WithScopesRequest(before.Id, out _),
            () => debugProtocolHost.WithEvaluateRequest(before.Id, "myInt", out _),
        })
        {
            DapException staleRefused = Assert.Throws<DapException>(stale);
            Assert.Contains("earlier stop", staleRefused.Reason, StringComparison.Ordinal);
        }

        StoppedEvent stoppedEvent3 = await debugProtocolHost
            .WithStepOutRequest(stoppedEvent.ThreadId!.Value)
            .WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo3.filePath);
        Assert.Equal(StaticCalled, stopInfo3.line);

        StoppedEvent stoppedEvent4 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo4 = stoppedEvent4.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo4.filePath);
        Assert.Equal(StaticCalled, stopInfo4.line);

        debugProtocolHost.WithBreakpointsRequest(TestSet, Debuggees.Source("DebuggableConsoleApp", "Namespace1", "AnotherClass.cs"));

        StoppedEvent stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo5 = stoppedEvent5.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo5.filePath);
        Assert.Equal(TestSet, stopInfo5.line);

        StoppedEvent stoppedEvent6 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo6 = stoppedEvent6.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo6.filePath);
        Assert.Equal(ListMade, stopInfo6.line);

        StoppedEvent stoppedEvent7 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo7 = stoppedEvent7.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo7.filePath);
        Assert.Equal(StaticCalled, stopInfo7.line);

        // A line whose Step Into goes into code without source.
        debugProtocolHost.WithBreakpointsRequest(ListCopied, Debuggees.Source("DebuggableConsoleApp", "Namespace1", "AnotherClass.cs"));
        StoppedEvent stoppedEvent8 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo8 = stoppedEvent8.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo8.filePath);
        Assert.Equal(ListCopied, stopInfo8.line);

        StoppedEvent stoppedEvent9 = await debugProtocolHost.WithStepInRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo9 = stoppedEvent9.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo9.filePath);
        Assert.Equal(Returned, stopInfo9.line);

        List<int> threadIds = [first.ThreadId!.Value, steppedOn.ThreadId!.Value, stoppedEvent.ThreadId!.Value, stoppedEvent2.ThreadId!.Value, stoppedEvent3.ThreadId!.Value, stoppedEvent4.ThreadId!.Value, stoppedEvent5.ThreadId!.Value, stoppedEvent6.ThreadId!.Value, stoppedEvent7.ThreadId!.Value, stoppedEvent8.ThreadId!.Value, stoppedEvent9.ThreadId!.Value];
        Assert.Single(threadIds.Distinct());
    }

    /// <summary>Just My Code off: a step over onto a line with a breakpoint stops there once, and the next goes on to the
    /// line after; a breakpoint on a multi-line switch expression is stopped at; and Step Into a module without symbols
    /// stops in its decompiled source.</summary>
    [Fact]
    public async Task A_step_onto_a_breakpoints_line_stops_once_a_switch_expression_is_stopped_in_and_a_step_into_code_without_symbols_shows_it_decompiled()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id, false)
            .WaitForInitializedEvent();
        string program = Debuggees.Source("DebuggableConsoleApp", "Program.cs");
        debugProtocolHost
            .WithBreakpointsRequest([MyMethodCalled, NoMembersCalled], program)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("Program.cs", stopInfo.filePath);
        Assert.Equal(MyMethodCalled, stopInfo.line);

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("Program.cs", stopInfo2.filePath);
        Assert.Equal(NoMembersCalled, stopInfo2.line);

        StoppedEvent stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("Program.cs", stopInfo3.filePath);
        Assert.Equal(HitConditionCalled, stopInfo3.line);

        debugProtocolHost
            .WithClearBreakpointsRequest(program)
            .WithBreakpointsRequest([Operated, Switched], Debuggees.Source("DebuggableConsoleApp", "MultilineSwitchInMethodCall.cs"));
        StoppedEvent switched = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        Assert.EndsWith("MultilineSwitchInMethodCall.cs", switched.ReadStopInfo().filePath);
        Assert.Equal(Operated, switched.ReadStopInfo().line);
        switched = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        Assert.EndsWith("MultilineSwitchInMethodCall.cs", switched.ReadStopInfo().filePath);
        Assert.Equal(Switched, switched.ReadStopInfo().line);

        // Console.WriteLine ships no symbols.
        debugProtocolHost
            .WithClearBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MultilineSwitchInMethodCall.cs"))
            .WithBreakpointsRequest(LogWritten);
        StoppedEvent logged = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        Assert.EndsWith("MyClass.cs", logged.ReadStopInfo().filePath);
        Assert.Equal(LogWritten, logged.ReadStopInfo().line);
        StoppedEvent decompiled = await debugProtocolHost.WithStepInRequest(logged.ThreadId!.Value).WaitForStoppedEvent();
        Assert.EndsWith("Console.cs", decompiled.ReadStopInfo().filePath);
        // A newer ICSharpCode.Decompiler may move the decompiled code's line.
        Assert.Equal(1219, decompiled.ReadStopInfo().line);
    }

    [Fact]
    public async Task A_step_over_goes_on_past_another_threads_breakpoint_whose_condition_is_false()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "step", "step over the call while another thread passes", "a step lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "passed by another thread"), Condition: "pass < 0"));
        AssertStepEnded(await host.WaitForStoppedEvent(), "the call's step ends here");
    }

    [Fact]
    public async Task A_step_over_goes_on_past_another_threads_tracepoint_which_logs_meanwhile()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "step", "step over the call while another thread passes", "a step lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "passed by another thread"), LogMessage: "pass {pass}"));
        await host.WaitForEvent<OutputEvent>(output => output.Output.StartsWith("pass ", StringComparison.Ordinal));
        AssertStepEnded(await host.WaitForStoppedEvent(), "the call's step ends here");
    }

    [Fact]
    public async Task A_step_over_a_call_goes_on_past_a_breakpoint_inside_it_whose_condition_is_false()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "step", "step over the call while another thread passes", "a step lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "wait for the other thread"), Condition: "times == 0"));
        AssertStepEnded(await host.WaitForStoppedEvent(), "the call's step ends here");
    }

    [Fact]
    public async Task A_step_over_an_await_goes_on_past_another_threads_breakpoint_whose_condition_is_false()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "await", "step over the await while another thread passes", "a step over the await lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "passed by another thread"), Condition: "pass < 0"));
        AssertStepEnded(await host.WaitForStoppedEvent(), "the await's step ends here");
    }

    [Fact]
    public async Task A_step_over_an_await_stops_at_its_next_line_though_asking_the_builder_which_run_resumed_would_wait_on_a_paused_thread()
    {
        // The debuggee's builder answers which run resumed only once the passing thread has passed again.
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "waiting", "step over the await whose builder waits", "a step over the waiting builder's await lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "passed by another thread"), Condition: "pass < 0"));
        AssertStepEnded(await host.WaitForStoppedEvent(), "the waiting builder's step ends here");
    }

    [Fact]
    public async Task A_step_over_an_await_stops_at_its_next_line_though_asking_the_builder_which_run_leaves_would_wait_on_a_paused_thread()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "waiting first", "step over the await whose builder waits", "a step over the waiting builder's await lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "passed by another thread"), Condition: "pass < 0"));
        AssertStepEnded(await host.WaitForStoppedEvent(), "the waiting builder's step ends here");
    }

    [Fact]
    public async Task A_step_over_an_await_whose_method_goes_on_on_another_thread_shows_what_the_statement_called_there_returned()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await StepOverWhileAnotherThreadPasses(host, "elsewhere", "step over the await that goes on elsewhere",
            "a step over the await that goes on elsewhere lost runs on to here",
            new DapSourceBreakpoint(Debuggees.Line(Looping, "passed by another thread"), Condition: "pass < 0"));
        StoppedEvent stepped = await host.WaitForStoppedEvent();
        AssertStepEnded(stepped, "the step over the await that goes on elsewhere ends here");
        Assert.True(host.Locals(stepped).Any(v => (v.Name, v.Value) == ("Say() returned", "\"said elsewhere\"")),
            "Variables leaves out what the statement's call after the await returned, on the thread the method went on on");
    }

    [Fact]
    public async Task A_step_on_one_thread_and_another_threads_breakpoint_hit_as_it_ends_both_stop()
    {
        // Each turn the step's end and the racing thread's hit come together, either first.
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (int letGo, int stepped, int passed) = RaceLines;
        StoppedEvent stop = await LaunchRace(host, [new DapSourceBreakpoint(letGo), new DapSourceBreakpoint(passed)]);
        int stepping = stop.ThreadId!.Value;
        int both = 0;
        for (int turn = 1; turn <= RaceTurns; turn++)
        {
            Assert.Equal((StopReasons.Breakpoint, letGo, stepping), (stop.Reason, stop.Line, stop.ThreadId));
            List<StoppedEvent> stops = [await host.WithStepOverRequest(stepping).WaitForStoppedEvent()];
            while ((stop = await host.WithContinueRequest().WaitForStoppedEvent()).Line != letGo)
            {
                stops.Add(stop);
            }
            string seen = string.Join(", ", stops.Select(s => $"{s.Reason} at line {s.Line} on thread {s.ThreadId}"));
            Assert.True(stops.Count(s => (s.Reason, s.Line) == (StopReasons.Breakpoint, passed)) == 1,
                $"Turn {turn}: the racing thread's breakpoint was passed over as the step ended on the other thread; the stops were {seen}");
            bool stepStopped = stops.Any(s => (s.Reason, s.Line, s.ThreadId) == (StopReasons.Step, stepped, stepping));
            Assert.True(stops[0].Line == passed || stepStopped,
                $"Turn {turn}: the step was lost to the racing thread's breakpoint that stopped after it ended; the stops were {seen}");
            Assert.True(!stepStopped || stops[0].Reason == StopReasons.Step,
                $"Turn {turn}: the racing thread's breakpoint stopped before the step that had already ended; the stops were {seen}");
            both += stepStopped ? 1 : 0;
        }
        testOutputHelper.WriteLine($"Both stops were seen in {both} of {RaceTurns} turns");
    }

    [Fact]
    public async Task A_step_that_ends_while_another_threads_exception_breakpoint_tries_its_condition_stops_once_the_condition_is_tried()
    {
        // Each turn the racing thread's exception finds its handler before, as or after the step ends.
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (int letGo, int stepped, _) = RaceLines;
        DapExceptionOptions raced = new([new DapExceptionPathSegment(["CLR"]), new DapExceptionPathSegment(["RaceException"])], "always")
        {
            Condition = "$exception.Message.Length < 0",
        };
        StoppedEvent stop = await LaunchRace(host, [new DapSourceBreakpoint(letGo)], raced);
        int stepping = stop.ThreadId!.Value;
        for (int turn = 1; turn <= ThrowingTurns; turn++)
        {
            Assert.Equal((StopReasons.Breakpoint, letGo, stepping), (stop.Reason, stop.Line, stop.ThreadId));
            StoppedEvent step = await host.WithStepOverRequest(stepping).WaitForStoppedEvent();
            Assert.True((step.Reason, step.Line, step.ThreadId) == (StopReasons.Step, stepped, stepping),
                $"Turn {turn}: the step over the call stopped for {step.Reason} at line {step.Line} on thread {step.ThreadId}, not at line {stepped}");
            stop = await host.WithContinueRequest().WaitForStoppedEvent();
            Assert.True(stop.Line == letGo,
                $"Turn {turn}: the racing thread's exception stopped the program ({stop.Reason} at line {stop.Line} on thread {stop.ThreadId}), its "
                + "condition never holding: it could not be tried, the step's end having stopped the program while its getter ran");
        }
    }

    /// <summary>How many times a race between a step's end and another thread's breakpoint is run: each turn goes either way.</summary>
    private const int RaceTurns = 30;

    /// <summary>The throwing race's turns: its forty lengths of wait twice over, so the exception's handler is found before,
    /// as and after the step ends.</summary>
    private const int ThrowingTurns = 80;

    /// <summary>LoopingConsoleApp's lines in its race: where the stepping thread lets the racing one go, where the step over
    /// that ends, and the line the racing thread passes once let go.</summary>
    private static (int LetGo, int Stepped, int Passed) RaceLines => (Debuggees.Line(Looping, "step over the call that lets the racing thread go"),
        Debuggees.Line(Looping, "the step over the call that lets it go ends here"), Debuggees.Line(Looping, "passed by the racing thread"));

    /// <summary>LoopingConsoleApp launched racing with the breakpoints given, and throwing with the exception breakpoint when
    /// one is given; its first stop, the stepping thread at the line where it lets the racing thread go.</summary>
    private static async Task<StoppedEvent> LaunchRace(DebugSession host, List<DapSourceBreakpoint> breakpoints, DapExceptionOptions? exceptions = null)
    {
        string program = Debuggees.Program(Looping);
        host.WithInitializeRequest().Request(DapCommands.Launch, new LaunchArguments
        {
            Program = program,
            Args = exceptions is null ? ["race"] : ["race", "throws"],
            Cwd = Path.GetDirectoryName(program),
            Console = "internalConsole",
            JustMyCode = true,
            StopAtEntry = false,
        });
        await host.WaitForInitializedEvent();
        if (exceptions is not null)
        {
            host.Request(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], ExceptionOptions: [exceptions]));
        }
        host.WithBreakpointsRequest(Debuggees.Source(Looping, "Program.cs"), breakpoints).WithConfigurationDoneRequest();
        return await host.WaitForStoppedEvent();
    }

    /// <summary>LoopingConsoleApp launched in a mode (its arguments, by spaces) where its first thread waits for a second to
    /// pass a line, stopped on the line that waits and stepping over it, with the other breakpoint given and one where a
    /// step lost runs on to.</summary>
    private static async Task StepOverWhileAnotherThreadPasses(DebugSession host, string mode, string from, string runsOnTo, DapSourceBreakpoint other)
    {
        string program = Debuggees.Program(Looping);
        host.WithInitializeRequest().Request(DapCommands.Launch, new LaunchArguments
        {
            Program = program,
            Args = [.. mode.Split(' ')],
            Cwd = Path.GetDirectoryName(program),
            Console = "internalConsole",
            JustMyCode = true,
            StopAtEntry = false,
        });
        await host.WaitForInitializedEvent();
        host.WithBreakpointsRequest(Debuggees.Source(Looping, "Program.cs"),
            [new DapSourceBreakpoint(Debuggees.Line(Looping, from)), other, new DapSourceBreakpoint(Debuggees.Line(Looping, runsOnTo))])
            .WithConfigurationDoneRequest();
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        Assert.Equal(Debuggees.Line(Looping, from), stopped.Line);
        host.WithStepOverRequest(stopped.ThreadId!.Value);
    }

    private static void AssertStepEnded(StoppedEvent stepped, string at)
    {
        int line = Debuggees.Line(Looping, at);
        Assert.True(stepped.Reason == StopReasons.Step && stepped.Line == line,
            $"The step was lost to a breakpoint that did not stop, and the program ran on: it stopped for {stepped.Reason} at line {stepped.Line}, where the step would have stopped at line {line}");
    }
}

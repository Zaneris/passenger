using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class ConditionalBreakpointTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int LogWritten = Debuggees.Line("DebuggableConsoleApp", "MyClass.cs", "Console.WriteLine($\"Log_MyMethod\");");
    private static readonly int CountUp = Debuggees.Line("DebuggableConsoleApp", "HitConditionClass.cs", "_count++;");
    private static readonly int AfterCount = Debuggees.Line("DebuggableConsoleApp", "HitConditionClass.cs", "// breakpoint here");
    private static readonly int TestEnd = AfterCount + 1;

    [Gate]
    [Fact]
    public async Task A_condition_with_a_lambda_is_refused_saying_why()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse? response = await debugProtocolHost.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [new DapSourceBreakpoint(LogWritten, Condition: "_intList.Any(value => value == 4)")]);

        DapBreakpoint breakpoint = Assert.Single(response.Breakpoints);
        Assert.False(breakpoint.Verified);
        Assert.Equal("Lambda expressions are not supported in breakpoint conditions.", breakpoint.Message);
    }

    /// <summary>A condition nested past what the parser reads (interpolations within interpolations, each a level of a lexer
    /// that checks no stack) is refused when set, saying so, where reading it ended the adapter; the program then runs to a
    /// breakpoint after it, the refused one never bound.</summary>
    [Fact]
    public async Task A_condition_nested_past_what_the_parser_reads_is_refused_saying_so_and_the_adapter_runs_on()
    {
        string deep = string.Concat(Enumerable.Repeat("$\"{", 20_000)) + "0" + string.Concat(Enumerable.Repeat("}\"", 20_000)) + " == \"0\"";
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse response = await host.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [new DapSourceBreakpoint(LogWritten, Condition: deep), new DapSourceBreakpoint(Debuggees.MyMethodEnd)]);
        DapBreakpoint refused = response.Breakpoints[0];
        Assert.True(!refused.Verified && refused.Message == "The condition nests deeper than the debugger reads.",
            $"A condition too deep to read was taken, or refused saying something else: {refused.Message}");

        await host.ConfigurationDoneAsync().ResumeRuntimeAsync(debuggee.Id, startSuspended);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Debuggees.MyMethodEnd,
            $"The program stopped at line {stop.ReadStopInfo().line} of {stop.ReadStopInfo().filePath} before MyMethod's end: the refused breakpoint was bound");
    }

    /// <summary>A lambda condition is refused when set on a function breakpoint and on a data breakpoint, as on a line's,
    /// rather than stopping at every hit saying it could not be told: the program runs past the function breakpoint to the
    /// line breakpoint after it.</summary>
    [Fact]
    public async Task A_function_or_data_breakpoint_with_a_lambda_condition_is_refused_saying_why()
    {
        const string Lambda = "_intList.Any(value => value == 4)";
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        SetBreakpointsResponse functions = await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.MyClass.MyMethod", Condition: Lambda)]));
        SetDataBreakpointsResponse data = await host.RequestAsync<SetDataBreakpointsResponse>(DapCommands.SetDataBreakpoints,
            new SetDataBreakpointsArguments([new DapDataBreakpoint("data:1", "write", Condition: Lambda)]));
        foreach ((string kind, DapBreakpoint refused) in new[] { ("function", Assert.Single(functions.Breakpoints)), ("data", Assert.Single(data.Breakpoints)) })
        {
            Assert.True(!refused.Verified && refused.Reason == BreakpointReasons.Failed, $"The {kind} breakpoint's lambda condition was taken, to fail at every hit: {refused.Message}");
            Assert.Equal("Lambda expressions are not supported in breakpoint conditions.", refused.Message);
        }

        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(Debuggees.MyMethodEnd)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);
        StoppedEvent first = await host.WaitForStoppedEvent();
        Assert.True(first.ReadStopInfo().line == Debuggees.MyMethodEnd,
            $"The program stopped at line {first.ReadStopInfo().line} of {first.ReadStopInfo().filePath} before MyMethod's end: the refused function breakpoint was bound");
    }

    [Gate]
    [Fact]
    public async Task A_tracepoint_writes_its_message_with_the_values_in_it_and_never_stops()
    {
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [
                new DapSourceBreakpoint(LogWritten, LogMessage: "myInt is {myInt}, and the list has {_intList.Count} \\{literally\\}"),
                new DapSourceBreakpoint(Debuggees.MyMethodEnd),
            ])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);

        OutputEvent logged = await host.WaitForEvent<OutputEvent>(o => o.Output.StartsWith("myInt is", StringComparison.Ordinal));
        Assert.Equal("myInt is 4, and the list has 4 {literally}\n", logged.Output);
        Assert.Equal(DapOutputCategories.Console, logged.Category);
        // The program went on past the tracepoint, to the breakpoint after it.
        Assert.Equal(Debuggees.MyMethodEnd, (await host.WaitForStoppedEvent()).ReadStopInfo().line);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_whose_condition_holds_stops()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(LogWritten, Condition: "myInt == 4")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(LogWritten, stopInfo.line);
    }

    [Gate]
    [Theory]
    [InlineData("noSuchName")]
    [InlineData("myInt")]
    public async Task A_breakpoint_whose_condition_cannot_be_told_stops_and_says_why(string condition)
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse response = await debugProtocolHost.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
                [
                    new DapSourceBreakpoint(LogWritten, Condition: condition),
                    new DapSourceBreakpoint(Debuggees.MyMethodEnd)
                ]);
        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // An unknown name, or a condition that is not a bool, stops rather than going missing unseen.
        int id = response.Breakpoints[0].Id!.Value;
        BreakpointEvent said = await debugProtocolHost.WaitForEvent<BreakpointEvent>(e => e.Breakpoint.Id == id && e.Breakpoint.Message is not null && e.Breakpoint.Verified);
        Assert.StartsWith(BreakpointConditions.NotEvaluated(condition), said.Breakpoint.Message, StringComparison.Ordinal);
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal(LogWritten, stoppedEvent.ReadStopInfo().line);
        Assert.Equal([id], stoppedEvent.HitBreakpointIds);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoint_whose_condition_fails_never_stops()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
                [
                    new DapSourceBreakpoint(LogWritten, Condition: "myInt == 999"),
                    new DapSourceBreakpoint(Debuggees.MyMethodEnd)
                ])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // Should skip the conditional breakpoint (false condition) and hit the unconditional one after it
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(Debuggees.MyMethodEnd, stopInfo.line);
    }

    /// <summary>Equal to, as Rider's mode writes it, as a number alone, and as Visual Studio's "=".</summary>
    [Gate]
    [Theory]
    [InlineData("==2")]
    [InlineData("2")]
    [InlineData("= 2")]
    public async Task A_hit_count_of_N_stops_on_the_Nth_hit_alone(string hitCondition)
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: hitCondition)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // Should stop on 2nd hit, not 1st
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_count");
        Assert.Equal("2", evaluateResponse.Result);
    }

    [Gate]
    [Fact]
    public async Task A_breakpoints_hit_count_and_id_outlast_its_files_breakpoints_being_sent_again()
    {
        bool startSuspended = true;
        string file = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        SetBreakpointsResponse first = await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5")]);
        await host
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);

        // The third pass stops at the count, with the breakpoint after it two of its five hits in.
        StoppedEvent third = await host.WaitForStoppedEvent();
        Assert.Equal(CountUp, third.ReadStopInfo().line);
        Assert.Equal("2", await Count(host, third));

        // A breakpoint set in the file, as a click in the gutter or Run to Cursor does, then taken away again with the count's.
        SetBreakpointsResponse added = await host.SetBreakpointsAsync(file,
            [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5"), new DapSourceBreakpoint(TestEnd)]);
        StoppedEvent after = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal(TestEnd, after.ReadStopInfo().line);
        SetBreakpointsResponse removed = await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(AfterCount, HitCondition: "==5")]);

        // The one after the count counted on through both: it stops on the fifth pass, not five passes after the last of them.
        StoppedEvent fifth = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal(AfterCount, fifth.ReadStopInfo().line);
        Assert.Equal("5", await Count(host, fifth));
        Assert.Equal(first.Breakpoints[1].Id, added.Breakpoints[1].Id);
        Assert.Equal(first.Breakpoints[1].Id, removed.Breakpoints[0].Id);
        Assert.Equal(first.Breakpoints[0].Id, added.Breakpoints[0].Id);
        Assert.True(added.Breakpoints[2].Verified, "the breakpoint added while paused binds");
    }

    /// <summary>A breakpoint sent again at its line held for Run to Cursor, then as it was: it stops on the hit it reaches
    /// meanwhile, whatever its hit count says, and counts on from there to the hit its count names.</summary>
    [Fact]
    public async Task A_breakpoint_held_for_Run_to_Cursor_stops_at_its_next_hit_and_counts_on_from_its_hits()
    {
        bool startSuspended = true;
        string file = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5")]);
        await host
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);
        StoppedEvent third = await host.WaitForStoppedEvent();
        Assert.Equal((CountUp, "2"), (third.ReadStopInfo().line, await Count(host, third)));

        await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5", RunTo: true)]);
        StoppedEvent reached = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.True((AfterCount, "3") == (reached.ReadStopInfo().line, await Count(host, reached)),
            $"Run to Cursor's hold of the breakpoint stopped at line {reached.ReadStopInfo().line}, the count {await Count(host, reached)}, not at its next hit, the third");
        await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5")]);

        // Its fifth hit, not five after it was sent back.
        StoppedEvent fifth = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal((AfterCount, "5"), (fifth.ReadStopInfo().line, await Count(host, fifth)));
    }

    /// <summary>A hit count counts only hits whose condition held, through an edit of its message: _count &gt; 10 and ==4 stop
    /// at _count 14, as every debugger counts (GDScript's and the browser's tests stop alike), not on the twentieth pass, where
    /// one counting every hit would. The edited message logs \{ and \} as braces, a string quoted unless ,nq and a whole
    /// number in hexadecimal under ,h, as every debugger does; one writing {{ is refused saying how to write a brace.</summary>
    [Fact]
    public async Task A_hit_count_counts_the_hits_its_condition_held_on_through_an_edit_of_its_message_logged_as_every_debugger_logs_it()
    {
        bool startSuspended = true;
        string file = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        DapSourceBreakpoint counted = new(AfterCount, Condition: "_count > 10", HitCondition: "==4", LogMessage: "first {_count}", Suspend: true);
        DapSourceBreakpoint missed = new(TestEnd, Condition: "_count == 20", LogMessage: "missed {_count}", Suspend: true);
        await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, Condition: "_count == 12"), counted, missed]);
        await host
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);
        StoppedEvent paused = await host.WaitForStoppedEvent();
        Assert.Equal((CountUp, "12"), (paused.ReadStopInfo().line, await Count(host, paused)));

        SetBreakpointsResponse edited = await host.SetBreakpointsAsync(file, [counted with { LogMessage = "second \\{{_count}\\} {_name} {_name,nq} {_count,h}" }, missed,
            new DapSourceBreakpoint(CountUp, LogMessage: "{{_count}}")]);
        DapBreakpoint doubled = edited.Breakpoints[2];
        Assert.True(!doubled.Verified && doubled.Message == DisplayFormat.Misread("{{_count}}"),
            $"The tracepoint writing a brace as {{{{ was taken, to log what it was not meant to, or refused saying something else: {doubled.Message}");
        await host.ContinueAsync();
        OutputEvent logged = await host.WaitForEvent<OutputEvent>(o => o.Output.StartsWith("second", StringComparison.Ordinal) || o.Output.StartsWith("first", StringComparison.Ordinal)
            || o.Output.StartsWith("missed", StringComparison.Ordinal));
        StoppedEvent stopped = await host.WaitForStoppedEvent();
        (int line, string count) at = (stopped.ReadStopInfo().line, await Count(host, stopped));
        Assert.True(at == (AfterCount, "14") && logged.Output == "second {14} \"Ada\" Ada 0x0000000e\n",
            $"The breakpoint counting where _count > 10 stopped at line {at.line} with _count {at.count}, saying {logged.Output.Trim()}, not on its fourth such hit at line {AfterCount} with _count 14, saying second {{14}} \"Ada\" Ada 0x0000000e");
    }

    /// <summary>A breakpoint whose hit count is edited counts again from the edit, as one whose condition or place is edited
    /// does: ==5 edited to ==2 after its second hit stops two hits later, on the fourth pass.</summary>
    [Fact]
    public async Task A_breakpoint_whose_hit_count_is_edited_counts_again_from_the_edit()
    {
        bool startSuspended = true;
        string file = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        DapSourceBreakpoint missed = new(TestEnd, Condition: "_count == 9");
        await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5"), missed]);
        await host
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);
        StoppedEvent third = await host.WaitForStoppedEvent();
        Assert.Equal((CountUp, "2"), (third.ReadStopInfo().line, await Count(host, third)));

        await host.SetBreakpointsAsync(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==2"), missed]);
        StoppedEvent stopped = await host.ContinueAsync().WaitForStoppedEvent();
        (int line, string count) at = (stopped.ReadStopInfo().line, await Count(host, stopped));
        Assert.True(at == (AfterCount, "4"),
            $"The breakpoint whose hit count was edited to ==2 stopped at line {at.line} with _count {at.count}, not on its second hit after the edit at line {AfterCount} with _count 4");
    }

    /// <summary>Hit counts the debugger cannot read, each set in turn with the program's code loaded, where one it took would
    /// bind at once: each is refused saying why, the breakpoint beside it binds, and the program never stops at it.</summary>
    [Gate]
    [Fact]
    public async Task A_hit_count_the_debugger_cannot_read_is_refused_saying_why()
    {
        bool startSuspended = true;
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .InitializeAsync()
            .AttachAsync(debuggee.Id)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(Debuggees.MyMethodEnd)])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(debuggee.Id, startSuspended);
        Assert.Equal(Debuggees.MyMethodEnd, (await host.WaitForStoppedEvent()).ReadStopInfo().line);

        foreach (string hitCondition in new[] { "3 times", "abc", "%0", "==" })
        {
            SetBreakpointsResponse response = await host.SetBreakpointsAsync(Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"),
                [new DapSourceBreakpoint(AfterCount, HitCondition: hitCondition), new DapSourceBreakpoint(CountUp)]);
            DapBreakpoint refused = response.Breakpoints[0];
            Assert.False(refused.Verified, $"The hit count \"{hitCondition}\" was taken");
            Assert.Equal(BreakpointReasons.Failed, refused.Reason);
            Assert.Contains($"The hit count \"{hitCondition}\" is not one the debugger can read", refused.Message, StringComparison.Ordinal);
            Assert.True(response.Breakpoints[1].Verified, $"the breakpoint beside the hit count \"{hitCondition}\" binds");
        }

        // It never stops: from the count the program runs on round its loop to MyMethod's end.
        Assert.Equal(CountUp, (await host.ContinueAsync().WaitForStoppedEvent()).ReadStopInfo().line);
        StoppedEvent next = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.EndsWith("MyClass.cs", next.ReadStopInfo().filePath, StringComparison.Ordinal);
        Assert.Equal(Debuggees.MyMethodEnd, next.ReadStopInfo().line);
    }

    /// <summary>HitConditionClass's count of its passes, where the program stopped.</summary>
    private static async Task<string> Count(DebugSession host, StoppedEvent stop)
    {
        StackTraceResponse stack = await host.StackTraceAsync(stop.ThreadId!.Value);
        EvaluateResponse value = await host.EvaluateAsync(stack.StackFrames!.First().Id, "_count");
        return value.Result;
    }

    [Gate]
    [Fact]
    public async Task A_hit_count_of_at_least_N_stops_on_every_hit_from_the_Nth()
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: ">=2")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // First stop should be on 2nd iteration (hit count >= 2)
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_count");
        Assert.Equal("2", evaluateResponse.Result);

        // Continue - should stop again on 3rd iteration
        StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo2.filePath);
        Assert.Equal(AfterCount, stopInfo2.line);

        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        int stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId2, "_count");
        Assert.Equal("3", evaluateResponse2.Result);
    }

    [Gate]
    [Fact]
    public async Task A_hit_count_of_every_N_stops_on_every_Nth_hit()
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        await debugProtocolHost
            .BreakpointsAsync(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: "%2")])
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        // First stop should be on 2nd iteration (2 % 2 == 0)
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "_count");
        Assert.Equal("2", evaluateResponse.Result);

        // Continue - should skip 3rd, stop on 4th (4 % 2 == 0)
        StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo2.filePath);
        Assert.Equal(AfterCount, stopInfo2.line);

        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        int stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
        EvaluateResponse? evaluateResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId2, "_count");
        Assert.Equal("4", evaluateResponse2.Result);
    }

    /// <summary>A condition that reads a pattern's variable (boxed is int k &amp;&amp; …) stops only where it holds: never
    /// where it is false on every round, and on the one round where it is true, which the round then read says.</summary>
    [Fact]
    public async Task A_condition_reading_a_patterns_variable_stops_only_where_it_holds()
    {
        string source = Debuggees.Source("PassengerCases", "Program.cs");
        int never = Debuggees.Line("PassengerCases", "Console.WriteLine($\"reading round {round}\");");
        int once = Debuggees.Line("PassengerCases", "Console.WriteLine($\"read round {round}\");");
        int after = Debuggees.Line("PassengerCases", "Console.WriteLine(\"reading\");");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host
            .BreakpointsAsync(source,
            [
                new DapSourceBreakpoint(never, Condition: "boxed is int k && k > 100"),
                new DapSourceBreakpoint(once, Condition: "boxed is int k && k + round == 44"),
                new DapSourceBreakpoint(after),
            ])
            .ConfigurationDoneAsync();

        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == once,
            $"The program stopped at line {stop.ReadStopInfo().line} ({stop.Reason}) before its condition held on round 2 at line {once}: a pattern's variable read in a condition did not evaluate");
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("2", (await host.EvaluateAsync(frame, "round")).Result);
        Assert.Equal(after, (await host.ContinueAsync().WaitForStoppedEvent()).ReadStopInfo().line);
    }
}

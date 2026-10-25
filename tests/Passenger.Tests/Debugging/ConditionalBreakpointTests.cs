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

    [Fact]
    public async Task A_condition_with_a_lambda_is_refused_saying_why()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost.WithBreakpointsRequest(
            Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [new DapSourceBreakpoint(LogWritten, Condition: "_intList.Any(value => value == 4)")],
            out SetBreakpointsResponse? response);

        DapBreakpoint breakpoint = Assert.Single(response.Breakpoints);
        Assert.False(breakpoint.Verified);
        Assert.Equal("Lambda expressions are not supported in breakpoint conditions.", breakpoint.Message);
    }

    [Fact]
    public async Task A_tracepoint_writes_its_message_with_the_values_in_it_and_never_stops()
    {
        bool startSuspended = true;
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        host
            .WithBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
            [
                new DapSourceBreakpoint(LogWritten, LogMessage: "myInt is {myInt}, and the list has {_intList.Count} \\{literally\\}"),
                new DapSourceBreakpoint(Debuggees.MyMethodEnd),
            ])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(debuggee.Id, startSuspended);

        OutputEvent logged = await host.WaitForEvent<OutputEvent>(o => o.Output.StartsWith("myInt is", StringComparison.Ordinal));
        Assert.Equal("myInt is 4, and the list has 4 {literally}\n", logged.Output);
        Assert.Equal(DapOutputCategories.Console, logged.Category);
        // The program went on past the tracepoint, to the breakpoint after it.
        Assert.Equal(Debuggees.MyMethodEnd, (await host.WaitForStoppedEvent()).ReadStopInfo().line);
    }

    [Fact]
    public async Task A_breakpoint_whose_condition_holds_stops()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost
            .WithBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(LogWritten, Condition: "myInt == 4")])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(LogWritten, stopInfo.line);
    }

    [Theory]
    [InlineData("noSuchName")]
    [InlineData("myInt")]
    public async Task A_breakpoint_whose_condition_cannot_be_told_stops_and_says_why(string condition)
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost
            .WithBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
                [
                    new DapSourceBreakpoint(LogWritten, Condition: condition),
                    new DapSourceBreakpoint(Debuggees.MyMethodEnd)
                ], out SetBreakpointsResponse response)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // An unknown name, or a condition that is not a bool, stops rather than going missing unseen.
        int id = response.Breakpoints[0].Id!.Value;
        BreakpointEvent said = await debugProtocolHost.WaitForEvent<BreakpointEvent>(e => e.Breakpoint.Id == id && e.Breakpoint.Message is not null && e.Breakpoint.Verified);
        Assert.StartsWith(BreakpointConditions.NotEvaluated(condition), said.Breakpoint.Message, StringComparison.Ordinal);
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal(LogWritten, stoppedEvent.ReadStopInfo().line);
        Assert.Equal([id], stoppedEvent.HitBreakpointIds);
    }

    [Fact]
    public async Task A_breakpoint_whose_condition_fails_never_stops()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost
            .WithBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"),
                [
                    new DapSourceBreakpoint(LogWritten, Condition: "myInt == 999"),
                    new DapSourceBreakpoint(Debuggees.MyMethodEnd)
                ])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // Should skip the conditional breakpoint (false condition) and hit the unconditional one after it
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(Debuggees.MyMethodEnd, stopInfo.line);
    }

    /// <summary>Equal to, as Rider's mode writes it, as a number alone, and as Visual Studio's "=".</summary>
    [Theory]
    [InlineData("==2")]
    [InlineData("2")]
    [InlineData("= 2")]
    public async Task A_hit_count_of_N_stops_on_the_Nth_hit_alone(string hitCondition)
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost
            .WithBreakpointsRequest(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: hitCondition)])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // Should stop on 2nd hit, not 1st
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_count", out EvaluateResponse? evaluateResponse);
        Assert.Equal("2", evaluateResponse.Result);
    }

    [Fact]
    public async Task A_breakpoints_hit_count_and_id_outlast_its_files_breakpoints_being_sent_again()
    {
        bool startSuspended = true;
        string file = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        host
            .WithBreakpointsRequest(file, [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5")], out SetBreakpointsResponse first)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(debuggee.Id, startSuspended);

        // The third pass stops at the count, with the breakpoint after it two of its five hits in.
        StoppedEvent third = await host.WaitForStoppedEvent();
        Assert.Equal(CountUp, third.ReadStopInfo().line);
        Assert.Equal("2", Count(host, third));

        // A breakpoint set in the file, as a click in the gutter or Run to Cursor does, then taken away again with the count's.
        host.WithBreakpointsRequest(file,
            [new DapSourceBreakpoint(CountUp, HitCondition: "==3"), new DapSourceBreakpoint(AfterCount, HitCondition: "==5"), new DapSourceBreakpoint(TestEnd)],
            out SetBreakpointsResponse added);
        StoppedEvent after = await host.WithContinueRequest().WaitForStoppedEvent();
        Assert.Equal(TestEnd, after.ReadStopInfo().line);
        host.WithBreakpointsRequest(file, [new DapSourceBreakpoint(AfterCount, HitCondition: "==5")], out SetBreakpointsResponse removed);

        // The one after the count counted on through both: it stops on the fifth pass, not five passes after the last of them.
        StoppedEvent fifth = await host.WithContinueRequest().WaitForStoppedEvent();
        Assert.Equal(AfterCount, fifth.ReadStopInfo().line);
        Assert.Equal("5", Count(host, fifth));
        Assert.Equal(first.Breakpoints[1].Id, added.Breakpoints[1].Id);
        Assert.Equal(first.Breakpoints[1].Id, removed.Breakpoints[0].Id);
        Assert.Equal(first.Breakpoints[0].Id, added.Breakpoints[0].Id);
        Assert.True(added.Breakpoints[2].Verified, "the breakpoint added while paused binds");
    }

    /// <summary>Hit counts the debugger cannot read, each set in turn with the program's code loaded, where one it took would
    /// bind at once: each is refused saying why, the breakpoint beside it binds, and the program never stops at it.</summary>
    [Fact]
    public async Task A_hit_count_the_debugger_cannot_read_is_refused_saying_why()
    {
        bool startSuspended = true;
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process debuggee);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(debuggee.Id)
            .WaitForInitializedEvent();
        host
            .WithBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), [new DapSourceBreakpoint(Debuggees.MyMethodEnd)])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(debuggee.Id, startSuspended);
        Assert.Equal(Debuggees.MyMethodEnd, (await host.WaitForStoppedEvent()).ReadStopInfo().line);

        foreach (string hitCondition in new[] { "3 times", "abc", "%0", "==" })
        {
            host.WithBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs"),
                [new DapSourceBreakpoint(AfterCount, HitCondition: hitCondition), new DapSourceBreakpoint(CountUp)],
                out SetBreakpointsResponse response);
            DapBreakpoint refused = response.Breakpoints[0];
            Assert.False(refused.Verified, $"The hit count \"{hitCondition}\" was taken");
            Assert.Equal("failed", refused.Reason);
            Assert.Contains($"The hit count \"{hitCondition}\" is not one the debugger can read", refused.Message, StringComparison.Ordinal);
            Assert.True(response.Breakpoints[1].Verified, $"the breakpoint beside the hit count \"{hitCondition}\" binds");
        }

        // It never stops: from the count the program runs on round its loop to MyMethod's end.
        Assert.Equal(CountUp, (await host.WithContinueRequest().WaitForStoppedEvent()).ReadStopInfo().line);
        StoppedEvent next = await host.WithContinueRequest().WaitForStoppedEvent();
        Assert.EndsWith("MyClass.cs", next.ReadStopInfo().filePath, StringComparison.Ordinal);
        Assert.Equal(Debuggees.MyMethodEnd, next.ReadStopInfo().line);
    }

    /// <summary>HitConditionClass's count of its passes, where the program stopped.</summary>
    private static string Count(DebugSession host, StoppedEvent stop)
    {
        host.WithStackTraceRequest(stop.ThreadId!.Value, out StackTraceResponse stack);
        host.WithEvaluateRequest(stack.StackFrames!.First().Id, "_count", out EvaluateResponse value);
        return value.Result;
    }

    [Fact]
    public async Task A_hit_count_of_at_least_N_stops_on_every_hit_from_the_Nth()
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost
            .WithBreakpointsRequest(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: ">=2")])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // First stop should be on 2nd iteration (hit count >= 2)
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_count", out EvaluateResponse? evaluateResponse);
        Assert.Equal("2", evaluateResponse.Result);

        // Continue - should stop again on 3rd iteration
        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo2.filePath);
        Assert.Equal(AfterCount, stopInfo2.line);

        debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2);
        int stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
        debugProtocolHost.WithEvaluateRequest(stackFrameId2, "_count", out EvaluateResponse? evaluateResponse2);
        Assert.Equal("3", evaluateResponse2.Result);
    }

    [Fact]
    public async Task A_hit_count_of_every_N_stops_on_every_Nth_hit()
    {
        bool startSuspended = true;
        string hitConditionFilePath = Debuggees.Source("DebuggableConsoleApp", "HitConditionClass.cs");

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        debugProtocolHost
            .WithBreakpointsRequest(hitConditionFilePath, [new DapSourceBreakpoint(AfterCount, HitCondition: "%2")])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // First stop should be on 2nd iteration (2 % 2 == 0)
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo.filePath);
        Assert.Equal(AfterCount, stopInfo.line);

        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        int stackFrameId = stackTraceResponse.StackFrames!.First().Id;
        debugProtocolHost.WithEvaluateRequest(stackFrameId, "_count", out EvaluateResponse? evaluateResponse);
        Assert.Equal("2", evaluateResponse.Result);

        // Continue - should skip 3rd, stop on 4th (4 % 2 == 0)
        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("HitConditionClass.cs", stopInfo2.filePath);
        Assert.Equal(AfterCount, stopInfo2.line);

        debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2);
        int stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
        debugProtocolHost.WithEvaluateRequest(stackFrameId2, "_count", out EvaluateResponse? evaluateResponse2);
        Assert.Equal("4", evaluateResponse2.Result);
    }
}

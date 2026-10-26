using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Just My Code down to the method: what the user's own code marks as not theirs ([DebuggerStepThrough],
/// [DebuggerNonUserCode], [DebuggerHidden]) is passed over by Step Into, and a catch in it leaves an exception
/// unhandled by the user (upstream SharpDbg #52 and #49).</summary>
[Collection(Alone.Name)]
public class JustMyCodeTests(ITestOutputHelper testOutputHelper)
{
    private static string Source => Debuggees.Source("PassengerCases", "Program.cs");

    private static int Line(string code) => Debuggees.Line("PassengerCases", code);

    /// <summary>An exception caught in code marked not the user's stops as user-unhandled; a step from it stops where the
    /// user's code goes on; Step Into then passes over each method marked not the user's, a breakpoint in one is not hit and
    /// says why, and an evaluation that never returns on the way is stopped and the debugger goes on.</summary>
    [Fact]
    public async Task An_exception_caught_in_code_marked_not_the_users_stops_as_user_unhandled_and_step_into_passes_over_such_code()
    {
        int marked = Line("[DebuggerStepThrough] public static void StepThrough()");
        int end = Line("Console.WriteLine(\"end\");");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([DapExceptionFilters.UserUnhandled]));
        await host.BreakpointsAsync(Source, [new DapSourceBreakpoint(marked), new DapSourceBreakpoint(end)]).ConfigurationDoneAsync();

        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal((StopReasons.Exception, Line("Wrapper.CallWithCatch(")), (stop.Reason, stop.ReadStopInfo().line));
        int thread = stop.ThreadId!.Value;
        ExceptionInfoResponse exception = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(thread));
        Assert.Equal("userUnhandled", exception.BreakMode);
        Assert.Equal("System.InvalidOperationException", exception.Details?.FullTypeName);

        stop = await host.StepOverAsync(thread).WaitForStoppedEvent();
        Assert.True(stop.Reason == StopReasons.Step, $"The step from the exception ran on to the {stop.Reason} at line {stop.Line}");
        Assert.Equal(Line("Wrapper.CallWithCatch("), stop.ReadStopInfo().line);
        stop = await host.StepOverAsync(thread).WaitForStoppedEvent();
        Assert.Equal((StopReasons.Step, Line("Attributed.StepThrough();")), (stop.Reason, stop.ReadStopInfo().line));

        stop = await host.StepInAsync(thread).WaitForStoppedEvent();
        Assert.Equal((Source, Line("Attributed.NonUser();")), (stop.ReadStopInfo().filePath, stop.ReadStopInfo().line));
        stop = await host.StepInAsync(thread).WaitForStoppedEvent();
        Assert.Equal((Source, Line("Attributed.Hidden();")), (stop.ReadStopInfo().filePath, stop.ReadStopInfo().line));

        int frame = (await host.TopFrameAsync(thread)).Id;
        EvaluateResponse timedOut = await host.EvaluateAsync(frame, "Slow.Forever");
        Assert.Contains("was stopped", timedOut.Result, StringComparison.Ordinal);
        EvaluateResponse after = await host.EvaluateAsync(frame, "1 + 1");
        Assert.Equal("2", after.Result);
        stop = await host.StepInAsync(thread).WaitForStoppedEvent();
        Assert.Equal((Source, end), (stop.ReadStopInfo().filePath, stop.ReadStopInfo().line));

        SetBreakpointsResponse again = await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetBreakpoints,
            new SetBreakpointsArguments(new DapSource { Path = Source }, [new DapSourceBreakpoint(marked)]));
        DapBreakpoint unbound = Assert.Single(again.Breakpoints);
        Assert.False(unbound.Verified);
        Assert.Contains("Just My Code", unbound.Message, StringComparison.Ordinal);
    }

    /// <summary>A step from where an exception is thrown goes past a handler outside the user's code, and stops in the user's
    /// own catch; and every thread listed at a later stop has frames to show.</summary>
    [Fact]
    public async Task A_step_from_where_an_exception_is_thrown_goes_past_a_handler_outside_the_users_code_or_stops_in_their_catch()
    {
        string thrown = "try { throw new Exception(\"Message1\"); }";
        string waiting = "Console.WriteLine(\"both workers are waiting\");";
        await using DebugSession host = await Launch(breakpointLine: Line(waiting), exceptionFilters: [DapExceptionFilters.All]);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal((StopReasons.Exception, Line("Wrapper.CallWithCatch(")), (stop.Reason, stop.ReadStopInfo().line));
        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        Assert.True(stop.Reason == StopReasons.Step, $"The step from the throw ran on to the {stop.Reason} at line {stop.Line}");
        Assert.Equal(Line("Wrapper.CallWithCatch("), stop.ReadStopInfo().line);

        // No filter until the throw in the user's own try, where it stops; the step from it stops in their catch.
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([]));
        await host.BreakpointsAsync([Line(thrown), Line(waiting)], Source);
        stop = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal((StopReasons.Breakpoint, Line(thrown)), (stop.Reason, stop.ReadStopInfo().line));
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([DapExceptionFilters.All]));
        stop = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal((StopReasons.Exception, Line(thrown)), (stop.Reason, stop.ReadStopInfo().line));
        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        Assert.Equal((StopReasons.Step, Line("catch (Exception e1)")), (stop.Reason, stop.ReadStopInfo().line));

        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([]));
        stop = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal((StopReasons.Breakpoint, Line(waiting)), (stop.Reason, stop.ReadStopInfo().line));
        IReadOnlyList<DapThread> threads = (await host.RequestAsync<ThreadsResponse>(DapCommands.Threads)).Threads;
        Assert.Equal("Main Thread", Assert.Single(threads, t => t.Id == stop.ThreadId).Name);
        Assert.Single(threads, t => t.Name == "worker-named");
        await Assert.AllAsync(threads, async t => Assert.NotEmpty((await host.RequestAsync<StackTraceResponse>(DapCommands.StackTrace, new StackTraceArguments(t.Id))).StackFrames));
    }

    [Fact]
    public async Task A_step_over_a_call_that_throws_and_catches_within_it_ends_at_the_next_line()
    {
        // At the call, not in the lambda on the same line that throws.
        int call = Line("Wrapper.CallWithCatch(");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync(Source, [new DapSourceBreakpoint(call, Column: 1)]).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(1, stop.ReadStopInfo().column);

        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        Assert.Equal((StopReasons.Step, Line("Attributed.StepThrough();")), (stop.Reason, stop.ReadStopInfo().line));
    }

    /// <summary>The thread running Main is marked as Main's whatever the program named it, as an app names its UI
    /// thread.</summary>
    [Fact]
    public async Task The_thread_running_Main_is_marked_as_Mains_under_the_name_the_program_gave_it()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["named main"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync(Line("Console.WriteLine(\"both workers are waiting\");"), Source)
            .ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();

        IReadOnlyList<DapThread> threads = (await host.RequestAsync<ThreadsResponse>(DapCommands.Threads)).Threads;
        DapThread main = Assert.Single(threads, t => t.IsMain == true);
        Assert.True(main.Id == stop.ThreadId && main.Name == "UI",
            $"Main's thread, named UI, was to be the one marked; the threads are {string.Join(", ", threads.Select(t => $"{t.Id} {t.Name}{(t.IsMain == true ? " (main)" : "")}"))}, stopped on {stop.ThreadId}");
        Assert.DoesNotContain(threads, t => t.Name == "Main Thread");
    }

    private Task<DebugSession> Launch(int breakpointLine, IReadOnlyList<string> exceptionFilters) =>
        DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", breakpointLine, exceptionFilters);
}

using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class FunctionBreakpointTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task A_function_breakpoint_by_a_bare_method_name_stops_in_every_method_of_that_name()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("SameNamedClass.Test")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        debugProtocolHost
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("SameNamedClass.cs", topFrame.Source?.Path);
        Assert.Equal(Opened("SomeFolder/SameNamedClass.cs", "public static void Test()"), topFrame.Line);
        Assert.Equal(2, topFrame.Column);
        Assert.Equal(3, topFrame.EndColumn);

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2);
        DapStackFrame topFrame2 = stackTraceResponse2.StackFrames.Single();

        Assert.EndsWith("SameNamedClass2.cs", topFrame2.Source?.Path);
        Assert.Equal(Opened("SomeFolder/SameNamedClass2.cs", "public static void Test()"), topFrame2.Line);
        Assert.Equal(2, topFrame2.Column);
        Assert.Equal(3, topFrame2.EndColumn);
    }

    [Fact]
    public async Task A_function_breakpoint_by_a_full_name_stops_in_that_method_alone()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.MyClass.MyMethod")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        debugProtocolHost
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
        topFrame.ShouldBeAtSourceLocation("MyClass.cs", "public void MyMethod(long myParam, int myIntParam)", 2, 3);
    }

    [Fact]
    public async Task A_function_breakpoint_on_a_generic_classs_method_stops_in_it()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<T, U>.MyNestedGenericClass<T, U>.Test")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        debugProtocolHost
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
        topFrame.ShouldBeAtSourceLocation("MyClassContainingAnotherClass.cs", "public static void Test()", 3, 4);
    }

    [Fact]
    public async Task A_function_breakpoints_hit_count_goes_on_when_another_is_set_beside_it_and_it_stops_on_its_third_hit()
    {
        // As the IDE sends XAML Binding Errors again with a property breakpoint added between its hits.
        using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program);
        await host
            .WithInitializeRequest()
            .WithAttachRequest(program.Id)
            .WaitForInitializedEvent();
        DapFunctionBreakpoint third = new("DebuggableConsoleApp.HitConditionClass.Test", HitCondition: "==3");
        DapFunctionBreakpoint everyPass = new("DebuggableConsoleApp.MyClassNoMembers.MyMethod");
        SetBreakpointsResponse before = host.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([third, everyPass]));
        host.WithConfigurationDoneRequest();

        // Each pass of the program's loop stops in MyMethod first, then calls Test, whose hit count counts on.
        Assert.EndsWith("MyClassNoMembers.cs", (await host.WaitForStoppedEvent()).Source?.Path);
        Assert.EndsWith("MyClassNoMembers.cs", (await host.WithContinueRequest().WaitForStoppedEvent()).Source?.Path);
        SetBreakpointsResponse after = host.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([third, everyPass, new DapFunctionBreakpoint("DebuggableConsoleApp.Nowhere.Nothing")]));
        Assert.EndsWith("MyClassNoMembers.cs", (await host.WithContinueRequest().WaitForStoppedEvent()).Source?.Path);

        StoppedEvent hit = await host.WithContinueRequest().WaitForStoppedEvent();
        Assert.True(hit.Source?.Path?.EndsWith("HitConditionClass.cs", StringComparison.Ordinal) == true,
            $"The third pass through Test did not stop it, its hit count started again by the breakpoint set beside it: the program stopped in {hit.Source?.Path}:{hit.Line}");
        host.WithEvaluateRequest(host.GetTopStackFrame(hit.ThreadId!.Value).Id, "_count", out EvaluateResponse passes);
        Assert.Equal("2", passes.Result);
        Assert.True(before.Breakpoints.Select(b => b.Id).SequenceEqual(after.Breakpoints.Take(2).Select(b => b.Id)),
            "The function breakpoints asked for again just as they were were made anew, with new ids");
    }

    [Fact]
    public async Task A_function_breakpoint_naming_no_parameters_stops_in_every_overload()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.OverloadedMethodsClass.OverloadedMethod")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        debugProtocolHost
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        await AssertBreakpointsHit();

        // Also test that classless method resolution works
        SetBreakpointsResponse result2 = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("OverloadedMethod")]));
        debugProtocolHost.WithContinueRequest();

        await AssertBreakpointsHit();

        return;

        async Task AssertBreakpointsHit()
        {
            StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
            debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
            DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
            topFrame.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod()", 2, 3);

            StoppedEvent stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
            debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2);
            DapStackFrame topFrame2 = stackTraceResponse2.StackFrames.Single();
            topFrame2.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam)", 2, 3);

            StoppedEvent stoppedEvent3 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
            debugProtocolHost.WithStackTraceRequest(stoppedEvent3.ThreadId!.Value, out StackTraceResponse? stackTraceResponse3);
            DapStackFrame topFrame3 = stackTraceResponse3.StackFrames.Single();
            topFrame3.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(double doubleParam)", 2, 3);

            StoppedEvent stoppedEvent4 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
            debugProtocolHost.WithStackTraceRequest(stoppedEvent4.ThreadId!.Value, out StackTraceResponse? stackTraceResponse4);
            DapStackFrame topFrame4 = stackTraceResponse4.StackFrames.Single();
            topFrame4.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(MyClass? myClass)", 2, 3);

            StoppedEvent stoppedEvent5 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
            debugProtocolHost.WithStackTraceRequest(stoppedEvent5.ThreadId!.Value, out StackTraceResponse? stackTraceResponse5);
            DapStackFrame topFrame5 = stackTraceResponse5.StackFrames.Single();
            topFrame5.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam, MyClass? myClass)", 2, 3);
        }
    }

    [Fact]
    public async Task A_function_breakpoint_naming_its_parameters_stops_in_that_overload_alone()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.OverloadedMethodsClass.OverloadedMethod(int, MyClass)")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        debugProtocolHost
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
        topFrame.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam, MyClass? myClass)", 2, 3);

        // Continuing lands us at the same breakpoint, since we only break on the single matching overload
        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2);
        DapStackFrame topFrame2 = stackTraceResponse2.StackFrames.Single();
        topFrame2.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam, MyClass? myClass)", 2, 3);
    }

    /// <summary>The line of a method's opening brace in a file of DebuggableConsoleApp, where a function breakpoint stops.</summary>
    internal static int Opened(string file, string declaration) => Debuggees.Line("DebuggableConsoleApp", file, declaration) + 1;
}

file static class Extensions
{
    extension(DapStackFrame frame)
    {
        /// <summary>The frame at the opening brace of the method a file of DebuggableConsoleApp declares, between its columns.</summary>
        public void ShouldBeAtSourceLocation(string fileName, string declaration, int column, int endColumn)
        {
            int line = FunctionBreakpointTests.Opened(fileName, declaration);
            Assert.EndsWith(fileName, frame.Source?.Path);
            Assert.Equal(line, frame.Line);
            Assert.Equal(line, frame.EndLine);
            Assert.Equal(column, frame.Column);
            Assert.Equal(endColumn, frame.EndColumn);
        }
    }
}

using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class FunctionBreakpointTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>The gate's function breakpoints as one flow, to take their place once it has its record on CI: by a
    /// bare name in every method of it, by a full name in that one alone, in a generic class's method, in every
    /// overload or the one its parameters name, and a hit count going on beside another one set.</summary>
    [Fact]
    public async Task The_gates_function_breakpoints_hold_as_one_flow()
    {
        await Flow.StepApartAsync("a function breakpoint by a bare method name stops in every method of that name", () => new FunctionBreakpointTests(testOutputHelper), t => t.A_function_breakpoint_by_a_bare_method_name_stops_in_every_method_of_that_name());
        await Flow.StepApartAsync("a function breakpoint by a full name stops in that method alone", () => new FunctionBreakpointTests(testOutputHelper), t => t.A_function_breakpoint_by_a_full_name_stops_in_that_method_alone());
        await Flow.StepApartAsync("a function breakpoint on a generic classs method stops in it", () => new FunctionBreakpointTests(testOutputHelper), t => t.A_function_breakpoint_on_a_generic_classs_method_stops_in_it());
        await Flow.StepApartAsync("a function breakpoints hit count goes on when another is set beside it and it stops on its third hit", () => new FunctionBreakpointTests(testOutputHelper), t => t.A_function_breakpoints_hit_count_goes_on_when_another_is_set_beside_it_and_it_stops_on_its_third_hit());
        await Flow.StepApartAsync("a function breakpoint naming no parameters stops in every overload", () => new FunctionBreakpointTests(testOutputHelper), t => t.A_function_breakpoint_naming_no_parameters_stops_in_every_overload());
        await Flow.StepApartAsync("a function breakpoint naming its parameters stops in that overload alone", () => new FunctionBreakpointTests(testOutputHelper), t => t.A_function_breakpoint_naming_its_parameters_stops_in_that_overload_alone());
    }

    [Gate]
    [Fact]
    public async Task A_function_breakpoint_by_a_bare_method_name_stops_in_every_method_of_that_name()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("SameNamedClass.Test")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("SameNamedClass.cs", topFrame.Source?.Path);
        Assert.Equal(Opened("SomeFolder/SameNamedClass.cs", "public static void Test()"), topFrame.Line);
        Assert.Equal(2, topFrame.Column);
        Assert.Equal(3, topFrame.EndColumn);

        StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        DapStackFrame topFrame2 = stackTraceResponse2.StackFrames.Single();

        Assert.EndsWith("SameNamedClass2.cs", topFrame2.Source?.Path);
        Assert.Equal(Opened("SomeFolder/SameNamedClass2.cs", "public static void Test()"), topFrame2.Line);
        Assert.Equal(2, topFrame2.Column);
        Assert.Equal(3, topFrame2.EndColumn);
    }

    [Gate]
    [Fact]
    public async Task A_function_breakpoint_by_a_full_name_stops_in_that_method_alone()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.MyClass.MyMethod")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
        topFrame.ShouldBeAtSourceLocation("MyClass.cs", "public void MyMethod(long myParam, int myIntParam)", 2, 3);
    }

    [Gate]
    [Fact]
    public async Task A_function_breakpoint_on_a_generic_classs_method_stops_in_it()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<T, U>.MyNestedGenericClass<T, U>.Test")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
        topFrame.ShouldBeAtSourceLocation("MyClassContainingAnotherClass.cs", "public static void Test()", 3, 4);
    }

    [Gate]
    [Fact]
    public async Task A_function_breakpoints_hit_count_goes_on_when_another_is_set_beside_it_and_it_stops_on_its_third_hit()
    {
        // As the IDE sends XAML Binding Errors again with a property breakpoint added between its hits.
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program);
        await host
            .InitializeAsync()
            .AttachAsync(program.Id)
            .WaitForInitializedEvent();
        DapFunctionBreakpoint third = new("DebuggableConsoleApp.HitConditionClass.Test", HitCondition: "==3");
        DapFunctionBreakpoint everyPass = new("DebuggableConsoleApp.MyClassNoMembers.MyMethod");
        SetBreakpointsResponse before = await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([third, everyPass]));
        await host.ConfigurationDoneAsync();

        // Each pass of the program's loop stops in MyMethod first, then calls Test, whose hit count counts on.
        Assert.EndsWith("MyClassNoMembers.cs", (await host.WaitForStoppedEvent()).Source?.Path);
        Assert.EndsWith("MyClassNoMembers.cs", (await host.ContinueAsync().WaitForStoppedEvent()).Source?.Path);
        SetBreakpointsResponse after = await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([third, everyPass, new DapFunctionBreakpoint("DebuggableConsoleApp.Nowhere.Nothing")]));
        Assert.EndsWith("MyClassNoMembers.cs", (await host.ContinueAsync().WaitForStoppedEvent()).Source?.Path);

        StoppedEvent hit = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.True(hit.Source?.Path?.EndsWith("HitConditionClass.cs", StringComparison.Ordinal) == true,
            $"The third pass through Test did not stop it, its hit count started again by the breakpoint set beside it: the program stopped in {hit.Source?.Path}:{hit.Line}");
        EvaluateResponse passes = await host.EvaluateAsync((await host.TopFrameAsync(hit.ThreadId!.Value)).Id, "_count");
        Assert.Equal("2", passes.Result);
        Assert.True(before.Breakpoints.Select(b => b.Id).SequenceEqual(after.Breakpoints.Take(2).Select(b => b.Id)),
            "The function breakpoints asked for again just as they were were made anew, with new ids");
    }

    /// <summary>A function breakpoint sent again, as an edit in the Breakpoints window sends it, counts on from its hits through
    /// an edit of its message and again from an edit of its hit count: ==3 given a message after Test's first call stops on
    /// its third, then edited to ==2 stops on its fifth, two calls after the edit.</summary>
    [Fact]
    public async Task A_function_breakpoint_sent_again_counts_on_through_an_edit_of_its_message_and_again_from_an_edit_of_its_hit_count()
    {
        await using DebugSession host = DebugSession.ForAttach(testOutputHelper, startSuspended: true, out Process program);
        await host
            .InitializeAsync()
            .AttachAsync(program.Id)
            .WaitForInitializedEvent();
        DapFunctionBreakpoint everyPass = new("DebuggableConsoleApp.MyClassNoMembers.MyMethod");
        DapFunctionBreakpoint test = new("DebuggableConsoleApp.HitConditionClass.Test", HitCondition: "==3");
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([test, everyPass]));
        await host.ConfigurationDoneAsync();

        // Each pass of the program's loop stops in MyMethod first, then calls Test: the second pass's stop comes after Test's first hit.
        Assert.EndsWith("MyClassNoMembers.cs", (await host.WaitForStoppedEvent()).Source?.Path);
        Assert.EndsWith("MyClassNoMembers.cs", (await host.ContinueAsync().WaitForStoppedEvent()).Source?.Path);
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([test with { LogMessage = "calls before {_count}", Suspend = true }, everyPass]));
        Assert.Equal("2", await CountAtTestAsync(host, "its third call, the count going on through the edit of its message"));

        Assert.EndsWith("MyClassNoMembers.cs", (await host.ContinueAsync().WaitForStoppedEvent()).Source?.Path);
        await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([test with { HitCondition = "==2", LogMessage = "calls before {_count}", Suspend = true }, everyPass]));
        Assert.Equal("4", await CountAtTestAsync(host, "its fifth call, the count started again by the edit of its hit count"));
    }

    /// <summary>The calls of Test before the one its breakpoint next stops in, going on past MyMethod's stops; a failure says
    /// the stop <paramref name="expected"/>.</summary>
    private static async Task<string> CountAtTestAsync(DebugSession host, string expected)
    {
        for (int pass = 0; pass < 6; pass++)
        {
            StoppedEvent stop = await host.ContinueAsync().WaitForStoppedEvent();
            if (stop.Source?.Path?.EndsWith("HitConditionClass.cs", StringComparison.Ordinal) == true)
            {
                return (await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, "_count")).Result;
            }
        }
        Assert.Fail($"Test's breakpoint never stopped in six passes, where it stops on {expected}");
        return string.Empty;
    }

    [Gate]
    [Fact]
    public async Task A_function_breakpoint_naming_no_parameters_stops_in_every_overload()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.OverloadedMethodsClass.OverloadedMethod")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        await AssertBreakpointsHit();

        // Also test that classless method resolution works
        SetBreakpointsResponse result2 = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("OverloadedMethod")]));
        await debugProtocolHost.ContinueAsync();

        await AssertBreakpointsHit();

        return;

        async Task AssertBreakpointsHit()
        {
            StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
            StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
            DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
            topFrame.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod()", 2, 3);

            StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
            StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
            DapStackFrame topFrame2 = stackTraceResponse2.StackFrames.Single();
            topFrame2.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam)", 2, 3);

            StoppedEvent stoppedEvent3 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
            StackTraceResponse? stackTraceResponse3 = await debugProtocolHost.StackTraceAsync(stoppedEvent3.ThreadId!.Value);
            DapStackFrame topFrame3 = stackTraceResponse3.StackFrames.Single();
            topFrame3.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(double doubleParam)", 2, 3);

            StoppedEvent stoppedEvent4 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
            StackTraceResponse? stackTraceResponse4 = await debugProtocolHost.StackTraceAsync(stoppedEvent4.ThreadId!.Value);
            DapStackFrame topFrame4 = stackTraceResponse4.StackFrames.Single();
            topFrame4.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(MyClass? myClass)", 2, 3);

            StoppedEvent stoppedEvent5 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
            StackTraceResponse? stackTraceResponse5 = await debugProtocolHost.StackTraceAsync(stoppedEvent5.ThreadId!.Value);
            DapStackFrame topFrame5 = stackTraceResponse5.StackFrames.Single();
            topFrame5.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam, MyClass? myClass)", 2, 3);
        }
    }

    [Gate]
    [Fact]
    public async Task A_function_breakpoint_naming_its_parameters_stops_in_that_overload_alone()
    {
        bool startSuspended = true;
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        SetBreakpointsResponse result = await debugProtocolHost.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints, new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("DebuggableConsoleApp.OverloadedMethodsClass.OverloadedMethod(int, MyClass)")]));
        int breakpointId = result.Breakpoints.Single().Id!.Value;

        await debugProtocolHost
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();
        topFrame.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam, MyClass? myClass)", 2, 3);

        // Continuing lands us at the same breakpoint, since we only break on the single matching overload
        StoppedEvent stoppedEvent2 = await debugProtocolHost.ContinueAsync().WaitForStoppedEvent();
        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);
        DapStackFrame topFrame2 = stackTraceResponse2.StackFrames.Single();
        topFrame2.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", "public static void OverloadedMethod(int intParam, MyClass? myClass)", 2, 3);
    }

    /// <summary>One library loaded into two load contexts is two modules over one image at one address: a function
    /// breakpoint set once both have loaded stops in the code each context runs, the first context's call first.</summary>
    [Fact]
    public async Task A_function_breakpoint_stops_in_a_library_loaded_into_each_of_two_load_contexts()
    {
        int loaded = Debuggees.Line("PassengerCases", "Console.WriteLine(\"contexts loaded\");");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["contexts"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync(loaded, Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        Assert.Equal(loaded, (await host.WaitForStoppedEvent()).ReadStopInfo().line);

        SetBreakpointsResponse set = await host.RequestAsync<SetBreakpointsResponse>(DapCommands.SetFunctionBreakpoints,
            new SetFunctionBreakpointsArguments([new DapFunctionBreakpoint("LinkedLibrary.Entry.Twice")]));
        Assert.True(set.Breakpoints.Single().Verified, $"The function breakpoint did not bind: {set.Breakpoints.Single().Message}");
        foreach ((string context, string value) in new[] { ("first", "10"), ("second", "11") })
        {
            StoppedEvent stop = await host.ContinueAsync().WaitForStoppedEvent();
            string given = Assert.Single(await host.LocalsAsync(stop), v => v.Name == "value").Value;
            Assert.True(given == value, $"The {context} load context's call of Twice, given {value}, was to stop, but the stop was in the call given {given}: "
                + "the breakpoint was bound in one context's module alone");
        }
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

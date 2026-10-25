using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class StackTraceTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int Selected = Debuggees.Line("DebuggableConsoleApp", "ClassWithBclCall.cs", "return x * 2;");
    private static readonly int Ranged = Debuggees.Line("DebuggableConsoleApp", "ClassWithBclCall.cs", "int[] array = Enumerable.Range(1, 5)");
    private static readonly int BclCalled = Debuggees.Line("DebuggableConsoleApp", "ClassWithBclCall.Test(4);");
    private static readonly int NestedGenericCalled = Debuggees.Line("DebuggableConsoleApp", "MyNestedGenericClass<double, bool>.Test();");
    private static readonly int NestedGenericBody = Debuggees.Line("DebuggableConsoleApp", "MyClassContainingAnotherClass.cs", "public static void Test()") + 2;
    private static readonly int AnotherMethodAwaits = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "await Task.Delay(10);");
    private static readonly int InnerAwaited = Debuggees.Line("DebuggableConsoleApp", "AsyncStackTraceClass.cs", "await Task.Delay(10);") + 1;
    private static readonly int MiddleAwaits = Debuggees.Line("DebuggableConsoleApp", "AsyncStackTraceClass.cs", "await InnerAsync();");
    private static readonly int TestAwaits = Debuggees.Line("DebuggableConsoleApp", "AsyncStackTraceClass.cs", "await MiddleAsync();");

    [Fact]
    public async Task A_stop_lists_its_frames_down_to_Main_the_librarys_subtle_and_an_async_methods_by_its_own_name()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);


        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id, justMyCode: true)
            .WaitForInitializedEvent();
        debugProtocolHost.Request(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([], [new(DapExceptionFilters.All), new(DapExceptionFilters.UserUnhandled)]));
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ClassWithBclCall.cs");
        debugProtocolHost
            .WithBreakpointsRequest([Selected], breakpointedFilePath)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("ClassWithBclCall.cs", stopInfo.filePath);
        Assert.Equal(Selected, stopInfo.line);

        // TODO: Handle formatting generic methods (and fully qualifying them), e.g. System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.Fill() instead of System.Linq.dll!RangeSelectIterator`2.Fill()
        List<DapStackFrame> expectedStackFrames =
        [
            new() { Id = 1, Column = 3, EndColumn =   16, Line = Selected, EndLine = Selected, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Selector(int x)",   Source = new DapSource { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
            new() { Id = 2, Column = 0, EndColumn = null, Line =  0, EndLine = null, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.Fill(System.Span<int> results, int start, System.Func<int, int> func)",    Source = null, PresentationHint = "subtle" },
            new() { Id = 3, Column = 0, EndColumn = null, Line =  0, EndLine = null, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.ToArray()",   Source = null, PresentationHint = "subtle" },
            new() { Id = 4, Column = 3, EndColumn =   67, Line = Ranged, EndLine = Ranged, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Test(int myParam)", Source = new DapSource { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
            new() { Id = 5, Column = 4, EndColumn =   29, Line = BclCalled, EndLine = BclCalled, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)",        Source = new DapSource { Name = "Program.cs",          SourceReference = 0, Path = Debuggees.Source("DebuggableConsoleApp", "Program.cs") } },
			// TODO: Return internal frames (thread.ActiveInternalFrames), such as a subtle "[Native to Managed Transition]".
		];

        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse, null);
        Assert.Equal(expectedStackFrames.Count, stackTraceResponse.StackFrames.Count);
        Assert.Equal(expectedStackFrames, stackTraceResponse.StackFrames.WithoutResolution());
        Assert.Equal([true, false, false, true, true], stackTraceResponse.StackFrames.Select(f => f.IsResolved));
        // Just My Code is on, and a frame is decompiled only when it is resolved.
        Assert.All(stackTraceResponse.StackFrames, frame => Assert.Null(frame.DecompiledSourceInfo));

        string breakpointedFilePath2 = Debuggees.Source("DebuggableConsoleApp", "MyClassContainingAnotherClass.cs");
        StoppedEvent stoppedEvent2 = await debugProtocolHost
            .WithBreakpointsRequest([], breakpointedFilePath)
            .WithBreakpointsRequest([NestedGenericBody], breakpointedFilePath2)
            .WithContinueRequest()
            .WaitForStoppedEvent();

        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("MyClassContainingAnotherClass.cs", stopInfo2.filePath);
        Assert.Equal(NestedGenericBody, stopInfo2.line);

        // Numbered on from the first stop's five frames, so an id from that stop never names one of these.
        List<DapStackFrame> expectedStackFrames2 =
        [
            new() { Id = 6, Column = 4, EndColumn =   5, Line = NestedGenericBody, EndLine = NestedGenericBody, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<int, string>.MyNestedGenericClass<double, bool>.Test()", Source = new DapSource { Name = "MyClassContainingAnotherClass.cs", SourceReference = 0, Path = breakpointedFilePath2 } },
            new() { Id = 7, Column = 4, EndColumn = 103, Line = NestedGenericCalled, EndLine = NestedGenericCalled, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)", Source = new DapSource { Name = "Program.cs", SourceReference = 0, Path = Debuggees.Source("DebuggableConsoleApp", "Program.cs") } },
        ];

        debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2, null);
        Assert.Equal(expectedStackFrames2.Count, stackTraceResponse2.StackFrames.Count);
        Assert.Equal(expectedStackFrames2, stackTraceResponse2.StackFrames.WithoutResolution());

        string breakpointedFilePath3 = Debuggees.Source("DebuggableConsoleApp", "Namespace1", "AnotherClass.cs");
        StoppedEvent stoppedEvent3 = await debugProtocolHost
            .WithBreakpointsRequest([], breakpointedFilePath2)
            .WithBreakpointsRequest([AnotherMethodAwaits], breakpointedFilePath3)
            .WithContinueRequest()
            .WaitForStoppedEvent();

        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo3.filePath);
        Assert.Equal(AnotherMethodAwaits, stopInfo3.line);

        debugProtocolHost.WithStackTraceRequest(stoppedEvent3.ThreadId!.Value, out StackTraceResponse? stackTraceResponse3, null);
        List<string> userFrameNames = stackTraceResponse3.StackFrames
            .Select(frame => frame.Name)
            .Where(name => name.StartsWith("DebuggableConsoleApp.dll!"))
            .ToList();
        Assert.Contains("DebuggableConsoleApp.dll!DebuggableConsoleApp.Namespace1.AnotherClass.AnotherMethodAsync()", userFrameNames);
        Assert.Contains("DebuggableConsoleApp.dll!DebuggableConsoleApp.MyAsyncClass.MyMethodAsync(int myParam)", userFrameNames);
        Assert.DoesNotContain(userFrameNames, name => name.Contains("MoveNext"));

    }

    [Fact]
    public async Task A_stop_in_an_awaited_method_lists_the_async_methods_awaiting_it_with_their_locals()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process process);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(process.Id, justMyCode: true)
            .WaitForInitializedEvent();
        string filePath = Debuggees.Source("DebuggableConsoleApp", "AsyncStackTraceClass.cs");
        debugProtocolHost
            .WithBreakpointsRequest([InnerAwaited], filePath)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(process.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse, null);

        Assert.Equal(
            ["DebuggableConsoleApp.dll!DebuggableConsoleApp.AsyncStackTraceClass.MiddleAsync()",
            "DebuggableConsoleApp.dll!DebuggableConsoleApp.AsyncStackTraceClass.TestAsync()"],
            stackTraceResponse.StackFrames.TakeLast(2).Select(frame => frame.Name));
        Assert.Equal([MiddleAwaits, TestAwaits], stackTraceResponse.StackFrames.TakeLast(2).Select(frame => frame.Line));

        DapStackFrame syntheticMiddleFrame = stackTraceResponse.StackFrames[^2];
        debugProtocolHost.WithScopesRequest(syntheticMiddleFrame.Id, out ScopesResponse? scopesResponse);
        debugProtocolHost.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out List<DapVariable>? variables);
        Assert.Contains(variables, variable => variable.Name == "middleValue" && variable.Value == "17");
    }

    [Fact]
    public async Task A_library_frame_without_source_is_decompiled_when_it_is_resolved()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process process);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(process.Id, justMyCode: false)
            .WaitForInitializedEvent();
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ClassWithBclCall.cs");
        debugProtocolHost
            .WithBreakpointsRequest([Selected], breakpointedFilePath)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(process.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse, null);
        DapStackFrame unresolvedFrame = stackTraceResponse.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));
        Assert.False(unresolvedFrame.IsResolved);
        Assert.Null(unresolvedFrame.DecompiledSourceInfo);

        ResolveStackFrameResponse response = debugProtocolHost.Request<ResolveStackFrameResponse>(DapCommands.ResolveStackFrame, new ResolveStackFrameArguments(unresolvedFrame.Id));

        Assert.Equal(unresolvedFrame.Id, response.StackFrame.Id);
        Assert.True(response.StackFrame.IsResolved);
        Assert.Equal("subtle", response.StackFrame.PresentationHint);
        Assert.EndsWith(".cs", response.StackFrame.Source?.Path);
        Assert.True(response.StackFrame.Line > 0);
        DapDecompiledSource? decompiledSourceInfo = response.StackFrame.DecompiledSourceInfo;
        Assert.NotNull(decompiledSourceInfo);
        Assert.Equal("System.Linq.Enumerable+RangeSelectIterator`2", decompiledSourceInfo.TypeFullName);
        Assert.EndsWith("System.Linq.dll", decompiledSourceInfo.Assembly.AssemblyPath);
        Assert.EndsWith("DebuggableConsoleApp.dll", decompiledSourceInfo.CallingUserCodeAssemblyPath);
    }

    [Fact]
    public async Task A_stop_in_a_method_emitted_code_calls_has_its_stack_down_to_Main_through_the_emitted_frame()
    {
        // System.Reflection.Emit's module has no file and no image in memory.
        int calledBack = Debuggees.Line("PassengerCases", "Console.WriteLine($\"called back with {value}\");");
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .WithInitializeRequest()
            .WithLaunchRequest(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["emitted"])
            .WaitForInitializedEvent();
        host.WithBreakpointsRequest(calledBack, Debuggees.Source("PassengerCases", "Program.cs")).WithConfigurationDoneRequest();

        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        host.WithStackTraceRequest(thread, out StackTraceResponse stack, levels: null);
        List<string> names = [.. stack.StackFrames.Select(frame => frame.Name)];
        Assert.True(names.Contains("PassengerCases.dll!Program.<Main>$(string[] args)"), $"The stack ended before Main: {string.Join(" < ", names)}");
        Assert.Equal("PassengerCases.dll!Emitted.CalledBack(int value)", names[0]);
        Assert.EndsWith("!EmittedCaller.CallBack(int)", names[1], StringComparison.Ordinal);
        Assert.Equal("PassengerCases.dll!Emitted.Run()", names[2]);

        // The emitted frame's argument, emitted without a name, is there by its place.
        host.WithScopesRequest(stack.StackFrames[1].Id, out ScopesResponse scopes);
        host.WithVariablesRequest(Assert.Single(scopes.Scopes).VariablesReference, out List<DapVariable> arguments);
        Assert.Equal(("arg_0", "7"), (Assert.Single(arguments).Name, arguments[0].Value));
        StoppedEvent stepped = await host.WithStepOutRequest(thread).WaitForStoppedEvent();
        Assert.Equal((StopReasons.Step, Debuggees.Line("PassengerCases", "callBack(7);")), (stepped.Reason, stepped.ReadStopInfo().line));
    }
}

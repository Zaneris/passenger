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

    [Fact]
    public async Task Steps_through_an_async_method_stop_at_each_of_its_lines_in_turn()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(Body, Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo.filePath);
        Assert.Equal(Body, stopInfo.line);

        debugProtocolHost.WithClearBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"));

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo2.filePath);
        Assert.Equal(Body + 1, stopInfo2.line);

        StoppedEvent stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo3.filePath);
        Assert.Equal(Body + 2, stopInfo3.line);

        StoppedEvent stoppedEvent4 = await debugProtocolHost.WithStepOverRequest(stoppedEvent3.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo4 = stoppedEvent4.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo4.filePath);
        Assert.Equal(Body + 3, stopInfo4.line);

        // stoppedEvent4's thread, since the thread may have changed after the await.
        StoppedEvent stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent4.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo5 = stoppedEvent5.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo5.filePath);
        Assert.Equal(Body + 4, stopInfo5.line);

        StoppedEvent stoppedEvent6 = await debugProtocolHost.WithStepInRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo6 = stoppedEvent6.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo6.filePath);
        Assert.Equal(AnotherMethodOpened, stopInfo6.line);

        StoppedEvent stoppedEvent7 = await debugProtocolHost.WithStepInRequest(stoppedEvent6.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo7 = stoppedEvent7.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo7.filePath);
        Assert.Equal(AnotherMethodOpened + 1, stopInfo7.line);

        StoppedEvent stoppedEvent8 = await debugProtocolHost.WithStepOutRequest(stoppedEvent7.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo8 = stoppedEvent8.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo8.filePath);
        Assert.Equal(Body + 5, stopInfo8.line);

        StoppedEvent stoppedEvent10 = await debugProtocolHost.WithStepInRequest(stoppedEvent8.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo10 = stoppedEvent10.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo10.filePath);
        Assert.Equal(AsyncVoidOpened, stopInfo10.line);

        StoppedEvent stoppedEvent11 = await debugProtocolHost.WithStepOutRequest(stoppedEvent10.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo11 = stoppedEvent11.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo11.filePath);
        Assert.Equal(Body + 5, stopInfo11.line);
    }
}

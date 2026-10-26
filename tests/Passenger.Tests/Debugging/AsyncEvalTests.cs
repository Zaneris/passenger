using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class AsyncEvalTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int BeforeSuspension = Debuggees.Line("DebuggableConsoleApp", "AsyncMethodEvalClass.cs", "// bp before suspension");
    private static readonly int AfterSuspension = Debuggees.Line("DebuggableConsoleApp", "AsyncMethodEvalClass.cs", "// bp after suspension");

    [Fact]
    public async Task An_evaluation_in_an_async_method_reads_what_the_method_sees()
    {
        bool startSuspended = true;

        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();

        string breakpointFilePath = Debuggees.Source("DebuggableConsoleApp", "AsyncMethodEvalClass.cs");
        await debugProtocolHost
            .BreakpointsAsync([BeforeSuspension, AfterSuspension], breakpointFilePath)
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal((breakpointFilePath, BeforeSuspension, 3), stoppedEvent.ReadStopInfo());
        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

        Assert.Single(scopesResponse.Scopes);
        DapScope scope = scopesResponse.Scopes.Single();

        List<DapVariable>? variables = await debugProtocolHost.VariablesAsync(scope.VariablesReference);

        Assert.Equal(2, variables.Count);

        await TestVariablesEval(stackTraceResponse.StackFrames!.First().Id);

        // To the breakpoint after the async suspension point.
        await debugProtocolHost.ContinueAsync();

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal((breakpointFilePath, AfterSuspension, 3), stoppedEvent2.ReadStopInfo());
        StackTraceResponse? stackTraceResponse2 = await debugProtocolHost.StackTraceAsync(stoppedEvent2.ThreadId!.Value);

        await TestVariablesEval(stackTraceResponse2.StackFrames!.First().Id);
        return;

        async Task TestVariablesEval(int stackFrameId)
        {
            EvaluateResponse? evaluateResponse = await debugProtocolHost.EvaluateAsync(stackFrameId, "localInt");
            Assert.Equal("4", evaluateResponse.Result);

            EvaluateResponse? evaluateResponse2 = await debugProtocolHost.EvaluateAsync(stackFrameId, "IntField");
            Assert.Equal("10", evaluateResponse2.Result);
        }
    }
}

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

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        string breakpointFilePath = Debuggees.Source("DebuggableConsoleApp", "AsyncMethodEvalClass.cs");
        debugProtocolHost
            .WithBreakpointsRequest([BeforeSuspension, AfterSuspension], breakpointFilePath)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal((breakpointFilePath, BeforeSuspension, 3), stoppedEvent.ReadStopInfo());
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

        Assert.Single(scopesResponse.Scopes);
        DapScope scope = scopesResponse.Scopes.Single();

        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        Assert.Equal(2, variables.Count);

        TestVariablesEval(stackTraceResponse.StackFrames!.First().Id);

        // To the breakpoint after the async suspension point.
        debugProtocolHost.WithContinueRequest();

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal((breakpointFilePath, AfterSuspension, 3), stoppedEvent2.ReadStopInfo());
        debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2);

        TestVariablesEval(stackTraceResponse2.StackFrames!.First().Id);
        return;

        void TestVariablesEval(int stackFrameId)
        {
            debugProtocolHost.WithEvaluateRequest(stackFrameId, "localInt", out EvaluateResponse? evaluateResponse);
            Assert.Equal("4", evaluateResponse.Result);

            debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntField", out EvaluateResponse? evaluateResponse2);
            Assert.Equal("10", evaluateResponse2.Result);
        }
    }
}

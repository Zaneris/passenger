using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class AsyncVariablesTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int FirstAwait = Debuggees.Line("DebuggableConsoleApp", "MyAsyncClass.cs", "int result = await AnotherClass.AnotherMethodAsync();");
    private static readonly int AnotherMethodOpened = Debuggees.Line("DebuggableConsoleApp", "Namespace1/AnotherClass.cs", "AnotherMethodAsync()") + 1;

    [Fact]
    public async Task A_stop_in_an_async_method_lists_its_locals_as_they_are()
    {
        bool startSuspended = true;

        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(FirstAwait, Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

        Assert.Single(scopesResponse.Scopes);
        DapScope scope = scopesResponse.Scopes.Single();

        List<DapVariable> expectedVariables =
        [
            new() {Name = "this", Value = "{DebuggableConsoleApp.MyAsyncClass}", Type = "DebuggableConsoleApp.MyAsyncClass", EvaluateName = "this", VariablesReference = 2 },
            new() {Name = "myParam", Value = "4", Type = "int", EvaluateName = "myParam" },
            new() {Name = "intVar", Value = "10", Type = "int", EvaluateName = "intVar" },
            new() {Name = "result", Value = "0", Type = "int", EvaluateName = "result" },
            new() {Name = "result2", Value = "0", Type = "int", EvaluateName = "result2" },
            new() {Name = "result3", Value = "0", Type = "int", EvaluateName = "result3" },

        ];

        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        Assert.Equal(6, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithStepInRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo.filePath);
        Assert.Equal(AnotherMethodOpened, stopInfo.line);

        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse2)
            .WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out ScopesResponse? scopesResponse2)
            .WithVariablesRequest(scopesResponse2.Scopes.Single().VariablesReference, out List<DapVariable>? variables2);

        List<DapVariable> staticAsyncMethodExpectedVariables =
        [
            new() {Name = "test", Value = "0", Type = "int", EvaluateName = "test" },
        ];

        variables2.ShouldBeEquivalentToDebuggerVariables(staticAsyncMethodExpectedVariables);

        StoppedEvent stoppedEvent3 = await debugProtocolHost
            .WithContinueRequest()
            .WaitForStoppedEvent();
        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent3.ThreadId!.Value, out StackTraceResponse? stackTraceResponse3)
            .WithScopesRequest(stackTraceResponse3.StackFrames!.First().Id, out ScopesResponse? scopesResponse3)
            .WithVariablesRequest(scopesResponse3.Scopes.Single().VariablesReference, out List<DapVariable>? variables3);
        // The same variables as the first time round the loop: their references reset on continue.
        variables3.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
}

file static class TestExtensions
{

}

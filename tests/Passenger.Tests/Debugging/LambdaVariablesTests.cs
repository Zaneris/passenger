using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class LambdaVariablesTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int InnerReturn = Debuggees.Line("DebuggableConsoleApp", "Lambdas/MyLambdaClass.cs", "// set breakpoint here");

    [Fact]
    public async Task A_stop_in_a_lambda_lists_the_variables_in_its_scope()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest([InnerReturn], Debuggees.Source("DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyLambdaClass.cs", stopInfo.filePath);
        Assert.Equal(InnerReturn, stopInfo.line);

        debugProtocolHost
            .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse)
            .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out ScopesResponse? scopesResponse);

        Assert.Single(scopesResponse.Scopes);
        DapScope scope = scopesResponse.Scopes.Single();

        List<DapVariable> expectedVariables =
        [
            new() {Name = "this", Value = "{DebuggableConsoleApp.Lambdas.MyLambdaClass}", Type = "DebuggableConsoleApp.Lambdas.MyLambdaClass", EvaluateName = "this", VariablesReference = 2 },
            new() {Name = "capturedString",  EvaluateName = "capturedString",  Value = "\"captured\"", Type = "string" },
            new() {Name = "innerLocalFromOuterLocalInt1",  EvaluateName = "innerLocalFromOuterLocalInt1",  Value = "4",  Type = "int" },
            new() {Name = "innerLocalFromOuterLocalInt2",  EvaluateName = "innerLocalFromOuterLocalInt2",  Value = "15",  Type = "int" },
            new() {Name = "innerLocalFromRootLocalInt",  EvaluateName = "innerLocalFromRootLocalInt",  Value = "10",  Type = "int" },
            new() {Name = "innerLocalFromRootLocalString", EvaluateName = "innerLocalFromRootLocalString", Value = "\"captured\"",  Type = "string" },
            new() {Name = "local",  EvaluateName = "local",  Value = "10",  Type = "int" },
            new() {Name = "outerLocalFromCapturedField",  EvaluateName = "outerLocalFromCapturedField",  Value = "4",  Type = "int" },
            new() {Name = "outerLocalFromLocal",  EvaluateName = "outerLocalFromLocal",  Value = "15",  Type = "int" },
            new() {Name = "result",  EvaluateName = "result",  Value = "25",  Type = "int" },
            new() {Name = "y", EvaluateName = "y", Value = "5",  Type = "int" },

        ];

        debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out List<DapVariable>? variables);

        Assert.Equal(11, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
}

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
        await using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .InitializeAsync()
            .AttachAsync(p2.Id)
            .WaitForInitializedEvent();
        await debugProtocolHost
            .BreakpointsAsync([InnerReturn], Debuggees.Source("DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
            .ConfigurationDoneAsync()
            .ResumeRuntimeAsync(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyLambdaClass.cs", stopInfo.filePath);
        Assert.Equal(InnerReturn, stopInfo.line);

        StackTraceResponse? stackTraceResponse = await debugProtocolHost.StackTraceAsync(stoppedEvent.ThreadId!.Value);
        ScopesResponse? scopesResponse = await debugProtocolHost.ScopesAsync(stackTraceResponse.StackFrames!.First().Id);

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

        List<DapVariable>? variables = await debugProtocolHost.VariablesAsync(scope.VariablesReference);

        Assert.Equal(11, variables.Count);
        variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
    }
}

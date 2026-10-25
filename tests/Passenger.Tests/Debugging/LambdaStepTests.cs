using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class LambdaStepTests(ITestOutputHelper testOutputHelper)
{
    private static readonly int InnerLambda = Debuggees.Line("DebuggableConsoleApp", "Lambdas/MyLambdaClass.cs", "int result = y * y;");
    private static readonly int AfterLambdas = Debuggees.Line("DebuggableConsoleApp", "Lambdas/MyLambdaClass.cs", "int value = 5;");
    private static readonly int OuterLambda = Debuggees.Line("DebuggableConsoleApp", "Lambdas/MyLambdaClass.cs", "Func<int, int> outerLambda = x =>");

    [Fact]
    public async Task Steps_into_and_through_a_lambda_stop_at_each_of_its_lines_in_turn()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest([InnerLambda, AfterLambdas], Debuggees.Source("DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // we should not stop in the inner lambda first, as it has not been invoked yet
        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyLambdaClass.cs", stopInfo.filePath);
        Assert.Equal(AfterLambdas, stopInfo.line);

        // continue, and now we should stop within the inner lambda
        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("MyLambdaClass.cs", stopInfo2.filePath);
        Assert.Equal(InnerLambda, stopInfo2.line);

        // Set breakpoint at lambda declaration
        debugProtocolHost.WithBreakpointsRequest(OuterLambda, Debuggees.Source("DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"));

        // Continue (while loop), and we should hit the lambda declaration breakpoint
        StoppedEvent stoppedEvent3 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("MyLambdaClass.cs", stopInfo3.filePath);
        Assert.Equal(OuterLambda, stopInfo3.line);
    }
}

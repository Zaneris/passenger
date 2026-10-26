using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>The debugger's own names: what each call a step ran returned, listed at the top of the frame's values and
/// named $ReturnValue in an expression, and the exception stopped at, $exception.</summary>
[Collection(DebugSession.Collection)]
public class SyntheticVariablesTests(ITestOutputHelper testOutputHelper)
{
    private const string Stepping = "SteppingConsoleApp";

    private static int Line(string code) => Debuggees.Line(Stepping, code);

    [Fact]
    public async Task A_step_over_lists_what_each_call_returned_in_the_order_they_returned_and_names_them_for_an_expression()
    {
        int line = Line("string described = Describe(Count());");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        Assert.DoesNotContain(await host.LocalsAsync(stop), v => v.Name.EndsWith(" returned", StringComparison.Ordinal));

        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        Assert.Equal(Line("double ratio = Ratio(3, 4);"), stop.ReadStopInfo().line);
        List<DapVariable> locals = await host.LocalsAsync(stop);
        Assert.True(locals.Count >= 2, $"The step's values were not listed: {string.Join(", ", locals.Select(v => v.Name))}");
        Assert.Equal(("Count() returned", "42", "int", "$ReturnValue1"), (locals[0].Name, locals[0].Value, locals[0].Type, locals[0].EvaluateName));
        Assert.Equal(("Describe() returned", "\"count 42\"", "string", "$ReturnValue"), (locals[1].Name, locals[1].Value, locals[1].Type, locals[1].EvaluateName));
        Assert.Equal("\"count 42\"", Assert.Single(locals, v => v.Name == "described").Value);

        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("\"count 42\"", (await Evaluate(host, frame, "$returnvalue")).Result);
        Assert.Equal("42", (await Evaluate(host, frame, "$ReturnValue1")).Result);
        Assert.Equal("8", (await Evaluate(host, frame, "$ReturnValue.Length")).Result);
        Assert.Equal("43", (await Evaluate(host, frame, "$ReturnValue1 + 1")).Result);

        // The next step's values replace the last's: a double, kept boxed as the int was.
        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        locals = await host.LocalsAsync(stop);
        Assert.Equal(("Ratio() returned", "0.75", "double", "$ReturnValue"), (locals[0].Name, locals[0].Value, locals[0].Type, locals[0].EvaluateName));
        Assert.DoesNotContain(locals, v => v.Name is "Count() returned" or "Describe() returned");
        frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("3", (await Evaluate(host, frame, "$ReturnValue * 4")).Result);

        // The runtime does not give back a returned struct, so nothing is listed for it.
        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        Assert.DoesNotContain(await host.LocalsAsync(stop), v => v.Name.EndsWith(" returned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_out_lists_what_the_method_stepped_out_of_returned()
    {
        int line = Line("string described = Describe(Count());");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        stop = await host.StepInAsync(thread).WaitForStoppedEvent();
        Assert.Equal(Line("static int Count() => 42;"), stop.ReadStopInfo().line);
        stop = await host.StepOutAsync(thread).WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        DapVariable returned = (await host.LocalsAsync(stop))[0];
        Assert.Equal(("Count() returned", "42"), (returned.Name, returned.Value));
        Assert.Equal("42", (await Evaluate(host, (await host.TopFrameAsync(thread)).Id, "$ReturnValue")).Result);
    }

    [Fact]
    public async Task A_stop_at_an_exception_lists_it_as_exception_and_an_expression_reads_its_message()
    {
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, Line("Throws.Run();"), [DapExceptionFilters.All]);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        stop = await host.ContinueAsync().WaitForStoppedEvent();
        Assert.Equal((StopReasons.Exception, Line("static void Fail(string reason) => throw new InvalidOperationException(reason);")), (stop.Reason, stop.ReadStopInfo().line));

        DapVariable exception = Assert.Single(await host.LocalsAsync(stop), v => v.Name == "$exception");
        Assert.Equal("System.InvalidOperationException", exception.Type);
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("\"bad input\"", (await Evaluate(host, frame, "$exception.Message")).Result);
    }

    private static async Task<EvaluateResponse> Evaluate(DebugSession host, int frame, string expression)
    {
        EvaluateResponse response = await host.EvaluateAsync(frame, expression);
        Assert.True(response.PresentationHint?.Attributes?.Contains("failedEvaluation") != true, $"{expression} did not evaluate: {response.Result}");
        return response;
    }
}

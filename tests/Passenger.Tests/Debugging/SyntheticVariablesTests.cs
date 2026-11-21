using System.Diagnostics;
using System.Globalization;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>The debugger's own names: what each call a step ran returned, listed at the top of the frame's values and
/// named $ReturnValue in an expression (or, in optimized code, where the runtime keeps none, why not), and the exception
/// stopped at, $exception.</summary>
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

        // A returned struct is listed where the runtime gives it back, from .NET 11 on; before, nothing is listed for it.
        stop = await host.StepOverAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
        locals = await host.LocalsAsync(stop);
        frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        if (int.Parse((await Evaluate(host, frame, "System.Environment.Version.Major")).Result, CultureInfo.InvariantCulture) >= 11)
        {
            Assert.Equal(("Locate() returned", "Spot { X = 2, Y = 3 }", "$ReturnValue"), (locals[0].Name, locals[0].Value, locals[0].EvaluateName));
        }
        else
        {
            Assert.DoesNotContain(locals, v => v.Name.EndsWith(" returned", StringComparison.Ordinal));
        }
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

    /// <summary>A step out of a method returning an int runs nothing in the program: its row is the value's bytes, so a box
    /// the program cannot make never holds the step. Only an expression naming $ReturnValue boxes it, and says why it could
    /// not where the box's evaluation is held unfinished (#2019).</summary>
    [Fact]
    public async Task A_step_out_of_a_method_returning_an_int_runs_nothing_in_the_program_and_only_an_expression_naming_it_boxes_it()
    {
        int line = Line("string described = Describe(Count());");
        await using DebugSession host = await LaunchHoldingBoxes(line, onNewestRuntime: false);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        stop = await host.StepInAsync(thread).WaitForStoppedEvent();
        stop = await host.StepOutAsync(thread).WaitForStoppedEvent();
        Assert.Equal((StopReasons.Step, line), (stop.Reason, stop.ReadStopInfo().line));
        DapVariable returned = (await host.LocalsAsync(stop))[0];
        Assert.Equal(("Count() returned", "42", "int", DapRows.ReturnValue), (returned.Name, returned.Value, returned.Type, returned.EvaluateName));
        Assert.False(host.EngineLog().Contains(HeldBox, StringComparison.Ordinal), $"The step out of Count() ran a box of the int it returned in the program:\n{host.EngineLog()}");

        EvaluateResponse boxed = await host.EvaluateAsync((await host.TopFrameAsync(thread)).Id, DapRows.ReturnValue, mayStand: true);
        Assert.True(boxed.Result.Contains("held unfinished", StringComparison.Ordinal) && boxed.PresentationHint?.Attributes?.Contains(DapAttributes.FailedEvaluation) == true,
            $"$ReturnValue, whose box the program could not make, did not say why: {boxed.Result}");
        await RunsToItsEnd(host);
    }

    /// <summary>A step out of a method returning a struct whose box the program cannot make, its evaluation held unfinished
    /// above the caller's frame: the step still stops in the caller, its row saying why the value was not kept, with no run
    /// with every thread let go offered, as nothing can bring it back, and the program goes on to its end once the evaluation
    /// can (#2019).</summary>
    [Fact]
    public async Task A_step_out_whose_box_of_the_returned_struct_is_held_unfinished_stops_in_the_caller_saying_why_it_was_not_kept()
    {
        int line = Line("Spot spot = Locate(2, 3);");
        await using DebugSession host = await LaunchHoldingBoxes(line, onNewestRuntime: true);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        string runtime = (await Evaluate(host, (await host.TopFrameAsync(thread)).Id, "System.Environment.Version.Major")).Result;
        Assert.True(int.Parse(runtime, CultureInfo.InvariantCulture) >= 11, $"The program ran on .NET {runtime}, which gives no returned struct back, so no box is made");

        stop = await host.StepInAsync(thread).WaitForStoppedEvent();
        Assert.Equal(Line("static Spot Locate(int x, int y) => new(x, y);"), stop.ReadStopInfo().line);
        stop = await host.StepOutAsync(thread).WaitForStoppedEvent();
        Assert.True((stop.Reason, stop.ReadStopInfo().line, (await host.TopFrameAsync(thread)).Line) == (StopReasons.Step, line, line),
            $"With the box of what Locate() returned held unfinished, the step out stopped for {stop.Reason} at line {stop.ReadStopInfo().line}, not in its caller at line {line}");
        DapVariable returned = (await host.LocalsAsync(stop))[0];
        string[] attributes = [.. returned.PresentationHint?.Attributes ?? []];
        Assert.True(returned.Name == "Locate() returned" && returned.Value.StartsWith("Not kept: ", StringComparison.Ordinal) && returned.Value.Contains("held unfinished", StringComparison.Ordinal)
            && attributes.Contains(DapAttributes.FailedEvaluation) && !attributes.Contains(DapAttributes.NeedsThreads),
            $"The row of what Locate() returned did not say why it was not kept, offering nothing to run again: {returned.Name} = {returned.Value} ({string.Join(", ", attributes)})");
        await RunsToItsEnd(host);
    }

    /// <summary>A step over a call returning a struct whose box the program cannot make, its evaluation held unfinished
    /// partway through the statement: the step goes on once the evaluation returns, stopping at the next line, its row
    /// saying why the value was not kept (#2019).</summary>
    [Fact]
    public async Task A_step_over_whose_box_of_a_returned_struct_is_held_unfinished_goes_on_to_the_next_line_once_the_box_returns()
    {
        int line = Line("Spot spot = Locate(2, 3);");
        await using DebugSession host = await LaunchHoldingBoxes(line, onNewestRuntime: true);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        string runtime = (await Evaluate(host, (await host.TopFrameAsync(thread)).Id, "System.Environment.Version.Major")).Result;
        Assert.True(int.Parse(runtime, CultureInfo.InvariantCulture) >= 11, $"The program ran on .NET {runtime}, which gives no returned struct back, so no box is made");

        Task<StoppedEvent> stepped = host.StepOverAsync(thread).WaitForStoppedEvent();
        Assert.True(await Waiting.UntilAsync(() => host.EngineLog().Contains(StepWaitsForBox, StringComparison.Ordinal), TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100)), $"The box of what Locate() returned was never left running partway through the step:\n{host.EngineLog()}");
        await host.RequestAsync(DapCommands.Input, new InputArguments("x", Close: true));
        stop = await stepped;
        int next = Line("Console.WriteLine($\"{described} {ratio} {spot}\");");
        Assert.True((stop.Reason, stop.ReadStopInfo().line) == (StopReasons.Step, next), $"Once the box's evaluation returned, the step stopped for {stop.Reason} at line {stop.ReadStopInfo().line}, not at the next line, {next}");
        DapVariable returned = (await host.LocalsAsync(stop))[0];
        Assert.True(returned.Name == "Locate() returned" && returned.Value.StartsWith("Not kept: ", StringComparison.Ordinal),
            $"The row of what Locate() returned did not say why it was not kept: {returned.Name} = {returned.Value}");
        (string output, int? exitCode) = await host.ContinueAsync().WaitForEnd();
        Assert.True(output.Contains("count 42 0.75 Spot { X = 2, Y = 3 }", StringComparison.Ordinal) && exitCode == 0,
            $"Let go, the program did not run on to its end as it would have (exit code {exitCode}):\n{output}");
    }

    /// <summary>What the engine logs as a step waits for a box's evaluation left running partway through it.</summary>
    private const string StepWaitsForBox = "the step goes on once it returns";

    /// <summary>What the engine logs as a box's hold makes the program wait.</summary>
    private const string HeldBox = "Held: the box";

    /// <summary>SteppingConsoleApp launched to a breakpoint, every box of a value a call returned waiting for the program's
    /// input first (<see cref="AdapterEnvironment.HoldBoxVariable"/>), on the newest .NET installed where asked.</summary>
    private async Task<DebugSession> LaunchHoldingBoxes(int line, bool onNewestRuntime)
    {
        DebugSession host = DebugSession.ForLaunch(testOutputHelper, environment: new Dictionary<string, string> { [AdapterEnvironment.HoldBoxVariable] = "1" });
        try
        {
            await host.InitializeAsync().LaunchAsync(Debuggees.Program(Stepping), stopAtEntry: false, justMyCode: true,
                environment: onNewestRuntime ? new Dictionary<string, string> { [RuntimeVariables.RollForward] = "LatestMajor" } : null).WaitForInitializedEvent();
            await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([]));
            await host.BreakpointsAsync(line, Debuggees.Source(Stepping, "Program.cs")).ConfigurationDoneAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>The program given the input its held box waits for, and let go: the evaluation returns and the program runs on
    /// to its end, every value it computed as it would have.</summary>
    private static async Task RunsToItsEnd(DebugSession host)
    {
        await host.RequestAsync(DapCommands.Input, new InputArguments("x", Close: true));
        (string output, int? exitCode) = await host.ContinueAsync().WaitForEnd();
        Assert.True(output.Contains("count 42 0.75 Spot { X = 2, Y = 3 }", StringComparison.Ordinal) && exitCode == 0,
            $"Let go once its held box had its input, the program did not run on to its end as it would have (exit code {exitCode}):\n{output}");
    }

    [Fact]
    public async Task A_step_out_of_a_method_returning_an_nint_or_an_nuint_lists_its_value()
    {
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, Stepping, Line("nint signed = Signed();"), []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;

        stop = await StepOutOf("static nint Signed() => -42;", "Signed() returned", "-42", "nint");
        stop = await host.StepOverAsync(thread).WaitForStoppedEvent();
        Assert.Equal(Line("nuint unsigned = Unsigned();"), stop.ReadStopInfo().line);
        await StepOutOf("static nuint Unsigned() => 4000000000;", "Unsigned() returned", "4000000000", "nuint");

        async Task<StoppedEvent> StepOutOf(string body, string name, string value, string type)
        {
            StoppedEvent inside = await host.StepInAsync(thread).WaitForStoppedEvent();
            Assert.Equal(Line(body), inside.ReadStopInfo().line);
            StoppedEvent back = await host.StepOutAsync(thread).WaitForStoppedEvent();
            DapVariable returned = (await host.LocalsAsync(back))[0];
            Assert.True((returned.Name, returned.Value, returned.Type) == (name, value, type),
                $"Stepped out, the first row was {returned.Name} = {returned.Value} ({returned.Type}), not {name} = {value} ({type}): what it returned was lost");
            Assert.Equal(value, (await Evaluate(host, (await host.TopFrameAsync(thread)).Id, "$ReturnValue")).Result);
            return back;
        }
    }

    [Fact]
    public async Task A_step_out_of_a_call_part_way_through_its_statement_lists_what_it_returned()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program(Stepping), stopAtEntry: false, justMyCode: true)
            .WaitForInitializedEvent();
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([]));
        await host.BreakpointsAsync([Line("int added = x + y;"), Line("return value * 2;")], Debuggees.Source(Stepping, "Program.cs")).ConfigurationDoneAsync();

        // Add(a, 1), then Twice(a) and Twice(b) as Add's arguments, then that Add: each returns part way through its statement.
        (string Name, string Value)[] expected = [("Add() returned", "2"), ("Twice() returned", "2"), ("Twice() returned", "4"), ("Add() returned", "6")];
        StoppedEvent stop = await host.WaitForStoppedEvent();
        for (int i = 0; i < expected.Length; i++)
        {
            if (i > 0)
            {
                stop = await host.ContinueAsync().WaitForStoppedEvent();
            }
            stop = await host.StepOutAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
            List<DapVariable> locals = await host.LocalsAsync(stop);
            Assert.True(locals.FirstOrDefault() is { } returned && (returned.Name, returned.Value) == expected[i],
                $"Stepped out to line {stop.ReadStopInfo().line}, the step's value was not listed as {expected[i].Name} {expected[i].Value}: {string.Join(", ", locals.Select(v => $"{v.Name} = {v.Value}"))}");
        }
    }

    /// <summary>Steps out of async methods compiled with state machines and with runtime async: a Task&lt;int&gt;'s, out of
    /// its run after its await and before it, one that never suspends, and a ValueTask&lt;int&gt;'s, each listing the value
    /// the method returned as a call's is, and naming it $ReturnValue.</summary>
    [Theory]
    [InlineData("StateMachineAsyncConsoleApp")]
    [InlineData("RuntimeAsyncConsoleApp")]
    public async Task A_step_out_of_an_async_method_lists_the_value_it_returned(string debuggee)
    {
        const string program = "RuntimeAsyncConsoleApp";
        string source = Debuggees.Source(program, "Program.cs");
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host.InitializeAsync().LaunchAsync(Debuggees.Program(debuggee), stopAtEntry: false, justMyCode: true).WaitForInitializedEvent();
        await host.RequestAsync(DapCommands.SetExceptionBreakpoints, new SetExceptionBreakpointsArguments([]));
        await host.BreakpointsAsync(source,
        [
            new DapSourceBreakpoint(Debuggees.Line(program, "int doubled = before * 2;"), Condition: "before == 1"),
            new DapSourceBreakpoint(Debuggees.Line(program, "int before = i;"), Condition: "i == 2"),
            new DapSourceBreakpoint(Debuggees.Line(program, "int result = n + 1;")),
            new DapSourceBreakpoint(Debuggees.Line(program, "int product = n * 3;")),
        ]).ConfigurationDoneAsync();
        (string Where, string Name, string Value)[] expected =
        [
            ("InnerAsync's run after its await", "InnerAsync() returned", "2"),
            ("InnerAsync before its await", "InnerAsync() returned", "4"),
            ("DoneAsync, which never suspends", "DoneAsync() returned", "4"),
            ("TripleAsync's ValueTask", "TripleAsync() returned", "12"),
        ];
        StoppedEvent stop = await host.WaitForStoppedEvent();
        foreach ((string where, string name, string value) in expected)
        {
            if (where != expected[0].Where)
            {
                stop = await host.ContinueAsync().WaitForStoppedEvent();
            }
            int thread = stop.ThreadId!.Value;
            stop = await host.StepOutAsync(thread).WaitForStoppedEvent();
            List<DapVariable> locals = await host.LocalsAsync(stop);
            Assert.True(locals.FirstOrDefault() is { } returned && (returned.Name, returned.Value, returned.Type, returned.EvaluateName) == (name, value, "int", DapRows.ReturnValue),
                $"{debuggee}: a step out of {where} stopped at line {stop.Line} without listing {name} = {value} first: {string.Join(", ", locals.Select(v => $"{v.Name} = {v.Value}"))}");
            Assert.Equal(value, (await Evaluate(host, (await host.TopFrameAsync(stop.ThreadId!.Value)).Id, "$ReturnValue")).Result);
        }
    }

    [Fact]
    public async Task An_attached_programs_step_out_lists_what_the_method_returned()
    {
        (DebugSession host, StoppedEvent stop) = await AttachToReturning("LoopingConsoleApp", justMyCode: true);
        await using (host)
        {
            long turn = long.Parse(Assert.Single(await host.LocalsAsync(stop), v => v.Name == "turn").Value, System.Globalization.CultureInfo.InvariantCulture);
            stop = await host.StepOutAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
            List<DapVariable> locals = await host.LocalsAsync(stop);
            Assert.True(locals.FirstOrDefault() is { Name: "Twice() returned" } returned && returned.Value == $"{turn * 2}",
                $"Stepped out of Twice({turn}) in the program attached to, its value was not listed: {string.Join(", ", locals.Select(v => $"{v.Name} = {v.Value}"))}");
        }
    }

    [Fact]
    public async Task An_attached_optimized_programs_step_out_says_why_no_value_is_listed()
    {
        // Optimized code is not the user's: the step goes out to it only with Just My Code off.
        (DebugSession host, StoppedEvent stop) = await AttachToReturning("LoopingConsoleAppOptimized", justMyCode: false);
        await using (host)
        {
            stop = await host.StepOutAsync(stop.ThreadId!.Value).WaitForStoppedEvent();
            List<DapVariable> locals = await host.LocalsAsync(stop);
            Assert.True(locals.FirstOrDefault() is { Name: "Twice() returned" } returned
                && returned.Value.StartsWith("Not shown: LoopingConsoleAppOptimized.dll is built optimized", StringComparison.Ordinal)
                && returned.PresentationHint?.Attributes?.Contains(DapAttributes.FailedEvaluation) == true,
                $"Stepped out of Twice() in an optimized program, Variables did not say why no value is listed: {string.Join(", ", locals.Select(v => $"{v.Name} = {v.Value}"))}");
        }
    }

    /// <summary>A LoopingConsoleApp build (its own, or its optimized copy) running "returns", attached to and stopped inside
    /// the method each turn calls for its value.</summary>
    private async Task<(DebugSession Host, StoppedEvent Stop)> AttachToReturning(string program, bool justMyCode)
    {
        const string looping = "LoopingConsoleApp";
        ProcessStartInfo start = ChildProcesses.Command(Debuggees.Apphost(program), ["returns"]);
        start.RedirectStandardOutput = true;
        Process running = ChildProcesses.Start(start);
        DebugSession host = DebugSession.ForAttach(testOutputHelper, running);
        try
        {
            Assert.Equal("returning", await running.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            await host.InitializeAsync().AttachAsync(running.Id, justMyCode).WaitForInitializedEvent();
            await host.BreakpointsAsync(Debuggees.Line(looping, "return turn * 2;"), Debuggees.Source(looping, "Program.cs")).ConfigurationDoneAsync();
            return (host, await host.WaitForStoppedEvent());
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
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
        Assert.True(response.PresentationHint?.Attributes?.Contains(DapAttributes.FailedEvaluation) != true, $"{expression} did not evaluate: {response.Result}");
        return response;
    }
}

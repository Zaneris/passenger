using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>By-ref slots of a frame in PassengerCases' Refs (ref, out and in parameters, a member of an in struct, a ref
/// local, a struct method's this) through the protocol: evaluated, set and read in conditions as the managed locations they
/// are, a change written where they refer to, which the program goes on with; shown as copies of what they refer to show,
/// in a frame named as declared.</summary>
[Collection(DebugSession.Collection)]
public class ByRefSlotsTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string File = Debuggees.Source("PassengerCases", "Program.cs");
    private static readonly int Increment = Debuggees.Line("PassengerCases", "value += 3;");
    private static readonly int Assigned = Debuggees.Line("PassengerCases", "Console.WriteLine($\"assigned {value}\");");
    private static readonly int AddOne = Debuggees.Line("PassengerCases", "return value + one;");
    private static readonly int Climb = Debuggees.Line("PassengerCases", "value += 40;");
    private static readonly int Grow = Debuggees.Line("PassengerCases", "Y += by;");
    private static readonly int Copied = Debuggees.Line("PassengerCases", "Dial dialCopy = dial;");
    private static readonly int Summed = Debuggees.Line("PassengerCases", "sum = dialCopy.Level + spotCopy.X + momentCopy.Day;");
    private static readonly int Doubled = Debuggees.Line("PassengerCases", "return doubled;");

    /// <summary>A ref, an out and an in parameter and a ref local evaluate and take a value, the program reading back each
    /// value set where the slot refers to: the caller's variable, the array's element.</summary>
    [Fact]
    public async Task Ref_out_and_in_parameters_and_a_ref_local_evaluate_and_take_a_value_where_they_refer_to()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await LaunchToAsync(host, [new DapSourceBreakpoint(Increment), new DapSourceBreakpoint(Assigned), new DapSourceBreakpoint(AddOne)], Increment);

        Assert.Equal("1", await Evaluate(host, frame, "value"));
        Assert.Equal("2", await Evaluate(host, frame, "value + 1"));
        Assert.Equal("1", await Evaluate(host, frame, "first"));
        Assert.Equal("10", await Set(host, frame, "value", "10"));
        Assert.Equal("7", await Set(host, frame, "first", "7"));
        Assert.True(await Evaluate(host, frame, "value * 2") == "20", $"value = 10 left value {await Evaluate(host, frame, "value")}");

        frame = await ContinueToAsync(host, Assigned);
        Assert.Equal("42", await Evaluate(host, frame, "value"));
        Assert.Equal("7", await Set(host, frame, "value", "7"));

        frame = await ContinueToAsync(host, AddOne);
        Assert.True(await Evaluate(host, frame, "value") == "13", $"The in parameter reads {await Evaluate(host, frame, "value")} where counted, set to 10 and raised by 3, is 13");
        Assert.Equal("20", await Set(host, frame, "value", "20"));
        Assert.Equal("21", await Evaluate(host, frame, "value + one"));

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("refs 20 7 21 7 160 5 6", output, StringComparison.Ordinal);
    }

    /// <summary>A member of an in parameter, a struct C# keeps readonly, takes a value written into the caller's struct,
    /// which the program goes on with.</summary>
    [Fact]
    public async Task A_member_of_an_in_parameter_takes_a_value_written_into_the_callers_struct()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await LaunchToAsync(host, [new DapSourceBreakpoint(AddOne)], AddOne);

        Assert.Equal("3", await Evaluate(host, frame, "iv.X"));
        Assert.Equal("5", await Set(host, frame, "iv.X", "5"));
        Assert.True(await Evaluate(host, frame, "iv.X + iv.Y") == "9", $"iv.X = 5 left iv.X {await Evaluate(host, frame, "iv.X")} and iv.Y {await Evaluate(host, frame, "iv.Y")}");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("refs 4 42 5 1 160 7 6", output, StringComparison.Ordinal);
    }

    /// <summary>A condition on a ref parameter is evaluated through it at every hit, so the breakpoint stops once, where it
    /// holds, never saying it could not be evaluated.</summary>
    [Fact]
    public async Task A_condition_on_a_ref_parameter_stops_only_where_it_holds()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await LaunchToAsync(host, [new DapSourceBreakpoint(Climb, Condition: "value > 100")], Climb);

        Assert.True(await Evaluate(host, frame, "value") == "120", $"The breakpoint on value > 100 stopped where value is {await Evaluate(host, frame, "value")}");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("refs 4 42 5 1 160 5 6", output, StringComparison.Ordinal);
    }

    /// <summary>In a struct's method, this evaluates as the struct, boxed and passed to its own methods, and an assignment
    /// to it changes the struct the method was called on.</summary>
    [Fact]
    public async Task A_struct_methods_this_evaluates_and_is_assigned()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await LaunchToAsync(host, [new DapSourceBreakpoint(Grow)], Grow);

        Assert.Equal("5", await Evaluate(host, frame, "this.X"));
        Assert.Equal("{Tile}", await Evaluate(host, frame, "this"));
        Assert.Equal("{Tile}", await Evaluate(host, frame, "(object)this"));
        Assert.Equal("true", await Evaluate(host, frame, "this.Equals(this)"));
        Assert.Equal("{Tile}", await Evaluate(host, frame, "this = new Tile { X = 10, Y = 20 }"));
        Assert.True(await Evaluate(host, frame, "X + Y") == "30", $"this = new Tile {{ X = 10, Y = 20 }} left X {await Evaluate(host, frame, "X")} and Y {await Evaluate(host, frame, "Y")}");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("refs 4 42 5 1 160 10 22", output, StringComparison.Ordinal);
    }

    /// <summary>A ref and an in parameter of a struct show as the copies of them beside them do, each with its type: a struct
    /// by its [DebuggerDisplay], a record struct and a DateTime by their ToString, read as rows, opened, evaluated (as a hover
    /// reads one) and in a tracepoint, beside a call that returns nothing; the frame names its ref, in and out parameters as
    /// declared; and a struct's this in its property shows by its [DebuggerDisplay].</summary>
    [Fact]
    public async Task A_by_reference_struct_shows_as_its_copy_does_and_its_frame_names_in_and_out_as_declared()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await LaunchWithAsync(host, [new DapSourceBreakpoint(Copied, LogMessage: "dial {dial}, collected {System.GC.Collect()}"), new DapSourceBreakpoint(Summed)]);
        OutputEvent logged = await host.WaitForEvent<OutputEvent>(output => output.Output.StartsWith("dial ", StringComparison.Ordinal));
        Assert.Equal($"dial Dial at 7, collected {DapRows.RanWithNoValue}\n", logged.Output);
        DapStackFrame shown = await StoppedFrameAsync(host, Summed);
        int frame = shown.Id;
        Assert.EndsWith("Refs.Shown(ref Dial dial, in Spot spot, ref System.DateTime moment, out int sum)", shown.Name, StringComparison.Ordinal);
        List<DapVariable> rows = await host.VariablesAsync(Assert.Single((await host.ScopesAsync(frame)).Scopes).VariablesReference);
        DapVariable Row(string name) => Assert.Single(rows, row => row.Name == name);
        Assert.Equal(("Dial at 7", "Dial"), (Row("dial").Value, Row("dial").Type));
        Assert.Equal(("Spot { X = 1, Y = 2 }", "Spot"), (Row("spot").Value, Row("spot").Type));
        foreach ((string byReference, string copy) in new[] { ("dial", "dialCopy"), ("spot", "spotCopy"), ("moment", "momentCopy") })
        {
            Assert.True((Row(byReference).Value, Row(byReference).Type) == (Row(copy).Value, Row(copy).Type),
                $"{byReference}, by reference, shows {Row(byReference).Value} ({Row(byReference).Type}), where its copy {copy} shows {Row(copy).Value} ({Row(copy).Type})");
            EvaluateResponse evaluated = await host.EvaluateAsync(frame, byReference);
            Assert.True((evaluated.Result, evaluated.Type) == (Row(copy).Value, Row(copy).Type),
                $"{byReference} evaluates to {evaluated.Result} ({evaluated.Type}), where its copy {copy} shows {Row(copy).Value} ({Row(copy).Type})");
        }
        List<DapVariable> members = await host.VariablesAsync(Row("dial").VariablesReference);
        Assert.Equal("7", Assert.Single(members, member => member.Name == "Level").Value);
        Assert.Equal("14", Assert.Single(members, member => member.Name == "Doubled").Value);

        await host.BreakpointsAsync(File, [new DapSourceBreakpoint(Doubled)]);
        frame = await ContinueToAsync(host, Doubled);
        rows = await host.VariablesAsync(Assert.Single((await host.ScopesAsync(frame)).Scopes).VariablesReference);
        Assert.True((Row("this").Value, Row("this").Type) == ("Dial at 7", "Dial"), $"In Dial's Doubled, this shows {Row("this").Value} ({Row("this").Type}), where its display is Dial at 7");

        await host.ClearBreakpointsAsync(File).ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("shown 28 28 14", output, StringComparison.Ordinal);
    }

    /// <summary>PassengerCases launched with Refs' breakpoints to its first stop, which must be at <paramref name="line"/>;
    /// its top frame.</summary>
    private static async Task<int> LaunchToAsync(DebugSession host, List<DapSourceBreakpoint> breakpoints, int line) =>
        (await LaunchToFrameAsync(host, breakpoints, line)).Id;

    private static async Task<DapStackFrame> LaunchToFrameAsync(DebugSession host, List<DapSourceBreakpoint> breakpoints, int line)
    {
        await LaunchWithAsync(host, breakpoints);
        return await StoppedFrameAsync(host, line);
    }

    /// <summary>PassengerCases launched with Refs' breakpoints, running.</summary>
    private static async Task LaunchWithAsync(DebugSession host, List<DapSourceBreakpoint> breakpoints)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["refs"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync(File, breakpoints).ConfigurationDoneAsync();
    }

    private static async Task<int> ContinueToAsync(DebugSession host, int line)
    {
        await host.ContinueAsync();
        return (await StoppedFrameAsync(host, line)).Id;
    }

    private static async Task<DapStackFrame> StoppedFrameAsync(DebugSession host, int line)
    {
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == line, $"The program was to stop at line {line}, but stopped at the {stop.Reason} at line {stop.Line}");
        return await host.TopFrameAsync(stop.ThreadId!.Value);
    }

    private static async Task<string> Set(DebugSession host, int frame, string expression, string value) =>
        (await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments(expression, value, frame))).Value;

    private static async Task<string> Evaluate(DebugSession host, int frame, string expression) => (await host.EvaluateAsync(frame, expression)).Result;
}

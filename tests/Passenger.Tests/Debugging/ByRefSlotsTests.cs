using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>By-ref slots of a frame in PassengerCases' Refs (ref, out and in parameters, a ref local, a struct method's
/// this) through the protocol: evaluated, set and read in conditions as the managed locations they are, a change written
/// where they refer to, which the program goes on with.</summary>
[Collection(DebugSession.Collection)]
public class ByRefSlotsTests(ITestOutputHelper testOutputHelper)
{
    private static readonly string File = Debuggees.Source("PassengerCases", "Program.cs");
    private static readonly int Increment = Debuggees.Line("PassengerCases", "value += 3;");
    private static readonly int Assigned = Debuggees.Line("PassengerCases", "Console.WriteLine($\"assigned {value}\");");
    private static readonly int AddOne = Debuggees.Line("PassengerCases", "return value + one;");
    private static readonly int Climb = Debuggees.Line("PassengerCases", "value += 40;");
    private static readonly int Grow = Debuggees.Line("PassengerCases", "Y += by;");

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
        Assert.Contains("assigned 7", output, StringComparison.Ordinal);
        Assert.Contains("refs 20 7 21 7 160 5 6", output, StringComparison.Ordinal);
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

    /// <summary>PassengerCases launched with Refs' breakpoints to its first stop, which must be at <paramref name="line"/>;
    /// its top frame.</summary>
    private static async Task<int> LaunchToAsync(DebugSession host, List<DapSourceBreakpoint> breakpoints, int line)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["refs"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync(File, breakpoints).ConfigurationDoneAsync();
        return await StoppedAtAsync(host, line);
    }

    private static async Task<int> ContinueToAsync(DebugSession host, int line)
    {
        await host.ContinueAsync();
        return await StoppedAtAsync(host, line);
    }

    private static async Task<int> StoppedAtAsync(DebugSession host, int line)
    {
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == line, $"The program was to stop at line {line}, but stopped at the {stop.Reason} at line {stop.Line}");
        return (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
    }

    private static async Task<string> Set(DebugSession host, int frame, string expression, string value) =>
        (await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments(expression, value, frame))).Value;

    private static async Task<string> Evaluate(DebugSession host, int frame, string expression) => (await host.EvaluateAsync(frame, expression)).Result;
}

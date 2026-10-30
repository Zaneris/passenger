using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Set Value through the protocol, at Setting.Run's stop in PassengerCases: what it writes is what was asked,
/// into what the expression names, its target run once, and the program goes on with it. A string holding a NUL is
/// written and read whole, and a pointer takes another address. In Typed.Make and Typed.Wrap, a variable typed by type
/// parameters, alone or in a larger type, takes a value fitting the type its frame runs it as.</summary>
[Collection(DebugSession.Collection)]
public class SetValueTests(ITestOutputHelper testOutputHelper)
{
    private const string Setting = "Console.WriteLine(\"setting\");";

    /// <summary>An object local holding a box another local shares takes null and then a new box: the reference changes,
    /// never the box the other local still holds.</summary>
    [Fact]
    public async Task Set_Value_on_an_object_holding_a_box_changes_the_reference_and_leaves_the_shared_box_as_it_was()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtSettingAsync(host);

        Assert.Equal("null", await Set(host, frame, "boxed", "null"));
        Assert.True(await Evaluate(host, frame, "boxed") == "null", $"boxed = null left boxed {await Evaluate(host, frame, "boxed")}, writing into its box");
        Assert.True(await Evaluate(host, frame, "shared") == "42", $"boxed = null changed the box shared holds to {await Evaluate(host, frame, "shared")}");
        Assert.Equal("7", await Set(host, frame, "boxed", "7"));
        Assert.Equal("7", await Evaluate(host, frame, "boxed"));
        Assert.True(await Evaluate(host, frame, "shared") == "42", $"boxed = 7 changed the box shared holds to {await Evaluate(host, frame, "shared")}");
        Assert.Equal("false", await Evaluate(host, frame, "ReferenceEquals(boxed, shared)"));

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("boxed 7, shared 42, same False", output, StringComparison.Ordinal);
    }

    /// <summary>Set Value on an element its index moves past runs the index once and answers the element it wrote, a real
    /// number written as a float is shown taking its suffix without the target being run for it.</summary>
    [Fact]
    public async Task Set_Value_runs_its_target_once_and_answers_the_value_it_wrote()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtSettingAsync(host);

        Assert.Equal("123", await Set(host, frame, "values[i++]", "123"));
        Assert.True(await Evaluate(host, frame, "i") == "2", $"values[i++] = 123 left i {await Evaluate(host, frame, "i")}, where its index runs once from 1");
        Assert.Equal(("123", "30"), (await Evaluate(host, frame, "values[1]"), await Evaluate(host, frame, "values[2]")));
        Assert.Equal("2.5", await Set(host, frame, "ratios[j++]", "2.5"));
        Assert.True(await Evaluate(host, frame, "j") == "1", $"ratios[j++] = 2.5 left j {await Evaluate(host, frame, "j")}, where its index runs once from 0");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("values 10 123 30, i 2, ratios 25, j 1", output, StringComparison.Ordinal);
    }

    /// <summary>A string written with a NUL inside it is the whole string in the program, its length counting past the NUL.</summary>
    [Fact]
    public async Task A_string_set_with_a_NUL_inside_it_is_the_whole_string_in_the_program()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtSettingAsync(host);

        Assert.Equal("\"A\\0B\"", await Set(host, frame, "text", "\"A\\0B\""));
        Assert.True(await Evaluate(host, frame, "text.Length") == "3", $"text = \"A\\0B\" made a string of length {await Evaluate(host, frame, "text.Length")}");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("text 3", output, StringComparison.Ordinal);
    }

    /// <summary>A string whose last character is a NUL shows it, escaped, beside its length.</summary>
    [Fact]
    public async Task A_string_ending_in_a_NUL_shows_it()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (StoppedEvent stop, int frame) = await StoppedAtSettingAsync(host);

        DapVariable ended = Assert.Single(await host.LocalsAsync(stop), v => v.Name == "ended");
        Assert.True(ended.Value == "\"A\\0\"", $"\"A\\0\" shows as {ended.Value}, its last character dropped");
        Assert.Equal("\"A\\0\"", await Evaluate(host, frame, "ended"));
        Assert.Equal("2", await Evaluate(host, frame, "ended.Length"));
    }

    /// <summary>A pointer set to another pointer, then to null, takes their addresses, the ints it pointed at left as they were.</summary>
    [Fact]
    public async Task Set_Value_on_a_pointer_changes_its_address_and_leaves_what_it_pointed_at()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtSettingAsync(host);

        await Set(host, frame, "p", "q");
        Assert.True(await Evaluate(host, frame, "p == q") == "true", $"p = q left p at {await Evaluate(host, frame, "p")}, q being {await Evaluate(host, frame, "q")}");
        Assert.True(await Evaluate(host, frame, "first") == "1", $"p = q wrote {await Evaluate(host, frame, "first")} into the int p pointed at");
        Assert.Equal(("2", "2"), (await Evaluate(host, frame, "*p"), await Evaluate(host, frame, "second")));
        await Set(host, frame, "p", "null");
        Assert.True(await Evaluate(host, frame, "p == null") == "true", $"p = null left p at {await Evaluate(host, frame, "p")}");
        Assert.True((await Evaluate(host, frame, "first"), await Evaluate(host, frame, "second")) == ("1", "2"),
            $"p = null changed the ints to {await Evaluate(host, frame, "first")} and {await Evaluate(host, frame, "second")}");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("set pointers 1 2 True True", output, StringComparison.Ordinal);
    }

    /// <summary>In a generic method's frame, a local and a parameter typed by the class's T (int) and by the method's own U
    /// (string) take values of those types, which the program goes on with, and a value of another type is refused naming
    /// the type the frame runs the parameter as.</summary>
    [Fact]
    public async Task Set_Value_on_a_variable_typed_by_a_type_parameter_takes_a_value_fitting_the_type_its_frame_runs()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["generic"])
            .WaitForInitializedEvent();
        int made = Debuggees.Line("PassengerCases", "return $\"{copy} {t} {other} {u}\";");
        await host.BreakpointsAsync([made], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == made, $"The program was to stop in Typed.Make at line {made}, but stopped at the {stop.Reason} at line {stop.Line}");
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;

        Assert.Equal("9", await Set(host, frame, "copy", "9"));
        Assert.Equal("7", await Set(host, frame, "t", "7"));
        Assert.Equal("\"five\"", await Set(host, frame, "other", "\"five\""));
        Assert.True(await Evaluate(host, frame, "copy") == "9", $"copy = 9 left copy {await Evaluate(host, frame, "copy")}");
        DapException refused = await Assert.ThrowsAsync<DapException>(() => Set(host, frame, "copy", "\"nine\""));
        Assert.True(refused.Message.Contains("'int'", StringComparison.Ordinal), $"A string set into copy, an int T, was refused saying \"{refused.Message}\", naming no int");
        DapException refusedU = await Assert.ThrowsAsync<DapException>(() => Set(host, frame, "u", "3"));
        Assert.True(refusedU.Message.Contains("'string'", StringComparison.Ordinal), $"An int set into u, a string U, was refused saying \"{refusedU.Message}\", naming no string");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("generic 9 7 five four", output, StringComparison.Ordinal);
    }

    /// <summary>In a generic method's frame, a List&lt;T&gt; local (T the class's, int) and a V? local (V the method's own
    /// struct, double) take values of the types the frame runs them as, null too, which the program goes on with, and a value
    /// of another type is refused naming the type the frame runs.</summary>
    [Fact]
    public async Task Set_Value_on_a_variable_typed_by_a_type_parameter_in_a_larger_type_takes_a_value_fitting_the_type_its_frame_runs()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["generic"])
            .WaitForInitializedEvent();
        int wrapped = Debuggees.Line("PassengerCases", "return $\"{list.Count} {list[0]} {maybe}\";");
        await host.BreakpointsAsync([wrapped], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == wrapped, $"The program was to stop in Typed.Wrap at line {wrapped}, but stopped at the {stop.Reason} at line {stop.Line}");
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;

        Assert.Equal("Count = 2", await Set(host, frame, "list", "new List<int> { 7, 8 }"));
        Assert.True(await Evaluate(host, frame, "list[1]") == "8", $"list = new List<int> {{ 7, 8 }} left list[1] {await Evaluate(host, frame, "list[1]")}");
        Assert.Equal("null", await Set(host, frame, "maybe", "null"));
        Assert.True(await Evaluate(host, frame, "maybe.HasValue") == "false", $"maybe = null left maybe {await Evaluate(host, frame, "maybe")}");
        Assert.Equal("1.5", await Set(host, frame, "maybe", "1.5"));
        Assert.True(await Evaluate(host, frame, "maybe") == "1.5", $"maybe = 1.5 left maybe {await Evaluate(host, frame, "maybe")}");
        DapException refused = await Assert.ThrowsAsync<DapException>(() => Set(host, frame, "list", "new List<string>()"));
        Assert.True(refused.Message.Contains("List<int>'", StringComparison.Ordinal),
            $"A List<string> set into list, a List<T> run as List<int>, was refused saying \"{refused.Message}\", naming no List<int>");
        DapException refusedMaybe = await Assert.ThrowsAsync<DapException>(() => Set(host, frame, "maybe", "\"one\""));
        Assert.True(refusedMaybe.Message.Contains("'double?'", StringComparison.Ordinal),
            $"A string set into maybe, a V? run as double?, was refused saying \"{refusedMaybe.Message}\", naming no double?");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("wrapped 2 7 1.5", output, StringComparison.Ordinal);
    }

    /// <summary>The program launched to Setting.Run's stop, and its top frame.</summary>
    private static async Task<(StoppedEvent Stop, int Frame)> StoppedAtSettingAsync(DebugSession host)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["setting"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Debuggees.Line("PassengerCases", Setting)], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Debuggees.Line("PassengerCases", Setting), $"The program was to stop at {Setting}, but stopped at the {stop.Reason} at line {stop.Line}");
        return (stop, (await host.TopFrameAsync(stop.ThreadId!.Value)).Id);
    }

    private static async Task<string> Set(DebugSession host, int frame, string expression, string value) =>
        (await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments(expression, value, frame))).Value;

    private static async Task<string> Evaluate(DebugSession host, int frame, string expression) => (await host.EvaluateAsync(frame, expression)).Result;
}

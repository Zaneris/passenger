using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>What the debugger reckons at Reckoning.Run's stop in PassengerCases, its rows and its evaluations, held to
/// what the program's runtime gives.</summary>
[Collection(DebugSession.Collection)]
public class ReckoningTests(ITestOutputHelper testOutputHelper)
{
    private const string Reckoning = "Console.WriteLine(\"reckoning\");";

    [Fact]
    public async Task A_long_based_flags_enum_keeps_its_bits_above_32_in_a_bitwise_operation()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtReckoningAsync(host);

        string both = await Evaluate(host, frame, "wide & big");
        Assert.True(both == "Big", $"wide & big, where both hold Big = 1L << 41, is {both}: the enums were cut to 32 bits");
        Assert.Equal("2199023255552", await Evaluate(host, frame, "(long)(wide & big)"));
        Assert.Equal("Low", await Evaluate(host, frame, "~big & wide"));
    }

    [Fact]
    public async Task A_pointer_type_is_its_own_type_never_the_type_it_points_at()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtReckoningAsync(host);

        string same = await Evaluate(host, frame, "typeof(int*) == typeof(int)");
        Assert.True(same == "false", $"typeof(int*) == typeof(int) is {same}, where the program finds them different");
        Assert.Equal("true", await Evaluate(host, frame, "typeof(int*).IsPointer"));
    }

    [Fact]
    public async Task A_formatted_hole_of_a_programs_value_takes_its_format()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (_, int frame) = await StoppedAtReckoningAsync(host);

        string month = await Evaluate(host, frame, "$\"{date:yyyy-MM}\"");
        Assert.True(month == "\"2026-10\"", $"$\"{{date:yyyy-MM}}\" is {month}, its format dropped");
        Assert.Equal("\"[  2026-10]\"", await Evaluate(host, frame, "$\"[{date,9:yyyy-MM}]\""));
    }

    [Fact]
    public async Task A_property_a_class_takes_from_a_closed_generic_base_is_read()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (StoppedEvent stop, _) = await StoppedAtReckoningAsync(host);

        DapVariable names = Assert.Single(await host.LocalsAsync(stop), v => v.Name == "names");
        List<DapVariable> members = await host.VariablesAsync(names.VariablesReference);
        DapVariable first = Assert.Single(members, v => v.Name == "First");
        Assert.True(first.Value == "\"Ada\"", $"Names : Listed<string> shows its First as {first.Value}, not the base's getter's \"Ada\"");
        Assert.Equal("1", Assert.Single(members, v => v.Name == "Count").Value);
    }

    [Fact]
    public async Task A_proxy_in_another_library_shows_its_members_and_one_in_no_library_leaves_the_raw_members()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        (StoppedEvent stop, _) = await StoppedAtReckoningAsync(host);
        List<DapVariable> locals = await host.LocalsAsync(stop);

        List<DapVariable> proxied = await host.VariablesAsync(Assert.Single(locals, v => v.Name == "counted").VariablesReference);
        Assert.True(proxied.Any(v => v.Name == "Doubled" && v.Value == "42"),
            $"counted opens to {string.Join(", ", proxied.Select(v => $"{v.Name} {v.Value}"))}, not its proxy's Doubled 42 from PassengerCasesProxies");
        Assert.Contains(proxied, v => v.Name == "Raw View");
        List<DapVariable> raw = await host.VariablesAsync(Assert.Single(locals, v => v.Name == "unproxied").VariablesReference);
        Assert.True(raw.Any(v => v.Name == "Raw" && v.Value == "4"),
            $"unproxied, whose proxy is in no assembly, opens to {string.Join(", ", raw.Select(v => $"{v.Name} {v.Value}"))}, not its own Raw 4");
    }

    /// <summary>The program launched to Reckoning.Run's stop, and its top frame.</summary>
    private static async Task<(StoppedEvent Stop, int Frame)> StoppedAtReckoningAsync(DebugSession host)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["reckoning"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Debuggees.Line("PassengerCases", Reckoning)], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Debuggees.Line("PassengerCases", Reckoning), $"The program was to stop at {Reckoning}, but stopped at the {stop.Reason} at line {stop.Line}");
        return (stop, (await host.TopFrameAsync(stop.ThreadId!.Value)).Id);
    }

    private static async Task<string> Evaluate(DebugSession host, int frame, string expression) => (await host.EvaluateAsync(frame, expression)).Result;
}

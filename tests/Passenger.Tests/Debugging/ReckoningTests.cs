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

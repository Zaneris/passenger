using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Set Value through the protocol, at Setting.Run's stop in PassengerCases: what it writes is what was asked,
/// into what the expression names, and the program goes on with it.</summary>
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

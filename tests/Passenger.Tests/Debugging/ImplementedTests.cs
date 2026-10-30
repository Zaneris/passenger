using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>What an evaluation calls at Implemented.Run's stop in PassengerCases, through the generic interfaces and bases
/// its values' types implement, held to what the program's own calls answer.</summary>
[Collection(DebugSession.Collection)]
public class ImplementedTests(ITestOutputHelper testOutputHelper)
{
    private const string Implemented = "Console.WriteLine(\"implemented\");";

    [Fact]
    public async Task A_call_through_a_generic_interface_or_base_is_made_on_that_type_as_the_value_implements_it()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        int frame = await StoppedAtImplementedAsync(host);

        string count = await Evaluate(host, frame, "((IDictionary<string, int>)ages).Count");
        Assert.True(count == "2", $"((IDictionary<string, int>)ages).Count, a count ICollection<KeyValuePair<string, int>> declares, is {count}, not the dictionary's 2");
        string readOnly = await Evaluate(host, frame, "readOnly.Count");
        Assert.True(readOnly == "2", $"readOnly.Count, of a dictionary declared as IReadOnlyDictionary<string, int>, is {readOnly}, not its 2");
        string compared = await Evaluate(host, frame, "((IComparable<string>)text).CompareTo(\"a\")");
        Assert.True(compared == "1", $"((IComparable<string>)text).CompareTo(\"a\") is {compared}, not the 1 \"hello\" compares to \"a\" with");
        string described = await Evaluate(host, frame, "tagged.Describe()");
        Assert.True(described == "\"valued Int32\"", $"tagged.Describe(), of Tagged<string, int> : Valued<int>, is {described}: the base was called as another instantiation than Valued<int>");
    }

    /// <summary>The program launched to Implemented.Run's stop, and its top frame.</summary>
    private static async Task<int> StoppedAtImplementedAsync(DebugSession host)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["implemented"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Debuggees.Line("PassengerCases", Implemented)], Debuggees.Source("PassengerCases", "Program.cs")).ConfigurationDoneAsync();
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.True(stop.ReadStopInfo().line == Debuggees.Line("PassengerCases", Implemented), $"The program was to stop at {Implemented}, but stopped at the {stop.Reason} at line {stop.Line}");
        return (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
    }

    private static async Task<string> Evaluate(DebugSession host, int frame, string expression) => (await host.EvaluateAsync(frame, expression)).Result;
}

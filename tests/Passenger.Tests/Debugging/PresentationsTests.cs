using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Type presentations a client gives the debugger at launch, for a library's types that carry no debugger
/// attributes of their own (PassengerCasesProxies' Presentable.cs, in PassengerCases' presented case), and a debugger view
/// a type names closed.</summary>
[Collection(DebugSession.Collection)]
public class PresentationsTests(ITestOutputHelper testOutputHelper)
{
    private static readonly IReadOnlyList<DapTypePresentation> Given =
    [
        new() { Type = "Library.Point2", Assembly = "PassengerCasesProxies", Value = "({X}, {Y})" },
        new() { Type = "Library.Node", Assembly = "PassengerCasesProxies", Value = "{Name,nq}", Rows = [new DapPresentedRow("Children", "GetChildren()")], First = "Library.TunedAttribute" },
        new() { Type = "Library.Point2", Assembly = "SomeOtherAssembly", Value = "never shown" },
    ];

    /// <summary>A value of a presented type reads as its presentation's format from its fields, its ToString never called; one
    /// of the program's type deriving from a presented type with rows opens to what its own type declares, the marked member
    /// first, then the rows, then Raw View, where the library's types' members are; one with a [DebuggerDisplay] of its own
    /// keeps it and its members.</summary>
    [Fact]
    public async Task A_presented_value_reads_from_its_fields_and_opens_to_its_own_types_members_then_its_rows_then_Raw_View()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, Given);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        List<DapVariable> locals = await host.LocalsAsync(stop);

        DapVariable point = Assert.Single(locals, v => v.Name == "point");
        Assert.Equal("(1.5, -2)", point.Value);
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.True((await host.EvaluateAsync(frame, "Library.Point2.Calls")).Result == "0", "The point's ToString ran in the program to show a value its fields give");

        DapVariable root = Assert.Single(locals, v => v.Name == "root");
        Assert.Equal("root", root.Value);
        List<DapVariable> opened = await host.VariablesAsync(root.VariablesReference);
        Assert.True(opened.Select(r => r.Name).SequenceEqual(["Speed", "Own", "Children", "Raw View"]),
            $"The script's value opens to {Listed(opened)}, where Speed (marked), Own, then Children and Raw View were to be");
        DapVariable children = opened.Single(r => r.Name == "Children");
        Assert.Equal("root.GetChildren()", children.EvaluateName);
        DapVariable kid = Assert.Single(await host.VariablesAsync(children.VariablesReference));
        Assert.Equal("kid", kid.Value);
        List<DapVariable> raw = await host.VariablesAsync(opened.Single(r => r.Name == "Raw View").VariablesReference);
        Assert.True(raw.Any(r => r.Name == "Depth") && raw.Any(r => r.Name == "Name") && raw.Any(r => r.Name == "Rotation") && raw.Any(r => r.Name == "Own"),
            $"Raw View lists {Listed(raw)}, without the library's members beside the script's");

        DapVariable labelled = Assert.Single(locals, v => v.Name == "labelled");
        Assert.Equal("Labelled \"own\"", labelled.Value);
        List<DapVariable> own = await host.VariablesAsync(labelled.VariablesReference);
        Assert.True(own.Any(r => r.Name == "Label") && own.Any(r => r.Name == "Depth") && !own.Any(r => r.Name == "Children"),
            $"A type with a [DebuggerDisplay] of its own opens to {Listed(own)}, not to its members as before");
    }

    /// <summary>A [DebuggerTypeProxy] naming its generic view closed (typeof(BagView&lt;int&gt;)) on a type that is not
    /// generic opens the value to the view's rows, made with the type arguments the name gives.</summary>
    [Fact]
    public async Task A_debugger_view_named_closed_shows_the_rows_of_a_value_of_a_type_that_is_not_generic()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, null);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        DapVariable bag = Assert.Single(await host.LocalsAsync(stop), v => v.Name == "bag");
        List<DapVariable> rows = await host.VariablesAsync(bag.VariablesReference);
        Assert.True(rows.Take(3).Select(r => r.Value).SequenceEqual(["5", "6", "7"]) && rows.Any(r => r.Name == "Raw View"),
            $"The bag opens to {Listed(rows)}, where its view's 5, 6 and 7 and a Raw View were to be; the engine said "
            + string.Join(" | ", host.EngineLog().Split('\n').Where(l => l.Contains("proxy", StringComparison.OrdinalIgnoreCase))));
    }

    private static string Listed(List<DapVariable> rows) => string.Join(", ", rows.Select(r => $"{r.Name} = {r.Value}"));

    private static async Task Launch(DebugSession host, IReadOnlyList<DapTypePresentation>? presentations)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["presented"], presentations: presentations)
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Debuggees.Line("PassengerCases", "Presented.cs", "Console.WriteLine($\"presented")], Debuggees.Source("PassengerCases", "Presented.cs"))
            .ConfigurationDoneAsync();
    }
}

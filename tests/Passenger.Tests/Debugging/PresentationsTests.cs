using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Type presentations a client gives the debugger at launch, for a library's types that carry no debugger
/// attributes of their own (PassengerCasesProxies' Presentable.cs, in PassengerCases' presented case), a debugger view
/// a type names closed, displays and views an assembly declares for a type (Targeted.cs), and a dictionary's rows.</summary>
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

    /// <summary>A dictionary's key–value rows, from the runtime's own view of it, each name its value's own type in the type
    /// column, where they named the view's item type: an int's int, and in a dictionary of objects each value's type, a
    /// null's the type it is declared as.</summary>
    [Fact]
    public async Task A_dictionarys_rows_name_the_types_of_their_values()
    {
        await using DebugSession host = await LaunchTargeted();
        List<DapVariable> locals = await host.LocalsAsync(await host.WaitForStoppedEvent());
        List<DapVariable> ages = await host.VariablesAsync(Assert.Single(locals, v => v.Name == "ages").VariablesReference);
        Assert.True(ages.FirstOrDefault() is { Name: "[\"ann\"]", Value: "31", Type: "int" }, $"The ages open to {Typed(ages)}, where [\"ann\"] = 31 (int) was to be first");
        List<DapVariable> mixed = await host.VariablesAsync(Assert.Single(locals, v => v.Name == "mixed").VariablesReference);
        Assert.True(mixed.Take(3).Select(r => (r.Name, r.Value, r.Type)).SequenceEqual([("[\"text\"]", "\"words\"", "string"), ("[\"number\"]", "4.5", "double"), ("[\"none\"]", "null", "object")]),
            $"The mixed values open to {Typed(mixed)}, each value's own type not named");
    }

    /// <summary>A [DebuggerDisplay] and a [DebuggerTypeProxy] the program's assembly declares for a type it names (Target,
    /// TargetTypeName) show its values as the type's own would: the runtime's Version and a library's Gauge read as their
    /// displays say, and a library's Bookcase opens to the program's view of it, then Raw View.</summary>
    [Fact]
    public async Task An_assemblys_display_and_view_for_a_type_show_its_values_as_its_own_attributes_would()
    {
        await using DebugSession host = await LaunchTargeted();
        List<DapVariable> locals = await host.LocalsAsync(await host.WaitForStoppedEvent());
        Assert.True(locals.Single(v => v.Name == "version") is { Value: "v1.2", Type: "System.Version" }, $"The version reads {Typed(locals.Where(v => v.Name == "version"))}, not as the assembly's display says");
        Assert.Equal("2.5 bar", locals.Single(v => v.Name == "gauge").Value);
        List<DapVariable> bookcase = await host.VariablesAsync(locals.Single(v => v.Name == "bookcase").VariablesReference);
        Assert.True(bookcase.Select(r => (r.Name, r.Value)).SequenceEqual([("Count", "2"), ("First", "\"Dune\""), ("Raw View", "")]),
            $"The bookcase opens to {Typed(bookcase)}, not to the view the program's assembly declares for it");
    }

    private static string Listed(List<DapVariable> rows) => string.Join(", ", rows.Select(r => $"{r.Name} = {r.Value}"));

    private static string Typed(IEnumerable<DapVariable> rows) => string.Join(", ", rows.Select(r => $"{r.Name} = {r.Value} ({r.Type})"));

    private async Task<DebugSession> LaunchTargeted()
    {
        DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["targeted"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([Debuggees.Line("PassengerCases", "Targeted.cs", "Console.WriteLine($\"targeted")], Debuggees.Source("PassengerCases", "Targeted.cs"))
            .ConfigurationDoneAsync();
        return host;
    }

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

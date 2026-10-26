using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>What the Locals scope lists in the shapes Passenger's tests add (PassengerCases).</summary>
[Collection(DebugSession.Collection)]
public class VariablesCasesTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task A_parameter_a_lambda_captures_is_listed_once_with_its_live_value()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine(f());");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);

        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        List<DapVariable> locals = await host.LocalsAsync(stop);
        Assert.Equal("41", Assert.Single(locals, v => v.Name == "captured").Value);
        Assert.Single(locals, v => v.Name == "f");
    }

    [Fact]
    public async Task An_exception_wrapping_another_reports_the_inner_one()
    {
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", Debuggees.Line("PassengerCases", "Console.WriteLine(f());"), [DapExceptionFilters.All]);

        // Every exception stops, the earlier cases' too.
        int wrapping = Debuggees.Line("PassengerCases", "throw new InvalidOperationException(\"Message2\", e1)");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        for (int stops = 0; stop.ReadStopInfo().line != wrapping && stops < 10; stops++)
        {
            stop = await host.ContinueAsync().WaitForStoppedEvent();
        }
        Assert.Equal((StopReasons.Exception, wrapping), (stop.Reason, stop.ReadStopInfo().line));

        ExceptionInfoResponse info = await host.RequestAsync<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stop.ThreadId!.Value));
        Assert.Equal("System.InvalidOperationException", info.Details?.FullTypeName);
        DapExceptionDetails inner = Assert.Single(info.Details!.InnerException!);
        Assert.Equal(("System.Exception", "Message1", "$exception.InnerException"), (inner.FullTypeName, inner.Message, inner.EvaluateName));
        Assert.Empty(inner.InnerException!);
        Assert.Contains("Innermost exception: System.Exception", info.Description, StringComparison.Ordinal);
    }

    /// <summary>A C# 15 union shows the value it holds as that value's type, by its row and by evaluation, and opens to that
    /// value's members, one holding none showing null.</summary>
    [Fact]
    public async Task A_union_shows_the_value_it_holds_as_that_values_type_opening_to_its_members()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine($\"unions");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        List<DapVariable> locals = await host.LocalsAsync(stop);
        DapVariable Local(string name) => Assert.Single(locals, v => v.Name == name);
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        foreach ((string name, string type, string held) in new[] { ("cat", "Cat", "Tom"), ("dog", "Dog", "Rex"), ("bird", "Bird", "Tweety") })
        {
            DapVariable pet = Local(name);
            string shown = $"{type} {{ Name = {held} }}";
            Assert.True((pet.Type, pet.Value) == (type, shown), $"The Pet holding a {type} shows as {pet.Value} ({pet.Type}), not the {type} it holds");
            List<DapVariable> members = await host.VariablesAsync(pet.VariablesReference);
            Assert.Equal($"\"{held}\"", Assert.Single(members, m => m.Name == "Name").Value);
            EvaluateResponse evaluated = await host.EvaluateAsync(frame, name);
            Assert.Equal((shown, type), (evaluated.Result, evaluated.Type));
        }
        DapVariable none = Local("none");
        Assert.Equal(("null", "Pet", 0), (none.Value, none.Type, none.VariablesReference));
    }

    [Fact]
    public async Task DebuggerDisplay_renders_as_Visual_Studio_does()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine(\"displays\");");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        List<DapVariable> locals = await host.LocalsAsync(stop);
        DapVariable Local(string name) => Assert.Single(locals, v => v.Name == name);

        // A string fragment is quoted unless ",nq" says not; \{ and \} are braces.
        Assert.Equal("12.5 USD", Local("money").Value);
        Assert.Equal("12.5 \"USD\"", Local("quoted").Value);
        Assert.Equal("{3}", Local("braced").Value);
        // A fragment that fails says so in its own place; the rest still shows.
        Assert.StartsWith("Value=2 Bad=<error: The name 'NoSuchMember' does not exist", Local("missing").Value, StringComparison.Ordinal);
        Assert.Equal("head -> tail", Local("head").Value);
        Assert.StartsWith("tail -> <error:", Local("tail").Value, StringComparison.Ordinal);
        // Type= is the type column; Name= names a member's row, not a local's.
        Assert.Equal(("11", "RetypedThing"), (Local("labelled").Value, Local("labelled").Type));
        List<DapVariable> holder = await host.VariablesAsync(Local("holder").VariablesReference);
        DapVariable item = Assert.Single(holder);
        Assert.Equal(("renamed", "11", "RetypedThing"), (item.Name, item.Value, item.Type));
        // The attribute is inherited; a value inside a display is shown by its own.
        Assert.Equal("base b", Local("derived").Value);
        Assert.Equal("box of 1 EUR", Local("box").Value);
        List<DapVariable> entries = await host.VariablesAsync(Local("byName").VariablesReference);
        DapVariable entry = Assert.Single(entries, e => e.Name != "Raw View");
        Assert.Equal(("[\"origin\"]", "(0, 0)"), (entry.Name, entry.Value));

        // RootHidden lifts an object's members as it does an array's elements.
        List<DapVariable> lifted = await host.VariablesAsync(Local("lifted").VariablesReference);
        Assert.Equal(["A", "B"], lifted.Select(v => v.Name).Order());
        // A proxy whose constructor throws leaves the value's own members, not the exception's.
        List<DapVariable> angry = await host.VariablesAsync(Local("angry").VariablesReference);
        Assert.Equal("1", Assert.Single(angry, v => v.Name == "Real").Value);
        Assert.DoesNotContain(angry, v => v.Name is "_message" or "StackTrace");
    }

    /// <summary>A value whose ToString throws shows its type and still opens to its members.</summary>
    [Fact]
    public async Task A_value_whose_ToString_throws_shows_its_type_and_opens_to_its_members()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine(\"displays\");");
        await using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        DapVariable unprintable = Assert.Single(await host.LocalsAsync(stop), v => v.Name == "unprintable");
        Assert.True(unprintable.Value == "{Unprintable}" && unprintable.VariablesReference > 0,
            $"The value whose ToString throws shows \"{unprintable.Value}\" with {(unprintable.VariablesReference > 0 ? "" : "no ")}members to open");
        List<DapVariable> members = await host.VariablesAsync(unprintable.VariablesReference);
        Assert.Equal("5", Assert.Single(members, v => v.Name == "Kept").Value);

        // Evaluated, it is the same value, not a failure.
        EvaluateResponse evaluated = await host.EvaluateAsync((await host.TopFrameAsync(stop.ThreadId!.Value)).Id, "unprintable");
        Assert.Equal("{Unprintable}", evaluated.Result);
    }
}

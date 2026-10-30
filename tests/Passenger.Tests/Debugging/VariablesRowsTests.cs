using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>The rows a stop's values list in PassengerCases' rows case: what a value opens to, what a frame lists as its
/// variables, and which rows reach only the debugger's copies.</summary>
[Collection(DebugSession.Collection)]
public class VariablesRowsTests(ITestOutputHelper testOutputHelper)
{
    /// <summary>An indexer is no row: a class with one, a list's and a dictionary's raw views and a tuple list none, and no
    /// getter is called in the program to show one; the members beside it are listed as before.</summary>
    [Fact]
    public async Task An_indexer_is_no_row_of_its_object_and_nothing_is_evaluated_for_one()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"rows {shelf[0]}");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        List<DapVariable> locals = await host.LocalsAsync(stop);

        List<(string Owner, DapVariable Row)> rows = [];
        foreach (string name in (string[])["shelf", "names", "ages", "pair"])
        {
            List<DapVariable> members = await host.VariablesAsync(Assert.Single(locals, v => v.Name == name).VariablesReference);
            if (members.FirstOrDefault(m => m.Name == "Raw View") is { } raw)
            {
                members.AddRange(await host.VariablesAsync(raw.VariablesReference));
            }
            rows.AddRange(members.Select(member => (name, member)));
        }
        string[] indexers = [.. rows.Where(r => r.Row.Name == "Item" || r.Row.Name.EndsWith(".Item", StringComparison.Ordinal)).Select(r => $"{r.Owner}: {r.Row.Name} = {r.Row.Value}")];
        Assert.True(indexers.Length == 0, $"An indexer is listed as a row, its getter called with no index: {string.Join("; ", indexers)}");
        Assert.True(rows.Any(r => r is ("shelf", { Name: "Count", Value: "2" })) && rows.Any(r => r is ("names", { Name: "Count", Value: "2" })),
            $"The members beside the indexers are not listed as before: {string.Join("; ", rows.Select(r => $"{r.Owner}: {r.Row.Name} = {r.Row.Value}"))}");
        Assert.DoesNotContain("TargetParameterCountException", host.EngineLog(), StringComparison.Ordinal);
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        Assert.Equal("0", (await host.EvaluateAsync(frame, "Shelf.Reads")).Result);
    }

    /// <summary>A local only a local function captures is listed under its own name, with its live value, in the frame that
    /// declares it and in the local function's, with no row of the closure holding it nor that closure in the local
    /// function's frame name; an iterator's frame lists its own variables and none of its enumerator's properties.</summary>
    [Fact]
    public async Task A_local_a_local_function_captures_is_listed_in_both_frames_and_an_iterators_frame_lists_no_current()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"called {Call(1)}\");", "return called;", "yield return taken;");

        StoppedEvent declaring = await host.WaitForStoppedEvent();
        List<DapVariable> outer = await host.LocalsAsync(declaring);
        Assert.True(outer.SingleOrDefault(v => v.Name == "captive")?.Value == "5" && !outer.Any(Generated),
            $"Run's frame lists {Listed(outer)}, where captive = 5 is to be listed by its name and no closure");

        StoppedEvent called = await host.ContinueAsync().WaitForStoppedEvent();
        DapStackFrame local = await host.TopFrameAsync(called.ThreadId!.Value);
        List<DapVariable> inner = await host.LocalsAsync(called);
        Assert.True(inner.SingleOrDefault(v => v.Name == "captive")?.Value == "5" && inner.SingleOrDefault(v => v.Name == "called")?.Value == "6" && !inner.Any(Generated),
            $"Call's frame lists {Listed(inner)}, where x = 1, called = 6 and captive = 5 are to be listed by their names and no closure");
        Assert.DoesNotContain("DisplayClass", local.Name, StringComparison.Ordinal);

        StoppedEvent iterating = await host.ContinueAsync().WaitForStoppedEvent();
        List<DapVariable> iterator = await host.LocalsAsync(iterating);
        Assert.True(iterator.SingleOrDefault(v => v.Name == "count")?.Value == "2" && iterator.SingleOrDefault(v => v.Name == "taken")?.Value == "0"
            && !iterator.Any(v => v.Name.Contains("Current", StringComparison.Ordinal)),
            $"The iterator's frame lists {Listed(iterator)}, where count = 2 and taken = 0 are to be listed and no Current");

        static bool Generated(DapVariable row) => row.Name.Contains('<') || row.Name.Contains('$') || row.Name.StartsWith("arg_", StringComparison.Ordinal)
            || row.Type?.Contains("DisplayClass", StringComparison.Ordinal) == true;

        static string Listed(List<DapVariable> rows) => string.Join(", ", rows.Select(v => $"{v.Name} = {v.Value}"));
    }

    /// <summary>Each row's evaluate name is C# the evaluator takes, reading the row's own value: a HashSet's elements, a
    /// dictionary's entry and its Key and Value, a list's element (by its index in the list, which Set Value writes) and the
    /// members of its raw view, an explicit interface implementation's among them, and a tuple's item; Raw View, a row the
    /// debugger adds, has none.</summary>
    [Fact]
    public async Task Each_rows_evaluate_name_reads_its_own_value_and_a_row_the_debugger_adds_has_none()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"rows {shelf[0]}");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        List<DapVariable> locals = await host.LocalsAsync(stop);

        List<DapVariable> set = await Opened("set");
        List<DapVariable> ages = await Opened("ages");
        DapVariable entry = Assert.Single(ages, row => row.Name != "Raw View");
        List<DapVariable> names = await Opened("names");
        DapVariable raw = Assert.Single(names, row => row.Name == "Raw View");
        List<DapVariable> listed = await host.VariablesAsync(raw.VariablesReference);
        List<DapVariable> rows =
        [
            .. set.Where(row => row.Name != "Raw View"), entry, .. await host.VariablesAsync(entry.VariablesReference), names[0], .. listed.Where(row => row.VariablesReference == 0),
            .. await Opened("pair"),
        ];
        Assert.Contains(listed, row => row.Name.Contains("ICollection<T>.IsReadOnly", StringComparison.Ordinal));
        Assert.Equal("names[0]", names[0].EvaluateName);
        List<string> wrong = [];
        foreach (DapVariable row in rows)
        {
            EvaluateResponse? read = row.EvaluateName is { } expression ? await host.EvaluateAsync(frame, expression) : null;
            if (read?.Result != row.Value)
            {
                wrong.Add($"{row.Name} = {row.Value} by {row.EvaluateName ?? "no expression"}: {read?.Result}");
            }
        }
        Assert.True(wrong.Count == 0, $"Rows whose evaluate name does not read their value: {string.Join("; ", wrong)}");
        Assert.True(raw.EvaluateName is null, $"Raw View, a row the debugger adds, is given the expression {raw.EvaluateName}");

        async Task<List<DapVariable>> Opened(string local) => await host.VariablesAsync(Assert.Single(locals, v => v.Name == local).VariablesReference);
    }

    /// <summary>Each row reads its own value by its evaluate name where its owner is held as another type, is named by a
    /// keyword or is static: a boxed List's and array's elements, an IEnumerable-held HashSet's and List's, a local @class, a
    /// field holder.@event, and a type's static members (Shelf.Reads, Keyworded.@static).</summary>
    [Fact]
    public async Task Rows_held_as_another_type_named_by_a_keyword_or_static_read_their_own_value_by_their_evaluate_name()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"rows {shelf[0]}");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        List<DapVariable> locals = await host.LocalsAsync(stop);

        List<DapVariable> holder = await Opened("holder");
        List<DapVariable> rows =
        [
            .. Elements(await Opened("boxedList")), .. Elements(await Opened("boxedArray")), .. Elements(await Opened("setAsEnumerable")),
            .. Elements(await Opened("listAsEnumerable")), Assert.Single(locals, v => v.Name == "class"), Assert.Single(holder, row => row.Name == "event"),
            .. await StaticsOf(holder), .. await StaticsOf(await Opened("shelf")),
        ];
        string[] named = [.. rows.Select(row => $"{row.Name}: {row.EvaluateName}")];
        Assert.True(rows.Count == 8, $"The rows are {string.Join("; ", named)}, where four elements, class, event, static and Reads are to be listed");
        Assert.Contains("class: @class", named);
        Assert.Contains("event: holder.@event", named);
        Assert.Contains("static: Keyworded.@static", named);
        Assert.Contains("Reads: Shelf.Reads", named);
        List<string> wrong = [];
        foreach (DapVariable row in rows)
        {
            EvaluateResponse? read = row.EvaluateName is { } expression ? await host.EvaluateAsync(frame, expression) : null;
            if (read?.Result != row.Value)
            {
                wrong.Add($"{row.Name} = {row.Value} by {row.EvaluateName ?? "no expression"}: {read?.Result}");
            }
        }
        Assert.True(wrong.Count == 0, $"Rows whose evaluate name does not read their value, so Set Value, Add to Watches and Inspect fail on them: {string.Join("; ", wrong)}");

        async Task<List<DapVariable>> Opened(string local) => await host.VariablesAsync(Assert.Single(locals, v => v.Name == local).VariablesReference);

        static IEnumerable<DapVariable> Elements(List<DapVariable> rows) => rows.Where(row => row.Name.StartsWith('['));

        async Task<List<DapVariable>> StaticsOf(List<DapVariable> members) =>
            await host.VariablesAsync(Assert.Single(members, row => row.Name == "Static members").VariablesReference);
    }

    /// <summary>The rows a debugger view shows a collection by, a HashSet's elements and a dictionary's entry with its Key
    /// and Value, say they are read-only, being the view's copies; a list's elements, reached by their index in it, and the
    /// members of a HashSet's raw view, its own, do not.</summary>
    [Fact]
    public async Task A_collections_rows_through_its_view_are_read_only_and_a_lists_elements_and_raw_members_are_not()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"rows {shelf[0]}");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        List<DapVariable> locals = await host.LocalsAsync(stop);

        List<DapVariable> set = await Opened("set");
        DapVariable entry = Assert.Single(await Opened("ages"), row => row.Name != "Raw View");
        List<DapVariable> copies = [.. set.Where(row => row.Name != "Raw View"), entry, .. await host.VariablesAsync(entry.VariablesReference)];
        List<DapVariable> names = await Opened("names");
        List<DapVariable> own = [.. names.Where(row => row.Name != "Raw View"), .. await host.VariablesAsync(Assert.Single(set, row => row.Name == "Raw View").VariablesReference)];
        Assert.True(copies.Count == 5 && copies.All(ReadOnly), $"The view's rows are {Listed(copies)}, where the set's two elements and the entry with its Key and Value are each to be read-only");
        Assert.True(own.Count > 2 && !own.Any(ReadOnly), $"The list's elements and the set's raw members are {Listed(own)}, where none is to be read-only");

        async Task<List<DapVariable>> Opened(string local) => await host.VariablesAsync(Assert.Single(locals, v => v.Name == local).VariablesReference);

        static bool ReadOnly(DapVariable row) => row.PresentationHint?.Attributes?.Contains("readOnly") == true;

        static string Listed(List<DapVariable> rows) => string.Join(", ", rows.Select(row => $"{row.Name} = {row.Value}{(ReadOnly(row) ? " (read-only)" : "")}"));
    }

    /// <summary>A nullable struct's member under its Value reads by its evaluate name, through the Value, and takes a value
    /// set there, which the program goes on with.</summary>
    [Fact]
    public async Task A_nullable_structs_member_under_its_value_reads_by_its_evaluate_name_and_takes_a_value()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        await Launch(host, "Console.WriteLine($\"rows {shelf[0]}");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int frame = (await host.TopFrameAsync(stop.ThreadId!.Value)).Id;
        List<DapVariable> locals = await host.LocalsAsync(stop);

        DapVariable held = Assert.Single(await host.VariablesAsync(Assert.Single(locals, v => v.Name == "place").VariablesReference), row => row.Name == "Value");
        DapVariable x = Assert.Single(await host.VariablesAsync(held.VariablesReference), row => row.Name == "X");
        Assert.Equal(("place.Value.X", "3"), (x.EvaluateName, x.Value));
        Assert.Equal("3", (await host.EvaluateAsync(frame, x.EvaluateName!)).Result);
        Assert.Equal("10", (await host.RequestAsync<SetExpressionResponse>(DapCommands.SetExpression, new SetExpressionArguments(x.EvaluateName!, "10", frame))).Value);
        Assert.True((await host.EvaluateAsync(frame, "place.Value.X + place.Value.Y")).Result == "14", $"place.Value.X = 10 left place {(await host.EvaluateAsync(frame, "place")).Result}");

        await host.ContinueAsync();
        (string output, _) = await host.WaitForEnd();
        Assert.Contains("rows a 2 2 1 (1, one) 10", output, StringComparison.Ordinal);
    }

    /// <summary>PassengerCases launched on its rows case, stopping at the lines with this code on them.</summary>
    private static async Task Launch(DebugSession host, params string[] codes)
    {
        await host
            .InitializeAsync()
            .LaunchAsync(Debuggees.Program("PassengerCases"), stopAtEntry: false, justMyCode: true, arguments: ["rows"])
            .WaitForInitializedEvent();
        await host.BreakpointsAsync([.. codes.Select(code => Debuggees.Line("PassengerCases", code))], Debuggees.Source("PassengerCases", "Program.cs"))
            .ConfigurationDoneAsync();
    }
}

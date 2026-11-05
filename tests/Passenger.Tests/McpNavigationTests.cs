using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) finding its way through C# code: the solution's own symbols found
/// without a source generator's, the name itself first, usages read by file with the symbol the place took, a generator's
/// place (a usage, a declaration) in a copy of its code with its line's text, a place with no symbol said to be one, or
/// the symbol its line declares taken; symbols named rather than placed, found exactly, each overload told apart by its
/// documentation id, an indexer by this[] or Item; a hover card as plain text, and a fix's Suppress and Configure choices
/// counted until asked for; and calls on many files leaving open, in the IDE and the language server, only the file the
/// agent opened and the one it left unsaved.</summary>
public sealed class McpNavigationTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpNavigationTests(RestoredFolders folders)
        : base(folders)
    {
    }

    private const string Words = "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
        + "    [GeneratedRegex(@\"\\w+\")]\n    private static partial Regex Word();\n\n"
        + "    public static int Count(string text) => Word().Count(text);\n\n"
        + "    public static int Count(string text, int from) => Word().Count(text[from..]);\n}\n";

    private const string Shapes = "using System.Text.Json.Serialization;\n\n[JsonSerializable(typeof(int))]\npartial class ShapeContext : JsonSerializerContext\n{\n}\n\n"
        + "static class UsesContext\n{\n    public static object Info => ShapeContext.Default.Int32;\n}\n";

    private const string Tally = "static class Tally\n{\n    public static int Of(string text) => Words.Count(text) + Words.Count(text, 1);\n\n"
        + "    public static int CountAll(string[] texts) => texts.Sum(Words.Count);\n}\n";

    /// <summary>A symbol of a project a solution filter leaves out, which C# reads only as its built assembly, is declared in
    /// that project's own file, never in the server's stub of the assembly: a method, a property and a primary constructor,
    /// and a method whose source takes another parameter than the assembly built before it; the framework's still a stub.</summary>
    [Fact]
    public async Task Go_to_declaration_into_a_project_a_solution_filter_leaves_out_opens_its_source_at_the_declaration()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-unloaded-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Greeter.cs", "namespace Lib;\n\npublic sealed class Greeter(string who)\n{\n    public string Who { get; } = who;\n\n"
            + "    public string Greet(string greeting) => $\"{greeting}, {Who}\";\n}\n");
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console().With("  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "App/Program.cs", "Lib.Greeter greeter = new(\"Ada\");\nSystem.Console.WriteLine(greeter.Greet(\"Hello\") + greeter.Who);\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "App.slnf", "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [ \"App\\\\App.csproj\" ]\n  }\n}\n");
        await Restore.RunAsync(Path.Combine(folder, "Shop.slnx"));
        // Built by one server for the next to load: an assembly missing as a server loads its project stays missing from it.
        await using (McpClient building = await Start(["--mcp", Path.Combine(folder, "App.slnf")]))
        {
            JsonElement built = await Call(building, McpTools.Build);
            Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");
        }
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);

        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync(client, 2, 34));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 5), await DeclaredAsync(client, 2, 59));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 3), await DeclaredAsync(client, 1, 23));
        // Its source changed since its assembly was built: the method of that name, however many parameters it takes now.
        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("Greet(string greeting)", "Greet(string greeting, int times = 1)", StringComparison.Ordinal));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync(client, 2, 34));
        // Beside an overload taking as many parameters of another type, the one the assembly's signature names, alone.
        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("    public string Greet(string greeting, int times = 1)",
            "    public string Greet(int times) => times.ToString();\n\n    public string Greet(string greeting)", StringComparison.Ordinal));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 9), await DeclaredAsync(client, 2, 34));
        (string console, _) = await DeclaredAsync(client, 2, 16);
        Assert.Contains("MetadataAsSource", console, StringComparison.Ordinal);

        async Task<(string Path, int Line)> DeclaredAsync(McpClient asking, int line, int column)
        {
            JsonElement place = Assert.Single((await Call(asking, McpTools.Definition, new() { ["path"] = "App/Program.cs", ["line"] = line, ["column"] = column })).EnumerateArray());
            return (place.GetProperty("path").GetString()!, place.GetProperty("line").GetInt32());
        }
    }

    [Fact]
    public async Task An_agent_finds_the_solutions_own_symbols_reads_usages_in_order_and_hears_when_no_symbol_is_where_it_asked()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Shapes.cs", Shapes);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The regex generator declares Words and Word again in a file on no disk, which the agent could never open.
        JsonElement[] found = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word" })).EnumerateArray()];
        Assert.True(found.Length > 0 && found.All(s => s.GetProperty("path").GetString() == "Words.cs"),
            $"find_symbol Word answered places beyond the solution's own files: {string.Join(", ", found.Select(s => s.GetRawText()))}");

        JsonElement used = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 });
        (string Path, int Line, int Column, string Text)[] uses = [.. Used(used)];
        Assert.Equal(["Words.cs:6", "Words.cs:8", "Words.cs:10"], uses.Take(3).Select(u => $"{u.Path}:{u.Line}"));
        // The generator's declaration last, in a copy of its code the agent can read, at its line with that line's code.
        Assert.True(uses.Length == 4 && uses[3].Path.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", StringComparison.Ordinal)
            && uses[3].Text.Contains("Regex Word()", StringComparison.Ordinal) && File.ReadAllLines(uses[3].Path)[uses[3].Line - 1].Contains("Regex Word()", StringComparison.Ordinal),
            $"usages of a GeneratedRegex method answered no readable place in the generator's code: {string.Join(", ", uses.Select(u => $"{u.Path}:{u.Line} {u.Text}"))}");
        Assert.Equal("Words.Word() (method)", used.GetProperty("symbol").GetString());
        JsonElement made = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "Shapes.cs", ["line"] = 10, ["column"] = 48 })).EnumerateArray());
        Assert.True(made.GetProperty("path").GetString()!.EndsWith($"{Path.DirectorySeparatorChar}ShapeContext.g.cs", StringComparison.Ordinal)
            && made.GetProperty("text").GetString()!.Contains(" Default ", StringComparison.Ordinal),
            $"definition of a JSON context's Default answered no place in the generator's code with its text: {made.GetRawText()}");

        // By file, each named once, then line, whichever the server found first, after the symbol the place took.
        JsonElement counted = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 23 });
        Assert.Equal(["Aaa.cs:3:48", "Aaa.cs:5:67", "Words.cs:8:23"], Used(counted).Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
        Assert.Equal(("Words.Count(string) (method)", 3, 2), (counted.GetProperty("symbol").GetString(), counted.GetProperty("count").GetInt32(), counted.GetProperty("files").GetArrayLength()));

        // A column off the name, on the line that declares it, still finds it.
        JsonElement fromStart = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 1 });
        Assert.Equal(counted.GetRawText(), fromStart.GetRawText());
        string declared = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Words.cs", ["line"] = 10, ["column"] = 2 })).GetString()!;
        Assert.Contains("int Words.Count(string text, int from)", declared, StringComparison.Ordinal);

        CallToolResult nothing = await Result(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 2, ["column"] = 1 });
        Assert.True(nothing.IsError == true && Text(nothing).Contains("No symbol at Words.cs:2:1", StringComparison.Ordinal),
            $"usages where no symbol is answered: {Text(nothing)}");
    }

    /// <summary>The code tools on a source generator's code copied, where usages led the agent, answer at once: its outline
    /// from its own text, and what needs the language server, which never holds a copy, refused in the window's words,
    /// naming the generator and the project.</summary>
    [Fact]
    public async Task An_agent_asking_about_a_generators_code_where_usages_led_it_is_answered_from_its_text_or_refused_at_once()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        await using McpClient client = await Start(["--mcp", Folder]);
        (string path, int line, int column, _) = Used(await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 }))[^1];
        Assert.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", path, StringComparison.Ordinal);

        const string Refused = $"RegexGenerator.g.cs is a source generator's code, made by RegexGenerator for Sum ({TestProjects.Framework}), copied to read: "
            + "the C# language server answers in the code it is made from";
        foreach ((string tool, Dictionary<string, object?> arguments) in new (string, Dictionary<string, object?>)[]
        {
            (McpTools.SymbolInfo, new() { ["path"] = path, ["line"] = line, ["column"] = column }),
            (McpTools.Diagnostics, new() { ["path"] = path }),
            (McpTools.Definition, new() { ["path"] = path, ["line"] = line, ["column"] = column }),
        })
        {
            System.Diagnostics.Stopwatch asked = System.Diagnostics.Stopwatch.StartNew();
            CallToolResult refused = await Result(client, tool, arguments);
            Assert.True(refused.IsError == true && Text(refused).Contains(Refused, StringComparison.Ordinal),
                $"{tool} on the generator's code copied answered after {asked.Elapsed.TotalSeconds:0.0} s: {Text(refused)}");
        }

        string outline = (await Call(client, McpTools.Outline, new() { ["path"] = path })).GetString()!;
        Assert.True(outline.StartsWith("Words (class, line ", StringComparison.Ordinal) && outline.Contains($"\n  Word() (method, line {line})\n", StringComparison.Ordinal),
            $"outline of the generator's code copied is not what its text declares, Word() on line {line}:\n{outline}");
    }

    [Fact]
    public async Task An_agent_names_a_symbol_in_place_of_its_place_finds_names_exactly_and_tells_overloads_apart_by_their_documentation_ids()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Figures.cs", Figures);
        await using McpClient client = await Start(["--mcp", Folder]);

        // Go to Symbol's matches take in CountAll; an exact find, the two overloads alone, each by its id.
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count" })).EnumerateArray(), s => s.GetProperty("name").GetString() == "CountAll");
        Assert.Equal(["Count Words.cs:8 M:Words.Count(System.String)", "Count Words.cs:10 M:Words.Count(System.String,System.Int32)"],
            (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count", ["exact"] = true })).EnumerateArray()
                .Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("path").GetString()}:{s.GetProperty("line").GetInt32()} {s.GetProperty("id").GetString()}"));

        string both = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Words.Count" })).GetString()!;
        Assert.True(both.Contains("Words.Count(string) (method) at Words.cs:8:23, M:Words.Count(System.String)", StringComparison.Ordinal)
            && both.Contains("Words.Count(string, int) (method) at Words.cs:10:23, M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal)
            && both.Contains("int Words.Count(string text, int from)", StringComparison.Ordinal), $"symbol_info of Words.Count answered:\n{both}");
        Assert.EndsWith("Documentation id: T:System.Text.RegularExpressions.Regex",
            (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 28 })).GetString(), StringComparison.Ordinal);

        Assert.Equal(["Aaa.cs:3:68", "Words.cs:10:23"], Used(await Call(client, McpTools.Usages, new() { ["symbol"] = "Words.Count(string, int)" }))
            .Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
        CallToolResult ambiguous = await Result(client, McpTools.Usages, new() { ["symbol"] = "Count" });
        Assert.True(ambiguous.IsError == true && Text(ambiguous).Contains("Count names 2 symbols", StringComparison.Ordinal)
            && Text(ambiguous).Contains("M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal), $"usages of a name two overloads share answered: {Text(ambiguous)}");
        JsonElement declared = Assert.Single((await Call(client, McpTools.Definition, new() { ["symbol"] = "M:Words.Count(System.String)" })).EnumerateArray());
        Assert.Equal(("Words.cs", 8), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        Assert.Equal(["Words.Count(string)", "Words.Count(string, int)"],
            (await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Words.Word" })).EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        CallToolResult ofType = await Result(client, McpTools.Hierarchy, new() { ["symbol"] = "Words" });
        Assert.True(ofType.IsError == true && Text(ofType).Contains("Words is a type, which has no callers or callees: direction supertypes", StringComparison.Ordinal),
            $"hierarchy of a type, its direction left as callers, answered: {Text(ofType)}");
        Assert.Equal(["Figures.cs:3:28"], Placed(await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Square.Area", ["direction"] = "supertypes" })));
        Assert.Equal(["Figures.cs:8:28"], Placed(await Call(client, McpTools.Hierarchy, new() { ["symbol"] = "Shape.Area", ["direction"] = "subtypes" })));

        Assert.Equal("Renamed in 1 file: Aaa.cs", (await Call(client, McpTools.Rename, new() { ["symbol"] = "Tally.CountAll", ["newName"] = "All" })).GetString());
        Assert.Contains("public static int All(string[] texts)", File.ReadAllText(Path.Combine(Folder, "Aaa.cs")), StringComparison.Ordinal);
    }

    private const string Figures = "abstract class Shape\n{\n    public abstract double Area();\n}\n\n"
        + "sealed class Square(double side) : Shape\n{\n    public override double Area() => side * side;\n}\n";

    /// <summary>Each hierarchy entry's place as "path:line:column".</summary>
    private static IEnumerable<string> Placed(JsonElement entries) =>
        entries.EnumerateArray().Select(e => $"{e.GetProperty("path").GetString()}:{e.GetProperty("line").GetInt32()}:{e.GetProperty("column").GetInt32()}");

    private const string Shelf = "sealed class Shelf\n{\n    private readonly string[] books = [\"a\", \"b\"];\n\n"
        + "    /// <summary>The book_at a place on the shelf, from a List&lt;string&gt; of them.</summary>\n    public string this[int at] => books[at];\n\n"
        + "    public int Size()\n    {\n        int unused = 1;\n        return books.Length;\n    }\n}\n";

    [Fact]
    public async Task An_agent_finds_a_name_itself_first_an_indexer_by_name_reads_a_card_as_plain_text_and_a_fixs_suppressions_counted()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Shelf.cs", Shelf);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The name itself before the names that start with it, each in the type that holds it, not a project's words.
        JsonElement[] counts = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Count" })).EnumerateArray()];
        Assert.Equal(["Count Words", "Count Words", "CountAll Tally"],
            counts.Take(3).Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("container").GetString()}"));

        // An indexer by this[] and by Item, its metadata's name, wherever a symbol is named.
        JsonElement indexer = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "this[]" })).EnumerateArray());
        Assert.Equal(("this[]", "Shelf", "Shelf.cs", 6, "P:Shelf.Item(System.Int32)"), (indexer.GetProperty("name").GetString(), indexer.GetProperty("container").GetString(),
            indexer.GetProperty("path").GetString(), indexer.GetProperty("line").GetInt32(), indexer.GetProperty("id").GetString()));
        Assert.Contains((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Item" })).EnumerateArray(), s => s.GetProperty("id").GetString() == "P:Shelf.Item(System.Int32)");
        JsonElement used = await Call(client, McpTools.Usages, new() { ["symbol"] = "Shelf.Item" });
        Assert.Equal(("Shelf.this[int] (property)", "Shelf.cs:6"), (used.GetProperty("symbol").GetString(), string.Join(", ", Used(used).Select(u => $"{u.Path}:{u.Line}"))));

        // The hover card's words, with none of Markdown's escapes or fences.
        string card = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Shelf.this[int]" })).GetString()!;
        Assert.True(card.Contains("string Shelf.this[int at] { get; }", StringComparison.Ordinal) && card.Contains("The book_at a place on the shelf, from a List<string> of them.", StringComparison.Ordinal)
            && !card.Contains('\\', StringComparison.Ordinal) && !card.Contains("```", StringComparison.Ordinal), $"symbol_info of the indexer said:\n{card}");

        // A warning's Suppress and Configure choices are counted, and listed when the agent names that action.
        Dictionary<string, object?> unused = new() { ["path"] = "Shelf.cs", ["line"] = 10, ["column"] = 13 };
        string suppress = Assert.Single((await Call(client, McpTools.CodeActions, unused)).EnumerateArray().Select(a => a.GetString()!), a => a.StartsWith("Suppress", StringComparison.Ordinal));
        Assert.EndsWith(" choices: title lists them", suppress, StringComparison.Ordinal);
        string title = suppress[..suppress.IndexOf(" [", StringComparison.Ordinal)];
        string listed = Assert.Single((await Call(client, McpTools.CodeActions, new(unused) { ["title"] = title })).EnumerateArray()).GetString()!;
        Assert.Contains("Suppress CS0219 → in Source", listed, StringComparison.Ordinal);
    }

    /// <summary>A call holds its files open while it runs, then keeps the last few the agent asked about: after reads of
    /// many files the IDE lists open only the file the agent opened in the editor and the one its edit could not save (a
    /// read-only file), and the language server holds those and at most <see cref="PassengerTools.FilesKept"/> more, the
    /// most recently asked, so a long session holds no more than it is working on.</summary>
    [Fact]
    public async Task An_agents_calls_on_many_files_leave_open_only_the_file_it_opened_the_one_its_edit_left_unsaved_and_the_last_few_asked()
    {
        await TakeFolderAsync();
        string[] parts = [.. Enumerable.Range(1, 12).Select(i => $"Part{i:00}.cs")];
        foreach (string part in parts)
        {
            Write(part, $"static class {Path.GetFileNameWithoutExtension(part)}\n{{\n    public static int Of(int value) => Helper.Twice(value) + 1;\n}}\n");
        }
        Write("Locked.cs", "static class Locked\n{\n    public static int Of(int value) => value;\n}\n");
        File.SetAttributes(Path.Combine(Folder, "Locked.cs"), FileAttributes.ReadOnly);
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });

        await Call(client, McpTools.Open, new() { ["path"] = "Program.cs" });
        foreach (string file in parts.Append("Program.cs").Append("Helper.cs"))
        {
            await Call(client, McpTools.Outline, new() { ["path"] = file });
            await Call(client, McpTools.Diagnostics, new() { ["path"] = file });
            await Call(client, McpTools.EditorText, new() { ["path"] = file, ["to"] = 2 });
            if (file.StartsWith("Part", StringComparison.Ordinal))
            {
                await Call(client, McpTools.SymbolInfo, new() { ["path"] = file, ["line"] = 3, ["column"] = 23 });
                await Call(client, McpTools.Usages, new() { ["path"] = file, ["line"] = 3, ["column"] = 44 });
            }
        }
        string edited = Text(await Call(client, McpTools.Edit, new() { ["path"] = "Locked.cs", ["oldText"] = "=> value;", ["newText"] = "=> value + 1;" }));
        Assert.Contains("not saved: Locked.cs is read-only", edited, StringComparison.Ordinal);

        string[] open = [.. (await Call(client, McpTools.Workspace)).GetProperty("openFiles").EnumerateArray().Select(f => f.GetString()!)];
        Assert.True(open.SequenceEqual(["Program.cs", "Locked.cs (unsaved)"]),
            $"The IDE lists open {string.Join(", ", open)}, where the agent left open only Program.cs and the unsaved Locked.cs");
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        string[] server = [];
        Assert.True(await Waiting.UntilAsync(() =>
            {
                server = ServerOpen(trace);
                return server.Contains("Program.cs") && server.Contains("Locked.cs") && server.Contains("Helper.cs") && server.Length <= 2 + PassengerTools.FilesKept
                    && !server.Contains(parts[0]);
            }, TimeSpan.FromSeconds(10).Scaled(), Cancel),
            $"The language server was last told open {string.Join(", ", server)}, where the agent left open Program.cs, Locked.cs and at most {PassengerTools.FilesKept} of the files it asked about last, Helper.cs among them");
    }

    /// <summary>A file an agent asks diagnostics of stays open in the language server after the call, so a second ask of it,
    /// unchanged, answers what the first did from the server's last pull, sending none (the protocol trace).</summary>
    [Fact]
    public async Task A_second_diagnostics_of_an_unchanged_file_answers_from_the_servers_last_pull_sending_none()
    {
        await TakeFolderAsync();
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });
        string first = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Contains("unused", first, StringComparison.Ordinal);
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        // The window's own pulls of the file just opened, and one a refresh of the server's as it read the file brings, come first.
        await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" });
        int pulls = -1;
        for (int round = 0; round < 20 && Pulls(trace, "Helper.cs") != pulls; round++)
        {
            pulls = Pulls(trace, "Helper.cs");
            Waiting.For(TimeSpan.FromSeconds(1), "the trace of the first calls' pulls to settle");
        }
        string second = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Equal(first, second);
        Assert.True(Pulls(trace, "Helper.cs") == pulls,
            $"The second ask of Helper.cs, unchanged, sent the language server {Pulls(trace, "Helper.cs") - pulls} pulls of its diagnostics, where its last pull answered");
        Assert.Contains("Helper.cs", ServerOpen(trace));
    }

    /// <summary>How many pulls of a file's diagnostics lsp.log's trace says the language server was sent.</summary>
    private static int Pulls(string trace, string file)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal)
            && l.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DocumentDiagnostic}\"", StringComparison.Ordinal) && l.Contains("/" + file + "\"", StringComparison.Ordinal));
    }

    /// <summary>The names of the files the language server was told open and not since closed, by lsp.log's trace of what it
    /// was sent.</summary>
    private static string[] ServerOpen(string trace)
    {
        Dictionary<string, int> open = new(StringComparer.Ordinal);
        string text;
        using (FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (StreamReader reader = new(read))
        {
            text = reader.ReadToEnd();
        }
        foreach (string line in text.Split('\n'))
        {
            int change = !line.Contains("] --> ", StringComparison.Ordinal) ? 0
                : line.Contains("\"method\":\"textDocument/didOpen\"", StringComparison.Ordinal) ? 1
                : line.Contains("\"method\":\"textDocument/didClose\"", StringComparison.Ordinal) ? -1 : 0;
            if (change != 0 && System.Text.RegularExpressions.Regex.Match(line, "\"uri\":\"([^\"]+)\"") is { Success: true } uri)
            {
                open[uri.Groups[1].Value] = open.GetValueOrDefault(uri.Groups[1].Value) + change;
            }
        }
        return [.. open.Where(o => o.Value > 0).Select(o => Path.GetFileName(new Uri(o.Key).LocalPath)).Order(StringComparer.Ordinal)];
    }
}

using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) finding its way through C# code: the solution's own symbols found
/// without a source generator's, the name itself first, usages read by file with the symbol the place took, a generator's
/// place (a usage, a declaration) in a copy of its code with its line's text, a place with no symbol said to be one, or
/// the symbol its line declares taken; symbols named rather than placed, found exactly, each overload told apart by its
/// documentation id, an indexer by this[] or Item, a record's positional property as Record.Property; a hover card as plain
/// text, and a fix's Suppress and Configure choices counted until asked for; calls on many files leaving open, in the IDE
/// and the language server, only the file the agent opened and the one it left unsaved; and a type a code action has just
/// moved known at once in its new file.</summary>
public sealed class McpNavigationTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpNavigationTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    private const string Words = "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
        + "    [GeneratedRegex(@\"\\w+\")]\n    private static partial Regex Word();\n\n"
        + "    public static int Count(string text) => Word().Count(text);\n\n"
        + "    public static int Count(string text, int from) => Word().Count(text[from..]);\n}\n";

    private const string Shapes = "using System.Text.Json.Serialization;\n\n[JsonSerializable(typeof(int))]\npartial class ShapeContext : JsonSerializerContext\n{\n}\n\n"
        + "static class UsesContext\n{\n    public static object Info => ShapeContext.Default.Int32;\n}\n";

    private const string Gallery = "namespace Gallery.Shapes;\n\npublic class Polygon<T>\n{\n    public int Corners() => 3;\n\n"
        + "    public sealed class Edge\n    {\n        public int Length;\n    }\n}\n";

    private const string Tally = "static class Tally\n{\n    public static int Of(string text) => Words.Count(text) + Words.Count(text, 1);\n\n"
        + "    public static int CountAll(string[] texts) => texts.Sum(Words.Count);\n}\n";

    /// <summary>A restored app and the library a solution filter leaves out of it, neither built.</summary>
    private async Task<string> AppWithoutItsLibraryAsync()
    {
        string folder = AppAndLibrary();
        await Restore.RunAsync(Path.Combine(folder, "Shop.slnx"));
        return folder;
    }

    /// <summary>An app and the library a solution filter leaves out of it, not yet restored.</summary>
    private string AppAndLibrary()
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
        return folder;
    }

    /// <summary>MAUI expressions as an agent reads them as one flow, each step one test's check in an instance of its
    /// own: a page's InitializeComponent answered from the generator's declaration in a copy of its code, an attached
    /// property's element, a method and its type renamed with the expression naming them, a definition asked first
    /// waiting for the generator's code, and an element's usages listing each expression and the code-
    /// behind's.</summary>
    [Fact]
    public async Task An_agent_reads_and_renames_what_a_MAUI_expression_names_through_the_generators_code()
    {
        await Flow.StepApartAsync("definition of a maui pages initialize component answers the generators declaration in a copy of its code", () => new McpNavigationTests(restored), t => t.Definition_of_a_maui_pages_initialize_component_answers_the_generators_declaration_in_a_copy_of_its_code());
        await Flow.StepApartAsync("definition of the element an attached propertys read names in a maui expression goes to the element", () => new McpNavigationTests(restored), t => t.Definition_of_the_element_an_attached_propertys_read_names_in_a_maui_expression_goes_to_the_element());
        await Flow.StepApartAsync("renaming a method and its type a maui expression names renames the expression with them", () => new McpNavigationTests(restored), t => t.Renaming_a_method_and_its_type_a_maui_expression_names_renames_the_expression_with_them());
        await Flow.StepApartAsync("definition in a maui expression asked first waits for the generators code and answers the element", () => new McpNavigationTests(restored), t => t.Definition_in_a_maui_expression_asked_first_waits_for_the_generators_code_and_answers_the_element());
        await Flow.StepApartAsync("usages of an element a maui expression reads list the element each expression and the code behinds use and rename renames them", () => new McpNavigationTests(restored), t => t.Usages_of_an_element_a_maui_expression_reads_list_the_element_each_expression_and_the_code_behinds_use_and_rename_renames_them());
    }

    /// <summary>Code questions as files, builds and loads change as one flow, each step one test's check in an instance
    /// of its own: what a restore brings waited for beside one that fails, a project a solution filter leaves out taken
    /// in by its builds, diagnostics during a build not outlasting it, find_symbol answering from files another program
    /// wrote, made or deleted as they stand, Unload and Reload Project, and Go to Declaration into a project the filter
    /// leaves out.</summary>
    [Fact]
    public async Task An_agents_code_questions_answer_from_the_files_and_builds_as_they_stand_waiting_for_what_a_restore_or_load_brings()
    {
        await Flow.StepApartAsync("definition of what a restore brings waits for its projects load again beside a restore that fails", () => new McpNavigationTests(restored), t => t.Definition_of_what_a_restore_brings_waits_for_its_projects_load_again_beside_a_restore_that_fails());
        await Flow.StepApartAsync("a first build of a project a solution filter leaves out takes its assembly in and each build after it what it added", () => new McpNavigationTests(restored), t => t.A_first_build_of_a_project_a_solution_filter_leaves_out_takes_its_assembly_in_and_each_build_after_it_what_it_added());
        await Flow.StepApartAsync("diagnostics asked while a build goes on do not outlast it once it has answered with what it wrote", () => new McpNavigationTests(restored), t => t.Diagnostics_asked_while_a_build_goes_on_do_not_outlast_it_once_it_has_answered_with_what_it_wrote());
        await Flow.StepApartAsync("find symbol asked right after another program wrote a closed file answers from it as written", () => new McpNavigationTests(restored), t => t.Find_symbol_asked_right_after_another_program_wrote_a_closed_file_answers_from_it_as_written());
        await Flow.StepApartAsync("find symbol asked after another program deleted made or wrote a file answers from the files as they stand (false, false)", () => new McpNavigationTests(restored), t => t.Find_symbol_asked_after_another_program_deleted_made_or_wrote_a_file_answers_from_the_files_as_they_stand(false, false));
        await Flow.StepApartAsync("find symbol asked after another program deleted made or wrote a file answers from the files as they stand (true, false)", () => new McpNavigationTests(restored), t => t.Find_symbol_asked_after_another_program_deleted_made_or_wrote_a_file_answers_from_the_files_as_they_stand(true, false));
        await Flow.StepApartAsync("find symbol asked after another program deleted made or wrote a file answers from the files as they stand (false, true)", () => new McpNavigationTests(restored), t => t.Find_symbol_asked_after_another_program_deleted_made_or_wrote_a_file_answers_from_the_files_as_they_stand(false, true));
        await Flow.StepApartAsync("unload Project takes a project out of the language server until Reload Project loads it again", () => new McpNavigationTests(restored), t => t.Unload_Project_takes_a_project_out_of_the_language_server_until_Reload_Project_loads_it_again());
        await Flow.StepApartAsync("go to declaration into a project a solution filter leaves out opens its source at the declaration", () => new McpNavigationTests(restored), t => t.Go_to_declaration_into_a_project_a_solution_filter_leaves_out_opens_its_source_at_the_declaration());
    }

    /// <summary>Symbols as an agent finds them as one flow, each step one test's check in an instance of its own: the
    /// solution's own symbols and usages in order, generated symbols and files told apart after the person's and
    /// answered from their text, a hierarchy followed from them, either half of a partial method, the dependency map a
    /// level at a time, symbols named in place of places, a record's positional property, a rename answering once git's
    /// index holds its move, and a card read as plain text.</summary>
    [Fact]
    public async Task An_agent_finds_symbols_usages_and_hierarchies_the_persons_first_and_a_generators_answered_from_its_text()
    {
        await Flow.StepApartAsync("an agent finds the solutions own symbols reads usages in order and hears when no symbol is where it asked", () => new McpNavigationTests(restored), t => t.An_agent_finds_the_solutions_own_symbols_reads_usages_in_order_and_hears_when_no_symbol_is_where_it_asked());
        await Flow.StepApartAsync("an agents find symbol lists a generated symbol it names first before the solutions own names like it", () => new McpNavigationTests(restored), t => t.An_agents_find_symbol_lists_a_generated_symbol_it_names_first_before_the_solutions_own_names_like_it());
        await Flow.StepApartAsync("usages tell an agent which files are generated code and list them after the persons", () => new McpNavigationTests(restored), t => t.Usages_tell_an_agent_which_files_are_generated_code_and_list_them_after_the_persons());
        await Flow.StepApartAsync("an agent asking about a generators code where usages led it is answered from its text or refused at once", () => new McpNavigationTests(restored), t => t.An_agent_asking_about_a_generators_code_where_usages_led_it_is_answered_from_its_text_or_refused_at_once());
        await Flow.StepApartAsync("an agent follows the hierarchy from a place in a generators code", () => new McpNavigationTests(restored), t => t.An_agent_follows_the_hierarchy_from_a_place_in_a_generators_code());
        await Flow.StepApartAsync("either half of a partial method answers its callers and what its body calls", () => new McpNavigationTests(restored), t => t.Either_half_of_a_partial_method_answers_its_callers_and_what_its_body_calls());
        await Flow.StepApartAsync("an agent reads the dependency map a level at a time down to what each type calls", () => new McpNavigationTests(restored), t => t.An_agent_reads_the_dependency_map_a_level_at_a_time_down_to_what_each_type_calls());
        await Flow.StepApartAsync("an agent names a symbol in place of its place finds names exactly and tells overloads apart by their documentation ids", () => new McpNavigationTests(restored), t => t.An_agent_names_a_symbol_in_place_of_its_place_finds_names_exactly_and_tells_overloads_apart_by_their_documentation_ids());
        await Flow.StepApartAsync("an agent names a records positional property as Record Property and gets its uses declaration and rename as from a use", () => new McpNavigationTests(restored), t => t.An_agent_names_a_records_positional_property_as_Record_Property_and_gets_its_uses_declaration_and_rename_as_from_a_use());
        await Flow.StepApartAsync("an agent gets a records positional propertys callers from a use by name and at its declaration", () => new McpNavigationTests(restored), t => t.An_agent_gets_a_records_positional_propertys_callers_from_a_use_by_name_and_at_its_declaration());
        await Flow.StepApartAsync("an agents rename of a type answers once gits index holds its files move", () => new McpNavigationTests(restored), t => t.An_agents_rename_of_a_type_answers_once_gits_index_holds_its_files_move());
        await Flow.StepApartAsync("an agent finds a name itself first an indexer by name reads a card as plain text and a fixs suppressions counted", () => new McpNavigationTests(restored), t => t.An_agent_finds_a_name_itself_first_an_indexer_by_name_reads_a_card_as_plain_text_and_a_fixs_suppressions_counted());
    }

    /// <summary>What an agent's calls leave and ask as one flow, each step one test's check in an instance of its own:
    /// calls on many files leaving open only those it opened or left unsaved, a second diagnostics of an unchanged file
    /// sending the server nothing, and a question at once after a code action moved a type to its own file finding it
    /// there.</summary>
    [Fact]
    public async Task An_agents_calls_leave_only_its_own_files_open_and_ask_the_server_only_for_what_changed()
    {
        await Flow.StepApartAsync("an agents calls on many files leave open only the file it opened the one its edit left unsaved and the last few asked", () => new McpNavigationTests(restored), t => t.An_agents_calls_on_many_files_leave_open_only_the_file_it_opened_the_one_its_edit_left_unsaved_and_the_last_few_asked());
        await Flow.StepApartAsync("a second diagnostics of an unchanged file answers from the servers last pull sending none", () => new McpNavigationTests(restored), t => t.A_second_diagnostics_of_an_unchanged_file_answers_from_the_servers_last_pull_sending_none());
        await Flow.StepApartAsync("what an agent asks at once after a code action moved a type to a file of its own finds the type there", () => new McpNavigationTests(restored), t => t.What_an_agent_asks_at_once_after_a_code_action_moved_a_type_to_a_file_of_its_own_finds_the_type_there());
    }

    /// <summary>The language server a window takes up from its head start as one flow, each step one test's check in an
    /// instance of its own: a definition asked as the window takes up a server that restored ahead waiting for what the
    /// restore brought, and a project kept unloaded staying out of it.</summary>
    [AvaloniaFact]
    public void An_agents_definition_waits_for_the_server_a_window_takes_up_and_a_project_kept_unloaded_stays_out()
    {
        Flow.StepApart("definition asked as the window takes up a server that restored ahead waits for what the restore brought", () => new McpNavigationTests(restored), t => t.Definition_asked_as_the_window_takes_up_a_server_that_restored_ahead_waits_for_what_the_restore_brought());
        Flow.StepApart("a project kept unloaded leaves the language server a window takes up from its head start", () => new McpNavigationTests(restored), t => t.A_project_kept_unloaded_leaves_the_language_server_a_window_takes_up_from_its_head_start());
    }

    /// <summary>A MAUI page's code-behind calls the InitializeComponent MAUI's source generator declares of its XAML: definition
    /// goes to that declaration in a copy of the generator's code the agent can read (the read-only copy Go to Declaration
    /// opens), at its line with that line's code, asked first thing of a folder never restored: the server's restore as it
    /// loads brings MAUI's build files and generator, so the answer waits for the projects' load after it.</summary>
    private async Task Definition_of_a_maui_pages_initialize_component_answers_the_generators_declaration_in_a_copy_of_its_code()
    {
        string folder = MauiPage("mcp-maui-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder]);

        JsonElement made = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 })).EnumerateArray());
        string copy = made.GetProperty("path").GetString()!;
        Assert.True(copy.EndsWith($"{Path.DirectorySeparatorChar}MainPage.xaml.sg.cs", StringComparison.Ordinal)
            && made.GetProperty("text").GetString() == "private partial void InitializeComponent();"
            && File.ReadAllLines(copy)[made.GetProperty("line").GetInt32() - 1].Contains("InitializeComponent()", StringComparison.Ordinal),
            $"definition of a MAUI page's InitializeComponent answered no place in the generator's code with its text: {made.GetRawText()}");
    }

    /// <summary>A MAUI page's C# expression reading an attached property from a named element (<c>CounterBtn.(Grid.Row)</c>),
    /// which MAUI's generator writes as the getter's call (<c>Grid.GetRow(CounterBtn)</c>), the element's name after the
    /// type's: definition from the element's name goes to the element, never to the type the call names first.</summary>
    private async Task Definition_of_the_element_an_attached_propertys_read_names_in_a_maui_expression_goes_to_the_element()
    {
        string folder = MauiPage("mcp-maui-attached-");
        DeleteAtEnd(folder);
        const string Read = "IsVisible=\"{CounterBtn.(Grid.Row) GT 0}\"";
        string page = File.ReadAllText(Path.Combine(folder, "MainPage.xaml")).Replace("Text=\"Hello, World!\"", $"Text=\"Hello, World!\"\n                {Read}", StringComparison.Ordinal);
        TestFolders.Write(folder, "MainPage.xaml", page);
        string[] lines = page.Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains(Read, StringComparison.Ordinal));
        int column = lines[line].IndexOf("CounterBtn", StringComparison.Ordinal) + "Coun".Length;
        await using McpClient client = await Start(["--mcp", folder]);

        // Asked of the code-behind first, which waits for the load after the restore bringing MAUI's generator.
        await Call(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 });
        JsonElement answer = await Call(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml", ["line"] = line + 1, ["column"] = column + 1 });
        int element = Array.FindIndex(lines, l => l.Contains("x:Name=\"CounterBtn\"", StringComparison.Ordinal));
        Assert.True(answer.GetArrayLength() == 1 && answer[0].GetProperty("path").GetString() == "MainPage.xaml"
            && answer[0].GetProperty("line").GetInt32() == element + 1 && answer[0].GetProperty("text").GetString() == "x:Name=\"CounterBtn\"",
            $"definition of the element CounterBtn, read in {Read}, answered no place naming it: {answer.GetRawText()}");
    }

    /// <summary>An agent's rename of a method, then of its type, that a MAUI page's C# expression names through a XAML
    /// prefix renames the expression with them, as their C# uses, so the page still names what it calls; and a rename from
    /// the expression renames the method as from its declaration.</summary>
    private async Task Renaming_a_method_and_its_type_a_maui_expression_names_renames_the_expression_with_them()
    {
        (string folder, string page) = MauiPageCallingHelper("mcp-maui-renamed-");
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder]);

        await Call(client, McpTools.Rename, new() { ["path"] = "Helper.cs", ["line"] = 5, ["column"] = 26, ["newName"] = "Yell" });
        await Call(client, McpTools.Rename, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 21, ["newName"] = "Speaker" });
        string renamed = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"));
        Assert.True(renamed == page.Replace("{local:Helper.Shout(", "{local:Speaker.Yell(", StringComparison.Ordinal),
            $"renaming Shout to Yell and Helper to Speaker left the expression naming them otherwise, or changed more: {renamed}");

        // Renamed again from the expression itself, as from the method's declaration.
        int at = renamed.IndexOf("Yell(", StringComparison.Ordinal);
        await Call(client, McpTools.Rename, new()
        {
            ["path"] = "MainPage.xaml", ["line"] = renamed[..at].Count(c => c == '\n') + 1, ["column"] = at - renamed.LastIndexOf('\n', at) + 1, ["newName"] = "Cheer",
        });
        Assert.Contains("public static string Cheer(string text)", File.ReadAllText(Path.Combine(folder, "Speaker.cs")), StringComparison.Ordinal);
        string again = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"));
        Assert.True(again == page.Replace("{local:Helper.Shout(", "{local:Speaker.Cheer(", StringComparison.Ordinal),
            $"renaming Yell to Cheer from the expression left it naming Yell, or changed more: {again}");
    }

    /// <summary>An agent's first question of a MAUI page's C# expression, asked as a folder never restored opens, waits as its
    /// first C# question does, through the server's restore, the load after it and its generators' run: definition of the
    /// element the expression reads answers its x:Name, from the field MAUI's generator makes, never nothing.</summary>
    private async Task Definition_in_a_maui_expression_asked_first_waits_for_the_generators_code_and_answers_the_element()
    {
        string folder = MauiPage("mcp-maui-first-");
        DeleteAtEnd(folder);
        const string Read = "IsVisible=\"{CounterBtn.(Grid.Row) GT 0}\"";
        string page = File.ReadAllText(Path.Combine(folder, "MainPage.xaml")).Replace("Text=\"Hello, World!\"", $"Text=\"Hello, World!\"\n                {Read}", StringComparison.Ordinal);
        TestFolders.Write(folder, "MainPage.xaml", page);
        string[] lines = page.Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains(Read, StringComparison.Ordinal));
        int column = lines[line].IndexOf("CounterBtn", StringComparison.Ordinal) + "Coun".Length;
        await using McpClient client = await Start(["--mcp", folder]);

        JsonElement answer = await Call(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml", ["line"] = line + 1, ["column"] = column + 1 });
        int element = Array.FindIndex(lines, l => l.Contains("x:Name=\"CounterBtn\"", StringComparison.Ordinal));
        Assert.True(answer.GetArrayLength() == 1 && answer[0].GetProperty("path").GetString() == "MainPage.xaml" && answer[0].GetProperty("line").GetInt32() == element + 1,
            $"definition of CounterBtn in {Read}, asked as the folder opened, answered no place naming the element: {answer.GetRawText()}");
    }

    /// <summary>An element a MAUI page's C# expressions read by its name (<c>label.(Grid.Row)</c>, the field MAUI's generator
    /// makes for it): usages from the name in an expression lists the element, each expression reading it and the
    /// code-behind's use, as from a code-behind's field, and none of the generator's own code; and rename from the element
    /// renames each of them.</summary>
    private async Task Usages_of_an_element_a_maui_expression_reads_list_the_element_each_expression_and_the_code_behinds_use_and_rename_renames_them()
    {
        string folder = MauiPage("mcp-maui-element-");
        DeleteAtEnd(folder);
        string page = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"))
            .Replace("Text=\"Hello, World!\"", "x:Name=\"label\"\n                Text=\"Hello, World!\"", StringComparison.Ordinal)
            .Replace("Text=\"Click me\"", "Text=\"Click me\"\n                IsVisible=\"{label.(Grid.Row) GT 0}\"\n                IsEnabled=\"{label.(Grid.Column) LT 2}\"",
                StringComparison.Ordinal);
        TestFolders.Write(folder, "MainPage.xaml", page);
        string code = File.ReadAllText(Path.Combine(folder, "MainPage.xaml.cs")).Replace("Announce(CounterBtn.Text)", "Announce(label.Text)", StringComparison.Ordinal);
        TestFolders.Write(folder, "MainPage.xaml.cs", code);
        string[] lines = page.Split('\n');
        (string, int, int, string) At(string path, string[] of, string text, string name)
        {
            int line = Array.FindIndex(of, l => l.Contains(text, StringComparison.Ordinal));
            return (path, line + 1, of[line].IndexOf(text, StringComparison.Ordinal) + text.IndexOf(name, StringComparison.Ordinal) + 1, of[line].Trim());
        }
        (string Path, int Line, int Column, string Text) read = At("MainPage.xaml", lines, "{label.(Grid.Row)", "label");
        await using McpClient client = await Start(["--mcp", folder]);

        // Asked of the code-behind first, which waits for the load after the restore bringing MAUI's generator.
        await Call(client, McpTools.Definition, new() { ["path"] = "MainPage.xaml.cs", ["line"] = 9, ["column"] = 9 });
        JsonElement answer = await Call(client, McpTools.Usages, new() { ["path"] = read.Path, ["line"] = read.Line, ["column"] = read.Column + 2 });
        (string, int, int, string)[] expected =
        [
            At("MainPage.xaml", lines, "x:Name=\"label\"", "label"), read, At("MainPage.xaml", lines, "{label.(Grid.Column)", "label"),
            At("MainPage.xaml.cs", code.Split('\n'), "Announce(label.Text)", "label"),
        ];
        Assert.True(Used(answer).Order().SequenceEqual(expected.Order()),
            $"usages of the element label, read in {read.Text}, answered other than its x:Name, both expressions and the code-behind's use: {answer.GetRawText()}");

        (_, int named, int at, _) = expected[0];
        await Call(client, McpTools.Rename, new() { ["path"] = "MainPage.xaml", ["line"] = named, ["column"] = at + 1, ["newName"] = "caption" });
        string renamed = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"));
        string behind = File.ReadAllText(Path.Combine(folder, "MainPage.xaml.cs"));
        Assert.True(renamed == page.Replace("\"label\"", "\"caption\"", StringComparison.Ordinal).Replace("{label.", "{caption.", StringComparison.Ordinal)
            && behind == code.Replace("(label.", "(caption.", StringComparison.Ordinal),
            $"renaming the element label to caption left a use of it, or changed more: {renamed}\n{behind}");
    }

    /// <summary>A MAUI page's folder whose language server, started ahead of the agent's window, loaded, restored and began
    /// loading again with MAUI's generator before the window took it up: the window's C# is ready only once it has heard
    /// what the server said meanwhile, its restore among it, so an agent's question asked as the window opens waits for the
    /// load after the restore and its generator's run, and definition of the page's InitializeComponent goes to the
    /// generator's declaration, never to nothing as of the load before the restore.</summary>
    private void Definition_asked_as_the_window_takes_up_a_server_that_restored_ahead_waits_for_what_the_restore_brought()
    {
        string folder = MauiPage("mcp-maui-ahead-");
        DeleteAtEnd(folder);
        string page = Path.Combine(folder, "MainPage.xaml.cs");
        LaunchOptions launch = LaunchOptions.Parse([folder]);
        HeadStart ahead = HeadStart.Begin(launch, languageServer: true)!;
        MainWindow window = new() { Width = 1200, Height = 800, AgentsAllowed = false, AgentsOwn = true, HeadStart = ahead };
        bool started = false;
        try
        {
            Ui.PumpUntil(() => RestoredAhead(Path.Combine(folder, "MauiApp1.csproj")), "the language server started ahead to restore MauiApp1", TimeSpan.FromMinutes(3));
            window.Show();
            PassengerTools tools = new(window);
            window.Start(launch);
            started = true;
            // Taken up as the window starts, with what it said still to be heard: an agent's wait reads this as it looks.
            global::Passenger.App.CSharp.CSharpService csharp = window.CSharpService is { Session: not null } taken ? taken
                : throw new Xunit.Sdk.XunitException($"The window did not take up the language server started ahead as it started: {window.CSharpService?.State}");
            Assert.False(csharp.IsReady && !csharp.ReloadingRestored(page) && !csharp.AwaitsRestore(page),
                "As the window took up a server that restored before then, C# was ready with no restore known, so an agent's question would be answered as of the load before it");
            IReadOnlyList<Place> places = Ui.Await(tools.Definition(page, 9, 9), "the agent's definition of InitializeComponent", AgentLimit);
            Place made = Assert.Single(places);
            Assert.True(made.Path.EndsWith($"{Path.DirectorySeparatorChar}MainPage.xaml.sg.cs", StringComparison.Ordinal) && made.Text == "private partial void InitializeComponent();",
                $"definition of a MAUI page's InitializeComponent, asked as the window took up a server that restored ahead, answered: {made}");
        }
        finally
        {
            if (!started)
            {
                Ui.AwaitPumped(ahead.EndAsync(), "the language server started ahead to end");
            }
            window.Close();
        }

        // The server's own log says its restore ended, written as it says so though no window hears it yet.
        static bool RestoredAhead(string project) => Directory.Exists(global::Passenger.App.CSharp.CSharpService.LogFolder)
            && Directory.EnumerateFiles(global::Passenger.App.CSharp.CSharpService.LogFolder, "server-*.log").Select(Ui.Shared)
                .Any(log => log.IndexOf($"dotnet restore on {project}", StringComparison.Ordinal) is >= 0 and int begun
                    && log.IndexOf("Restore complete", begun, StringComparison.Ordinal) >= 0);
    }

    /// <summary>A folder of a MAUI app's page and its code-behind, never restored.</summary>
    internal static string MauiPage(string prefix)
    {
        string folder = PathNames.Canonical(TestFolders.Create(prefix));
        TestFolders.Write(folder, "MauiApp1.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <RootNamespace>MauiApp1</RootNamespace>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="{AvaloniaApp.Metadata("MauiVersion")}" />
              </ItemGroup>
            </Project>

            """);
        TestFolders.Write(folder, "MainPage.xaml", MauiXamlTests.Gallery.MainPage);
        TestFolders.Write(folder, "MainPage.xaml.cs", MauiXamlTests.Gallery.MainPageCode);
        return folder;
    }

    /// <summary>A MAUI page's folder (<see cref="MauiPage"/>) whose page's C# expression calls the app's Helper.Shout, declared
    /// in Helper.cs, through a XAML prefix; with the page's text as written.</summary>
    internal static (string Folder, string Page) MauiPageCallingHelper(string prefix)
    {
        string folder = MauiPage(prefix);
        TestFolders.Write(folder, "Helper.cs", "namespace MauiApp1;\n\npublic static class Helper\n{\n    public static string Shout(string text) => text.ToUpperInvariant();\n}\n");
        string page = File.ReadAllText(Path.Combine(folder, "MainPage.xaml"))
            .Replace("<ContentPage x:Class=\"MauiApp1.MainPage\">", "<ContentPage x:Class=\"MauiApp1.MainPage\" xmlns:local=\"clr-namespace:MauiApp1\">", StringComparison.Ordinal)
            .Replace("Text=\"Hello, World!\"", "Text=\"{local:Helper.Shout('hello')}\"", StringComparison.Ordinal);
        TestFolders.Write(folder, "MainPage.xaml", page);
        return (folder, page);
    }

    /// <summary>Two projects each given a package once loaded: the server loads both again and restores both, one restore
    /// failing (its package no feed holds), the other bringing code by the package's build files, which its project loads
    /// again with after. An agent's question of that code asked while the restores run waits for that load, whatever the
    /// other's restore did, so definition of the package's class goes to its declaration in the package.</summary>
    private async Task Definition_of_what_a_restore_brings_waits_for_its_projects_load_again_beside_a_restore_that_fails()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-restored-"));
        DeleteAtEnd(folder);
        await using StandInFeed feed = new();
        feed.Add("Passenger.Sample", "1.0.0", new Dictionary<string, string>
        {
            ["build/Passenger.Sample.targets"] = "<Project>\n  <ItemGroup>\n    <Compile Include=\"$(MSBuildThisFileDirectory)Brought.cs\" />\n  </ItemGroup>\n</Project>\n",
            ["build/Brought.cs"] = "namespace Sample;\n\npublic static class Brought\n{\n    public static string Say() => \"brought\";\n}\n",
        });
        TaskCompletionSource answering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Hold("Passenger.Missing", answering.Task);
        // Only the stand-in, and restores kept out of the person's own package cache.
        TestFolders.Write(folder, "nuget.config", $"<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"stand-in\" value=\"{feed.Index}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />\n"
            + "  </packageSources>\n  <config>\n    <add key=\"globalPackagesFolder\" value=\"packages\" />\n  </config>\n</configuration>\n");
        TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Uses/Caller.cs", "namespace Uses;\n\npublic static class Caller\n{\n    public static string Call() => Sample.Brought.Say();\n}\n");
        TestFolders.Write(folder, "Lacks/Lacks.csproj", TestProjects.Library());
        await using McpClient client = await Start(["--mcp", folder]);
        Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Caller", ["exact"] = true })).EnumerateArray());

        TestFolders.Write(folder, "Uses/Uses.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Sample\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Lacks/Lacks.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Passenger.Missing\" Version=\"1.0.0\" />\n  </ItemGroup>\n"));
        Assert.True(await Waiting.UntilAsync(() => feed.Requests.Any(r => r.Path.StartsWith("/flat/passenger.missing/", StringComparison.Ordinal)), TimeSpan.FromMinutes(3), Cancel),
            $"Lacks's restore never asked the feed for its package{Said()}");
        Heard heard = new();
        Task<CallToolResult> asked = Result(client, McpTools.Definition, new() { ["path"] = "Uses/Caller.cs", ["line"] = 5, ["column"] = 43 }, heard: heard);
        // Told of the call a moment on, the agent's question waits on the restores; Lacks's then fails, no feed holding its package.
        Assert.True(await Waiting.UntilAsync(() => heard.Told.Count > 0 || asked.IsCompleted, TimeSpan.FromMinutes(1), Cancel), $"The agent's definition was never under way{Said()}");
        answering.SetResult();
        string said = Text(await asked);
        Assert.True(said.Contains($"{Path.DirectorySeparatorChar}Brought.cs", StringComparison.Ordinal) || said.Contains("/Brought.cs", StringComparison.Ordinal),
            $"definition of a class a restore brought, asked as it ran beside a restore that failed, answered: {said}");
    }

    /// <summary>The solution a filter leaves a library out of, as a fresh clone opens it: the assembly of the library is
    /// not there as C# loads the app, so nothing of it is known, and the first build, however quick, takes it in with no
    /// restart, what the agent asks next finding the library's types; each build after that brings what it added.</summary>
    private async Task A_first_build_of_a_project_a_solution_filter_leaves_out_takes_its_assembly_in_and_each_build_after_it_what_it_added()
    {
        string folder = await AppWithoutItsLibraryAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);

        Assert.Contains("CS0246", await ErrorsAsync());
        await BuiltAsync();
        Assert.Empty(await ErrorsAsync());
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync());

        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("    public string Who", "    public string Twice() => Who + Who;\n\n    public string Who", StringComparison.Ordinal));
        await Call(client, McpTools.Edit, new() { ["path"] = "App/Program.cs", ["oldText"] = "greeter.Who);", ["newText"] = "greeter.Twice());" });
        Assert.Contains("CS1061", await ErrorsAsync());
        await BuiltAsync();
        Assert.Empty(await ErrorsAsync());

        async Task<string[]> ErrorsAsync() => [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = "App/Program.cs" })).EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "error").Select(d => d.GetProperty("code").GetString()!)];

        async Task BuiltAsync()
        {
            JsonElement built = await Call(client, McpTools.Build);
            Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");
        }

        async Task<(string Path, int Line)> DeclaredAsync()
        {
            JsonElement place = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "App/Program.cs", ["line"] = 2, ["column"] = 34 })).EnumerateArray());
            return (place.GetProperty("path").GetString()!, place.GetProperty("line").GetInt32());
        }
    }

    /// <summary>What build answers holds whenever the language server hears of the assembly it wrote: told as the build goes
    /// on, the server reads it only at the build's end, so what diagnostics answered between the two is no answer to ask
    /// after it, with nothing else said to the server since. (The server's own read of it may come first: nothing here asks
    /// what the ask under way answered.)</summary>
    private async Task Diagnostics_asked_while_a_build_goes_on_do_not_outlast_it_once_it_has_answered_with_what_it_wrote()
    {
        string folder = await AppWithoutItsLibraryAsync();
        // The app's compile, after the library's, held once the test puts "hold" there until it puts "go" (ten minutes at most).
        TestFolders.Write(folder, "Directory.Build.targets", """
            <Project>
              <Target Name="Held" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true' And '$(MSBuildProjectName)' == 'App' And Exists('$(MSBuildThisFileDirectory)hold')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="rm -f hold; touch held; n=0; while [ ! -f go ] &amp;&amp; [ $n -lt 2400 ]; do sleep 0.25; n=`expr $n + 1`; done" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="powershell -NoProfile -Command &quot;Remove-Item hold; New-Item held -ItemType File | Out-Null; $n = 0; while (-not (Test-Path go) -and $n -lt 2400) { Start-Sleep -Milliseconds 250; $n++ }&quot;" />
              </Target>
            </Project>
            """);
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["traceProtocols"] = true } });
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        await ErrorsAsync();
        await BuiltAsync(Call(client, McpTools.Build));

        TestFolders.Write(folder, "Lib/Greeter.cs", File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")).Replace("    public string Who", "    public string Twice() => Who + Who;\n\n    public string Who", StringComparison.Ordinal));
        await Call(client, McpTools.Edit, new() { ["path"] = "App/Program.cs", ["oldText"] = "greeter.Who);", ["newText"] = "greeter.Twice());" });
        await ErrorsAsync();
        int told = Told(trace);
        TestFolders.Write(folder, "hold", string.Empty);
        Task<JsonElement> building = Call(client, McpTools.Build);
        await Until(() => File.Exists(Path.Combine(folder, "held")) && Told(trace) > told, "the build to reach the app's compile with the language server told of the library's assembly");
        await ErrorsAsync();
        TestFolders.Write(folder, "go", string.Empty);
        await BuiltAsync(building);

        string[] errors = await ErrorsAsync();
        Assert.True(errors.Length == 0, $"build answered, but diagnostics still said what it said before the build took in the method the library gained: {string.Join(", ", errors)}");

        async Task<string[]> ErrorsAsync() => [.. (await Call(client, McpTools.Diagnostics, new() { ["path"] = "App/Program.cs" })).EnumerateArray()
            .Where(d => d.GetProperty("severity").GetString() == "error").Select(d => d.GetProperty("code").GetString()!)];

        static async Task BuiltAsync(Task<JsonElement> asked)
        {
            JsonElement built = await asked;
            Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");
        }
    }

    /// <summary>How many times lsp.log's trace says the language server was told the library's reference assembly changed.</summary>
    private static int Told(string trace)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal)
            && l.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DidChangeWatchedFiles}\"", StringComparison.Ordinal) && l.Contains("/ref/Lib.dll\"", StringComparison.Ordinal));
    }

    /// <summary>A closed file another program writes in place, which the folder's watcher never hears, is what find_symbol
    /// asked at once answers from, every time: the type written there found, the one written over gone, where the answer
    /// was of the file as it stood until the watcher heard of the write.</summary>
    private async Task Find_symbol_asked_right_after_another_program_wrote_a_closed_file_answers_from_it_as_written()
    {
        await TakeFolderAsync();
        Write("Shapes.cs", "static class Shape0\n{\n}\n");
        await using McpClient client = await Start(["--mcp", Folder], environment: new() { [WatchedTree.UnheardVariable] = "Shapes.cs" });
        Assert.Single(await FoundAsync("Shape0"));
        for (int i = 1; i <= 5; i++)
        {
            Write("Shapes.cs", $"static class Shape{i}\n{{\n}}\n");
            JsonElement[] written = await FoundAsync($"Shape{i}");
            JsonElement[] over = await FoundAsync($"Shape{i - 1}");
            Assert.True(written.Length == 1 && over.Length == 0, $"find_symbol right after Shapes.cs's write {i} of 5 found Shape{i}, written there, {written.Length} "
                + $"times and Shape{i - 1}, written over, {over.Length} times: an answer of the file as it stood before{Said()}");
        }

        async Task<JsonElement[]> FoundAsync(string name) =>
            [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = name })).EnumerateArray().Where(s => s.GetProperty("name").GetString() == name)];
    }

    /// <summary>A closed file another program deletes, makes or writes in place, none of it heard by the folder's watcher,
    /// is what find_symbol asked at once answers from: the deleted file's type gone, the made file's found, the type
    /// written in place found and the one written over gone, whether the question comes a moment or seconds after and
    /// whatever time the program set the file's write to (back a month, as a copy keeping its time does).</summary>
    private async Task Find_symbol_asked_after_another_program_deleted_made_or_wrote_a_file_answers_from_the_files_as_they_stand(bool waited, bool backDated)
    {
        await TakeFolderAsync();
        string shapes = Path.Combine(Folder, "Shapes.cs");
        Write("Shapes.cs", "static class Shape0\n{\n}\n");
        await using McpClient client = await Start(["--mcp", Folder], environment: new() { [WatchedTree.UnheardVariable] = "Shapes.cs" });
        Assert.Single(await FoundAsync("Shape0"));

        File.Delete(shapes);
        await AfterAsync();
        Assert.True(await FoundAsync("Shape0") is [], $"find_symbol right after Shapes.cs was deleted found Shape0 in it{Said()}");

        Write("Shapes.cs", "static class Shape1\n{\n}\n");
        await AfterAsync(TimeSpan.FromDays(30));
        Assert.True(await FoundAsync("Shape1") is [_], $"find_symbol right after Shapes.cs was made again did not find Shape1 in it{Said()}");

        Write("Shapes.cs", "static class Shape2\n{\n}\n");
        await AfterAsync(TimeSpan.FromDays(60));
        JsonElement[] written = await FoundAsync("Shape2");
        JsonElement[] over = await FoundAsync("Shape1");
        Assert.True(written.Length == 1 && over.Length == 0, $"find_symbol right after Shapes.cs was written in place found Shape2, written there, {written.Length} "
            + $"times and Shape1, written over, {over.Length} times: an answer of the file as it stood before{Said()}");

        // The file's write time set back where the case asks it, then the question asked at once or seconds later.
        async Task AfterAsync(TimeSpan? back = null)
        {
            if (backDated && back is { } age)
            {
                File.SetLastWriteTimeUtc(shapes, DateTime.UtcNow - age);
            }
            if (waited)
            {
                await Waiting.ForAsync(TimeSpan.FromSeconds(3), "past the moments just after a write", TestContext.Current.CancellationToken);
            }
        }

        async Task<JsonElement[]> FoundAsync(string name) =>
            [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = name })).EnumerateArray().Where(s => s.GetProperty("name").GetString() == name)];
    }

    /// <summary>Unload Project takes the project out of the language server: nothing is answered from the text the server
    /// read of it, so Go to Declaration from what references it finds the line where it is now, its symbols are found no
    /// more, and a file come under it is neither waited for nor brings it back; Reload Project has it loaded again.</summary>
    private async Task Unload_Project_takes_a_project_out_of_the_language_server_until_Reload_Project_loads_it_again()
    {
        string folder = await AppWithoutItsLibraryAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "Shop.slnx")]);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The solution did not build: {built}");
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 7), await DeclaredAsync());
        Assert.NotEmpty(await FoundAsync("Greeter"));

        await ChosenAsync(AppActions.UnloadProject);
        Assert.Empty(await FoundAsync("Greeter"));
        TestFolders.Write(folder, "Lib/Greeter.cs", "// A line above.\n" + File.ReadAllText(Path.Combine(folder, "Lib/Greeter.cs")));
        Assert.Equal((Path.Combine("Lib", "Greeter.cs"), 8), await DeclaredAsync());

        TestFolders.Write(folder, "Lib/Fresh.cs", "namespace Lib;\n\npublic sealed class Fresh;\n");
        CallToolResult none = await Result(client, McpTools.SymbolInfo, new() { ["symbol"] = "Fresh" });
        Assert.True(none.IsError == true && Text(none).Contains("No symbol of the solution's own code is named Fresh", StringComparison.Ordinal),
            $"A file come under an unloaded project was waited for, or loaded the project again: {Text(none)}");

        await ChosenAsync(AppActions.ReloadProject);
        await Until(async () => (await FoundAsync("Fresh")).Length > 0, "Reload Project to load the project with the file come under it");

        async Task<(string Path, int Line)> DeclaredAsync()
        {
            JsonElement place = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "App/Program.cs", ["line"] = 2, ["column"] = 34 })).EnumerateArray());
            return (place.GetProperty("path").GetString()!, place.GetProperty("line").GetInt32());
        }

        async Task<JsonElement[]> FoundAsync(string name) => [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = name })).EnumerateArray()];

        // The Explorer's row chosen, which is what the action acts on.
        async Task ChosenAsync(string action)
        {
            await Call(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "ListBox#Rows/\"Lib\"" });
            await Call(client, McpTools.IdeAction, new() { ["action"] = action });
        }
    }

    /// <summary>A window keeping the folder's state takes up the language server started ahead, which loaded every project
    /// before anything read what the folder keeps: the project the person left unloaded is taken out of it, so the server
    /// holds what the window says is loaded. An agent's own Passenger keeps no state, so this window is built by hand.</summary>
    private void A_project_kept_unloaded_leaves_the_language_server_a_window_takes_up_from_its_head_start()
    {
        string folder = AppAndLibrary();
        Restore.Run(Path.Combine(folder, "Shop.slnx"));
        string lib = Path.Combine(folder, "Lib", "Lib.csproj");
        string state = PathNames.Canonical(TestFolders.Create("mcp-kept-"));
        DeleteAtEnd(state);
        TestFolders.Write(ProjectState.FolderOf(state, folder), "workspace.json", JsonSerializer.Serialize(new { version = 1, unloadedProjects = new[] { lib } }));
        LaunchOptions launch = LaunchOptions.Parse([Path.Combine(folder, "Shop.slnx")]);
        MainWindow window = new() { Width = 1200, Height = 800, AgentsAllowed = false, AgentsOwn = true, StateFolder = state, HeadStart = HeadStart.Begin(launch, languageServer: true) };
        try
        {
            window.Show();
            window.KeepLayout();
            window.Start(launch);
            Ui.WaitFor(() => window.CSharpService?.Session is { IsLoaded: true }, "the language server started ahead to load the projects", TimeSpan.FromMinutes(2));
            Assert.Equal(["App"], window.Session.Projects.Select(p => p.Name));
            Ui.WaitFor(() => Found("Greeter") == 0, () => $"Lib, kept unloaded, to leave the language server (it knows {Found("Greeter")} Greeter)", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }

        int Found(string name) => Ui.Await(window.CSharpService!.Session!.SymbolsNamedAsync(name, 5), $"the language server's symbols named {name}", TimeSpan.FromMinutes(1)).Symbols.Count;
    }

    /// <summary>A symbol of a project a solution filter leaves out, which C# reads only as its built assembly, is declared in
    /// that project's own file, never in the server's stub of the assembly: a method, a property and a primary constructor,
    /// and a method whose source takes another parameter than the assembly built before it; the framework's still a stub.</summary>
    private async Task Go_to_declaration_into_a_project_a_solution_filter_leaves_out_opens_its_source_at_the_declaration()
    {
        string folder = await AppWithoutItsLibraryAsync();
        await using McpClient client = await Start(["--mcp", Path.Combine(folder, "App.slnf")]);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The filter's app did not build with the project it leaves out: {built}");

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

    private async Task An_agent_finds_the_solutions_own_symbols_reads_usages_in_order_and_hears_when_no_symbol_is_where_it_asked()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        Write("Shapes.cs", Shapes);
        Write("Gallery.cs", Gallery);
        await using McpClient client = await Start(["--mcp", Folder]);

        // Words and Word at the person's places though the regex generator declares them again; its class after, in a copy.
        JsonElement[] found = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word" })).EnumerateArray()];
        int own = found.TakeWhile(s => s.GetProperty("path").GetString() == "Words.cs").Count();
        JsonElement[] generated = found[own..];
        Assert.True(own == 2 && generated.Any(s => s.GetProperty("name").GetString() == "Word_0") && generated.All(InGeneratorsCode)
            && !generated.Any(s => s.GetProperty("name").GetString() is "Word" or "Words"),
            $"find_symbol Word answered no place of the person's for Words and Word, then the generator's class in its code: {string.Join(", ", found.Select(s => s.GetRawText()))}");
        // The partial method in its type by the type's name alone, the partial type in none, as for a type of one part.
        Assert.True(found[..own].All(s => s.GetProperty("name").GetString() switch
        {
            "Word" => s.TryGetProperty("container", out JsonElement type) && type.GetString() == "Words",
            "Words" => !s.TryGetProperty("container", out _),
            _ => false,
        }), $"find_symbol Word named the containers of a partial type's members otherwise than by the type's name: {string.Join(", ", found[..own].Select(s => s.GetRawText()))}");
        // A type's container is its namespace, a member's or a nested type's its type, found by name exactly or not.
        foreach ((string name, string container) in new[] { ("Polygon", "Gallery.Shapes"), ("Corners", "Polygon<T>"), ("Edge", "Polygon<T>"), ("Length", "Polygon<T>.Edge") })
        {
            JsonElement exact = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = name, ["exact"] = true })).EnumerateArray());
            JsonElement matched = (await Call(client, McpTools.FindSymbol, new() { ["query"] = name })).EnumerateArray()
                .First(s => s.GetProperty("path").GetString() == "Gallery.cs" && s.GetProperty("line").GetInt32() == exact.GetProperty("line").GetInt32());
            Assert.True(Container(exact) == container && Container(matched) == container,
                $"find_symbol {name} named its container {Container(exact) ?? "none"} found exactly and {Container(matched) ?? "none"} found by Go to Symbol, not {container}");
        }
        static string? Container(JsonElement symbol) => symbol.TryGetProperty("container", out JsonElement container) ? container.GetString() : null;
        JsonElement word = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word", ["exact"] = true })).EnumerateArray());
        Assert.Equal(("Words.cs", 6), (word.GetProperty("path").GetString(), word.GetProperty("line").GetInt32()));
        JsonElement named = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word_0", ["exact"] = true })).EnumerateArray());
        Assert.True(InGeneratorsCode(named) && named.GetProperty("kind").GetString() == "class",
            $"find_symbol of the class the generator declares, by its name, answered {named.GetRawText()}, not its place in the generator's code");
        string card = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Word_0" })).GetString()!;
        Assert.True(card.Contains("RegexGenerator.g.cs", StringComparison.Ordinal) && card.Contains("Word_0", StringComparison.Ordinal),
            $"symbol_info of the class the generator declares answered: {card}");

        // A place in the regex generator's code, its line's text as a copy of that code the agent can open reads it.
        static bool InGeneratorsCode(JsonElement symbol) => symbol.GetProperty("path").GetString() is { } path
            && path.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs", StringComparison.Ordinal) && File.Exists(path)
            && File.ReadAllLines(path)[symbol.GetProperty("line").GetInt32() - 1].Contains(symbol.GetProperty("name").GetString()!, StringComparison.Ordinal);

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

    /// <summary>find_symbol lists the name itself first though only a source generator declares it (the regex generator's
    /// class Word_0), before the solution's own names that start with it, so no limit cuts it; the solution's own come before
    /// the rest of what generators declare.</summary>
    private async Task An_agents_find_symbol_lists_a_generated_symbol_it_names_first_before_the_solutions_own_names_like_it()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Counts.cs", "static class Word_0Count\n{\n    public static int Word_0Total;\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement[] found = [.. (await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word_0" })).EnumerateArray()];
        // The generator's class and its constructor, both named Word_0.
        JsonElement[] named = [.. found.TakeWhile(s => s.GetProperty("name").GetString() == "Word_0")];
        Assert.True(named.Length > 0 && named.All(s => s.GetProperty("path").GetString()!.EndsWith("RegexGenerator.g.cs", StringComparison.Ordinal))
            && found.Skip(named.Length).Take(2).Select(s => s.GetProperty("name").GetString()).SequenceEqual(["Word_0Count", "Word_0Total"]),
            $"find_symbol Word_0 did not list the generator's Word_0 first, then the solution's own names starting with it: {string.Join(", ", found.Select(s => s.GetRawText()))}");
        JsonElement first = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Word_0", ["limit"] = 1 })).EnumerateArray());
        Assert.True(first.GetProperty("name").GetString() == "Word_0", $"find_symbol Word_0 with a limit of 1 cut the name itself for {first.GetRawText()}");
    }

    /// <summary>usages says which files are generated code, as Roslyn tells it, and lists them after the person's: a source
    /// generator's, a .g.cs a tool wrote, and a file whose first comment says &lt;auto-generated&gt;; a file written by hand
    /// says nothing of it.</summary>
    private async Task Usages_tell_an_agent_which_files_are_generated_code_and_list_them_after_the_persons()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Hand.cs", "static class Hand\n{\n    public static int Of(string text) => Words.Count(text);\n}\n");
        Write("Made.cs", "// <auto-generated/>\nstatic class Made\n{\n    public static int Of(string text) => Words.Count(text);\n}\n");
        Write("Tool.g.cs", "static class Tool\n{\n    public static int Of(string text) => Words.Count(text);\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement counted = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 23 });
        Assert.Equal(["Hand.cs", "Words.cs", "Made.cs generated", "Tool.g.cs generated"], Files(counted));
        JsonElement word = await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 });
        string[] files = Files(word);
        Assert.True(files is ["Words.cs", var made] && made.EndsWith($"{Path.DirectorySeparatorChar}RegexGenerator.g.cs generated", StringComparison.Ordinal),
            $"usages of a GeneratedRegex method did not say its generator's file is generated code: {word.GetRawText()}");

        // Each file by its path, with "generated" after it where the answer says so.
        static string[] Files(JsonElement usages) => [.. usages.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString() + (f.TryGetProperty("generated", out JsonElement made) && made.GetBoolean() ? " generated" : string.Empty))];
    }

    /// <summary>The code tools on a source generator's code copied, where usages led the agent, answer at once: its outline
    /// from its own text, and what needs the language server, which never holds a copy, refused in the window's words,
    /// naming the generator and the project.</summary>
    private async Task An_agent_asking_about_a_generators_code_where_usages_led_it_is_answered_from_its_text_or_refused_at_once()
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

    /// <summary>hierarchy at a place in a source generator's code copied, where usages and definition led the agent, answers
    /// as in the code it is made from, the server asked of its own document: the regex generator's half of a partial method
    /// has that method's callers and what its body calls, and the JSON generator's half of a class what derives from it.</summary>
    private async Task An_agent_follows_the_hierarchy_from_a_place_in_a_generators_code()
    {
        await TakeFolderAsync();
        Write("Words.cs", Words);
        Write("Shapes.cs", Shapes);
        Write("Deep.cs", "sealed class DeepContext : ShapeContext\n{\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        (string regex, int line, int column, _) = Used(await Call(client, McpTools.Usages, new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 }))[^1];

        JsonElement callers = await Call(client, McpTools.Hierarchy, new() { ["path"] = regex, ["line"] = line, ["column"] = column });
        Assert.Equal(["Words.Count(string)", "Words.Count(string, int)"], callers.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        JsonElement callee = Assert.Single((await Call(client, McpTools.Hierarchy, new() { ["path"] = regex, ["line"] = line, ["column"] = column, ["direction"] = "callees" })).EnumerateArray());
        Assert.True(callee.GetProperty("path").GetString() == regex && callee.GetProperty("name").GetString()!.EndsWith(".Instance", StringComparison.Ordinal),
            $"callees of the generator's half of Word answered no place in its code: {callee.GetRawText()}");

        string context = Assert.Single((await Call(client, McpTools.Definition, new() { ["path"] = "Shapes.cs", ["line"] = 10, ["column"] = 48 })).EnumerateArray()).GetProperty("path").GetString()!;
        int declared = Array.FindIndex(File.ReadAllLines(context), l => l.Contains("partial class ShapeContext", StringComparison.Ordinal)) + 1;
        JsonElement derived = await Call(client, McpTools.Hierarchy, new() { ["path"] = context, ["line"] = declared, ["column"] = 25, ["direction"] = "subtypes" });
        Assert.Equal(["DeepContext Deep.cs:1"], derived.EnumerateArray().Select(d => $"{d.GetProperty("name").GetString()} {d.GetProperty("path").GetString()}:{d.GetProperty("line").GetInt32()}"));
    }

    /// <summary>An agent reads the dependency map a level at a time, as the compiler binds the code: the projects, a project's
    /// namespaces, a namespace's types with where each is written, and each edge to the id of what is used, counted by kind,
    /// the top-level statements' call of Helper.Twice among them, an edge breaking the folder's layer rules marked so; an id
    /// no answer gave is refused, saying so.</summary>
    private async Task An_agent_reads_the_dependency_map_a_level_at_a_time_down_to_what_each_type_calls()
    {
        await TakeFolderAsync();
        // Shout's namespace a layer of its own, let use nothing else, so its call of Helper breaks the rules.
        Write("Layered/Shout.cs", "namespace Layered;\n\nstatic class Shout\n{\n    public static int Loud() => Helper.Twice(2);\n}\n");
        Write("passenger-layers.json", "{\n  \"passengerLayers\": 1,\n  \"layers\": [\n    { \"name\": \"Loud\", \"namespaces\": [\"Layered.*\"] },\n"
            + "    { \"name\": \"Rest\", \"namespaces\": [\"**\"] }\n  ]\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        JsonElement sum = Assert.Single((await Call(client, McpTools.Dependencies)).GetProperty("nodes").EnumerateArray());
        Assert.Equal("Sum", sum.GetProperty("name").GetString());
        JsonElement spaces = await Call(client, McpTools.Dependencies, new() { ["node"] = sum.GetProperty("id").GetString() });
        JsonElement global = spaces.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("name").GetString() == "<global namespace>");
        JsonElement loud = Assert.Single(spaces.GetProperty("edges").EnumerateArray());
        Assert.True(loud.GetProperty("from").GetString()!.EndsWith("|Layered", StringComparison.Ordinal) && loud.TryGetProperty("broken", out JsonElement broken) && broken.GetBoolean(),
            $"Layered's call of Helper answered as not breaking the rules: {loud.GetRawText()}");
        JsonElement types = await Call(client, McpTools.Dependencies, new() { ["node"] = global.GetProperty("id").GetString() });
        Assert.Equal(["Helper class Helper.cs:1", "Program class Program.cs:1"], types.GetProperty("nodes").EnumerateArray()
            .Select(n => $"{n.GetProperty("name").GetString()} {n.GetProperty("kind").GetString()} {n.GetProperty("path").GetString()}:{n.GetProperty("line").GetInt32()}"));
        JsonElement call = Assert.Single(types.GetProperty("edges").EnumerateArray());
        Assert.True(call.GetProperty("from").GetString()!.EndsWith("|T:Program", StringComparison.Ordinal)
            && call.GetProperty("to").GetString()!.EndsWith("|M:Helper.Twice(System.Int32)", StringComparison.Ordinal)
            && call.GetProperty("kinds").GetProperty("calls").GetInt32() == 1 && !call.TryGetProperty("broken", out _), $"the top-level statements' edge was {call.GetRawText()}");
        CallToolResult refused = await Result(client, McpTools.Dependencies, new() { ["node"] = "nowhere" });
        Assert.True(refused.IsError == true && Text(refused).Contains("No node 'nowhere'", StringComparison.Ordinal), $"a node no answer gave answered: {Text(refused)}");
    }

    /// <summary>A partial member's two halves are one member to hierarchy: asked at the half that declares it or the half with
    /// its body, it answers the callers, bound to the one, and the callees, written in the other.</summary>
    private async Task Either_half_of_a_partial_method_answers_its_callers_and_what_its_body_calls()
    {
        await TakeFolderAsync();
        Write("Halves.cs", "static partial class Halves\n{\n    public static partial int Twice(int value);\n\n    public static int Quad(int value) => Twice(Twice(value));\n}\n\n"
            + "static partial class Halves\n{\n    public static partial int Twice(int value) => Helper(value) * 2;\n\n    private static int Helper(int value) => value;\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        foreach (int line in new[] { 3, 10 })
        {
            Assert.Equal(["Halves.Quad(int)"], (await Call(client, McpTools.Hierarchy, new() { ["path"] = "Halves.cs", ["line"] = line, ["column"] = 31 }))
                .EnumerateArray().Select(c => c.GetProperty("name").GetString()));
            Assert.Equal(["Halves.Helper(int)"], (await Call(client, McpTools.Hierarchy, new() { ["path"] = "Halves.cs", ["line"] = line, ["column"] = 31, ["direction"] = "callees" }))
                .EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        }
    }

    private async Task An_agent_names_a_symbol_in_place_of_its_place_finds_names_exactly_and_tells_overloads_apart_by_their_documentation_ids()
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

    private const string Feeds = "public sealed record ReleaseFeed(string Latest, int Count)\n{\n    public string Upper { get; } = Latest.ToUpperInvariant();\n}\n\n"
        + "public readonly record struct Spot(int Across, int Down);\n";

    private const string Readers = "static class Readers\n{\n    public static string Of(ReleaseFeed feed, Spot spot) => feed.Latest + feed.Count + spot.Down;\n\n"
        + "    public static ReleaseFeed Next(ReleaseFeed feed) => feed with { Latest = \"2.0\" };\n}\n";

    /// <summary>A record's positional property, which its primary constructor's parameter declares, is named as
    /// Record.Property, of a record class and a record struct, as find_symbol lists it: usages, definition and rename by that
    /// name answer what they answer from a use of it, as the editor's Find Usages, Go to Declaration and Rename do, the
    /// property, not the parameter, said to be what the place took.</summary>
    private async Task An_agent_names_a_records_positional_property_as_Record_Property_and_gets_its_uses_declaration_and_rename_as_from_a_use()
    {
        await TakeFolderAsync();
        Write("Feeds.cs", Feeds);
        Write("Readers.cs", Readers);
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement listed = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Latest", ["kind"] = "property" })).EnumerateArray());
        JsonElement exact = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Latest", ["exact"] = true })).EnumerateArray());
        Assert.Equal(("property", "ReleaseFeed", "Feeds.cs:1:41"), Found(listed));
        Assert.Equal((Found(listed), "P:ReleaseFeed.Latest"), (Found(exact), exact.GetProperty("id").GetString()));

        await FoundAsync("ReleaseFeed.Latest", 66, ["Feeds.cs:1:41", "Feeds.cs:3:36", "Readers.cs:3:66", "Readers.cs:5:69"]);
        await FoundAsync("Spot.Down", 93, ["Feeds.cs:6:52", "Readers.cs:3:93"]);
        Assert.Equal(["Feeds.cs:6:52"], Placed(await Call(client, McpTools.Definition, new() { ["symbol"] = "P:Spot.Down" })));

        Assert.Equal("Renamed in 2 files: Feeds.cs, Readers.cs", (await Call(client, McpTools.Rename, new() { ["symbol"] = "ReleaseFeed.Latest", ["newName"] = "Newest" })).GetString());
        Assert.Equal("Renamed in 2 files: Feeds.cs, Readers.cs", (await Call(client, McpTools.Rename, new() { ["symbol"] = "Spot.Down", ["newName"] = "Below" })).GetString());
        Assert.Equal(Feeds.Replace("Latest", "Newest", StringComparison.Ordinal).Replace("Down", "Below", StringComparison.Ordinal), File.ReadAllText(Path.Combine(Folder, "Feeds.cs")));
        Assert.Equal(Readers.Replace("Latest", "Newest", StringComparison.Ordinal).Replace("Down", "Below", StringComparison.Ordinal), File.ReadAllText(Path.Combine(Folder, "Readers.cs")));

        async Task FoundAsync(string named, int column, string[] uses)
        {
            Dictionary<string, object?> fromUse = new() { ["path"] = "Readers.cs", ["line"] = 3, ["column"] = column };
            JsonElement byName = await Call(client, McpTools.Usages, new() { ["symbol"] = named });
            JsonElement byUse = await Call(client, McpTools.Usages, fromUse);
            Assert.True(byName.GetProperty("symbol").GetString() == $"{named} (property)" && byUse.GetProperty("symbol").GetString() == $"{named} (property)",
                $"usages of {named} said it took {byName.GetProperty("symbol")} by name and {byUse.GetProperty("symbol")} from a use: an agent naming the property is told of another symbol");
            Assert.Equal(uses, Used(byName).Select(u => $"{u.Path}:{u.Line}:{u.Column}"));
            Assert.Equal(Used(byUse), Used(byName));
            Assert.Equal([uses[0]], Placed(await Call(client, McpTools.Definition, new() { ["symbol"] = named })));
            Assert.Equal([uses[0]], Placed(await Call(client, McpTools.Definition, fromUse)));
        }

        static (string Kind, string? Container, string Place) Found(JsonElement symbol) => (symbol.GetProperty("kind").GetString()!, symbol.GetProperty("container").GetString(),
            $"{symbol.GetProperty("path").GetString()}:{symbol.GetProperty("line").GetInt32()}:{symbol.GetProperty("column").GetInt32()}");
    }

    /// <summary>hierarchy of a record's positional property answers its callers, the members that read or set it, each at
    /// its first use, from a use of it in another file, by name and at its declaration, as for any property, where it
    /// answered that the record was a type with no callers; its callees none, as an auto-property's. A method asked about at
    /// a use in another file answers as at its declaration, its callees the positional properties it reads; the record's
    /// parameter read in its own body is no member, and said to be none.</summary>
    private async Task An_agent_gets_a_records_positional_propertys_callers_from_a_use_by_name_and_at_its_declaration()
    {
        await TakeFolderAsync();
        Write("Feeds.cs", Feeds);
        Write("Readers.cs", Readers);
        Write("Both.cs", "static class Both\n{\n    public static string Of(ReleaseFeed feed, Spot spot) => Readers.Of(feed, spot) + Readers.Of(feed, spot);\n}\n");
        await using McpClient client = await Start(["--mcp", Folder]);

        foreach (Dictionary<string, object?> asked in new Dictionary<string, object?>[]
        {
            new() { ["path"] = "Readers.cs", ["line"] = 3, ["column"] = 66 },
            new() { ["symbol"] = "ReleaseFeed.Latest" },
            new() { ["path"] = "Feeds.cs", ["line"] = 1, ["column"] = 41 },
        })
        {
            Assert.Equal(["Readers.Of(ReleaseFeed, Spot) Readers.cs:3:66", "Readers.Next(ReleaseFeed) Readers.cs:5:69"], await CallsAsync(asked));
            Assert.Empty(await CallsAsync(new(asked) { ["direction"] = "callees" }));
        }
        Assert.Equal(["Readers.Of(ReleaseFeed, Spot) Readers.cs:3:93"], await CallsAsync(new() { ["symbol"] = "Spot.Down" }));

        Dictionary<string, object?> ofUsed = new() { ["path"] = "Both.cs", ["line"] = 3, ["column"] = 69 };
        Assert.Equal(["Both.Of(ReleaseFeed, Spot) Both.cs:3:69"], await CallsAsync(ofUsed));
        Assert.Equal(["ReleaseFeed.Latest Feeds.cs:1:41", "ReleaseFeed.Count Feeds.cs:1:53", "Spot.Down Feeds.cs:6:52"],
            await CallsAsync(new(ofUsed) { ["direction"] = "callees" }));

        CallToolResult parameter = await Result(client, McpTools.Hierarchy, new() { ["path"] = "Feeds.cs", ["line"] = 3, ["column"] = 36 });
        Assert.True(parameter.IsError == true && Text(parameter).Contains("No method, property or event there", StringComparison.Ordinal),
            $"hierarchy at the record's parameter read in its own body answered: {Text(parameter)}");

        async Task<string[]> CallsAsync(Dictionary<string, object?> arguments) =>
            [.. (await Call(client, McpTools.Hierarchy, arguments)).EnumerateArray()
                .Select(c => $"{c.GetProperty("name").GetString()} {c.GetProperty("path").GetString()}:{c.GetProperty("line").GetInt32()}:{c.GetProperty("column").GetInt32()}")];
    }

    /// <summary>rename of a type answers once Git's index holds its file's move: git status, run the moment the answer comes,
    /// as an agent's shell runs it, shows the file renamed, never the old one deleted and the new one untracked.</summary>
    private async Task An_agents_rename_of_a_type_answers_once_gits_index_holds_its_files_move()
    {
        string folder = await TakeFolderAsync();
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        await TestGit.InitAsync(folder);
        await using McpClient client = await Start(["--mcp", folder]);
        string said = Text(await Call(client, McpTools.Rename, new() { ["symbol"] = "Helper", ["newName"] = "Doubler" }));
        string[] status = (await TestGit.RunAsync(folder, "status", "--porcelain", "--untracked-files=all")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(said.EndsWith("; renamed Helper.cs to Doubler.cs", StringComparison.Ordinal), $"The agent's rename of Helper to Doubler answered: {said}");
        Assert.True(status.Length == 2 && status[0].StartsWith('R') && status[0].EndsWith(" Helper.cs -> Doubler.cs", StringComparison.Ordinal) && status[1] == " M Program.cs",
            $"git status, run as rename answered, did not show Helper.cs renamed to Doubler.cs and Program.cs changed:\n{string.Join('\n', status)}");
    }

    private const string Figures = "abstract class Shape\n{\n    public abstract double Area();\n}\n\n"
        + "sealed class Square(double side) : Shape\n{\n    public override double Area() => side * side;\n}\n";

    /// <summary>Each hierarchy entry's place as "path:line:column".</summary>
    private static IEnumerable<string> Placed(JsonElement entries) =>
        entries.EnumerateArray().Select(e => $"{e.GetProperty("path").GetString()}:{e.GetProperty("line").GetInt32()}:{e.GetProperty("column").GetInt32()}");

    private const string Shelf = "sealed class Shelf\n{\n    private readonly string[] books = [\"a\", \"b\"];\n\n"
        + "    /// <summary>The book_at a place on the shelf, from a List&lt;string&gt; of them kept under <c>C:\\temp</c> in C:\\temp\\books.</summary>\n"
        + "    public string this[int at] => books[at];\n\n"
        + "    public int Size()\n    {\n        int unused = 1;\n        return books.Length;\n    }\n}\n";

    private async Task An_agent_finds_a_name_itself_first_an_indexer_by_name_reads_a_card_as_plain_text_and_a_fixs_suppressions_counted()
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

        // The hover card's words, with none of Markdown's escapes or fences, a path's backslashes kept as code and in words.
        string card = (await Call(client, McpTools.SymbolInfo, new() { ["symbol"] = "Shelf.this[int]" })).GetString()!;
        Assert.True(card.Contains("string Shelf.this[int at] { get; }", StringComparison.Ordinal)
            && card.Contains(@"The book_at a place on the shelf, from a List<string> of them kept under `C:\temp` in C:\temp\books.", StringComparison.Ordinal)
            && !card.Contains("```", StringComparison.Ordinal), $"symbol_info of the indexer said:\n{card}");

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
    private async Task An_agents_calls_on_many_files_leave_open_only_the_file_it_opened_the_one_its_edit_left_unsaved_and_the_last_few_asked()
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
    /// unchanged, answers what the first did from the server's last pull, sending none (the protocol trace), once nothing has
    /// told the server anything since that pull (<see cref="AskedAgainAsync"/>).</summary>
    private async Task A_second_diagnostics_of_an_unchanged_file_answers_from_the_servers_last_pull_sending_none()
    {
        await TakeFolderAsync();
        await using McpClient client = await StartTracedAsync();
        string trace = Path.Combine(OwnState, "logs", "lsp.log");
        int asked = TraceLines(trace).Length;
        string first = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
        Assert.Contains("unused", first, StringComparison.Ordinal);
        // The window's own pulls of the file just opened come first, then the server's word after its load: the ask after that word pulls again.
        await MovedAfterLoadAsync();
        await AskedAgainAsync(client, trace, first, asked, "The second ask of Helper.cs, unchanged,", meanwhile: () => Task.CompletedTask);
        Assert.Contains("Helper.cs", ServerOpen(trace));
    }

    /// <summary>Asks diagnostics of Helper.cs again, after <paramref name="meanwhile"/>, until an ask finds nothing told the
    /// language server since the ask before it began (the first at lsp.log's line <paramref name="asked"/>), and finds that
    /// one answers <paramref name="first"/> sending no pull. An ask after something told it (its word that diagnostics may
    /// have changed, a change on disk Passenger heard, as when the first walks of the watchers of a server started ahead tell
    /// what its builds wrote) rightly pulls again, so it is asked again; one that pulls though nothing was told fails.</summary>
    private async Task AskedAgainAsync(McpClient client, string trace, string first, int asked, string what, Func<Task> meanwhile)
    {
        List<string> told = [];
        for (int ask = 0; ask < 6; ask++)
        {
            int pulls = Settled(trace);
            int asking = TraceLines(trace).Length;
            await meanwhile();
            string again = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" }));
            Assert.Equal(first, again);
            await TracedAsync(client, trace);
            if (Pulls(trace, "Helper.cs") == pulls)
            {
                return;
            }
            string[] moved = MovedSince(trace, asked, PullLine(trace, "Helper.cs", pulls + 1));
            Assert.True(moved.Length > 0,
                $"{what} sent the language server {Pulls(trace, "Helper.cs") - pulls} pulls of its diagnostics though nothing was told the server since the ask before it, whose pull answered");
            told.Add(string.Join("; ", moved));
            asked = asking;
        }
        Assert.Fail($"{what} pulled again at each of six asks, each after something was told the language server since the ask before it: {string.Join(" | ", told)}");
    }

    /// <summary>The count of lsp.log's pulls of Helper.cs once it has held still a second, the window's own pulls of it done.</summary>
    private static int Settled(string trace)
    {
        int pulls = -1;
        for (int round = 0; round < 20 && Pulls(trace, "Helper.cs") != pulls; round++)
        {
            pulls = Pulls(trace, "Helper.cs");
            Waiting.For(TimeSpan.FromSeconds(1), "the trace of the calls' pulls to settle");
        }
        return pulls;
    }

    /// <summary>The line of lsp.log of pull request <paramref name="number"/> of a file, counted from 1; past the end where
    /// there is none.</summary>
    private static int PullLine(string trace, string file, int number)
    {
        string[] lines = TraceLines(trace);
        int seen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsPull(lines[i], file) && ++seen == number)
            {
                return i;
            }
        }
        return lines.Length;
    }

    /// <summary>Whether a line of lsp.log is a pull of a file's diagnostics sent the server.</summary>
    private static bool IsPull(string line, string file) => line.Contains("] --> ", StringComparison.Ordinal)
        && line.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DocumentDiagnostic}\"", StringComparison.Ordinal) && line.Contains("/" + file + "\"", StringComparison.Ordinal);

    /// <summary>What lsp.log says moved the server's answers (<see cref="Passenger.Lsp.LanguageServer.Changes"/>) from line
    /// <paramref name="from"/> to <paramref name="to"/>: each notification it was sent, but the protocol's own, and its word
    /// that its projects loaded or its diagnostics may have changed, and its asks for a project's builds.</summary>
    private static string[] MovedSince(string trace, int from, int to)
    {
        string[] lines = TraceLines(trace);
        string[] moving = [Passenger.Lsp.LspMethods.DiagnosticRefresh, Passenger.Lsp.LspMethods.ProjectInitializationComplete, Passenger.Roslyn.Extension.ProjectMethods.Info];
        return [.. lines[Math.Min(from, lines.Length)..Math.Min(to, lines.Length)].Select(l => (Line: l, Method: MethodOf(l))).Where(l => l.Method is { } method
            && (l.Line.Contains("] --> ", StringComparison.Ordinal) && !l.Line.Contains("\"id\":", StringComparison.Ordinal) && !method.StartsWith("$/", StringComparison.Ordinal)
                || l.Line.Contains("] <-- ", StringComparison.Ordinal) && moving.Contains(method)))
            .Select(l => l.Method!).Distinct()];

        static string? MethodOf(string line) =>
            line.IndexOf("\"method\":\"", StringComparison.Ordinal) is var at and >= 0 && line.IndexOf('"', at + 10) is var end and > 0 ? line[(at + 10)..end] : null;
    }

    /// <summary>A type a code action moved to a file of its own is known at once to what the agent asks next of a file using
    /// it, each tool asked first after a move of its own: the language server takes the new file into the project a moment
    /// later, and answered before then, diagnostics said the type could not be found and the other tools answered without
    /// it, of another symbol or none.</summary>
    private async Task What_an_agent_asks_at_once_after_a_code_action_moved_a_type_to_a_file_of_its_own_finds_the_type_there()
    {
        await TakeFolderAsync();
        string[] kinds = ["Circle", "Oval", "Star", "Ring", "Kite"];
        Write("Shapes.cs", "namespace Shop;\n\npublic class Square\n{\n    public int Side;\n}\n"
            + string.Concat(kinds.Select(k => $"\npublic class {k}\n{{\n    public double Radius;\n}}\n")));
        string garden = "namespace Shop;\n\npublic static class Garden\n{\n" + string.Concat(kinds.Select(k => $"    public static double Of{k}({k} shape) => shape.Radius;\n")) + "}\n";
        Write("Garden.cs", garden);
        await using McpClient client = await Start(["--mcp", Folder]);
        Assert.Empty(Errors(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Garden.cs" })));

        string[] errors = Errors(await MovedThenAsync("Circle", used => Call(client, McpTools.Diagnostics, new() { ["path"] = "Garden.cs" })));
        Assert.True(errors.Length == 0, $"diagnostics of Garden.cs at once after Circle's move answered errors the language server has yet to clear, which an agent would fix: {string.Join("; ", errors)}");
        JsonElement declared = Assert.Single((await MovedThenAsync("Oval", used => Call(client, McpTools.Definition, used))).EnumerateArray());
        Assert.Equal(("Oval.cs", 3), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        string card = (await MovedThenAsync("Star", used => Call(client, McpTools.SymbolInfo, used))).GetString()!;
        Assert.True(card.Contains("class Shop.Star", StringComparison.Ordinal), $"symbol_info of Star at once after its move answered of another symbol: {card}");
        JsonElement usages = await MovedThenAsync("Ring", used => Call(client, McpTools.Usages, used));
        Assert.Equal(("Ring (class)", "Garden.cs, Ring.cs"), (usages.GetProperty("symbol").GetString(), string.Join(", ", Used(usages).Select(u => u.Path).Distinct().Order(StringComparer.Ordinal))));
        JsonElement[] found = [.. (await MovedThenAsync("Kite", _ => Call(client, McpTools.FindSymbol, new() { ["query"] = "Kite" }))).EnumerateArray()];
        Assert.True(found.Any(f => f.GetProperty("kind").GetString() == "class" && f.GetProperty("path").GetString() == "Kite.cs"),
            $"find_symbol Kite at once after its move found no class Kite in Kite.cs: {string.Join(", ", found.Select(f => f.GetRawText()))}");

        // The light bulb's Move type to X.cs on the type's name, then at once the tool, at the type's use in Garden.cs.
        async Task<JsonElement> MovedThenAsync(string kind, Func<Dictionary<string, object?>, Task<JsonElement>> ask)
        {
            (int line, int column) = At(File.ReadAllText(Path.Combine(Folder, "Shapes.cs")), $"class {kind}", "class ".Length);
            Assert.Equal($"Applied 'Move type to {kind}.cs' to 2 files", (await Call(client, McpTools.ApplyCodeAction,
                new() { ["path"] = "Shapes.cs", ["line"] = line, ["column"] = column, ["title"] = $"Move type to {kind}.cs" })).GetString());
            (int usedLine, int usedColumn) = At(garden, $"({kind} shape", 1);
            return await ask(new() { ["path"] = "Garden.cs", ["line"] = usedLine, ["column"] = usedColumn });
        }

        static string[] Errors(JsonElement found) =>
            [.. found.EnumerateArray().Where(d => d.GetProperty("severity").GetString() == "error").Select(d => $"{d.GetProperty("code").GetString()} {d.GetProperty("message").GetString()}")];

        static (int Line, int Column) At(string text, string what, int into)
        {
            int offset = text.IndexOf(what, StringComparison.Ordinal) + into;
            return (text[..offset].Count(c => c == '\n') + 1, offset - text.LastIndexOf('\n', offset - 1));
        }
    }

    /// <summary>Passenger started as the agent's server over the Sum project, tracing what it says to its language servers
    /// (lsp.log) from its start, as the settings it starts on ask.</summary>
    private async Task<McpClient> StartTracedAsync()
    {
        File.WriteAllText(Path.Combine(OwnState, StateStore.SettingsFile), new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = global::Passenger.App.Settings.AppSettings.Upgrades.Count,
            ["traceProtocols"] = true,
        }.ToJsonString());
        return await Start(["--mcp", Folder]);
    }

    /// <summary>Waits until the C# server's log, kept from its start, notes its word that its diagnostics may have changed
    /// since it last said its projects loaded: it runs its generators again a second after a load, then says so, and from
    /// that word on, while nothing else changes, a pull it answered stands.</summary>
    private async Task MovedAfterLoadAsync()
    {
        string folder = Path.Combine(OwnState, "logs", "roslyn");
        string[] said = [];
        Assert.True(await Waiting.UntilAsync(() =>
            {
                said = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "server-*.log").Order(StringComparer.Ordinal).LastOrDefault() is { } log ? TraceLines(log) : [];
                int loaded = Array.FindLastIndex(said, l => l.TrimEnd().EndsWith(Passenger.Lsp.LanguageServer.LoadedNote, StringComparison.Ordinal));
                return loaded >= 0 && said.Skip(loaded + 1).Any(l => l.TrimEnd().EndsWith(Passenger.Lsp.LanguageServer.RefreshedNote, StringComparison.Ordinal));
            }, TimeSpan.FromSeconds(30).Scaled(), Cancel),
            $"The C# server never said its diagnostics may have changed after it last said its projects loaded, as it does once its generators have run again; its log:\n{string.Join('\n', said.TakeLast(20))}");
    }

    /// <summary>A log's lines as they stand, the file read however Passenger holds it.</summary>
    private static string[] TraceLines(string trace)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n');
    }

    /// <summary>How many pulls of a file's diagnostics lsp.log's trace says the language server was sent.</summary>
    private static int Pulls(string trace, string file)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal)
            && l.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DocumentDiagnostic}\"", StringComparison.Ordinal) && l.Contains("/" + file + "\"", StringComparison.Ordinal));
    }

    /// <summary>Has every message sent the language server so far reach lsp.log, whose lines are written a moment after they
    /// cross: a hover card asked of Helper.cs now, its request found there after them.</summary>
    private async Task TracedAsync(McpClient client, string trace)
    {
        int hovers = Sent(trace, Passenger.Roslyn.Extension.ExtensionMethods.Hover);
        await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Helper.cs", ["line"] = 3, ["column"] = 23 });
        Assert.True(await Waiting.UntilAsync(() => Sent(trace, Passenger.Roslyn.Extension.ExtensionMethods.Hover) > hovers, TimeSpan.FromSeconds(10).Scaled(), Cancel),
            "A hover card asked of Helper.cs never reached lsp.log's trace of what the language server was sent");
    }

    /// <summary>How many requests of a method lsp.log's trace says the server was sent.</summary>
    private static int Sent(string trace, string method)
    {
        using FileStream read = new(trace, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(read);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("] --> ", StringComparison.Ordinal) && l.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal));
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

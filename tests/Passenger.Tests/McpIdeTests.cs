using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;
using Passenger.Core;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>The IDE's own windows for an agent: Find in Files in the window's scopes and masks, a file's Local History read
/// and reverted, what Problems and the Build window list beyond a build's answer, the person's settings read and changed as
/// Settings changes them, and a Razor component's errors, names and usages. Attach Profiler on a program already running
/// is <see cref="McpIdeProfilingTests"/>'.</summary>
public sealed class McpIdeTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpIdeTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    /// <summary>An agent's IDE tools as one flow, each step one test's check in an instance of its own: a label put
    /// while another holds Local History, an action waiting for its file's grammar and for its work, thousands of
    /// warnings answered errors first, settings and what .editorconfig sets over them, a named state's settings kept
    /// its own, find, revert, problems and the build, a scope inside node_modules or bin, a picture of the window, and
    /// a Razor component's errors.</summary>
    [Fact]
    public async Task An_agents_IDE_tools_answer_settings_problems_finds_history_and_pictures_as_the_window_holds_them()
    {
        await Flow.StepApartAsync("a label put while another passenger holds local history is answered as waiting and listed once let go", () => new McpIdeTests(restored), t => t.A_label_put_while_another_passenger_holds_local_history_is_answered_as_waiting_and_listed_once_let_go());
        await Flow.StepApartAsync("an action waits for the grammar reading the file it asks of", () => new McpIdeTests(restored), t => t.An_action_waits_for_the_grammar_reading_the_file_it_asks_of());
        await Flow.StepApartAsync("problems of thousands of warnings answer within the limit errors first and a path or severity reaches the rest", () => new McpIdeTests(restored), t => t.Problems_of_thousands_of_warnings_answer_within_the_limit_errors_first_and_a_path_or_severity_reaches_the_rest());
        await Flow.StepApartAsync("settings say what the folders editorconfig files set over each setting", () => new McpIdeTests(restored), t => t.Settings_say_what_the_folders_editorconfig_files_set_over_each_setting());
        await Flow.StepApartAsync("an agents passenger opens on the settings kept in its named state and keeps its changes its own", () => new McpIdeTests(restored), t => t.An_agents_passenger_opens_on_the_settings_kept_in_its_named_state_and_keeps_its_changes_its_own());
        await Flow.StepApartAsync("an agent finds text in the windows scopes reverts a file through local history and reads problems and the build window", () => new McpIdeTests(restored), t => t.An_agent_finds_text_in_the_windows_scopes_reverts_a_file_through_local_history_and_reads_problems_and_the_build_window());
        await Flow.StepApartAsync("an agents directory scope inside node modules or bin finds and replaces what its files hold", () => new McpIdeTests(restored), t => t.An_agents_directory_scope_inside_node_modules_or_bin_finds_and_replaces_what_its_files_hold());
        await Flow.StepApartAsync("an agents ide action waits for its work and answers what it came to asking nobody", () => new McpIdeTests(restored), t => t.An_agents_ide_action_waits_for_its_work_and_answers_what_it_came_to_asking_nobody());
        await Flow.StepApartAsync("an agents picture of passengers window at any scale shows the code and the active tabs name", () => new McpIdeTests(restored), t => t.An_agents_picture_of_passengers_window_at_any_scale_shows_the_code_and_the_active_tabs_name());
        await Flow.StepApartAsync("an agent reads a razor components errors names and usages through roslyns razor", () => new McpIdeTests(restored), t => t.An_agent_reads_a_razor_components_errors_names_and_usages_through_roslyns_razor());
    }

    /// <summary>A label put while another Passenger on the same state holds Local History past the call's wait is answered as
    /// waiting for it, never as put, and is listed once that Passenger lets go (#1791).</summary>
    private async Task A_label_put_while_another_passenger_holds_local_history_is_answered_as_waiting_and_listed_once_let_go()
    {
        await TakeFolderAsync();
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.LocalHistoryLabel, new() { ["name"] = "first" });
        string root = Path.Combine(Passenger.App.State.ProjectState.FolderOf(OwnState, PathNames.Canonical(Folder)), "localHistory");
        Assert.True(File.Exists(Path.Combine(root, "changes.log")), $"No Local History log at {root} after a label was put");

        string answer;
        using (FileStream holding = new(Path.Combine(root, "changes.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            answer = (await Call(client, McpTools.LocalHistoryLabel, new() { ["name"] = "held" })).GetString()!;
        }
        Assert.True(answer.StartsWith("Label held waits for another Passenger", StringComparison.Ordinal),
            $"A label put while another Passenger held Local History past the call's wait answered \"{answer}\", as if it were kept");
        JsonElement labelled = (await Call(client, McpTools.LocalHistory)).GetProperty("rows")[0];
        Assert.True(labelled.GetProperty("title").GetString() == "held" && labelled.GetProperty("label").GetBoolean(),
            $"Once the other Passenger let go, Local History's newest row is {labelled.GetRawText()}, not the label held");
    }

    /// <summary>A grammar takes a moment to read a long file, whose lines inside strings Convert Indentation must know: the action
    /// waits for it, right after the file opens and again after the edit its first conversion made, where each refused the agent.</summary>
    private async Task An_action_waits_for_the_grammar_reading_the_file_it_asks_of()
    {
        await TakeFolderAsync();
        Write("long.py", string.Concat(Enumerable.Range(0, 6000).Select(i => $"def f{i}(a):\n    if a:\n        return [x for x in a]\n    return None\n\n\n")));
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.Open, new() { ["path"] = "long.py" });
        foreach (string action in (string[])[AppActions.ConvertIndentationToTabs, AppActions.ConvertIndentationToSpaces])
        {
            CallToolResult converted = await Result(client, McpTools.IdeAction, new() { ["action"] = action });
            Assert.True(converted.IsError != true, $"{action} answered: {Text(converted)}");
        }
    }

    /// <summary>A solution of thousands of warnings and an error answers Problems' lists within their limit, the error first,
    /// saying how many warnings it left out and how to ask for them: severity error gives the error alone, a folder's path the
    /// lists within it, and each file's path its own, read on from the line its words name past the limit, which together
    /// reach every warning.</summary>
    private async Task Problems_of_thousands_of_warnings_answer_within_the_limit_errors_first_and_a_path_or_severity_reaches_the_rest()
    {
        await TakeFolderAsync();
        const int Files = 12;
        const int Each = 250;
        Write("Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
        for (int file = 0; file < Files; file++)
        {
            Write($"Warnings/W{file}.cs", $"static class W{file}\n{{\n    static void M()\n    {{\n{string.Concat(Enumerable.Range(0, Each).Select(i => $"        int v{i} = {i};\n"))}    }}\n}}\n");
        }
        await using McpClient client = await Start(["--mcp", Folder]);
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = true } });

        JsonElement answer = await Call(client, McpTools.Diagnostics);
        Assert.True(answer.GetRawText().Length < 64 * 1024, $"Problems' lists of thousands of warnings answered {answer.GetRawText().Length:N0} characters");
        JsonElement[] solution = [.. answer.GetProperty("solution").EnumerateArray()];
        int listed = solution.Length + answer.GetProperty("build").GetArrayLength() + answer.GetProperty("toolset").GetArrayLength();
        Assert.True(listed == AgentAnswers.MostFindings && solution[0].GetProperty("code").GetString() == "CS0029",
            $"The lists held {listed} findings, the solution's first {solution[0].GetRawText()}, where {AgentAnswers.MostFindings} were to be listed, the error first");
        string more = answer.GetProperty("more").GetString()!;
        int warnings = Files * Each;
        System.Text.RegularExpressions.Match said = System.Text.RegularExpressions.Regex.Match(more, @"^([\d,]+) more findings left out: ([\d,]+) warnings; .*severity.*a folder's path");
        int left = said.Success ? int.Parse(said.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture) : 0;
        Assert.True(said.Success && said.Groups[1].Value == said.Groups[2].Value && left + listed - 1 >= warnings,
            $"What the lists left out, every warning past the {listed - 1} listed of at least {warnings}, was said as: {more}");

        JsonElement errors = await Call(client, McpTools.Diagnostics, new() { ["severity"] = "error" });
        Assert.Equal(["CS0029 Bad.cs:3"], errors.GetProperty("solution").EnumerateArray().Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}"));
        Assert.False(errors.TryGetProperty("more", out _), "The errors alone were said to leave some out");

        JsonElement folder = await Call(client, McpTools.Diagnostics, new() { ["path"] = "Warnings" });
        Assert.True(folder.GetProperty("solution").EnumerateArray().All(p => p.GetProperty("path").GetString()!.StartsWith("Warnings", StringComparison.Ordinal)),
            $"A folder's lists named a file outside it: {folder.GetProperty("solution").GetRawText()[..Math.Min(400, folder.GetProperty("solution").GetRawText().Length)]}");

        // Each file's own, its answer read on from the line its words name where it holds them past the limit.
        int reached = 0;
        for (int file = 0; file < Files; file++)
        {
            for (int? from = null; ;)
            {
                Dictionary<string, object?> asked = new() { ["path"] = $"Warnings/W{file}.cs", ["severity"] = "warning" };
                if (from is int line)
                {
                    asked["from"] = line;
                }
                CallToolResult own = await Result(client, McpTools.Diagnostics, asked);
                using JsonDocument ownJson = JsonDocument.Parse(((TextContentBlock)own.Content[0]).Text);
                reached += ownJson.RootElement.GetArrayLength();
                if (own.Content is not [_, TextContentBlock { Text: var words }])
                {
                    break;
                }
                System.Text.RegularExpressions.Match next = System.Text.RegularExpressions.Regex.Match(words, @"severity warning with from (\d+) lists those from that line on$");
                Assert.True(next.Success, $"W{file}.cs's warnings past the limit were said as: {words}");
                from = int.Parse(next.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        Assert.Equal(warnings, reached);
    }

    /// <summary>An agent's settings say, setting by setting, what the folder's .editorconfig files set over it for the files
    /// their sections name, as Settings does under its rows: GDScript's layout the line length scripts take, XAML's
    /// nothing, set for scripts alone, and the final line break what Markdown files take.</summary>
    private async Task Settings_say_what_the_folders_editorconfig_files_set_over_each_setting()
    {
        await TakeFolderAsync();
        Write(".editorconfig", "root = true\n\n[*.gd]\nmax_line_length = 120\n\n[*.md]\ninsert_final_newline = false\n");
        await using McpClient client = await Start(["--mcp", Folder]);
        JsonElement[] settings = [.. (await Call(client, McpTools.Settings)).GetProperty("settings").EnumerateArray()];
        JsonElement Named(string name) => settings.Single(s => s.GetProperty("name").GetString() == name);
        string[] said = Named("gdScriptFormat").TryGetProperty("editorConfig", out JsonElement lines) ? [.. lines.EnumerateArray().Select(l => l.GetString()!)] : [];
        Assert.True(said.SequenceEqual(["./.editorconfig sets max_line_length = 120 for *.gd"]),
            $"gdScriptFormat said the folder's .editorconfig sets over it: {string.Join("; ", said)}");
        Assert.False(Named("xamlFormat").TryGetProperty("editorConfig", out JsonElement xaml),
            $"xamlFormat was said to be set over by a max_line_length set for scripts alone: {xaml}");
        Assert.Equal("./.editorconfig sets insert_final_newline = false for *.md", Named("ensureFinalLineBreak").GetProperty("editorConfig")[0].GetString());
    }

    /// <summary>An agent's Passenger whose state PASSENGER_SETTINGS names opens on the settings.json kept there, as a person's
    /// window would: solution-wide analysis on there is on from its start, listing an error of a file nobody opened, and the
    /// settings stay its own, kept nowhere, so a change it makes leaves the file as it was.</summary>
    private async Task An_agents_passenger_opens_on_the_settings_kept_in_its_named_state_and_keeps_its_changes_its_own()
    {
        await TakeFolderAsync();
        Write("Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
        string file = Path.Combine(OwnState, global::Passenger.App.State.StateStore.SettingsFile);
        string kept = new System.Text.Json.Nodes.JsonObject { ["version"] = global::Passenger.App.Settings.AppSettings.Upgrades.Count, ["solutionWideAnalysis"] = true }.ToJsonString();
        File.WriteAllText(file, kept);
        McpClient client = await Start(["--mcp", Folder]);

        JsonElement settings = await Call(client, McpTools.Settings);
        Assert.False(settings.GetProperty("kept").GetBoolean(), "The agent's Passenger said its settings are kept, where they are its own");
        Assert.True(settings.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "solutionWideAnalysis").GetProperty("value").GetBoolean(),
            "The agent's Passenger did not open on solution-wide analysis on, as its state's settings.json keeps it");
        string[] solution = [.. (await Call(client, McpTools.Diagnostics)).GetProperty("solution").EnumerateArray().Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(solution.Contains("CS0029 Bad.cs:3"), $"Solution-wide analysis on from the start listed: {string.Join("; ", solution)}");

        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["editorFontSize"] = 17 } });
        await client.DisposeAsync();
        // Ended, it has written all it would keep.
        Assert.True(LastPassenger().WaitForExit(TimeSpan.FromMinutes(1).Scaled()), "The agent's Passenger did not end with its session");
        Assert.Equal(kept, File.ReadAllText(file));
    }

    /// <summary>An agent's Passenger on a new state finds text in each scope, reverts a file through Local History and reads
    /// Problems and the Build window; solution-wide analysis is on from its start, as a new state has it, listing the error of
    /// a file nobody opened, and settings_set turning it off stops it, Problems answering no Solution tab.</summary>
    private async Task An_agent_finds_text_in_the_windows_scopes_reverts_a_file_through_local_history_and_reads_problems_and_the_build_window()
    {
        await TakeFolderAsync();
        const string helper = "static class Helper\n{\n    public static int Twice(int value)\n    {\n        int unused = 1;\n        return value * 2;\n    }\n}\n";
        Write("Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
        Write("Notes.txt", "one two\ntwo three\n");
        // An earlier version wrote off whether or not the person turned it off, so off is left to the default, and on kept.
        foreach ((bool kept, bool stays) in new[] { (true, true), (false, false) })
        {
            System.Text.Json.Nodes.JsonObject earlier = new() { ["solutionWideAnalysis"] = kept };
            global::Passenger.App.Settings.AppSettings.Upgrades[5](earlier);
            Assert.True(earlier.ContainsKey("solutionWideAnalysis") == stays, $"Version 5's solutionWideAnalysis {kept} was {(stays ? "dropped" : "kept")} as it was read");
        }
        // The default as a person's new state has it, not the test run's.
        await using McpClient client = await Start(["--mcp", Folder], environment: new() { [global::Passenger.App.CSharp.CSharpService.SolutionWideVariable] = null });

        JsonElement found = await Call(client, McpTools.FindInFiles, new() { ["text"] = "Twice" });
        Assert.Equal(["Helper.cs:3", "Program.cs:4"], Places(found));
        Assert.Equal(["Helper.cs:3"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = "Twice", ["mask"] = "*.cs, !Program.cs" })));
        Assert.Equal(["Program.cs:4"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = @"Twice\(\w\)", ["regex"] = true })));
        await Call(client, McpTools.Open, new() { ["path"] = "Helper.cs", ["line"] = 1, ["column"] = 1 });
        Assert.Equal(["Helper.cs:3"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = "Twice", ["scope"] = "openFiles" })));
        foreach ((Dictionary<string, object?> arguments, string said) in new (Dictionary<string, object?>, string)[]
        {
            (new() { ["text"] = "(", ["regex"] = true }, "Invalid pattern '('"),
            // A directory given alone is the directory scope's, never passed over for the whole solution's.
            (new() { ["text"] = "Twice", ["directory"] = "nowhere" }, "Directory is not found"),
            (new() { ["text"] = "Twice", ["scope"] = "openFiles", ["directory"] = "." }, "give scope directory, or leave directory out"),
            (new() { ["text"] = "Twice", ["mask"] = "*.cs; *.json" }, "File masks should be comma-separated"),
        })
        {
            CallToolResult refused = await Result(client, McpTools.FindInFiles, arguments);
            Assert.True(refused.IsError == true && Text(refused).Contains(said, StringComparison.Ordinal), $"find_in_files {JsonSerializer.Serialize(arguments)} answered: {Text(refused)}");
        }

        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = "/* a */ " });
        await Call(client, McpTools.SaveAll);
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["text"] = "/* b */ " });
        await Call(client, McpTools.SaveAll);
        JsonElement[] rows = [.. (await Call(client, McpTools.LocalHistory, new() { ["path"] = "Helper.cs" })).GetProperty("rows").EnumerateArray()];
        Assert.True(rows.Length >= 2 && rows[0].GetProperty("title").GetString() == "Change" && rows[1].GetProperty("title").GetString() == "Change",
            $"Helper.cs's history after two saves: {string.Join("; ", rows.Select(r => r.GetRawText()))}");
        JsonElement before = await Call(client, McpTools.LocalHistory, new() { ["path"] = "Helper.cs", ["set"] = rows[0].GetProperty("set").GetInt64() });
        Assert.True(before.GetProperty("exists").GetBoolean());
        Assert.Equal("/* a */ " + helper, before.GetProperty("text").GetString());

        Assert.Equal("Label agent was here put", (await Call(client, McpTools.LocalHistoryLabel, new() { ["name"] = "agent was here" })).GetString());
        JsonElement labelled = (await Call(client, McpTools.LocalHistory)).GetProperty("rows")[0];
        Assert.Equal(("agent was here", true), (labelled.GetProperty("title").GetString(), labelled.GetProperty("label").GetBoolean()));
        await Call(client, McpTools.LocalHistoryRevert, new() { ["path"] = "Helper.cs", ["set"] = rows[1].GetProperty("set").GetInt64() });
        Assert.Equal(helper, File.ReadAllText(Path.Combine(Folder, "Helper.cs")));
        Assert.EndsWith("\tstatic class Helper", (await Call(client, McpTools.EditorText, new() { ["path"] = "Helper.cs", ["to"] = 1 })).GetString(), StringComparison.Ordinal);
        string reverted = (await Call(client, McpTools.LocalHistory, new() { ["path"] = "Helper.cs" })).GetProperty("rows")[0].GetProperty("title").GetString()!;
        Assert.StartsWith("Revert to ", reverted, StringComparison.Ordinal);
        CallToolResult unknown = await Result(client, McpTools.LocalHistoryRevert, new() { ["path"] = "Helper.cs", ["set"] = 999_999 });
        Assert.True(unknown.IsError == true && Text(unknown).Contains("No change set 999999", StringComparison.Ordinal), $"A revert to no change set answered: {Text(unknown)}");

        JsonElement built = await Call(client, McpTools.Build);
        Assert.False(built.GetProperty("succeeded").GetBoolean());
        JsonElement problems = await Call(client, McpTools.Diagnostics);
        string[] build = [.. problems.GetProperty("build").EnumerateArray().Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(build.Contains("CS0029 Bad.cs:3") && build.Contains("CS0219 Helper.cs:5"), $"Problems' Build tab, for an agent: {string.Join("; ", build)}");
        Assert.Equal(JsonValueKind.Array, problems.GetProperty("toolset").ValueKind);
        string[] analysed = [.. problems.GetProperty("solution").EnumerateArray().Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(analysed.Contains("CS0029 Bad.cs:3"), $"Solution-wide analysis, on from a new state's start, listed: {string.Join("; ", analysed)}");
        JsonElement turnedOff = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = false } })).EnumerateArray());
        Assert.Equal((true, false), (turnedOff.GetProperty("before").GetBoolean(), turnedOff.GetProperty("after").GetBoolean()));
        Assert.False((await Call(client, McpTools.Diagnostics)).TryGetProperty("solution", out _), "Problems' solution tab was answered with solution-wide analysis turned off");
        string output = (await Call(client, McpTools.Output, new() { ["of"] = "build" })).GetString()!;
        Assert.True(output.Contains("CS0029", StringComparison.Ordinal), $"The Build window's console, for an agent, lacks the build's error:\n{output}");
        // A run refused for a failed build names its errors, so the agent fixes them without a second call to find them.
        CallToolResult notRun = await Result(client, McpTools.Run);
        Assert.True(notRun.IsError == true && Text(notRun).Contains("Bad.cs:3:", StringComparison.Ordinal) && Text(notRun).Contains("CS0029", StringComparison.Ordinal),
            $"A run refused for a failed build answered: {Text(notRun)}");
        // The action waits for its build, and says what came of it.
        CallToolResult rebuilt = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.RebuildSolution });
        Assert.True(rebuilt.IsError == true && Text(rebuilt).Contains("Rebuild failed", StringComparison.Ordinal) && Text(rebuilt).Contains("Problems lists them", StringComparison.Ordinal),
            $"Rebuild Solution with an error to find answered: {Text(rebuilt)}");

        JsonElement turnedOn = Assert.Single((await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["solutionWideAnalysis"] = true } })).EnumerateArray());
        Assert.Equal((false, true), (turnedOn.GetProperty("before").GetBoolean(), turnedOn.GetProperty("after").GetBoolean()));
        string[] solution = [.. (await Call(client, McpTools.Diagnostics)).GetProperty("solution").EnumerateArray().Select(p => $"{p.GetProperty("code").GetString()} {p.GetProperty("path").GetString()}:{p.GetProperty("line").GetInt32()}")];
        Assert.True(solution.Contains("CS0029 Bad.cs:3"), $"Solution-wide analysis's problems, for an agent: {string.Join("; ", solution)}");
        // A windowless Passenger's settings are its own, kept nowhere.
        Assert.False((await Call(client, McpTools.Settings)).GetProperty("kept").GetBoolean());

        Assert.Equal("Replaced 2 matches in 1 file: Notes.txt",
            (await Call(client, McpTools.ReplaceInFiles, new() { ["text"] = "two", ["replacement"] = "2", ["mask"] = "*.txt" })).GetString());
        Assert.Equal("one 2\n2 three\n", File.ReadAllText(Path.Combine(Folder, "Notes.txt")));
        Assert.Equal("Replaced 1 match in 1 file: Helper.cs",
            (await Call(client, McpTools.ReplaceInFiles, new() { ["text"] = "unused", ["replacement"] = "spare", ["scope"] = "currentFile" })).GetString());
        Assert.Equal(helper.Replace("unused", "spare", StringComparison.Ordinal), File.ReadAllText(Path.Combine(Folder, "Helper.cs")));

        string edited = Text(await Call(client, McpTools.Edit, new() { ["path"] = "Helper.cs", ["oldText"] = "return value * 2;", ["newText"] = "int twice = value * 2;\n        return twice;" }));
        Assert.Equal("Changed Helper.cs at line 6, saved:\n     6\t        int twice = value * 2;\n     7\t        return twice;", edited);
        string spare = helper.Replace("unused", "spare", StringComparison.Ordinal);
        Assert.Equal(spare.Replace("return value * 2;", "int twice = value * 2;\n        return twice;", StringComparison.Ordinal), File.ReadAllText(Path.Combine(Folder, "Helper.cs")));
        foreach ((Dictionary<string, object?> arguments, string said) in new (Dictionary<string, object?>, string)[]
        {
            (new() { ["path"] = "Helper.cs", ["oldText"] = "value * 3", ["newText"] = "x" }, "Helper.cs has no \"value * 3\" as written"),
            (new() { ["path"] = "Helper.cs", ["oldText"] = "int ", ["newText"] = "long " }, "Helper.cs has \"int \" 4 times (lines 3, 5, 6), so nothing changed: give more of the text around the one to change, or all true"),
        })
        {
            CallToolResult refused = await Result(client, McpTools.Edit, arguments);
            Assert.True(refused.IsError == true && Text(refused).Contains(said, StringComparison.Ordinal), $"edit {JsonSerializer.Serialize(arguments)} answered: {Text(refused)}");
        }
        Assert.Equal("Changed Helper.cs in 2 places, saved:\n     6\t        int doubled = value * 2;\n     7\t        return doubled;",
            Text(await Call(client, McpTools.Edit, new() { ["path"] = "Helper.cs", ["oldText"] = "twice", ["newText"] = "doubled", ["all"] = true })));
        Assert.Equal(spare.Replace("return value * 2;", "int doubled = value * 2;\n        return doubled;", StringComparison.Ordinal), File.ReadAllText(Path.Combine(Folder, "Helper.cs")));

        // As an agent's own file tools write: the open file must answer with the disk's text at once.
        File.WriteAllText(Path.Combine(Folder, "Helper.cs"), "static class Helper\n{\n    public static int Twice(int value) => value * 3;\n}\n");
        Assert.EndsWith("\t    public static int Twice(int value) => value * 3;",
            (await Call(client, McpTools.EditorText, new() { ["path"] = "Helper.cs", ["from"] = 3, ["to"] = 3 })).GetString(), StringComparison.Ordinal);
    }

    /// <summary>find_in_files and replace_in_files in a directory inside a folder the folder's search passes over
    /// (node_modules, bin) search its files all the same, which the solution's search still leaves out.</summary>
    private async Task An_agents_directory_scope_inside_node_modules_or_bin_finds_and_replaces_what_its_files_hold()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-passed-over-"));
        DeleteAtEnd(folder);
        TestFolders.Write(folder, "node_modules/pkg/index.js", "// needle in package\n");
        TestFolders.Write(folder, "bin/Debug/out.txt", "needle in bin\n");
        TestFolders.Write(folder, "src/a.js", "// needle in src\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        Assert.Equal([Path.Combine("src", "a.js") + ":1"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = "needle" })));
        foreach ((string directory, string file) in new[] { ("node_modules/pkg", Path.Combine("node_modules", "pkg", "index.js")), ("bin/Debug", Path.Combine("bin", "Debug", "out.txt")) })
        {
            string[] found = Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = "needle", ["scope"] = "directory", ["directory"] = directory }));
            Assert.True(found.SequenceEqual([file + ":1"]), $"find_in_files in the directory {directory} found [{string.Join(", ", found)}], where its {Path.GetFileName(file)} holds needle");
        }
        Assert.Equal($"Replaced 1 match in 1 file: {Path.Combine("bin", "Debug", "out.txt")}", (await Call(client, McpTools.ReplaceInFiles,
            new() { ["text"] = "needle", ["replacement"] = "pin", ["scope"] = "directory", ["directory"] = "bin/Debug" })).GetString());
        Assert.Equal("pin in bin\n", File.ReadAllText(Path.Combine(folder, "bin", "Debug", "out.txt")));
        Assert.Equal("// needle in package\n", File.ReadAllText(Path.Combine(folder, "node_modules", "pkg", "index.js")));
        Assert.Equal("// needle in src\n", File.ReadAllText(Path.Combine(folder, "src", "a.js")));
    }

    /// <summary>An agent on the person's window reads their settings and changes them as Settings does: asked first, a number
    /// brought into its range, kept in settings.json and in force in the window at once; a name or choice Settings lacks
    /// changes nothing, and Enable MCP Server and the program Godot runs on stay the person's.</summary>
    [AvaloniaFact]
    public void An_agent_reads_and_changes_the_persons_settings_as_settings_does_once_they_allow_it()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = OwnState, StartsLanguageServer = false };
        window.Show();
        window.KeepSettings();
        window.OpenFolder(Folder);
        window.OpenFile(Path.Combine(Folder, "Program.cs"));
        Ui.Settle();
        window.ApplySettings(window.Settings with { McpServer = true });
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement read = Await(Waiting.Apart(() => Call(client, McpTools.Settings)), "the agent reading the settings");
            Assert.True(read.GetProperty("kept").GetBoolean(), "The person's window's settings were read as kept nowhere");
            JsonElement font = Setting(read, "editorFontSize");
            Assert.Equal((13.0, 6.0, 48.0), (font.GetProperty("value").GetDouble(), font.GetProperty("min").GetDouble(), font.GetProperty("max").GetDouble()));
            Assert.Equal(["Always", "ForWarningsOrErrors", "ForErrors", "Never"],
                Setting(read, "showBuildResults").GetProperty("choices").EnumerateArray().Select(c => c.GetString()!));

            Task<JsonElement> changing = Waiting.Apart(() => Call(client, McpTools.SettingsSet, new()
            {
                ["changes"] = new Dictionary<string, object> { ["editorFontSize"] = 99, ["showBuildResults"] = "always" },
            }));
            Press(Question(window, "the question for the agent's settings change"), AgentToolDialog.AllowForSession);
            JsonElement[] changed = [.. Await(changing, "the agent's settings change").EnumerateArray()];
            JsonElement size = Assert.Single(changed, c => c.GetProperty("name").GetString() == "editorFontSize");
            Assert.Equal((13.0, 48.0), (size.GetProperty("before").GetDouble(), size.GetProperty("after").GetDouble()));
            Assert.Equal("brought into its range, 6 to 48", size.GetProperty("note").GetString());
            Assert.Equal("Always", Assert.Single(changed, c => c.GetProperty("name").GetString() == "showBuildResults").GetProperty("after").GetString());
            Assert.Equal(48, window.Settings.EditorFontSize);
            Assert.Equal(48, window.EditorArea.CodeEditor.FontSize);
            Assert.Equal(global::Passenger.App.Settings.BuildResultsMode.Always, window.Settings.ShowBuildResults);
            WaitFor(() => Ui.Shared(Path.Combine(OwnState, "settings.json")).Contains("\"editorFontSize\": 48", StringComparison.Ordinal), "settings.json to keep the agent's change");

            foreach ((Dictionary<string, object> changes, string said) in new (Dictionary<string, object>, string)[]
            {
                (new() { ["editorFontSise"] = 12, ["editorFontSize"] = 12 }, "Settings has no setting editorFontSise"),
                (new() { ["showBuildResults"] = "Sometimes" }, "showBuildResults is one of Always, ForWarningsOrErrors, ForErrors, Never, not \"Sometimes\""),
                (new() { ["editorFontSize"] = "large" }, "editorFontSize takes a number, not \"large\""),
                (new() { ["mcpServer"] = false }, "Enable MCP Server is the person's to change"),
                (new() { ["agentFolders"] = new[] { Folder } }, "The folders agents join unasked are the person's to change"),
                (new() { ["godotExecutable"] = "godot --verbose" }, "godotExecutable names a program Passenger runs, or code it loads: the person sets it in Settings"),
            })
            {
                CallToolResult refused = Await(Waiting.Apart(() => Result(client, McpTools.SettingsSet, new() { ["changes"] = changes })), "the refused settings change");
                Assert.True(refused.IsError == true && Text(refused).Contains(said, StringComparison.Ordinal), $"settings_set {JsonSerializer.Serialize(changes)} answered: {Text(refused)}");
            }
            Assert.Equal(48, window.Settings.EditorFontSize);
            Assert.True(window.Settings.McpServer, "An agent turned the person's MCP server off");
        }
        finally
        {
            Gone(client);
            window.Close();
        }

        static JsonElement Setting(JsonElement read, string name) =>
            read.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);
    }

    /// <summary>ide_action waits for the work its action begins and answers what it came to: Fetch from a remote that is not
    /// there fails with git's words; Abort Rebase in an agent's own IDE, which has nobody to ask, aborts the rebase the
    /// agent's shell began a moment before; Continue Rebase right after the shell's interactive rebase stopped to edit goes
    /// on with it, Git's state read fresh; Settings…, a dialog nobody is there to answer, is closed and said. Each answered "Ran"
    /// before, the fetch's failure unsaid, the questions left open, and Continue Rebase refused as no rebase was known.</summary>
    private async Task An_agents_ide_action_waits_for_its_work_and_answers_what_it_came_to_asking_nobody()
    {
        string repository = TestFolders.Create("mcp-action-outcome-");
        DeleteAtEnd(repository);
        string notes = Path.Combine(repository, "Notes.txt");
        File.WriteAllText(notes, "one\ntwo\nthree\n");
        await TestGit.InitAsync(repository);
        await TestGit.RunAsync(repository, "remote", "add", "origin", Path.Combine(repository, "gone", "remote.git"));
        await TestGit.RunAsync(repository, "switch", "-q", "-c", "feature");
        File.WriteAllText(notes, "one\nTWO\nthree\n");
        await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in capitals");
        await TestGit.RunAsync(repository, "switch", "-q", "main");
        File.WriteAllText(notes, "one\nII\nthree\n");
        await TestGit.RunAsync(repository, "commit", "-q", "-am", "Two in Roman");
        await TestGit.RunAsync(repository, "switch", "-q", "feature");
        await using McpClient client = await Start(["--mcp", repository], languageServer: false);
        await Call(client, McpTools.VcsStatus);

        CallToolResult fetched = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.Fetch });
        Assert.True(fetched.IsError == true && Text(fetched).Contains("Fetch failed: fatal: ", StringComparison.Ordinal),
            $"Fetch from a remote that is not there answered: {Text(fetched)}");

        string feature = (await TestGit.RunAsync(repository, "rev-parse", "HEAD")).Trim();
        ChildResult rebasing = await ChildProcesses.RunAsync(ChildProcesses.Command("git", ["rebase", "main"], repository), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
        Assert.False(rebasing.Succeeded, $"The rebase was to stop for its conflict: {rebasing.Output}");
        Assert.Equal("Abort rebase succeeded", (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.AbortRebase })).GetString());
        Assert.False(Directory.Exists(Path.Combine(repository, ".git", "rebase-merge")), "Abort Rebase answered while the rebase was still in progress");
        Assert.Equal(("feature", feature), ((await TestGit.RunAsync(repository, "rev-parse", "--abbrev-ref", "HEAD")).Trim(), (await TestGit.RunAsync(repository, "rev-parse", "HEAD")).Trim()));
        JsonElement windows = await Call(client, McpTools.AppWindows, new() { ["app"] = "ide" });
        Assert.DoesNotContain("Abort Rebase", windows.GetRawText(), StringComparison.Ordinal);

        ProcessStartInfo editing = ChildProcesses.Command("git", ["rebase", "-i", "HEAD~1"], repository);
        // The list's one step, the commit's pick, made an edit, as a person's editor would.
        editing.Environment[GitVariables.SequenceEditor] = "sed -i.bak -e 1s/^pick/edit/";
        ChildResult stopping = await ChildProcesses.RunAsync(editing, limit: TimeSpan.FromMinutes(1), cancel: Cancel);
        Assert.True(File.Exists(Path.Combine(repository, ".git", "rebase-merge", "amend")), $"The rebase was to stop to edit: {stopping.Error}{stopping.Output}");
        Assert.Equal("Rebase successful", (await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ContinueRebase })).GetString());
        Assert.False(Directory.Exists(Path.Combine(repository, ".git", "rebase-merge")), "Continue Rebase answered while the rebase was still in progress");

        CallToolResult settings = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.ShowSettings });
        Assert.True(settings.IsError == true && Text(settings).Contains("which nobody is at this IDE to answer: it was closed unanswered", StringComparison.Ordinal),
            $"Settings… in an agent's own IDE answered: {Text(settings)}");
        Assert.True(await Waiting.UntilAsync(async () => (await Call(client, McpTools.AppWindows, new() { ["app"] = "ide" })).GetProperty("windows").GetArrayLength() == 1, TimeSpan.FromSeconds(10), Cancel),
            $"Settings' dialog was left open: {(await Call(client, McpTools.AppWindows, new() { ["app"] = "ide" })).GetRawText()}");
    }

    /// <summary>An agent's picture of Passenger's own window at 2 pixels a unit shows the code and the active tab's name as
    /// at 1, where at any scale but 1 the editor's text and the tab's title and icons were left out: drawn after a clip of
    /// their own, they were scaled twice over, off the picture.</summary>
    private async Task An_agents_picture_of_passengers_window_at_any_scale_shows_the_code_and_the_active_tabs_name()
    {
        string folder = TestFolders.Create("mcp-picture-");
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "Shown.cs"),
            string.Concat(Enumerable.Range(0, 40).Select(i => $"public static class Shown{i} {{ public static int Value => {i} * 1024 + {i}; }}\n")));
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        await Call(client, McpTools.Open, new() { ["path"] = "Shown.cs" });
        foreach (string control in (string[])["CodeEditor", "Border.tab.active"])
        {
            int once = Edges(await PictureAsync(client, control, 1));
            int twice = Edges(await PictureAsync(client, control, 2));
            Assert.True(once > 50 && twice >= once, $"{control} drawn at scale 2 has {twice} edges of glyphs, at scale 1 {once}: what it shows was left out");
        }
    }

    private async Task<byte[]> PictureAsync(McpClient client, string control, double scale)
    {
        CallToolResult picture = await Result(client, McpTools.AppScreenshot, new() { ["app"] = "ide", ["control"] = control, ["scale"] = scale });
        return Assert.IsType<ImageContentBlock>(picture.Content[0]).DecodedData.ToArray();
    }

    /// <summary>The pixels of a picture whose colour differs sharply from the next to their right: the edges glyphs draw,
    /// which flat fills have only at their ends.</summary>
    private static int Edges(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        SKColor[] pixels = bitmap.Pixels;
        int edges = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = y * bitmap.Width, end = x + bitmap.Width - 1; x < end; x++)
            {
                (SKColor a, SKColor b) = (pixels[x], pixels[x + 1]);
                if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 96)
                {
                    edges++;
                }
            }
        }
        return edges;
    }

    /// <summary>Razor for an agent: a component's errors, what a name in its markup is, and a method's usages across its
    /// markup and its code block.</summary>
    private async Task An_agent_reads_a_razor_components_errors_names_and_usages_through_roslyns_razor()
    {
        string blazor = PathNames.Canonical(TestFolders.Create("mcp-razor-"));
        DeleteAtEnd(blazor);
        await TestFolders.PrepareAsync(async () =>
        {
            await BlazorApp.CreateAsync(blazor);
            File.WriteAllText(Path.Combine(blazor, "Components", "Sum.razor"),
                "<p>@Total</p>\n\n@code {\n    private int Total => Twice(21);\n\n    private static int Twice(int value) => value * 2;\n}\n");
            File.WriteAllText(Path.Combine(blazor, "Components", "Broken.razor"), "<p>@Missing</p>\n");
            await Restore.RunAsync(blazor);
        }, blazor);
        await using McpClient client = await Start(["--mcp", blazor]);

        JsonElement error = Assert.Single((await Call(client, McpTools.Diagnostics, new() { ["path"] = "Components/Broken.razor" })).EnumerateArray(),
            d => d.GetProperty("severity").GetString() == "error");
        Assert.Equal(("CS0103", 1), (error.GetProperty("code").GetString(), error.GetProperty("line").GetInt32()));
        string total = (await Call(client, McpTools.SymbolInfo, new() { ["path"] = "Components/Sum.razor", ["line"] = 1, ["column"] = 6 })).GetString()!;
        Assert.True(total.Contains("Total", StringComparison.Ordinal) && total.Contains("int", StringComparison.Ordinal), $"symbol_info on a Razor component's name said: {total}");
        string[] used = [.. Used(await Call(client, McpTools.Usages, new() { ["path"] = "Components/Sum.razor", ["line"] = 6, ["column"] = 25 }))
            .Select(p => $"{p.Path}:{p.Line}").Order(StringComparer.Ordinal)];
        Assert.Equal([Path.Combine("Components", "Sum.razor") + ":4", Path.Combine("Components", "Sum.razor") + ":6"], used);
    }

    /// <summary>A Find in Files answer's matches as path:line.</summary>
    private static string[] Places(JsonElement found) =>
        [.. found.GetProperty("matches").EnumerateArray().Select(m => $"{m.GetProperty("path").GetString()}:{m.GetProperty("line").GetInt32()}").Order(StringComparer.Ordinal)];
}

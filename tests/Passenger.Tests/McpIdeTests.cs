using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The IDE's own windows for an agent: Find in Files in the window's scopes and masks, a file's Local History read
/// and reverted, what Problems and the Build window list beyond a build's answer, the person's settings read and changed as
/// Settings changes them, and a Razor component's errors, names and usages. Attach Profiler on a program already running
/// is <see cref="McpIdeProfilingTests"/>'.</summary>
public sealed class McpIdeTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpIdeTests(RestoredFolders folders)
        : base(folders)
    {
    }

    [Fact]
    public async Task An_agent_finds_text_in_the_windows_scopes_reverts_a_file_through_local_history_and_reads_problems_and_the_build_window()
    {
        const string helper = "static class Helper\n{\n    public static int Twice(int value)\n    {\n        int unused = 1;\n        return value * 2;\n    }\n}\n";
        Write("Bad.cs", "class Bad\n{\n    public int Count = \"none\";\n}\n");
        Write("Notes.txt", "one two\ntwo three\n");
        await using McpClient client = await Start(["--mcp", Folder]);

        JsonElement found = await Call(client, McpTools.FindInFiles, new() { ["text"] = "Twice" });
        Assert.Equal(["Helper.cs:3", "Program.cs:4"], Places(found));
        Assert.Equal(["Helper.cs:3"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = "Twice", ["mask"] = "*.cs, !Program.cs" })));
        Assert.Equal(["Program.cs:4"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = @"Twice\(\w\)", ["regex"] = true })));
        await Call(client, McpTools.Open, new() { ["path"] = "Helper.cs", ["line"] = 1, ["column"] = 1 });
        Assert.Equal(["Helper.cs:3"], Places(await Call(client, McpTools.FindInFiles, new() { ["text"] = "Twice", ["scope"] = "openFiles" })));
        foreach ((Dictionary<string, object?> arguments, string said) in new (Dictionary<string, object?>, string)[]
        {
            (new() { ["text"] = "(", ["regex"] = true }, "Invalid pattern '('"),
            (new() { ["text"] = "Twice", ["scope"] = "directory", ["directory"] = "nowhere" }, "Directory is not found"),
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
        Assert.False(problems.TryGetProperty("solution", out _), "Problems' solution tab was answered with solution-wide analysis off");
        string output = (await Call(client, McpTools.Output, new() { ["of"] = "build" })).GetString()!;
        Assert.True(output.Contains("CS0029", StringComparison.Ordinal), $"The Build window's console, for an agent, lacks the build's error:\n{output}");
        // A run refused for a failed build names its errors, so the agent fixes them without a second call to find them.
        CallToolResult notRun = await Result(client, McpTools.Run);
        Assert.True(notRun.IsError == true && Text(notRun).Contains("Bad.cs:3:", StringComparison.Ordinal) && Text(notRun).Contains("CS0029", StringComparison.Ordinal),
            $"A run refused for a failed build answered: {Text(notRun)}");

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

    /// <summary>An agent on the person's window reads their settings and changes them as Settings does: asked first, a number
    /// brought into its range, kept in settings.json and in force in the window at once; a name or choice Settings lacks
    /// changes nothing, and Enable MCP Server stays the person's.</summary>
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
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            JsonElement read = Await(Task.Run(() => Call(client, McpTools.Settings)), "the agent reading the settings");
            Assert.True(read.GetProperty("kept").GetBoolean(), "The person's window's settings were read as kept nowhere");
            JsonElement font = Setting(read, "editorFontSize");
            Assert.Equal((13.0, 6.0, 48.0), (font.GetProperty("value").GetDouble(), font.GetProperty("min").GetDouble(), font.GetProperty("max").GetDouble()));
            Assert.Equal(["Always", "ForWarningsOrErrors", "ForErrors", "Never"],
                Setting(read, "showBuildResults").GetProperty("choices").EnumerateArray().Select(c => c.GetString()!));

            Task<JsonElement> changing = Task.Run(() => Call(client, McpTools.SettingsSet, new()
            {
                ["changes"] = new Dictionary<string, object> { ["editorFontSize"] = 99, ["showBuildResults"] = "always" },
            }));
            Press(Question(window, "the question for the agent's settings change"), "Allow");
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
            })
            {
                CallToolResult refused = Await(Task.Run(() => Result(client, McpTools.SettingsSet, new() { ["changes"] = changes })), "the refused settings change");
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

    /// <summary>Razor for an agent: a component's errors, what a name in its markup is, and a method's usages across its
    /// markup and its code block.</summary>
    [Fact]
    public async Task An_agent_reads_a_razor_components_errors_names_and_usages_through_roslyns_razor()
    {
        string blazor = PathNames.Canonical(TestFolders.Create("mcp-razor-"));
        DeleteAtEnd(blazor);
        TestFolders.Prepare(() =>
        {
            BlazorApp.Create(blazor);
            File.WriteAllText(Path.Combine(blazor, "Components", "Sum.razor"),
                "<p>@Total</p>\n\n@code {\n    private int Total => Twice(21);\n\n    private static int Twice(int value) => value * 2;\n}\n");
            File.WriteAllText(Path.Combine(blazor, "Components", "Broken.razor"), "<p>@Missing</p>\n");
            Restore.Run(blazor);
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

/// <summary>Attach Profiler for an agent, McpIdeTests' where what it tests is held to a time: a profile's samples, so the class
/// runs alone.</summary>
[Collection(Alone.Name)]
public sealed class McpIdeProfilingTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpIdeProfilingTests(RestoredFolders folders)
        : base(folders)
    {
    }

    /// <summary>Attach Profiler for an agent: the program Run started, profiled from now for a while, left running after. Its
    /// hot spot is named at the editor's line, below a comment whose breaks the compiler counted into the PDB's lines.</summary>
    [Fact]
    public async Task An_agent_profiles_a_program_already_running_and_leaves_it_running()
    {
        Write("Program.cs", $"File.WriteAllText(Path.Combine(@\"{Folder}\", \"spin.pid\"), Environment.ProcessId.ToString());\n"
            + "long turns = 0;\nwhile (true)\n{\n    turns = Spin.Turn(turns);\n}\n\n"
            + $"/* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\nstatic class Spin\n{{\n    public static long Turn(long turns) => turns + 1;\n}}\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        CallToolResult unattached = await Result(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 1 });
        Assert.True(unattached.IsError == true, $"Attach Profiler with nothing running answered: {Text(unattached)}");

        Assert.True((await Call(client, McpTools.Run, new() { ["waitSeconds"] = 0 })).GetProperty("stillRunning").GetBoolean());
        int spinning = await WrittenId("spin.pid", "the program to start");
        JsonElement profiled = await Call(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 2 });
        Assert.True(profiled.GetProperty("samples").GetInt32() > 0, $"Attach Profiler sampled nothing: {profiled.GetRawText()}");
        // The editor's line 11, which the compiler, ending a line at each of the comment's four breaks, numbered 15.
        JsonElement turn = Assert.Single(profiled.GetProperty("hotSpots").EnumerateArray(), h => h.GetProperty("method").GetString()!.Contains("Spin.Turn", StringComparison.Ordinal));
        Assert.True(turn.TryGetProperty("source", out JsonElement source) && source.GetString() == "Program.cs:11", $"Spin.Turn's hot spot is not named at its line: {turn}");
        using Process program = Process.GetProcessById(spinning);
        Assert.False(program.HasExited, "The program an agent attached the profiler to ended with its snapshot");
        await Call(client, McpTools.Stop);
    }
}

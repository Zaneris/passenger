using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent debugging a Godot game of GDScript alone: app_tree, refused before it runs, names such a game among
/// those it reaches, and reaches it once it does; a script it edits (and so saves) reloads in the game, which then says the
/// edit's words, and hot_reload answers that Godot reloads each script as it is saved, with nothing else to apply, not that
/// Hot Reload is off; a click stopping at a breakpoint is said so at once. Godot runs headless, its user folders the test's; without a Godot 4 it skips saying
/// why. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotScriptGameTests : McpRig
{
    /// <summary>The scene's one node's script: each frame counts, and every tenth says its word and the count.</summary>
    private const string Ticker = "extends Node\n\nvar counter := 0\n\n\nfunc _process(_delta: float) -> void:\n\tcounter += 1\n"
        + "\tif counter % 10 == 0:\n\t\tprint(\"tick \", counter)\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-scripts-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public McpGodotScriptGameTests()
    {
        Assert.SkipWhen(godot is not { WhyNotScripts: null }, godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 game: {godot.WhyNotScripts}");
        File.WriteAllText(Path.Combine(folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Ticker\"\n"
            + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n");
        File.WriteAllText(Path.Combine(folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://ticker.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n");
        File.WriteAllText(Path.Combine(folder, "ticker.gd"), Ticker);
        DeleteAtEnd(folder);
        DeleteAtEnd(folder + "-home");
    }

    [Fact]
    public async Task A_script_saved_in_a_debugged_game_of_GDScript_alone_reloads_and_hot_reload_says_saved_scripts_reload()
    {
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        CallToolResult none = await Result(client, McpTools.AppTree);
        Assert.True(none.IsError == true && Text(none).Contains("a Godot 4 game in C# or GDScript", StringComparison.Ordinal),
            $"app_tree with no game running did not name a game of GDScript among those it reaches: {Text(none)}");
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 60 });
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("tick 20", StringComparison.Ordinal), "the game to say its word");
        Assert.Contains("#Main", Text(await Call(client, McpTools.AppTree, new() { ["depth"] = 1 })), StringComparison.Ordinal);

        await Call(client, McpTools.Edit, new() { ["path"] = "ticker.gd", ["oldText"] = "print(\"tick \", counter)", ["newText"] = "print(\"edited \", counter)" });
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("edited ", StringComparison.Ordinal), "the game to say the saved edit's words");

        JsonElement reload = await Call(client, McpTools.HotReload);
        string said = reload.GetProperty("said").GetString()!;
        Assert.True(reload.GetProperty("result").GetString() == "nothingToApply" && said.Contains("as it is saved", StringComparison.Ordinal)
            && !said.Contains("Hot Reload when debugging off", StringComparison.Ordinal),
            $"hot_reload on a game whose saved script had reloaded did not say that saved scripts reload: {reload}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>A scene of a button whose script counts each press.</summary>
    private const string Presses = "extends Node2D\n\nvar count := 0\n\nfunc _ready() -> void:\n\t$Button.pressed.connect(_on_pressed)\n\n"
        + "func _on_pressed() -> void:\n\tcount += 1\n\tprint(\"pressed \", count)\n";

    /// <summary>A click whose handler stops at a breakpoint is said at once as the game stopped under the debugger, never
    /// waited out and refused as a game paused, and debug_state says the stop.</summary>
    [Fact]
    public async Task A_click_that_stops_at_a_breakpoint_says_the_game_stopped()
    {
        File.WriteAllText(Path.Combine(folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://press.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Button\" type=\"Button\" parent=\".\"]\n"
            + "offset_right = 120.0\noffset_bottom = 40.0\ntext = \"Press\"\n");
        File.WriteAllText(Path.Combine(folder, "press.gd"), Presses);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "press.gd", ["line"] = 9 });
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 10 });
        await Until(async () => await Result(client, McpTools.AppTree) is { IsError: not true } tree && Text(tree).Contains("#Button", StringComparison.Ordinal), "the game's button");

        CallToolResult clicked = await Result(client, McpTools.AppClick, new() { ["control"] = "#Button" });
        Assert.True(clicked.IsError == true && Text(clicked).Contains("stopped under the debugger before it answered", StringComparison.Ordinal),
            $"A click whose handler stopped at a breakpoint answered: {Text(clicked)}");
        Assert.Equal("press.gd:9:1", (await Call(client, McpTools.DebugState)).GetProperty("location").GetString());
        await Call(client, McpTools.Stop);
    }

    /// <summary>tests_run asked as the folder opens waits for the tests its code declares: a GUT test in a script extending the
    /// project's own test class, known only once that class has been read, runs and passes (GUT the stand-in of
    /// <see cref="GodotGutTests"/>).</summary>
    [Fact]
    public async Task Tests_run_as_the_folder_opens_runs_a_gut_test_extending_the_projects_own_test_class()
    {
        string gut = PathNames.Canonical(TestFolders.Create("godot-mcp-gut-"));
        DeleteAtEnd(gut);
        TestFolders.Write(gut, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\nconfig/features=PackedStringArray(\"4.7\")\n");
        TestFolders.Write(gut, "addons/gut/gut_cmdln.gd", GodotGutTests.CommandLine);
        TestFolders.Write(gut, "addons/gut/test.gd", GodotGutTests.GutTest);
        TestFolders.Write(gut, "test/tools/spin_tester.gd", "class_name SpinTester\nextends GutTest\n\n\nfunc spun():\n\treturn 2\n");
        TestFolders.Write(gut, "test/test_named.gd", "extends SpinTester\n\n\nfunc test_by_name():\n\tassert_eq(spun(), 2, \"spun twice\")\n");
        await using McpClient client = await Start(["--mcp", gut], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        JsonElement ran = await Call(client, McpTools.TestsRun, new() { ["filter"] = "test_by_name" });
        Assert.True(ran.GetProperty("summary").GetString()?.StartsWith("Tests passed: 1, failed: 0 of 1", StringComparison.Ordinal) == true,
            $"tests_run as the folder opened did not run the GUT test extending the project's own test class: {ran}");
    }
}

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent debugging a Godot game of GDScript alone: app_tree, refused before it runs, names such a game among
/// those it reaches, and reaches it once it does; a script it edits (and so saves) reloads in the game, which then says the
/// edit's words, and hot_reload answers that Godot reloads each script as it is saved, with nothing else to apply, not that
/// Hot Reload is off; a click stopping at a breakpoint is said so at once; a condition holds as the game's own if reads it.
/// Godot runs headless, its user folders the test's; without a Godot 4 it skips saying why. Debugged, so run alone.</summary>
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

    /// <summary>Typing into a node that takes no focus (a Label) is refused saying so, never typed into whatever has the
    /// focus; a click whose handler stops at a breakpoint is said at once as the game stopped under the debugger, never
    /// waited out and refused as a game paused, and debug_state says the stop; Set Next Statement, which Godot's GDScript
    /// debugger cannot do, is refused saying so, never in the protocol's words.</summary>
    [Fact]
    public async Task Typing_into_a_label_is_refused_a_click_that_stops_at_a_breakpoint_says_so_and_set_next_statement_says_its_debugger_has_none()
    {
        File.WriteAllText(Path.Combine(folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://press.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Button\" type=\"Button\" parent=\".\"]\n"
            + "offset_right = 120.0\noffset_bottom = 40.0\ntext = \"Press\"\n\n[node name=\"Label\" type=\"Label\" parent=\".\"]\noffset_top = 50.0\ntext = \"Count\"\n");
        File.WriteAllText(Path.Combine(folder, "press.gd"), Presses);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "press.gd", ["line"] = 9 });
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 10 });
        await Until(async () => await Result(client, McpTools.AppTree) is { IsError: not true } tree && Text(tree).Contains("#Button", StringComparison.Ordinal), "the game's button");

        CallToolResult unfocused = await Result(client, McpTools.AppType, new() { ["control"] = "#Label", ["text"] = "typed" });
        Assert.True(unfocused.IsError == true && Text(unfocused).Contains("Label Label takes no focus, so nothing typed would reach it", StringComparison.Ordinal),
            $"Typing into a label, which takes no focus, answered: {Text(unfocused)}");

        CallToolResult clicked = await Result(client, McpTools.AppClick, new() { ["control"] = "#Button" });
        Assert.True(clicked.IsError == true && Text(clicked).Contains("stopped under the debugger before it answered", StringComparison.Ordinal),
            $"A click whose handler stopped at a breakpoint answered: {Text(clicked)}");
        Assert.Equal("press.gd:9:1", (await Call(client, McpTools.DebugState)).GetProperty("location").GetString());
        CallToolResult next = await Result(client, McpTools.DebugSetNextStatement, new() { ["path"] = "press.gd", ["line"] = 10 });
        Assert.True(next.IsError == true && Text(next).Contains(Passenger.App.Refusals.NoNextStatement, StringComparison.Ordinal),
            $"Set Next Statement in a game's GDScript answered: {Text(next)}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>A text box in the main window, and at the same place in a second window, in which a button says its press.</summary>
    private const string TwoWindows = "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://second.gd\" id=\"1\"]\n\n"
        + "[node name=\"Main\" type=\"Control\"]\nscript = ExtResource(\"1\")\nlayout_mode = 3\nanchors_preset = 0\noffset_right = 1000.0\noffset_bottom = 600.0\n\n"
        + "[node name=\"Field\" type=\"LineEdit\" parent=\".\"]\nlayout_mode = 0\noffset_left = 10.0\noffset_top = 60.0\noffset_right = 210.0\noffset_bottom = 90.0\n\n"
        + "[node name=\"Second\" type=\"Window\" parent=\".\"]\ntitle = \"Second\"\nposition = Vector2i(200, 100)\nsize = Vector2i(300, 200)\n\n"
        + "[node name=\"Inner\" type=\"LineEdit\" parent=\"Second\"]\noffset_left = 10.0\noffset_top = 60.0\noffset_right = 210.0\noffset_bottom = 90.0\n\n"
        + "[node name=\"Press\" type=\"Button\" parent=\"Second\"]\noffset_left = 10.0\noffset_top = 100.0\noffset_right = 110.0\noffset_bottom = 130.0\ntext = \"Press\"\n";

    /// <summary>Input aimed at a second window goes to it: a click on its text box, which sits where the main window's does, and
    /// typing land there, a click at a point of the window named presses what is there, and the main window takes input again
    /// once aimed at. Godot runs headless, whose windows are embedded in the main one.</summary>
    [Fact]
    public async Task Input_aimed_at_a_second_window_goes_to_it_by_control_and_by_a_point_of_the_window_named()
    {
        File.WriteAllText(Path.Combine(folder, "main.tscn"), TwoWindows);
        File.WriteAllText(Path.Combine(folder, "second.gd"), "extends Control\n\nfunc _ready() -> void:\n\t$Second/Press.pressed.connect(func() -> void: print(\"pressed in second\"))\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 10 });
        await Until(async () => await Result(client, McpTools.AppTree) is { IsError: not true } tree && Text(tree).Contains("#Inner", StringComparison.Ordinal), "the game's second window");

        string clicked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Inner" }));
        Assert.True(clicked.Contains("LineEdit#Inner @", StringComparison.Ordinal) && clicked.Contains("has the focus", StringComparison.Ordinal) && !clicked.Contains("#Field", StringComparison.Ordinal),
            $"A click on the second window's text box answered: {clicked}");
        Assert.Contains("it shows \"Ada\"", Text(await Call(client, McpTools.AppType, new() { ["control"] = "#Inner", ["text"] = "Ada" })), StringComparison.Ordinal);

        string pressed = Text(await Call(client, McpTools.AppClick, new() { ["window"] = "#Second", ["x"] = 50, ["y"] = 115 }));
        Assert.Contains("Button#Press @", pressed, StringComparison.Ordinal);
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("pressed in second", StringComparison.Ordinal), $"the game to hear the button pressed ({pressed})");

        string back = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Field" }));
        Assert.Contains("LineEdit#Field @", back, StringComparison.Ordinal);
        Assert.Contains("it shows \"Grace\"", Text(await Call(client, McpTools.AppType, new() { ["control"] = "#Field", ["text"] = "Grace" })), StringComparison.Ordinal);
        string shown = Text(await Call(client, McpTools.AppTree, new() { ["window"] = "#Second" }));
        Assert.True(shown.Contains("LineEdit#Inner \"Ada\"", StringComparison.Ordinal) && !shown.Contains("#Field", StringComparison.Ordinal), $"The second window's tree, named, reads:\n{shown}");

        CallToolResult none = await Result(client, McpTools.AppClick, new() { ["window"] = "#Nowhere", ["x"] = 1, ["y"] = 1 });
        Assert.True(none.IsError == true && Text(none).Contains("No window of", StringComparison.Ordinal), $"A window no one named answered: {Text(none)}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>Keys aimed at the main window's text box reach it though a second window holds the focus, which they take from it
    /// with nothing of the game hearing a press, where they went to that window as the answer named the text box; keys aimed at
    /// nothing stay with the window holding the focus.</summary>
    [Fact]
    public async Task Keys_aimed_at_the_main_window_reach_it_past_a_second_window_holding_the_focus()
    {
        File.WriteAllText(Path.Combine(folder, "main.tscn"), TwoWindows);
        File.WriteAllText(Path.Combine(folder, "second.gd"), "extends Control\n\nfunc _input(event: InputEvent) -> void:\n\tif event is InputEventMouseButton and event.position.x < 0:\n"
            + "\t\tprint(\"the game heard a press\")\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 10 });
        await Until(async () => await Result(client, McpTools.AppTree) is { IsError: not true } tree && Text(tree).Contains("#Inner", StringComparison.Ordinal), "the game's second window");

        await Call(client, McpTools.AppClick, new() { ["control"] = "#Inner" });
        Assert.Contains("it shows \"Ada\"", Text(await Call(client, McpTools.AppType, new() { ["text"] = "Ada" })), StringComparison.Ordinal);
        string typed = Text(await Call(client, McpTools.AppType, new() { ["control"] = "#Field", ["text"] = "Grace" }));
        Assert.True(typed.Contains("into LineEdit#Field @", StringComparison.Ordinal) && typed.Contains("it shows \"Grace\"", StringComparison.Ordinal), $"Typing aimed at the main window answered: {typed}");
        string shown = Text(await Call(client, McpTools.AppTree, new() { ["window"] = "#Second" }));
        Assert.True(shown.Contains("LineEdit#Inner \"Ada\"", StringComparison.Ordinal), $"The keys reached the second window as well, whose tree reads:\n{shown}");
        Assert.DoesNotContain("the game heard a press", Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>An exclusive window keeps every key until it closes, for a person as for an agent: keys aimed at the main window go
    /// to it, and the answer says so, where it named the control they missed.</summary>
    [Fact]
    public async Task Keys_aimed_past_an_exclusive_window_are_said_to_have_gone_to_it()
    {
        File.WriteAllText(Path.Combine(folder, "main.tscn"), TwoWindows.Replace("type=\"Window\" parent=\".\"", "type=\"AcceptDialog\" parent=\".\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "second.gd"), "extends Control\n\nfunc _ready() -> void:\n\t$Second.popup_centered.call_deferred()\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 10 });
        await Until(async () => await Result(client, McpTools.AppTree) is { IsError: not true } tree && Text(tree).Contains("#Inner", StringComparison.Ordinal), "the game's dialog");

        string typed = Text(await Call(client, McpTools.AppType, new() { ["control"] = "#Field", ["text"] = "Grace" }));
        Assert.True(typed.Contains(", not LineEdit#Field @", StringComparison.Ordinal) && typed.Contains("is exclusive, so it takes every key until it closes", StringComparison.Ordinal),
            $"Typing aimed past the dialog answered: {typed}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>A control whose script names a <c>text</c>, <c>disabled</c> and <c>editable</c> of its own, each read counted:
    /// its getter, its <c>_get</c>, and a plain <c>note</c>.</summary>
    private const string Counting = "extends Control\n\nvar getter_reads := 0\nvar get_reads := 0\nvar text := \"stored\":\n\tget:\n\t\tgetter_reads += 1\n\t\treturn \"by getter\"\n"
        + "var disabled := true\nvar editable := \"not a flag\"\nvar note := \"plain note\"\n\n\nfunc _get(_property: StringName) -> Variant:\n\tget_reads += 1\n\treturn null\n";

    /// <summary>app_tree reads what a node's class holds past its script, and a GDScript's own text as stored: looking at
    /// the tree, named properties among it, runs no getter and no <c>_get</c>, which app_details, reading through Godot's
    /// own inspection, runs and says it did.</summary>
    [Fact]
    public async Task App_tree_runs_no_getter_of_a_script_and_keeps_its_text_and_app_details_says_what_it_ran()
    {
        File.WriteAllText(Path.Combine(folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://counting.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Control\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Press\" type=\"Button\" parent=\".\"]\noffset_right = 120.0\noffset_bottom = 40.0\n"
            + "text = \"Press\"\ndisabled = true\n");
        File.WriteAllText(Path.Combine(folder, "counting.gd"), Counting);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.Run, new() { ["configuration"] = "Ticker", ["arguments"] = "--headless", ["waitSeconds"] = 10 });
        await Until(async () => await Result(client, McpTools.AppTree) is { IsError: not true } tree && Text(tree).Contains("#Press", StringComparison.Ordinal), "the game's button");

        string looked = Text(await Call(client, McpTools.AppTree, new() { ["properties"] = new[] { "note", "text", "custom_minimum_size" } }));
        Assert.Contains("#Main \"stored\"", looked, StringComparison.Ordinal);
        Assert.Contains("[disabled]", looked.Split('\n').First(l => l.Contains("#Main", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("note=plain note", looked, StringComparison.Ordinal);
        Assert.Contains("custom_minimum_size=(0, 0)", looked, StringComparison.Ordinal);
        Assert.Contains("#Press \"Press\"", looked, StringComparison.Ordinal);
        string members = Text(await Call(client, McpTools.AppDetails, new() { ["control"] = "#Main", ["members"] = true }));
        Assert.True(members.Contains("\"name\":\"getter_reads\",\"line\":3,\"value\":\"0\"", StringComparison.Ordinal)
            && members.Contains("\"name\":\"get_reads\",\"line\":4,\"value\":\"0\"", StringComparison.Ordinal),
            $"Looking at the tree ran the game's code, so that looking changed it: {members}");

        JsonElement details = await Call(client, McpTools.AppDetails, new() { ["control"] = "#Main" });
        Assert.Contains("text and _get ran as this was read", details.GetProperty("ran").GetString(), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>Members false to a game's <c>if</c> though no plain reading of the value says so, each a line's condition, then
    /// a member true.</summary>
    private const string Truths = "extends Node\n\nvar empty_list := []\nvar empty_map := {}\nvar zero_vector := Vector2.ZERO\nvar empty_name := &\"\"\n"
        + "var plain_basis := Basis()\nvar black := Color()\nvar no_call := Callable()\nvar some_list := [0]\n\n\nfunc _process(_delta: float) -> void:\n"
        + "\tvar first := 1\n\tvar second := 2\n\tvar third := 3\n\tvar fourth := 4\n\tvar fifth := 5\n\tvar sixth := 6\n\tvar seventh := 7\n\tvar last := 8\n";

    /// <summary>A conditional breakpoint holds as the game's own <c>if</c> reads its value: each line whose condition is a
    /// value Godot takes for false lets the game go on, and the game stops at the one Godot takes for true.</summary>
    [Fact]
    public async Task A_conditional_breakpoint_holds_as_the_games_own_if_reads_its_value()
    {
        File.WriteAllText(Path.Combine(folder, "ticker.gd"), Truths);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        string[] lines = Truths.Split('\n');
        string[] conditions = ["empty_list", "empty_map", "zero_vector", "empty_name", "plain_basis", "black", "no_call", "some_list"];
        string[] statements = ["var first", "var second", "var third", "var fourth", "var fifth", "var sixth", "var seventh", "var last"];
        foreach ((string condition, string statement) in conditions.Zip(statements))
        {
            int line = Array.FindIndex(lines, l => l.Contains(statement, StringComparison.Ordinal)) + 1;
            await Call(client, McpTools.BreakpointSet, new() { ["path"] = "ticker.gd", ["line"] = line, ["condition"] = condition });
        }
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["arguments"] = "--headless", ["waitSeconds"] = 60 });
        int stopped = Array.FindIndex(lines, l => l.Contains("var last", StringComparison.Ordinal)) + 1;
        JsonElement state = await Call(client, McpTools.DebugState);
        Assert.True(state.GetProperty("location").GetString() == $"ticker.gd:{stopped}:1",
            $"The game stopped at {state.GetProperty("location")}, where every condition before line {stopped} is false to the game's own if: {state}");
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
        GodotGutTests.WriteGut(gut);
        TestFolders.Write(gut, "test/tools/spin_tester.gd", "class_name SpinTester\nextends GutTest\n\n\nfunc spun():\n\treturn 2\n");
        TestFolders.Write(gut, "test/test_named.gd", "extends SpinTester\n\n\nfunc test_by_name():\n\tassert_eq(spun(), 2, \"spun twice\")\n");
        await using McpClient client = await Start(["--mcp", gut], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        JsonElement ran = await Call(client, McpTools.TestsRun, new() { ["filter"] = "test_by_name" });
        Assert.True(ran.GetProperty("summary").GetString()?.StartsWith("Tests passed: 1, failed: 0 of 1", StringComparison.Ordinal) == true,
            $"tests_run as the folder opened did not run the GUT test extending the project's own test class: {ran}");
    }
}

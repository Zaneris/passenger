using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Godot game McpGodotTests' agents run and drive: a Godot 4 C# project of one scene (its Main node's script
/// says its greeting every tenth frame and when its toggle button changes, beside a text box, and a GDScript node counts its
/// frames), on the Godot found, restored from that Godot's own packages, its launch profile running it headless with user
/// folders of the run's.</summary>
public sealed class GodotGameFolder : IAsyncLifetime
{
    public GodotGameFolder()
    {
        Folder = PathNames.Canonical(TestFolders.Create("godot-agent-"));
        Godot = GodotExecutables.Find(null);
    }

    public const string Script = """
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            private bool toggled;

            private bool late;

            [Export]
            public string Greeting { get; set; } = "frame";

            public override void _Ready()
            {
                string greeted = Greeting;
                GD.Print($"ready to say {greeted}");
                foreach (string pressing in (string[])["Plain", "Ui/Pressing", "Covered"])
                {
                    GetNode<Button>(pressing).Pressed += () => GD.Print($"pressed {pressing}");
                }
            }

            public override void _Process(double delta)
            {
                frames++;
                if (frames % 10 == 0)
                {
                    GD.Print($"{Greeting} {frames}");
                }
                if (!late && Time.GetTicksMsec() > 5000)
                {
                    late = true;
                    Later(Time.GetTicksMsec());
                }
                bool pressed = GetNode<Button>("Ui/Toggle").ButtonPressed;
                if (pressed != toggled)
                {
                    toggled = pressed;
                    GD.Print($"toggled {(pressed ? "on" : "off")}");
                }
            }

            private static void Later(ulong at)
            {
                GD.Print($"later, at {at} ms");
            }
        }

        """;

    /// <summary>The GDScript node's script, which counts its frames.</summary>
    public const string Ticker = "extends Node\n\nvar ticks := 0\n\n\nfunc _process(_delta: float) -> void:\n\tticks += 1\n";

    private const string Scene = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]
        [ext_resource type="Script" path="res://ticker.gd" id="2"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        [node name="Ui" type="Control" parent="."]
        layout_mode = 3
        anchors_preset = 0
        offset_right = 400.0
        offset_bottom = 300.0

        [node name="Toggle" type="Button" parent="Ui"]
        layout_mode = 0
        offset_left = 10.0
        offset_top = 10.0
        offset_right = 110.0
        offset_bottom = 40.0
        toggle_mode = true
        text = "Toggle"

        [node name="Field" type="LineEdit" parent="Ui"]
        layout_mode = 0
        offset_left = 10.0
        offset_top = 60.0
        offset_right = 210.0
        offset_bottom = 90.0

        [node name="Pressing" type="Button" parent="Ui"]
        layout_mode = 0
        offset_left = 120.0
        offset_top = 60.0
        offset_right = 220.0
        offset_bottom = 90.0
        action_mode = 0
        text = "Pressing"

        [node name="Ticker" type="Node" parent="."]
        script = ExtResource("2")

        [node name="Plain" type="Button" parent="."]
        offset_left = 120.0
        offset_top = 10.0
        offset_right = 220.0
        offset_bottom = 40.0
        text = "Plain"

        [node name="Covered" type="Button" parent="."]
        offset_left = 240.0
        offset_top = 10.0
        offset_right = 340.0
        offset_bottom = 40.0
        text = "Covered"

        [node name="Cover" type="Panel" parent="."]
        offset_left = 230.0
        offset_top = 0.0
        offset_right = 350.0
        offset_bottom = 50.0

        """;

    public string Folder { get; }

    /// <summary>The Godot found, or null.</summary>
    public GodotFound? Godot { get; }

    /// <summary>Why each test skips, where it does.</summary>
    public string? Skipped => Godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build to run these tests"
        : Godot.WhyNot is { } why ? $"The Godot found cannot run a C# game: {why}" : null;

    public ValueTask InitializeAsync() => Godot is not { WhyNot: null, Version: { } version, Packages: { } packages }
        ? ValueTask.CompletedTask
        : new(TestFolders.PrepareAsync(async () =>
        {
            GodotTests.Game.Make(Folder, version, packages, Script, Scene);
            File.WriteAllText(Path.Combine(Folder, "ticker.gd"), Ticker);
            Dictionary<string, string> homes = GodotTests.Game.Home(Folder + "-home");
            Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
            File.WriteAllText(Path.Combine(Folder, "Properties", "launchSettings.json"), JsonSerializer.Serialize(new
            {
                profiles = new Dictionary<string, object>
                {
                    ["Headless"] = new { commandName = "Project", commandLineArgs = "--headless", environmentVariables = homes },
                },
            }));
            await Restore.RunAsync(Folder);
        }, Folder));

    public ValueTask DisposeAsync()
    {
        TestFolders.Delete(Folder);
        TestFolders.Delete(Folder + "-home");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Agents running a Godot game in C# as they run an Avalonia app: run starts it, Passenger its remote debugger;
/// the app_ tools read its scene tree and a node's values and its scripts' members, set one (which the game then prints),
/// click its button and type into its text box through Passenger's helper in the game, and say plainly that a headless game
/// has no picture; debug_start stops it at a breakpoint in its script's _Process; and each of two copies run side by side
/// answers to its own name. Without a Godot 4 .NET build, each skips saying why.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotTests : McpRig, IClassFixture<GodotGameFolder>
{
    private readonly GodotGameFolder game;

    public McpGodotTests(GodotGameFolder game)
    {
        this.game = game;
        Assert.SkipWhen(game.Skipped is not null, game.Skipped ?? string.Empty);
    }

    private const string Headless = "Spinner: Headless";

    [Fact]
    public async Task An_agent_runs_a_godot_game_reads_its_nodes_sets_a_value_it_prints_clicks_and_types_into_it_and_hears_why_a_headless_game_has_no_picture()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");
        Assert.Contains("Passenger its remote debugger", ran.GetProperty("status").GetString(), StringComparison.Ordinal);

        string tree = await Game(client, McpTools.AppTree, []);
        Assert.True(tree.Contains("Main#Main @", StringComparison.Ordinal) && tree.Contains("Button#Toggle \"Toggle\" @", StringComparison.Ordinal)
            && tree.Contains("LineEdit#Field \"\" @", StringComparison.Ordinal) && !tree.Contains("PassengerAgent", StringComparison.Ordinal),
            $"The game's tree as an agent reads it lacks its nodes, where they are and what they show, or holds Passenger's helper:\n{tree}");
        JsonElement window = Assert.Single((await Call(client, McpTools.AppWindows)).GetProperty("windows").EnumerateArray());
        Assert.Equal("Window", window.GetProperty("type").GetString());

        JsonElement main = await Call(client, McpTools.AppDetails, new() { ["control"] = "#Main", ["properties"] = "Greeting" });
        JsonElement greeting = Assert.Single(main.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "Greeting");
        Assert.Equal("frame", greeting.GetProperty("value").GetString());
        Assert.EndsWith("Main.cs", main.GetProperty("xaml").GetString(), StringComparison.Ordinal);
        Assert.Contains("it holds hello now", await Game(client, McpTools.AppType, new() { ["control"] = "#Main", ["property"] = "Greeting", ["text"] = "hello" }),
            StringComparison.Ordinal);
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("hello ", StringComparison.Ordinal), "the game to print the greeting set");

        string clicked = await Game(client, McpTools.AppClick, new() { ["control"] = "#Toggle" });
        Assert.StartsWith("Clicked Button#Toggle @", clicked, StringComparison.Ordinal);
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("toggled on", StringComparison.Ordinal), $"the game to hear its button clicked ({clicked})");
        string typed = await Game(client, McpTools.AppType, new() { ["control"] = "#Field", ["text"] = "Ada" });
        Assert.Contains("it shows \"Ada\"", typed, StringComparison.Ordinal);
        Assert.Contains("LineEdit#Field @", typed, StringComparison.Ordinal);

        CallToolResult picture = await Result(client, McpTools.AppScreenshot);
        Assert.True(picture.IsError == true && Text(picture).Contains("runs headless", StringComparison.Ordinal)
            && Text(picture).Contains("draws nothing", StringComparison.Ordinal), $"A headless game's picture was answered with: {Text(picture)}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>An agent's click presses a plain button under a bare Node and one under a Control that acts as it is pressed,
    /// both beyond the 64 by 64 the headless display gives a game, which Passenger's helper gives its project's size; one a
    /// panel covers is refused, naming the panel.</summary>
    [Fact]
    public async Task An_agents_click_presses_plain_buttons_under_a_node_or_a_control_and_names_what_covers_one()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");
        foreach ((string control, string said) in (ValueTuple<string, string>[])[("#Plain", "pressed Plain"), ("#Pressing", "pressed Ui/Pressing")])
        {
            string clicked = await Game(client, McpTools.AppClick, new() { ["control"] = control });
            await Until(async () => Text(await Call(client, McpTools.Output)).Contains(said, StringComparison.Ordinal), $"the game to hear {control} pressed ({clicked})");
        }
        CallToolResult covered = await Result(client, McpTools.AppClick, new() { ["control"] = "#Covered" });
        Assert.True(covered.IsError == true && Text(covered).Contains("Button Covered is covered by Panel Cover", StringComparison.Ordinal),
            $"A click on a button a panel covers was answered with: {Text(covered)}");
        Assert.DoesNotContain("pressed Covered", Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>An agent pauses the game's loop and steps it a frame with ide_action, each answer saying the frame it is at,
    /// lets it run on, and with Select Element on has app_click pick the button at a point, its answer naming what it picked
    /// and the button taking no click of its own meanwhile.</summary>
    [Fact]
    public async Task An_agent_pauses_the_games_loop_steps_it_a_frame_and_picks_a_node_at_a_point_with_the_ide_and_app_tools()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");

        string paused = await Game(client, McpTools.IdeAction, new() { ["action"] = Passenger.App.AppActions.PauseGameLoop });
        System.Text.RegularExpressions.Match at = System.Text.RegularExpressions.Regex.Match(paused, "loop is paused at frame ([0-9,]+)");
        Assert.True(at.Success, $"Pause Game Loop did not say the frame the game is held at: {paused}");
        long frame = long.Parse(at.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        string stepped = await Game(client, McpTools.IdeAction, new() { ["action"] = Passenger.App.AppActions.NextFrame });
        Assert.Contains($"ran one frame, and is paused at frame {(frame + 1).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}", stepped, StringComparison.Ordinal);
        string resumed = await Game(client, McpTools.IdeAction, new() { ["action"] = Passenger.App.AppActions.PauseGameLoop });
        Assert.Contains("loop runs on", resumed, StringComparison.Ordinal);

        string picking = await Game(client, McpTools.IdeAction, new() { ["action"] = Passenger.App.AppActions.SelectElement });
        Assert.Contains(McpTools.AppClick, picking, StringComparison.Ordinal);
        string picked = await Game(client, McpTools.AppClick, new() { ["x"] = 60, ["y"] = 25 });
        Assert.Contains("Select Element picked Button#Toggle", picked, StringComparison.Ordinal);
        Assert.DoesNotContain("toggled on", Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>An agent runs the game and Run Another Copy beside it, and at once reads the copy's tree, the call waiting for the
    /// copy to open its scene; then reaches each copy's nodes by the name its run gives it, the first's too, though the Live
    /// Visual Tree follows the copy started last.</summary>
    [Fact]
    public async Task An_agent_reaches_each_of_two_copies_of_a_game_by_its_name_the_copys_tree_asked_as_it_starts()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");
        string another = await Game(client, McpTools.IdeAction, new() { ["action"] = Passenger.App.AppActions.RunAnotherCopy });
        string copied = $"{Headless} #2";
        string first = await Game(client, McpTools.AppTree, new() { ["app"] = copied });
        Assert.True(first.Contains("Main#Main @", StringComparison.Ordinal), $"The copy's tree asked as it started lacks its scene ({another}):\n{first}");
        List<int> processes = [];
        foreach (string copy in new[] { Headless, copied })
        {
            JsonElement windows = await Call(client, McpTools.AppWindows, new() { ["app"] = copy });
            Assert.True(windows.GetProperty("app").GetString() == copy, $"Asked for {copy}, the agent reached {windows} ({another})");
            processes.Add(windows.GetProperty("processId").GetInt32());
            Assert.Contains("Main#Main @", await Game(client, McpTools.AppTree, new() { ["app"] = copy }), StringComparison.Ordinal);
        }
        Assert.True(processes.Distinct().Count() == 2, $"Both names reached one game, process {processes[0]}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>A game an agent debugs starts with its audio muted, whatever the folder keeps: Mute Game Audio, run by the agent,
    /// lets it play.</summary>
    [Fact]
    public async Task A_game_an_agent_debugs_starts_muted()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: Homes);
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        string unmuted = await Game(client, McpTools.IdeAction, new() { ["action"] = Passenger.App.AppActions.MuteGameAudio });
        Assert.Contains("audio plays", unmuted, StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>An agent reads the members a running game's scripts declare with app_details' members: Main's private frame
    /// counter, read twice, climbs, the answer naming the node and its script, and its auto-property and other field beside it
    /// at their lines; the class of a GDScript (ticker) finds the node running it, whose count of its frames climbs too.</summary>
    [Fact]
    public async Task An_agent_reads_a_running_games_script_members_and_sees_its_frame_counter_climb()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: Homes);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");

        JsonElement main = await Members(client, "#Main");
        Assert.True(main.GetProperty("node").GetString()!.StartsWith("Main#Main @", StringComparison.Ordinal) && main.GetProperty("script").GetString() == "Main.cs",
            $"The members read do not name the node and the script they are of: {main}");
        string[] lines = GodotGameFolder.Script.Split('\n');
        foreach (string member in (string[])["frames", "toggled", "Greeting"])
        {
            int line = Array.FindIndex(lines, l => l.Contains($" {member};", StringComparison.Ordinal) || l.Contains($" {member} {{", StringComparison.Ordinal)) + 1;
            Assert.True(Member(main, member) is { } read && read.GetProperty("line").GetInt32() == line,
                $"Main's {member} is not read at line {line}, where Main.cs declares it: {main}");
        }
        Assert.Equal("\"frame\"", Member(main, "Greeting")!.Value.GetProperty("value").GetString());
        long first = Counted(main, "frames");
        long later = first;
        await Until(async () => (later = Counted(await Members(client, "#Main"), "frames")) > first, $"Main's frames to climb past {first}");

        JsonElement ticker = await Members(client, "ticker");
        Assert.True(ticker.GetProperty("node").GetString()!.StartsWith("ticker#Ticker @", StringComparison.Ordinal) && ticker.GetProperty("script").GetString() == "ticker.gd",
            $"The script's class did not find the node running ticker.gd: {ticker}");
        long ticks = Counted(ticker, "ticks");
        await Until(async () => Counted(await Members(client, "ticker"), "ticks") > ticks, $"the ticker's ticks to climb past {ticks}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>The one node's members app_details' members reads for a control.</summary>
    private async Task<JsonElement> Members(McpClient client, string control)
    {
        JsonElement read = await Call(client, McpTools.AppDetails, new() { ["control"] = control, ["members"] = true });
        return Assert.Single(read.GetProperty("nodes").EnumerateArray());
    }

    private static JsonElement? Member(JsonElement node, string name) =>
        node.GetProperty("members").EnumerateArray().Where(m => m.GetProperty("name").GetString() == name).Cast<JsonElement?>().FirstOrDefault();

    /// <summary>A whole number a member holds, as its hint says it.</summary>
    private static long Counted(JsonElement node, string name) =>
        Member(node, name) is { } read && long.TryParse(read.GetProperty("value").GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long count)
            ? count
            : throw new Xunit.Sdk.XunitException($"{node.GetProperty("node").GetString()} holds no whole number in {name}: {node}");

    /// <summary>An app_ tool's answer for the game; one refused fails with the game's last words, Godot's errors among them.</summary>
    private async Task<string> Game(McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await Result(client, tool, arguments, heard: new Heard());
        if (result.IsError == true)
        {
            Assert.Fail($"{tool} was refused: {Text(result)}\nThe game's last words:\n{Text(await Call(client, McpTools.Output, new() { ["lines"] = 40 }))}");
        }
        return Text(result);
    }

    [Fact]
    public async Task An_agent_debugs_a_godot_game_to_a_breakpoint_in_its_scripts_process()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: Homes);
        int line = Array.FindIndex(GodotGameFolder.Script.Split('\n'), l => l.Contains("frames++;", StringComparison.Ordinal)) + 1;
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Main.cs", ["line"] = line });
        JsonElement stop = await Call(client, McpTools.DebugStart, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Assert.Equal("paused", stop.GetProperty("state").GetString());
        Assert.StartsWith($"Main.cs:{line}:", stop.GetProperty("location").GetString(), StringComparison.Ordinal);
        CallToolResult paused = await Result(client, McpTools.AppTree);
        Assert.True(paused.IsError == true && Text(paused).Contains("paused in the debugger", StringComparison.Ordinal), $"A paused game's tree was answered with: {Text(paused)}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>debug_start of a game with a breakpoint in its script answers with the stop there itself, its line and the
    /// method's local, rather than that the game runs once it has connected: in _Ready, and in a method its frames first
    /// reach seconds after it starts.</summary>
    [Fact]
    public async Task Debug_start_answers_with_the_stop_at_a_breakpoint_in_the_games_ready_or_one_its_frames_reach_later()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: Homes);
        string[] lines = GodotGameFolder.Script.Split('\n');
        foreach ((string code, string local) in (ValueTuple<string, string>[])[("GD.Print($\"ready to say", "greeted"), ("GD.Print($\"later, at", "at")])
        {
            int line = Array.FindIndex(lines, l => l.Contains(code, StringComparison.Ordinal)) + 1;
            await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Main.cs", ["line"] = line });
            JsonElement stop = await Call(client, McpTools.DebugStart, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
            Assert.True(stop.GetProperty("state").GetString() == "paused" && stop.GetProperty("location").GetString()!.StartsWith($"Main.cs:{line}:", StringComparison.Ordinal),
                $"debug_start did not answer with the stop at Main.cs:{line}: {stop}");
            string[] locals = [.. stop.GetProperty("locals").EnumerateArray().Select(l => l.GetProperty("name").GetString()!)];
            Assert.True(locals.Contains(local), $"The stop at Main.cs:{line} has the locals {string.Join(", ", locals)}, without {local}");
            await Call(client, McpTools.Stop);
            await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Main.cs", ["line"] = line, ["remove"] = true });
        }
    }

    /// <summary>A breakpoint an agent sets in a GDScript file stops the game there, its GDScript debugged beside its C#: the
    /// breakpoint bound, debug_state naming the script's line, its locals and self, and the game's two sessions.</summary>
    [Fact]
    public async Task An_agent_sets_a_breakpoint_in_a_gdscript_file_and_debug_state_says_where_the_game_stopped_with_its_locals()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: Homes);
        int line = Array.FindIndex(GodotGameFolder.Ticker.Split('\n'), l => l.Contains("ticks += 1", StringComparison.Ordinal)) + 1;
        JsonElement set = Assert.Single((await Call(client, McpTools.BreakpointSet, new() { ["path"] = "ticker.gd", ["line"] = line })).EnumerateArray());
        Assert.Equal("set", set.GetProperty("status").GetString());
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        JsonElement state = default;
        await Until(async () => (state = await Call(client, McpTools.DebugState)).GetProperty("state").GetString() == "paused", "the game to stop at the breakpoint in ticker.gd");
        Assert.StartsWith($"ticker.gd:{line}:", state.GetProperty("location").GetString(), StringComparison.Ordinal);
        string[] locals = [.. state.GetProperty("locals").EnumerateArray().Select(l => l.GetProperty("name").GetString()!)];
        Assert.True(locals.Contains("_delta") && locals.Contains("self"), $"The stop's locals are {string.Join(", ", locals)}");
        Assert.Contains(state.GetProperty("sessions").EnumerateArray(), s => s.GetString() == "Spinner: Headless (GDScript): paused");
        JsonElement[] listed = [.. (await Call(client, McpTools.DebugState, new() { ["of"] = "breakpoints" })).EnumerateArray()];
        Assert.Contains(listed, b => b.TryGetProperty("path", out JsonElement at) && at.GetString() == "ticker.gd" && b.GetProperty("status").GetString() == "bound");
        Assert.Contains(listed, b => b.TryGetProperty("scriptErrors", out JsonElement errors) && errors.GetBoolean() && b.GetProperty("enabled").GetBoolean());
        await Call(client, McpTools.Stop);
    }

    /// <summary>A stop in the game's other language answers the call that let the game go, as one in its own does: debug_start
    /// with a breakpoint in GDScript alone answers with the GDScript stop, debug_continue of it with a stop in the C#, and
    /// debug_continue of that with the GDScript stop again, none of them waiting out its time and saying the game runs.</summary>
    [Fact]
    public async Task Debug_start_and_debug_continue_answer_with_a_stop_in_the_games_other_language()
    {
        await using McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: Homes);
        int tick = Array.FindIndex(GodotGameFolder.Ticker.Split('\n'), l => l.Contains("ticks += 1", StringComparison.Ordinal)) + 1;
        int frame = Array.FindIndex(GodotGameFolder.Script.Split('\n'), l => l.Contains("frames++;", StringComparison.Ordinal)) + 1;
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "ticker.gd", ["line"] = tick });
        JsonElement stop = await Call(client, McpTools.DebugStart, new() { ["configuration"] = Headless, ["waitSeconds"] = 180 });
        Stopped(stop, $"ticker.gd:{tick}:", "debug_start with a breakpoint in GDScript alone");

        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "ticker.gd", ["line"] = tick, ["remove"] = true });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Main.cs", ["line"] = frame });
        Stopped(await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 120 }), $"Main.cs:{frame}:", "debug_continue of the GDScript stop with a breakpoint in C#");

        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Main.cs", ["line"] = frame, ["remove"] = true });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "ticker.gd", ["line"] = tick });
        Stopped(await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 120 }), $"ticker.gd:{tick}:", "debug_continue of the C# stop with a breakpoint in GDScript");
        await Call(client, McpTools.Stop);

        static void Stopped(JsonElement answer, string at, string what) =>
            Assert.True(answer.GetProperty("state").GetString() == "paused" && answer.GetProperty("location").GetString()?.StartsWith(at, StringComparison.Ordinal) == true,
                $"{what} did not answer with the stop at {at}: {answer}");
    }

    /// <summary>The user folders a Godot editor the agent's Passenger starts for GDScript keeps, the run's.</summary>
    private Dictionary<string, string?> Homes => new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = game.Folder + "-home" };
}

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

            [Export]
            public string Greeting { get; set; } = "frame";

            public override void _Process(double delta)
            {
                frames++;
                if (frames % 10 == 0)
                {
                    GD.Print($"{Greeting} {frames}");
                }
                bool pressed = GetNode<Button>("Ui/Toggle").ButtonPressed;
                if (pressed != toggled)
                {
                    toggled = pressed;
                    GD.Print($"toggled {(pressed ? "on" : "off")}");
                }
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

        [node name="Ticker" type="Node" parent="."]
        script = ExtResource("2")

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
/// the app_ tools read its scene tree and a node's values, set one (which the game then prints), click its button and type
/// into its text box through Passenger's helper in the game, and say plainly that a headless game has no picture; and
/// debug_start stops it at a breakpoint in its script's _Process. Without a Godot 4 .NET build, each skips saying why.</summary>
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

    /// <summary>The user folders a Godot editor the agent's Passenger starts for GDScript keeps, the run's.</summary>
    private Dictionary<string, string?> Homes => new() { [Passenger.App.Godot.GodotScripts.HomeVariable] = game.Folder + "-home" };
}

using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A Godot 4 C# project whose launch profiles run %GODOT% on it, as every Chickensoft template's do: "🕹 Debug Game"
/// on <c>--path "./"</c> from its solution's folder, and "🧪 Debug Tests" with its test arguments after. Its Main node says,
/// as it is ready, the display it runs on, the GODOT it was given and its command line. Restored from the Godot found's
/// own packages.</summary>
public sealed class GodotProfileGameFolder : IAsyncLifetime
{
    public GodotProfileGameFolder()
    {
        Folder = PathNames.Canonical(TestFolders.Create("godot-profile-"));
        Godot = GodotExecutables.Find(null);
    }

    public const string Script = """
        using Godot;

        public partial class Main : Node
        {
            private int frames;

            public override void _Ready()
            {
                GD.Print($"display [{DisplayServer.GetName()}]");
                GD.Print($"GODOT [{OS.GetEnvironment("GODOT")}]");
                GD.Print($"arguments [{string.Join(' ', OS.GetCmdlineArgs())}]");
            }

            public override void _Process(double delta)
            {
                frames++;
            }
        }

        """;

    private const string Scene = """
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="res://Main.cs" id="1"]

        [node name="Main" type="Node"]
        script = ExtResource("1")

        [node name="Ui" type="Control" parent="."]
        layout_mode = 3
        anchors_preset = 0
        offset_right = 400.0
        offset_bottom = 300.0

        """;

    public const string LaunchSettings = """
        {
          "profiles": {
            "🕹 Debug Game": {
              "commandName": "Executable",
              "executablePath": "%GODOT%",
              "commandLineArgs": "--path \"./\"",
              "workingDirectory": "$(SolutionDir)"
            },
            "🧪 Debug Tests": {
              "commandName": "Executable",
              "executablePath": "%GODOT%",
              "commandLineArgs": "--path \"./\" --run-tests --listen-trace --quit-on-finish",
              "workingDirectory": "$(SolutionDir)"
            }
          }
        }
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
            File.WriteAllText(Path.Combine(Folder, "Spinner.slnx"), "<Solution>\n  <Project Path=\"Spinner.csproj\" />\n</Solution>\n");
            Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
            File.WriteAllText(Path.Combine(Folder, "Properties", "launchSettings.json"), LaunchSettings);
            await Restore.RunAsync(Path.Combine(Folder, "Spinner.csproj"));
        }, Folder));

    public ValueTask DisposeAsync()
    {
        TestFolders.Delete(Folder);
        TestFolders.Delete(Folder + "-home");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Agents running a Godot project's %GODOT% launch profiles with GODOT unset, the Godot Settings names taking its
/// place: each runs the game as Passenger's own Godot configuration does, headless with Passenger its remote debugger, its
/// tree read through app_tree, GODOT given to the game, its own arguments kept and its --path given once; Debug stops it at
/// a breakpoint in its script. Without a Godot 4 .NET build, each skips saying why.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotProfileTests : McpRig, IClassFixture<GodotProfileGameFolder>
{
    private const string Game = "Spinner: 🕹 Debug Game";

    private const string Tests = "Spinner: 🧪 Debug Tests";

    private readonly GodotProfileGameFolder game;

    public McpGodotProfileTests(GodotProfileGameFolder game)
    {
        this.game = game;
        Assert.SkipWhen(game.Skipped is not null, game.Skipped ?? string.Empty);
    }

    [Fact]
    public async Task A_profile_running_GODOT_on_the_project_runs_its_game_headless_with_the_remote_debugger_its_tree_read_and_debugs_it_to_a_breakpoint()
    {
        await using McpClient client = await Started();
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Game, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");
        Assert.Contains("Passenger its remote debugger", ran.GetProperty("status").GetString(), StringComparison.Ordinal);
        string said = await Ready(client);
        Assert.True(said.Contains("display [headless]", StringComparison.Ordinal), $"An agent's game did not run headless, so its window would be on the person's screen:\n{said}");
        Assert.False(said.Contains("GODOT []", StringComparison.Ordinal), $"The game was not given the GODOT its profile names:\n{said}");
        string tree = Text(await Call(client, McpTools.AppTree));
        Assert.True(tree.Contains("Main#Main @", StringComparison.Ordinal) && tree.Contains("Control#Ui @", StringComparison.Ordinal),
            $"The game's tree as an agent reads it lacks its nodes:\n{tree}");
        await Call(client, McpTools.Stop);

        int line = Array.FindIndex(GodotProfileGameFolder.Script.Split('\n'), l => l.Contains("frames++;", StringComparison.Ordinal)) + 1;
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Main.cs", ["line"] = line });
        JsonElement stop = await Call(client, McpTools.DebugStart, new() { ["configuration"] = Game, ["waitSeconds"] = 180 });
        await Until(async () => (stop = await Call(client, McpTools.DebugState)).GetProperty("state").GetString() == "paused", "the game to stop at the breakpoint in _Process");
        Assert.StartsWith($"Main.cs:{line}:", stop.GetProperty("location").GetString(), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    [Fact]
    public async Task A_profile_running_GODOT_with_its_own_arguments_gives_them_to_the_game_with_one_path()
    {
        await using McpClient client = await Started();
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = Tests, ["waitSeconds"] = 180 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The game ended as it started: {ran}");
        string said = await Ready(client);
        Assert.True(said.Contains("arguments [--run-tests --listen-trace --quit-on-finish]", StringComparison.Ordinal),
            $"The game was not given its profile's own arguments, which Godot hands it after its own:\n{said}");
        // The command line the Run console shows as it starts Godot.
        string started = said.Split('\n').FirstOrDefault(l => l.Contains("--listen-trace", StringComparison.Ordinal) && !l.Contains("arguments [", StringComparison.Ordinal)) ?? said;
        Assert.True(started.Split(' ').Count(a => a == "--path") == 1, $"Godot was not started with one --path: {started}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>The agent's Passenger on the project, GODOT taken out of its environment and the Godot found named in its
    /// settings, in user folders of the run's.</summary>
    private async Task<McpClient> Started()
    {
        McpClient client = await Start(["--mcp", game.Folder], languageServer: false, environment: new()
        {
            [GodotExecutables.Variable] = null,
            [Passenger.App.Godot.GodotUserFolders.HomeVariable] = game.Folder + "-home",
        });
        await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["godotExecutable"] = game.Godot!.Executable } });
        return client;
    }

    /// <summary>What the game said once ready: the display it runs on, its GODOT and its command line.</summary>
    private async Task<string> Ready(McpClient client)
    {
        string said = string.Empty;
        await Until(async () => (said = Text(await Call(client, McpTools.Output))).Contains("arguments [", StringComparison.Ordinal), "the game to say it is ready");
        return said;
    }
}

using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent debugging a Godot game of GDScript alone: a script it edits (and so saves) reloads in the game, which
/// then says the edit's words, and hot_reload answers that Godot reloads each script as it is saved, with nothing else to
/// apply, not that Hot Reload is off. Godot runs headless, its user folders the test's; without a Godot 4 it skips saying
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
        await Call(client, McpTools.DebugStart, new() { ["configuration"] = "Ticker", ["waitSeconds"] = 60 });
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("tick 20", StringComparison.Ordinal), "the game to say its word");

        await Call(client, McpTools.Edit, new() { ["path"] = "ticker.gd", ["oldText"] = "print(\"tick \", counter)", ["newText"] = "print(\"edited \", counter)" });
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("edited ", StringComparison.Ordinal), "the game to say the saved edit's words");

        JsonElement reload = await Call(client, McpTools.HotReload);
        string said = reload.GetProperty("said").GetString()!;
        Assert.True(reload.GetProperty("result").GetString() == "nothingToApply" && said.Contains("as it is saved", StringComparison.Ordinal)
            && !said.Contains("Hot Reload when debugging off", StringComparison.Ordinal),
            $"hot_reload on a game whose saved script had reloaded did not say that saved scripts reload: {reload}");
        await Call(client, McpTools.Stop);
    }
}

using ModelContextProtocol.Client;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent's renames of a Godot node, one straight after another and after a file written beside them: each finds
/// the closed scene as it stands, however long the folder's watcher takes to tell of it. A project of text files alone, so
/// neither C#'s server nor Godot starts.</summary>
public sealed class McpGodotRenameTests : McpRig
{
    private const string Player = "[gd_scene format=3]\n\n[node name=\"Player\" type=\"Node2D\"]\n\n[node name=\"Sprite\" type=\"Node2D\" parent=\".\"]\n";

    private const string Level = "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://player.tscn\" id=\"1_player\"]\n\n"
        + "[node name=\"Level\" type=\"Node2D\"]\nTarget = NodePath(\"Player/Sprite\")\n\n[node name=\"Player\" parent=\".\" instance=ExtResource(\"1_player\")]\n";

    [Fact]
    public async Task An_agents_renames_of_a_node_one_after_another_each_find_the_closed_scene_as_it_stands()
    {
        string folder = TestFolders.Create("mcp-godot-renames-");
        TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Renames\"\n");
        TestFolders.Write(folder, "player.tscn", Player);
        string level = TestFolders.Write(folder, "level.tscn", Level);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        Dictionary<string, object?> At(string name) => new() { ["path"] = "player.tscn", ["line"] = 5, ["column"] = 14, ["newName"] = name };

        await Call(client, McpTools.Rename, At("Body"));
        Assert.True(File.ReadAllText(level).Contains("NodePath(\"Player/Body\")", StringComparison.Ordinal), $"The first rename left the scene over it behind: {File.ReadAllText(level)}");
        await Call(client, McpTools.Rename, At("Frame"));
        Assert.True(File.ReadAllText(level).Contains("NodePath(\"Player/Frame\")", StringComparison.Ordinal), $"A rename right after the first was to find what it wrote: {File.ReadAllText(level)}");

        File.WriteAllText(level, "; Written by another program.\n" + File.ReadAllText(level));
        await Call(client, McpTools.Rename, At("Skin"));
        Assert.Equal("; Written by another program.\n" + Level.Replace("Player/Sprite", "Player/Skin", StringComparison.Ordinal), File.ReadAllText(level));
    }
}

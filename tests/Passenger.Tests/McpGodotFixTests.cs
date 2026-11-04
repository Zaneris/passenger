using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent fixing a Godot game's checks through code_actions and apply_code_action as a person does through Show
/// Context Actions: outline gives the scene's nodes, script and connections, and the open scene's connection to a method its
/// script lacks lists the method's making, which makes it in the script, saved, so the scene's mark goes; and outline names
/// an imported model a node instances. Without a Godot 4 .NET build, the fix skips saying why.</summary>
public sealed class McpGodotFixTests : McpRig
{
    [Fact]
    public async Task An_agent_makes_the_method_a_scenes_connection_calls_through_code_actions()
    {
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null },
            godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
                : $"The Godot found carries no packages a C# project restores from: {godot.WhyNot}");
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-fix-"));
        try
        {
            GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, GodotFixesTests.Script, GodotFixesTests.Scene);
            await Restore.RunAsync(folder);
            await using ModelContextProtocol.Client.McpClient client = await Start(["--mcp", folder]);
            await Until(async () => Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Main.cs" })).Contains("which Main has no method of", StringComparison.Ordinal),
                "Main.cs to be read against its scene");
            string outline = Text(await Call(client, McpTools.Outline, new() { ["path"] = "main.tscn" }));
            Assert.Equal($"""
                Main : Node2D (node, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "[node name=\"Main\"")})
                  Main.cs script
                  Hud : CanvasLayer (node, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "[node name=\"Hud\"")})
                    Score : Label (node, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "[node name=\"Score\"")})
                    Start : Button (node, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "[node name=\"Start\"")})
                      pressed → Main.OnStartPressed (connection, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "method=\"OnStartPressed\"")})
                      pressed → Main.OnQuitPressed (connection, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "method=\"OnQuitPressed\"")})
                    Sound : CheckButton (node, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "[node name=\"Sound\"")})
                      toggled → Main.OnSoundToggled (connection, line {GodotSceneTests.LineOf(GodotFixesTests.Scene, "method=\"OnSoundToggled\"")})
                """, outline.ReplaceLineEndings("\n"));
            int line = GodotSceneTests.LineOf(GodotFixesTests.Scene, "method=\"OnQuitPressed\"");
            int column = GodotFixesTests.Scene.Split('\n')[line - 1].IndexOf("OnQuitPressed", StringComparison.Ordinal) + 2;
            Dictionary<string, object?> at = new() { ["path"] = "main.tscn", ["line"] = line, ["column"] = column };
            string fix = "Create method OnQuitPressed() in Main";
            string[] offered = [.. (await Call(client, McpTools.CodeActions, at)).EnumerateArray().Select(a => a.GetString()!)];
            Assert.Contains($"{fix} [quickfix]", offered);

            string made = Text(await Call(client, McpTools.ApplyCodeAction, new(at) { ["title"] = fix }));
            Assert.Contains("Main.cs", made, StringComparison.Ordinal);
            Assert.Contains("private void OnQuitPressed()", File.ReadAllText(Path.Combine(folder, "Main.cs")), StringComparison.Ordinal);
            string marks = string.Empty;
            Exception? failed = await Record.ExceptionAsync(() => Until(async () => !(marks = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "main.tscn" })))
                .Contains("has no method OnQuitPressed", StringComparison.Ordinal), "the scene's mark of the missing method to go"));
            Assert.True(failed is null, $"{failed?.Message}\nThe scene's marks: {marks}");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>outline of a scene names the scene each node instances, an imported model's as a scene's, though no text says
    /// the model's nodes. Needs no Godot: the scene is Passenger's own reading.</summary>
    [Fact]
    public async Task An_agents_outline_of_a_scene_names_an_imported_model_a_node_instances()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-outline-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Outlined\"\n");
            TestFolders.Write(folder, "flag.tscn", "[gd_scene format=3]\n\n[node name=\"Flag\" type=\"Node3D\"]\n");
            TestFolders.Write(folder, "models/tree.glb", "glTF");
            TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=3 format=3]\n\n[ext_resource type=\"PackedScene\" path=\"res://flag.tscn\" id=\"1_flag\"]\n"
                + "[ext_resource type=\"PackedScene\" path=\"res://models/tree.glb\" id=\"2_tree\"]\n\n[node name=\"Main\" type=\"Node3D\"]\n\n"
                + "[node name=\"Flag\" parent=\".\" instance=ExtResource(\"1_flag\")]\n\n[node name=\"Tree\" parent=\".\" instance=ExtResource(\"2_tree\")]\n");
            await using ModelContextProtocol.Client.McpClient client = await Start(["--mcp", folder], languageServer: false);
            Assert.Equal("""
                Main : Node3D (node, line 6)
                  Flag : Node3D (node, line 8)
                    flag.tscn instance
                  Tree (node, line 10)
                    models/tree.glb instance
                """, Text(await Call(client, McpTools.Outline, new() { ["path"] = "main.tscn" })).ReplaceLineEndings("\n"));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }
}

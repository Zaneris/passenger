using ModelContextProtocol.Client;
using Passenger.App.Godot;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's diagnostics of a Godot scene it has just edited answer for the scene as the edit left it: a node put
/// under one the scene lacks is marked at once, and the mark goes at once as the node is put back. The scene is checked
/// by its own text, so no Godot runs.</summary>
public sealed class McpGodotSceneMarksTests : McpRig
{
    private const string Scene = """
        [gd_scene format=3]

        [node name="Main" type="Node2D"]

        [node name="Hud" type="CanvasLayer" parent="."]

        [node name="Score" type="Label" parent="Hud"]

        """;

    [Fact]
    public async Task An_agents_diagnostics_of_a_scene_it_has_just_edited_answer_for_the_edit()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-marks-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Marks\"\nconfig/features=PackedStringArray(\"4.7\")\n");
            TestFolders.Write(folder, "main.tscn", Scene);
            Dictionary<string, string?> homes = new() { [GodotUserFolders.HomeVariable] = folder + "-home" };
            await using McpClient client = await Start(["--mcp", folder], environment: homes, languageServer: false);
            Assert.Equal("[]", Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "main.tscn" })));

            await Call(client, McpTools.Edit, new() { ["path"] = "main.tscn", ["oldText"] = "parent=\"Hud\"", ["newText"] = "parent=\"Gone\"" });
            string marked = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "main.tscn" }));
            Assert.True(marked.Contains("No node 'Gone' is in the scene, so Godot leaves Score out", StringComparison.Ordinal),
                $"The agent was told the scene it had just broken was as before: {marked}");

            await Call(client, McpTools.Edit, new() { ["path"] = "main.tscn", ["oldText"] = "parent=\"Gone\"", ["newText"] = "parent=\"Hud\"" });
            string mended = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "main.tscn" }));
            Assert.True(mended == "[]", $"The agent was told the scene it had just mended was still broken: {mended}");
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(folder + "-home");
        }
    }
}

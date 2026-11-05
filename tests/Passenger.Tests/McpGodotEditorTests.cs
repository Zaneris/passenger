using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>godot_editor on an agent's own Passenger (--mcp), which no person's Godot editor connects to: every action, a play
/// too, refused saying the person's editor connects to their own window and how to reach it. Needs no Godot. Its Passenger is
/// a process of its own, so it runs beside the window tests.</summary>
[Collection(Beside.Name)]
public sealed class McpGodotEditorTests : McpRig
{
    [Fact]
    public async Task Godot_editor_on_an_agents_own_passenger_says_the_persons_editor_connects_to_their_window()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-editor-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Ticker\"\nrun/main_scene=\"res://main.tscn\"\n");
            TestFolders.Write(folder, "main.tscn", "[gd_scene format=3]\n\n[node name=\"Main\" type=\"Node\"]\n");
            await using McpClient client = await Start(["--mcp", folder], languageServer: false);
            foreach (string action in new[] { "read", "play" })
            {
                CallToolResult refused = await Result(client, McpTools.GodotEditor, new() { ["action"] = action });
                Assert.True(refused.IsError == true && Text(refused).Contains(Refusals.GodotEditorOfPerson, StringComparison.Ordinal),
                    $"godot_editor {action} on an agent's own Passenger answered: {Text(refused)}");
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }
}

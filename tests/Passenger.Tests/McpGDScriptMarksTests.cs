using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Godot;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's diagnostics of a GDScript give a warning's name as its code and Godot's words alone as its message,
/// as a C# mark's: Godot's server's where a Godot is found (its headless editor's user folders the test's, so run alone),
/// else Passenger's own reading's, which say the same.</summary>
[Collection(Alone.Name)]
public sealed class McpGDScriptMarksTests : McpRig
{
    private const string Words = "The local variable \"unused\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_unused\".";

    [Fact]
    public async Task An_agents_diagnostics_of_a_gdscript_give_a_warnings_name_as_its_code_and_godots_words_as_its_message()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-gdscript-marks-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Marks\"\n");
            TestFolders.Write(folder, "main.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tvar unused = 1\n");
            Dictionary<string, string?> homes = new() { [GodotUserFolders.HomeVariable] = folder + "-home" };
            await using McpClient client = await Start(["--mcp", folder], environment: homes);
            JsonElement unused = default;
            await Until(async () => (await Call(client, McpTools.Diagnostics, new() { ["path"] = "main.gd" })).EnumerateArray()
                .FirstOrDefault(f => f.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String && code.GetString() == "UNUSED_VARIABLE")
                is { ValueKind: JsonValueKind.Object } found
                && (unused = found).ValueKind == JsonValueKind.Object, "the unused variable's warning with UNUSED_VARIABLE as its code");
            Assert.Equal(("warning", Words, 5), (unused.GetProperty("severity").GetString(), unused.GetProperty("message").GetString(), unused.GetProperty("line").GetInt32()));
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(folder + "-home");
        }
    }
}

using ModelContextProtocol.Client;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent's <c>format</c> of a GDScript lays it out by Godot's style guide, as Reformat Code does, and saves it;
/// one already laid out is said to be. No project.godot, so no Godot server starts, nor C#'s.</summary>
public sealed class McpGDScriptFormatTests : McpRig
{
    [Fact]
    public async Task An_agent_reformats_a_gdscript_by_godots_style_guide_and_it_is_saved()
    {
        string folder = TestFolders.Create("mcp-gdscript-format-");
        TestFolders.Write(folder, "main.gd", "extends Node\nvar speed:=2.0\nfunc _ready():\n    var party = [\n        \"Godot\",\n        \"Godette\"\n    ]\n    print (party,speed*2)\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        Assert.Equal("Reformatted", (await Call(client, McpTools.Format, new() { ["path"] = "main.gd" })).GetString());
        Assert.Equal("extends Node\nvar speed := 2.0\n\n\nfunc _ready():\n\tvar party = [\n\t\t\"Godot\",\n\t\t\"Godette\",\n\t]\n\tprint(party, speed * 2)\n",
            File.ReadAllText(Path.Combine(folder, "main.gd")));
        Assert.Equal("Already formatted", (await Call(client, McpTools.Format, new() { ["path"] = "main.gd" })).GetString());
    }
}

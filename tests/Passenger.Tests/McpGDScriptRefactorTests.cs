using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent's <c>refactor</c> of a GDScript: Change Signature described, then applied through every call and the
/// string naming the function; Extract Variable over a selection; Safe Delete of a used function refused naming its uses;
/// each saved. No project.godot, so no Godot server starts, nor C#'s.</summary>
public sealed class McpGDScriptRefactorTests : McpRig
{
    private const string Script = "extends Node\n\n\nfunc hit(target, amount = 1):\n\tprint(target, amount)\n\n\nfunc _ready():\n\thit(\"a\")\n\thit(\"b\", 3)\n\tcall(\"hit\", \"c\")\n";

    [Fact]
    public async Task An_agent_changes_a_gdscript_functions_signature_extracts_a_variable_and_is_refused_a_safe_delete_of_a_used_one()
    {
        string folder = TestFolders.Create("mcp-gdscript-refactor-");
        string path = TestFolders.Write(folder, "main.gd", Script);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        JsonElement described = await Call(client, McpTools.Refactor, new() { ["kind"] = "change_signature", ["path"] = "main.gd", ["line"] = 4, ["column"] = 7 });
        Assert.Equal(["target", "amount"], described.GetProperty("dialog").GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        JsonElement changed = await Call(client, McpTools.Refactor, new()
        {
            ["kind"] = "change_signature", ["path"] = "main.gd", ["line"] = 4, ["column"] = 7,
            ["options"] = JsonSerializer.SerializeToElement(new { name = "strike", parameters = new object[] { new { from = "amount" }, new { from = "target", defaultValue = "null" } } }),
        });
        Assert.Equal("Change Signature done", changed.GetProperty("said").GetString());
        Assert.Equal("extends Node\n\n\nfunc strike(amount = 1, target = null):\n\tprint(target, amount)\n\n\nfunc _ready():\n\tstrike(1, \"a\")\n\tstrike(3, \"b\")\n\tcall(\"strike\", 1, \"c\")\n",
            File.ReadAllText(path));

        JsonElement extracted = await Call(client, McpTools.Refactor, new()
        {
            ["kind"] = "extract_variable", ["path"] = "main.gd", ["line"] = 9, ["column"] = 9, ["endLine"] = 9, ["endColumn"] = 10,
            ["options"] = JsonSerializer.SerializeToElement(new { name = "first" }),
        });
        Assert.Equal("Extract Variable done", extracted.GetProperty("said").GetString());
        Assert.Contains("\tvar first := 1\n\tstrike(first, \"a\")\n", File.ReadAllText(path), StringComparison.Ordinal);

        CallToolResult refused = await Result(client, McpTools.Refactor, new() { ["kind"] = "safe_delete", ["path"] = "main.gd", ["line"] = 4, ["column"] = 7 });
        Assert.True(refused.IsError == true && Text(refused).Contains("'strike' is still used", StringComparison.Ordinal), $"Safe Delete of a used function answered: {Text(refused)}");
    }
}

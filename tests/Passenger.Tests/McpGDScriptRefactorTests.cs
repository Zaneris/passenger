using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent's <c>refactor</c> of a GDScript: Change Signature described, then applied through every call and the
/// string naming the function; Extract Variable over a selection; Safe Delete of a used function refused naming its uses;
/// a rename held back by a call by a run-time name made with force; each saved. No project.godot, so no Godot server
/// starts, nor C#'s.</summary>
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

    [Fact]
    public async Task An_agents_rename_a_call_names_at_run_time_holds_back_is_refused_naming_force_and_made_with_it_leaves_the_call()
    {
        string folder = TestFolders.Create("mcp-gdscript-rename-");
        string path = TestFolders.Write(folder, "main.gd", "extends Node\n\n\nfunc hit(target):\n\tprint(target)\n\n\nfunc by_name(method: String) -> void:\n\tcall(method, 1)\n\thit(2)\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        CallToolResult refused = await Result(client, McpTools.Rename, new() { ["path"] = "main.gd", ["line"] = 4, ["column"] = 7, ["newName"] = "strike" });
        Assert.True(refused.IsError == true && Text(refused).Contains("names a method by what it reads at run time", StringComparison.Ordinal)
            && Text(refused).Contains("force: true", StringComparison.Ordinal), $"The rename held back by call(method) answered: {Text(refused)}");
        CallToolResult forced = await Result(client, McpTools.Rename, new() { ["path"] = "main.gd", ["line"] = 4, ["column"] = 7, ["newName"] = "strike", ["force"] = true });
        Assert.True(forced.IsError != true && Text(forced).Contains("leaving 1 place it can't see", StringComparison.Ordinal), $"The forced rename answered: {Text(forced)}");
        Assert.Equal("extends Node\n\n\nfunc strike(target):\n\tprint(target)\n\n\nfunc by_name(method: String) -> void:\n\tcall(method, 1)\n\tstrike(2)\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task An_agent_finds_and_renames_an_enums_value_and_an_inner_classs_member_at_the_first_ask_with_no_godot_started()
    {
        string folder = TestFolders.Create("mcp-gdscript-nested-");
        string path = TestFolders.Write(folder, "main.gd", "extends Node\n\nenum Mood { CALM, ANGRY }\n\nvar helper := Helper.new()\n\n\nfunc _ready() -> void:\n"
            + "\tprint(Mood.CALM)\n\thelper.count += 1\n\n\nclass Helper:\n\tvar count := 0\n\n\tfunc bump() -> void:\n\t\tcount += 1\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        JsonElement uses = await Call(client, McpTools.Usages, new() { ["path"] = "main.gd", ["line"] = 10, ["column"] = 9 });
        Assert.True(uses.GetProperty("count").GetInt32() == 3, $"Helper's count should be found where it is declared and both reads: {Text(uses)}");
        await Call(client, McpTools.Rename, new() { ["path"] = "main.gd", ["line"] = 3, ["column"] = 13, ["newName"] = "STILL" });
        await Call(client, McpTools.Rename, new() { ["path"] = "main.gd", ["line"] = 10, ["column"] = 9, ["newName"] = "total" });
        Assert.Equal("extends Node\n\nenum Mood { STILL, ANGRY }\n\nvar helper := Helper.new()\n\n\nfunc _ready() -> void:\n"
            + "\tprint(Mood.STILL)\n\thelper.total += 1\n\n\nclass Helper:\n\tvar total := 0\n\n\tfunc bump() -> void:\n\t\ttotal += 1\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task An_agent_changes_a_gdscript_signals_signature_through_its_emits_and_the_handler_connected()
    {
        string folder = TestFolders.Create("mcp-gdscript-signal-");
        string path = TestFolders.Write(folder, "main.gd", "extends Node\n\nsignal scored(points)\n\n\nfunc _ready():\n\tscored.connect(on_scored)\n\tscored.emit(3)\n\n\n"
            + "func on_scored(points):\n\tprint(points)\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        JsonElement described = await Call(client, McpTools.Refactor, new() { ["kind"] = "change_signature", ["path"] = "main.gd", ["line"] = 3, ["column"] = 9 });
        Assert.Equal(["points"], described.GetProperty("dialog").GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        await Call(client, McpTools.Refactor, new()
        {
            ["kind"] = "change_signature", ["path"] = "main.gd", ["line"] = 3, ["column"] = 9,
            ["options"] = JsonSerializer.SerializeToElement(new { parameters = new object[] { new { from = "points" }, new { name = "bonus", type = "int", callValue = "0" } } }),
        });
        Assert.Equal("extends Node\n\nsignal scored(points, bonus: int)\n\n\nfunc _ready():\n\tscored.connect(on_scored)\n\tscored.emit(3, 0)\n\n\n"
            + "func on_scored(points, bonus: int):\n\tprint(points)\n", File.ReadAllText(path));
    }
}

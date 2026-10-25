using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>An agent's migrations, through the tools it has: the Migrations actions by ide_action with what they would ask the
/// person as input, answering the context's migrations; and what keeps dotnet ef from working refused with its fixes, which
/// code_actions lists on the startup project's file and apply_code_action makes.</summary>
public sealed class McpMigrationsTests : McpRig
{
    [Fact]
    public async Task An_agent_fixes_what_dotnet_ef_lacks_through_code_actions_then_adds_and_applies_a_migration()
    {
        string folder = ShopApp.Make(design: false, tool: null);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        CallToolResult refused = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration, ["input"] = "Initial" });
        string said = Text(refused);
        Assert.True(refused.IsError == true && said.Contains("dotnet ef is not installed here", StringComparison.Ordinal)
            && said.Contains("code_actions on Shop.csproj:1:1", StringComparison.Ordinal), $"Add Migration with no tool answered: {said}");

        string pin = $"Pin the Local dotnet-ef to {ShopApp.Version}";
        string design = $"Add Microsoft.EntityFrameworkCore.Design {ShopApp.Version}";
        string[] offered = [.. (await Call(client, McpTools.CodeActions, new() { ["path"] = "Shop.csproj", ["line"] = 1, ["column"] = 1 })).EnumerateArray().Select(a => a.GetString()!)];
        Assert.Contains($"{pin} [quickfix]", offered);
        Assert.Contains($"{design} [quickfix]", offered);
        Assert.Contains("dotnet-tools.json", Text(await Call(client, McpTools.ApplyCodeAction, new() { ["path"] = "Shop.csproj", ["line"] = 1, ["column"] = 1, ["title"] = pin })),
            StringComparison.Ordinal);
        Assert.Contains("Shop.csproj", Text(await Call(client, McpTools.ApplyCodeAction, new() { ["path"] = "Shop.csproj", ["line"] = 1, ["column"] = 1, ["title"] = design })),
            StringComparison.Ordinal);

        string added = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration, ["input"] = "Initial" }));
        Assert.True(added.Contains("Added migration Initial", StringComparison.Ordinal) && added.Contains("_Initial: pending", StringComparison.Ordinal),
            $"Add Migration answered: {added}");
        string updated = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.UpdateDatabase }));
        Assert.Contains("_Initial: applied", updated, StringComparison.Ordinal);
        CallToolResult nameless = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration });
        Assert.True(nameless.IsError == true && Text(nameless).Contains("takes the migration's name as input", StringComparison.Ordinal),
            $"Add Migration with no name answered: {Text(nameless)}");
        string script = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ScriptMigrations, ["input"] = "..Initial" }));
        string file = script.Split(" into ", 2)[1].Split('\n')[0];
        Assert.Contains("CREATE TABLE \"Products\"", File.ReadAllText(file), StringComparison.Ordinal);
    }
}

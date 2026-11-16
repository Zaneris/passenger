using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What an agent with no window (<c>--mcp</c>) reaches: a setting naming a program Passenger runs is the person's
/// alone.</summary>
public sealed class McpReachTests : McpRig
{
    /// <summary>settings_set refuses an agent each setting naming a program Passenger runs or code it loads, saying the
    /// person sets it, and keeps the one the person set.</summary>
    [Fact]
    public async Task Settings_naming_a_program_passenger_runs_are_refused_to_an_agent_and_kept_as_the_person_set_them()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-reach-"));
        DeleteAtEnd(folder);
        KeepSettings(new JsonObject { ["terminalShell"] = "/bin/sh" });
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string[] named = [.. AppSettings.NamingPrograms.Select(JsonNamingPolicy.CamelCase.ConvertName).Order(StringComparer.Ordinal)];
        Assert.Equal(["debugBrowser", "eslintPackage", "godotExecutable", "nodeInterpreter", "prettierPackage", "terminalShell"], named);
        Assert.All(Declared.Every.OfType<ProgramSetting>(), program => Assert.Contains(program.Member, named));
        List<string> answered = [];
        foreach (string name in named)
        {
            CallToolResult refused = await Result(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { [name] = "/tmp/elsewhere" } });
            if (refused.IsError != true || !Text(refused).Contains($"{name} names a program Passenger runs, or code it loads: the person sets it in Settings, never an agent", StringComparison.Ordinal))
            {
                answered.Add($"{name}: {Text(refused)}");
            }
        }
        Assert.True(answered.Count == 0, $"settings_set took a program's setting from an agent:\n{string.Join('\n', answered)}");
        JsonElement[] settings = [.. (await Call(client, McpTools.Settings)).GetProperty("settings").EnumerateArray()];
        Assert.Equal("/bin/sh", settings.Single(s => s.GetProperty("name").GetString() == "terminalShell").GetProperty("value").GetString());
        Assert.True(settings.Where(s => s.GetProperty("name").GetString() is { } name && name != "terminalShell" && named.Contains(name)).All(s => s.GetProperty("value").ValueKind == JsonValueKind.Null),
            $"A program's setting refused was kept all the same: {string.Join(", ", settings.Select(s => s.GetRawText()))}");
    }
}

using System.Text.Json;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's export of a Godot game through build's preset, on a project of GDScript alone whose Godot user folders
/// hold no export templates: refused naming the template its preset takes, and a preset the project lacks refused naming
/// those it has. Godot is only asked its version; without a Godot 4 the test skips saying why. Its Passenger is a process
/// of its own, so it runs beside the window tests.</summary>
[Collection(Beside.Name)]
public sealed class McpGodotExportTests : McpRig
{
    [Fact]
    public async Task An_agents_export_through_a_preset_is_refused_naming_the_templates_it_lacks_or_the_presets_there_are()
    {
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is null || godot.WhyNotScripts is not null,
            godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run this test" : $"The Godot found cannot run a Godot 4 project: {godot.WhyNotScripts}");
        (string folder, _) = GodotExportTests.Game();
        try
        {
            Dictionary<string, string?> homes = new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" };
            await using ModelContextProtocol.Client.McpClient client = await Start(["--mcp", folder], environment: homes, languageServer: false);
            JsonElement refused = await Call(client, McpTools.Build, new() { ["preset"] = GodotExportTests.Preset });
            string outcome = refused.GetProperty("outcome").GetString()!;
            Assert.False(refused.GetProperty("succeeded").GetBoolean(), $"An export without its templates succeeded: {outcome}");
            Assert.Contains($"has no {GodotExportTests.Template} among its export templates", outcome, StringComparison.Ordinal);

            JsonElement unknown = await Call(client, McpTools.Build, new() { ["preset"] = "Elsewhere" });
            Assert.Contains($"has no preset Elsewhere: it has {GodotExportTests.Preset}", unknown.GetProperty("outcome").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(folder + "-home");
        }
    }
}

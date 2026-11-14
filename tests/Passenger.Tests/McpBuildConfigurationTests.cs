using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The build configuration through an agent's own Passenger (<c>--mcp</c>): workspace names the one every build
/// takes and the input that switches to each other, ide_action app.chooseBuildConfiguration lists them with no input,
/// saying what input switches, and switches by a name, and build then builds into that configuration's output.</summary>
[Collection(Beside.Name)]
public sealed class McpBuildConfigurationTests : McpRig
{
    [Fact]
    public async Task An_agent_reads_the_build_configuration_in_workspace_switches_it_by_name_and_build_takes_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-configuration-"));
        DeleteAtEnd(folder);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(folder, "Hello.csproj"), TestProjects.Console("<Configurations>Debug;Release;Staging</Configurations>"));
            File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"hello\");\n");
            await Restore.RunAsync(folder);
        }, folder);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        string said = string.Empty;
        await Waiting.UntilAsync(async () => (said = Configured(await Call(client, McpTools.Workspace))).StartsWith("Debug|AnyCPU (", StringComparison.Ordinal),
            TimeSpan.FromSeconds(60), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.Equal($"Debug|AnyCPU (ide_action {AppActions.ChooseBuildConfiguration} with input Release|AnyCPU or Staging|AnyCPU switches it)", said);
        string listed = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseBuildConfiguration }));
        Assert.Equal("The folder builds as Debug|AnyCPU; it can build as Debug|AnyCPU, Release|AnyCPU and Staging|AnyCPU: input Release (or Release|AnyCPU) switches it", listed);

        string chose = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseBuildConfiguration, ["input"] = "staging" }));
        Assert.StartsWith("Builds, runs and tests build as Staging (AnyCPU)", chose, StringComparison.Ordinal);
        Assert.StartsWith("Staging|AnyCPU (", Configured(await Call(client, McpTools.Workspace)), StringComparison.Ordinal);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The build under Staging did not succeed: {built}");
        Assert.True(File.Exists(Path.Combine(folder, "bin", "Staging", TestProjects.Framework, "Hello.dll")), $"build under Staging did not build into bin/Staging: {built}");
        Assert.False(File.Exists(Path.Combine(folder, "bin", "Debug", TestProjects.Framework, "Hello.dll")), $"build under Staging built into bin/Debug: {built}");
    }

    /// <summary>What workspace says of the build configuration, empty where it says nothing.</summary>
    private static string Configured(JsonElement workspace) =>
        workspace.TryGetProperty("buildConfiguration", out JsonElement said) ? said.GetString() ?? string.Empty : string.Empty;
}

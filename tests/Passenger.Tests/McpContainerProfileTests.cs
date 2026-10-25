using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent's profile of a program in a container (attach with container): recorded there, its hot method named at
/// its line from the PDB in the container, and the heap's types from a memory snapshot taken there. Skips, saying why,
/// where no Linux engine runs; the tools are the run's one publish, made before the class's first test
/// (<see cref="ContainerRunDebuggingTests.Tools"/>).</summary>
[Collection(Alone.Name)]
public sealed class McpContainerProfileTests(ContainerRunDebuggingTests.Tools tools) : McpRig, IClassFixture<ContainerRunDebuggingTests.Tools>
{
    [Fact]
    public async Task An_agent_profiles_a_program_in_a_container_its_hot_method_at_its_line_and_its_heap()
    {
        ContainerProfilingTests.Spins spins = await Task.Run(() => ContainerProfilingTests.Spins.Shared);
        Assert.SkipWhen(spins.WhyNot is not null, spins.WhyNot ?? string.Empty);
        string container = await Task.Run(() => spins.Start("agent", offWindow: true));
        await using McpClient client = await Start(["--mcp", spins.Folder], languageServer: false);

        CallToolResult unattached = await Result(client, McpTools.Profile, new() { ["container"] = container, ["seconds"] = 1 });
        Assert.True(unattached.IsError == true && Text(unattached).Contains("attach", StringComparison.Ordinal), $"A container's profile without attach answered: {Text(unattached)}");
        CallToolResult nowhere = await Result(client, McpTools.Profile, new() { ["attach"] = true, ["container"] = $"{container}-not", ["seconds"] = 1 });
        Assert.True(nowhere.IsError == true && Text(nowhere).Contains("debug_state of processes", StringComparison.Ordinal), $"A profile in no such container answered: {Text(nowhere)}");

        JsonElement profiled = await Call(client, McpTools.Profile, new() { ["attach"] = true, ["container"] = container, ["seconds"] = 2, ["memory"] = true });
        Assert.True(profiled.GetProperty("samples").GetInt32() > 0, $"The profile in the container sampled nothing: {profiled.GetRawText()}");
        JsonElement spin = Assert.Single(profiled.GetProperty("hotSpots").EnumerateArray(), h => h.GetProperty("method").GetString()!.StartsWith("Hot.Spin", StringComparison.Ordinal));
        Assert.True(spin.TryGetProperty("source", out JsonElement source) && source.GetString() == $"Program.cs:{ContainerProfilingTests.Spins.SpinLine}",
            $"The hot method in the container is not named at its line: {spin}");
        Assert.Contains(profiled.GetProperty("heap").EnumerateArray(), t => t.GetProperty("type").GetString() == "Keepsake");
        Assert.Equal("running", Docker.State(container));
    }

    /// <summary>A compose configuration profiled as an agent asks: its launched service recorded in its container from its
    /// start, its hot method named at its line in the workspace, its heap read there, and the run going on until stop.</summary>
    [Fact]
    public async Task An_agent_profiles_a_compose_configuration_its_services_hot_method_at_its_line_and_its_heap()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string shop, string project) = ContainerRunProfilingTests.Shop("agent");
        // Whichever service it launches spins.
        TestFolders.Write(shop, "Api/Program.cs", ContainerRunProfilingTests.Spinning);
        DeleteAtEnd(shop);
        await using McpClient client = await Start(["--mcp", shop], languageServer: false);
        try
        {
            JsonElement profiled = await Call(client, McpTools.Profile, new() { ["configuration"] = "Compose: compose.yaml", ["seconds"] = 2, ["memory"] = true, ["top"] = 200 });
            Assert.True(profiled.GetProperty("samples").GetInt32() > 0, $"The profile of the compose configuration sampled nothing: {profiled.GetRawText()}");
            JsonElement spin = Assert.Single(profiled.GetProperty("hotSpots").EnumerateArray(), h => h.GetProperty("method").GetString()!.StartsWith("Hot.Spin", StringComparison.Ordinal));
            Assert.True(spin.TryGetProperty("source", out JsonElement source) && source.GetString()!.EndsWith($"Program.cs:{ContainerRunProfilingTests.SpinLine}", StringComparison.Ordinal),
                $"The service's hot method is not named at its line: {spin}");
            Assert.Contains(profiled.GetProperty("heap").EnumerateArray(), t => t.GetProperty("type").GetString() == "Keepsake");
            Assert.Equal("running", ContainerRunsTests.States(project)["api"]);
        }
        finally
        {
            await Call(client, McpTools.Stop);
            Assert.True(await Waiting.UntilAsync(() => Docker.NoneLeft(project), TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken), "stop left the configuration's containers behind");
            // The headless IDE's state is not the run's, whose end removes only its own images: these go here.
            foreach (string image in Docker.Run("images", "-q", "--filter", $"label=com.docker.compose.project={project}").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Docker.Run("rmi", "-f", image);
            }
        }
    }
}

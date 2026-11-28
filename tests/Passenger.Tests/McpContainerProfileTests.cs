using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's profile of a program in a container (attach with container): recorded there, its hot method named at
/// its line from the PDB in the container, the heap's types from a memory snapshot taken there, and an Avalonia program's
/// frames said to be none there. Skips, saying why, where no Linux engine runs; the tools are the run's one publish, made
/// before the class's first test (<see cref="ContainerRunDebuggingTests.Tools"/>).</summary>
[Collection(Alone.Name)]
public sealed class McpContainerProfileTests(ContainerRunDebuggingTests.Tools tools) : McpRig, IClassFixture<ContainerRunDebuggingTests.Tools>
{
    [Fact]
    public async Task An_agent_profiles_a_program_in_a_container_its_hot_method_at_its_line_and_its_heap()
    {
        ContainerProfilingTests.Spins spins = await ContainerProfilingTests.Spins.SharedAsync();
        Assert.SkipWhen(spins.WhyNot is not null, spins.WhyNot ?? string.Empty);
        string container = await spins.StartAsync("agent");
        try
        {
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
            Assert.Equal("running", await Docker.StateAsync(container));
        }
        finally
        {
            // Its program spins a core for as long as it runs, which every class after this one would share.
            await Docker.RemoveAsync(container);
        }
    }

    /// <summary>An agent's profile of an Avalonia program in a container, recorded from its start, says it has no frames and
    /// why, as the Frames page does: the trace names the modules of code compiled as the program ran in lower case
    /// (avalonia.base), which are all of Avalonia's.</summary>
    [Fact]
    public async Task An_agents_profile_of_an_avalonia_program_in_a_container_says_why_it_has_no_frames()
    {
        string? whyNot = await Docker.WhyNotAsync();
        Assert.SkipWhen(whyNot is not null, whyNot ?? string.Empty);
        string root = TestFolders.Unowned("drawn-");
        DeleteAtEnd(root);
        string folder = PathNames.Canonical(Path.Combine(root, "Drawn"));
        await ContainerPrograms.PublishAsync($"linux-{await ContainerPrograms.ProcessorAsync()}");
        await ContainerPrograms.BuildAsync(folder, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>Drawn</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Avalonia" Version="{AvaloniaApp.Version}" />
              </ItemGroup>
            </Project>

            """, """
            // Spins in Avalonia's own code, as an app laying out and drawing would, with no window to draw in.
            using Avalonia;
            using Avalonia.Media;

            double total = 0;
            while (true)
            {
                Color color = Color.Parse("#FF8800");
                Matrix turned = Matrix.CreateRotation(total) * Matrix.CreateScale(2, 3);
                total += color.R + turned.M11 + new Rect(0, 0, 10, 10).Union(new Rect(5, 5, 20, 20)).Width;
            }

            """, Path.Combine(root, "out"));
        string image = await ContainerPrograms.ImageAsync(Path.Combine(root, "image"), "drawn", "mcr.microsoft.com/dotnet/runtime:10.0", Path.Combine(root, "out"), "Drawn");
        string container = $"ptest{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}drawn";
        // Held at its start, so the profile records it from there, as a container run's Profile does.
        await Docker.RunAsync(["run", "-d", .. Docker.AnyPids, "--name", container, "--label", Docker.RunLabel, "-e", "DOTNET_DefaultDiagnosticPortSuspend=1", image]);
        try
        {
            await using McpClient client = await Start(["--mcp", folder], languageServer: false);
            JsonElement profiled = await Call(client, McpTools.Profile, new() { ["attach"] = true, ["container"] = container, ["seconds"] = 3 });
            Assert.True(profiled.GetProperty("hotSpots").EnumerateArray().Any(h => h.GetProperty("method").GetString()!.StartsWith("Avalonia.", StringComparison.Ordinal)),
                $"The profile of the program in the container sampled none of Avalonia's code: {profiled.GetRawText()}");
            Assert.True(profiled.TryGetProperty("frames", out JsonElement frames) && frames.GetProperty("why").GetString() == Passenger.App.Profiling.FramesView.InContainer(container),
                $"The profile of an Avalonia program in a container does not say why it has no frames: {profiled.GetRawText()}");
        }
        finally
        {
            await Docker.RemoveAsync(container);
        }
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
            Assert.Equal("running", (await ContainerRunsTests.StatesAsync(project))["api"]);
        }
        finally
        {
            await Call(client, McpTools.Stop);
            Assert.True(await Waiting.UntilAsync(() => Docker.NoneLeftAsync(project), TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken), "stop left the configuration's containers behind");
            // The headless IDE's state is not the run's, whose end removes only its own images: these go here.
            foreach (string image in (await Docker.RunAsync("images", "-q", "--filter", $"label=com.docker.compose.project={project}")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                await Docker.RunAsync("rmi", "-f", image);
            }
        }
    }
}

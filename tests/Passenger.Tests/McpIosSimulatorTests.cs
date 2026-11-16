using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run.Devices;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's run of an Avalonia app's iOS head on a simulator through its own Passenger (<c>--mcp</c>), the head
/// not the configuration chosen: run with no simulator chosen is refused, saying how an agent chooses one, and leaves the
/// chosen configuration as it was; ide_action app.chooseRunTarget naming the head's configuration and an iPhone simulator
/// chooses it in one call, still leaving the choice of configuration; run then answers once the app has started there,
/// and chooses the head as a person's Run does. Skips, saying why, off a Mac, without the iOS
/// workload, Xcode or an iPhone simulator, or nuget.org's Avalonia iOS packages. Alone, the simulator shared with
/// AvaloniaSimulatorTests; a simulator the run booted is shut down at its end.</summary>
[Collection(Alone.Name)]
public sealed class McpIosSimulatorTests : McpRig
{
    private const string Name = "XApp";

    [Fact]
    public async Task An_agent_chooses_an_ios_heads_simulator_and_runs_it_in_a_call_each_without_changing_the_chosen_configuration_and_a_refused_run_leaves_it_too()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone);
        }
        string folder = PathNames.Canonical(TestFolders.Create("mcp-avalonia-ios-"));
        DeleteAtEnd(folder);
        (string project, string? unrestored) = await AvaloniaApp.CreateIosHeadAsync(folder, Name);
        if (unrestored is not null)
        {
            Assert.Skip(unrestored);
        }
        (IReadOnlyList<DeviceTarget> targets, AppleTools? tools, string? problem) =
            await AppleSimulators.ListAsync(project, MauiApp.Ios, new Dictionary<string, string>(), Cancel);
        if (tools is null)
        {
            Assert.Skip($"There is nothing to run on: {problem}");
        }
        if (targets.Where(t => t is { Platform: TargetPlatform.Ios, Kind: TargetKind.Simulator } && t.Name.StartsWith("iPhone", StringComparison.Ordinal))
            .OrderBy(t => t.Running ? 0 : 1).FirstOrDefault() is not { } target)
        {
            Assert.Skip("The iOS workload lists no iPhone simulator: add one in Xcode, with its iOS simulator runtime");
            return;
        }
        string head = $"{Name}.iOS";
        try
        {
            await using McpClient client = await Start(["--mcp", folder], languageServer: false);
            await Waiting.UntilAsync(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == head),
                TimeSpan.FromMinutes(3), Cancel, TimeSpan.FromMilliseconds(500));
            async Task<string?> ChosenAsync() => (await Call(client, McpTools.Workspace)).GetProperty("chosenConfiguration").GetString();
            string? chosen = await ChosenAsync();
            Assert.True(chosen is not null && chosen != head, $"The folder opened on {chosen} chosen");

            CallToolResult refused = await Result(client, McpTools.Run, new() { ["configuration"] = head });
            Assert.True(refused.IsError == true && Text(refused).Contains($"{Refusals.NoRunTarget(head)} ({McpTools.IdeAction} {AppActions.ChooseRunTarget} with input \"{head}:\"",
                StringComparison.Ordinal), $"run of the head with no simulator chosen did not say how an agent chooses one: {Text(refused)}");
            Assert.Equal(chosen, await ChosenAsync());

            string chose = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseRunTarget, ["input"] = $"{head}:{target.Name}" }));
            Assert.StartsWith($"{head} runs on {target.Name}", chose, StringComparison.Ordinal);
            Assert.Equal(chosen, await ChosenAsync());

            // Longer than the build, the install and the start take: the answer comes at the start, not at the wait's end.
            JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = head, ["waitSeconds"] = 3600 });
            Assert.True(ran.GetProperty("stillRunning").GetBoolean() && ran.GetProperty("status").GetString()?.StartsWith($"Running {head}, ", StringComparison.Ordinal) == true,
                $"run did not answer once the app started on {target.Name}: {ran}");
            Assert.Equal(head, await ChosenAsync());
            await Call(client, McpTools.Stop);
        }
        finally
        {
            if (!target.Running)
            {
                // The agent's Passenger shuts down a simulator it booted as it ends; one left by a failure is shut down here.
                await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["shutdown", target.Id]), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
            }
        }
    }
}

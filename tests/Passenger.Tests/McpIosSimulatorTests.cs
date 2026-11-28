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
/// and chooses the head as a person's Run does; that Passenger killed with its tree, the app ends there too and the
/// simulator it booted shuts down. Skips, saying why, off a Mac, without the iOS workload, Xcode or an iPhone simulator
/// shut down, or nuget.org's Avalonia iOS packages. Alone, the simulator shared with AvaloniaSimulatorTests; a simulator
/// the run booted is shut down at its end.</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class McpIosSimulatorTests : McpRig
{
    private const string Name = "XApp";

    /// <summary>The agent's choice and run, then its Passenger killed as a test runner kills a hung host, every process of
    /// its tree first, the deepest first, then itself: the app there, a process of this Mac that an end of simctl's launch
    /// leaves running, still ends with it, and the simulator is shut down, as its watchdog, in no tree of Passenger's, ends
    /// what it took on (#1792).</summary>
    [Fact]
    public async Task An_agent_chooses_an_ios_heads_simulator_and_runs_it_leaving_the_chosen_configuration_and_the_app_and_simulator_end_once_its_passenger_and_tree_are_killed()
    {
        (string folder, DeviceTarget target, AppleTools tools) = await PreparedAsync("mcp-avalonia-ios-", shutDown: true);
        string head = $"{Name}.iOS";
        int? app = null;
        try
        {
            McpClient client = await Start(["--mcp", folder], languageServer: false);
            // Each program it takes on is in its log as it does.
            await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["logEveryProgram"] = true } });
            await Until(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == head),
                "the head's run configuration");
            async Task<string?> ChosenAsync() => (await Call(client, McpTools.Workspace)).GetProperty("chosenConfiguration").GetString();
            string? chosen = await ChosenAsync();
            Assert.True(chosen is not null && chosen != head, $"The folder opened on {chosen} chosen");

            CallToolResult refused = await Result(client, McpTools.Run, new() { ["configuration"] = head });
            Assert.True(refused.IsError == true && Text(refused).Contains($"{Refusals.NoRunTarget(head).English} ({McpTools.IdeAction} {AppActions.ChooseRunTarget} with input \"{head}:\"",
                StringComparison.Ordinal), $"run of the head with no simulator chosen did not say how an agent chooses one: {Text(refused)}");
            Assert.Equal(chosen, await ChosenAsync());

            string chose = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseRunTarget, ["input"] = $"{head}:{target.Name}" }));
            Assert.StartsWith($"{head} runs on {target.Name}", chose, StringComparison.Ordinal);
            Assert.Equal(chosen, await ChosenAsync());

            // Longer than the build, the boot, the install and the start take: the answer comes at the start, not at the wait's end.
            JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = head, ["waitSeconds"] = 3600 });
            string status = ran.GetProperty("status").GetString() ?? string.Empty;
            Assert.True(ran.GetProperty("stillRunning").GetBoolean() && status.StartsWith($"Running {head}, ", StringComparison.Ordinal),
                $"run did not answer once the app started on {target.Name}: {ran}");
            Assert.Equal(head, await ChosenAsync());
            const string Said = " as process ";
            int at = status.LastIndexOf(Said, StringComparison.Ordinal);
            int launched = at >= 0 && int.TryParse(status.AsSpan(at + Said.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out int said) ? said : 0;
            Assert.True(launched > 0, $"run did not say the app's process on {target.Name}: {ran}");
            app = launched;
            string log = Path.Combine(OwnState, "logs", "passenger.log");
            await Until(() => File.Exists(log) && Ui.Shared(log).Contains($"Process {launched} started", StringComparison.Ordinal),
                "Passenger to take the app on, as its log says");

            System.Diagnostics.Process passenger = LastPassenger();
            int[] killed = [.. Processes.Descendants(passenger.Id).Reverse(), passenger.Id];
            foreach (int id in killed)
            {
                OwnProcess.Of(id).Kill(alone: true);
            }
            await Until(() => killed.All(id => !Processes.IsRunning(id)), "Passenger and every process of its tree killed");
            await Until(() => !Processes.IsRunning(launched), $"the app (process {launched}) to end on {target.Name} with the Passenger that ran it");
            await Until(async () => !await UpAsync(target, tools), $"{target.Name}, which the killed Passenger booted, to be shut down");
        }
        finally
        {
            if (app is int left && Libc.ExecutableOf(left) is { } program && program.Contains($"/Devices/{target.Id}/", StringComparison.Ordinal))
            {
                OwnProcess.Of(left).Kill(alone: true);
            }
            await ShutDownAsync(target, tools);
        }
    }

    /// <summary>The Avalonia app's iOS head in a folder of its own, restored, and the iPhone simulator to run it on, booted
    /// already where one is (shut down, where <paramref name="shutDown"/>, for the start to boot); a skip saying why where
    /// there is none.</summary>
    private async Task<(string Folder, DeviceTarget Target, AppleTools Tools)> PreparedAsync(string name, bool shutDown = false)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone.English);
        }
        string folder = PathNames.Canonical(TestFolders.Create(name));
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
        DeviceTarget? target = targets.Where(t => t is { Platform: TargetPlatform.Ios, Kind: TargetKind.Simulator } && t.Name.StartsWith("iPhone", StringComparison.Ordinal))
            .Where(t => !shutDown || !t.Running).OrderBy(t => t.Running ? 0 : 1).FirstOrDefault();
        if (target is null)
        {
            Assert.Skip(shutDown ? "The iOS workload lists no iPhone simulator shut down for the start to boot: every one is running"
                : "The iOS workload lists no iPhone simulator: add one in Xcode, with its iOS simulator runtime");
        }
        return (folder, target, tools);
    }

    /// <summary>Whether simctl lists the simulator booted.</summary>
    private static async Task<bool> UpAsync(DeviceTarget target, AppleTools tools)
    {
        ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["list", "devices"]), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
        return listed.Output.Split('\n').Any(line => line.Contains(target.Id, StringComparison.Ordinal) && line.Contains("(Booted)", StringComparison.Ordinal));
    }

    /// <summary>The simulator shut down where the test's Passenger booted it: as it ends it shuts down a simulator it booted,
    /// but one a failure or a kill left is shut down here.</summary>
    private static async Task ShutDownAsync(DeviceTarget target, AppleTools tools)
    {
        if (!target.Running)
        {
            await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["shutdown", target.Id]), limit: TimeSpan.FromMinutes(1), cancel: Cancel);
        }
    }
}

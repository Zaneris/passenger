using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>An agent's run and debug of an Android app (tests/AndroidApp) on an emulator through its own Passenger
/// (<c>--mcp</c>): ide_action app.chooseRunTarget chooses the emulator, and run, or debug_start once the session runs
/// there, answers once the app has started, naming the emulator and the app's process, rather than waiting out its
/// whole wait; stop ends it. An agent that goes the moment stop answers a debugged app's leaves nothing of Passenger's on
/// the emulator: the Passenger waits for the app's end there before it goes. Skips, saying why, without the Android
/// workload, its SDK and Java, or an emulator (and the relays, for the debugged app). Alone, as AndroidDeviceTests is, the
/// emulator being one for both.</summary>
[Collection(Alone.Name)]
public sealed class McpAndroidDeviceTests : McpRig
{
    private const string Package = "net.passengeride.tests.androidapp";

    [Fact]
    public async Task An_agents_run_of_an_android_app_on_an_emulator_answers_once_it_has_started_there_naming_the_emulator()
    {
        if (await ChosenAsync() is not ({ } client, { } target, { } tools))
        {
            return;
        }
        await using (client)
        {
            // Longer than the build, the install and the start take: the answer comes at the start, not at the wait's end.
            JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = "AndroidApp", ["waitSeconds"] = 3600 });
            string status = ran.GetProperty("status").GetString() ?? string.Empty;
            Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"run said the app ended on the emulator: {ran}");
            Assert.True(status.StartsWith($"Running AndroidApp, {Package} started on {target.Name} as process ", StringComparison.Ordinal),
                $"run did not answer once the app started on {target.Name}, naming it and the app's process: {ran}");
            Assert.True(int.TryParse(status[status.LastIndexOf(' ')..], out _), $"run named no process of the app's: {status}");

            await Call(client, McpTools.Stop);
            await EndedAsync(target, tools);
        }
    }

    [Fact]
    public async Task An_agents_debug_start_of_an_android_app_on_an_emulator_answers_once_its_session_runs_there_naming_the_emulator()
    {
        if (await ChosenAsync() is not ({ } client, { } target, { } tools))
        {
            return;
        }
        await using (client)
        {
            // Longer than the build, the install, the relay and the attach take: the answer comes once the app runs debugged.
            JsonElement debugged = await Call(client, McpTools.DebugStart, new() { ["configuration"] = "AndroidApp", ["waitSeconds"] = 3600 });
            string status = debugged.GetProperty("status").GetString() ?? string.Empty;
            Assert.True(debugged.GetProperty("state").GetString() == "running", $"debug_start did not leave the app running debugged on the emulator: {debugged}");
            Assert.True(status.Contains($", {Package} started on {target.Name} as process ", StringComparison.Ordinal),
                $"debug_start did not answer once the app started on {target.Name} debugged, naming it and the app's process: {debugged}");
            Assert.True(int.TryParse(status[status.LastIndexOf(' ')..], out _), $"debug_start named no process of the app's: {status}");

            await Call(client, McpTools.Stop);
            await EndedAsync(target, tools);
        }
    }

    /// <summary>An agent's own Passenger over a copy of the Android test app, the emulator chosen for its configuration
    /// with ide_action app.chooseRunTarget; skipped, saying why, where there is nothing to run it on.</summary>
    private async Task<(McpClient? Client, DeviceTarget? Target, AndroidTools? Tools)> ChosenAsync()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-android-"));
        DeleteAtEnd(folder);
        string source = Path.Combine(Debuggees.Root, "tests", "AndroidApp");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(source, f).StartsWith("bin", StringComparison.Ordinal)
            && !Path.GetRelativePath(source, f).StartsWith("obj", StringComparison.Ordinal)))
        {
            string copy = Path.Combine(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        string project = Path.Combine(folder, "AndroidApp.csproj");
        (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? problem) =
            await AndroidDevices.ListAsync(project, "net11.0-android", new Dictionary<string, string>(), Cancel);
        if (tools is null)
        {
            Assert.Skip($"There is nothing to run on: {problem}");
        }
        if (tools.Lacks is { } lacks)
        {
            Assert.Skip($"No Android app can be built here: {Refusals.NoAndroidPlatform(lacks)}");
        }
        if ((targets.FirstOrDefault(t => t.Running) ?? targets.FirstOrDefault(t => t.Kind == TargetKind.Emulator)) is not { } target)
        {
            Assert.Skip("No Android device is connected and no emulator is made (an AVD of an installed system image), so there is nothing to run on");
            return (null, null, null);
        }
        McpClient client = await Start(["--mcp", folder], languageServer: false);
        await Waiting.UntilAsync(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == "AndroidApp"),
            TimeSpan.FromMinutes(3), Cancel, TimeSpan.FromMilliseconds(500));
        string chose = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseRunTarget, ["input"] = target.Name }));
        Assert.Equal($"AndroidApp runs on {target.Name}", chose);
        return (client, target, tools);
    }

    /// <summary>Waits for the app to have ended on the emulator: its serial once it ran, else the first an emulator started takes.</summary>
    private async Task EndedAsync(DeviceTarget target, AndroidTools tools)
    {
        string serial = target.Running ? target.Id : "emulator-5554";
        await Waiting.UntilAsync(async () => (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", Package]))).Output.Trim().Length == 0,
            TimeSpan.FromSeconds(30), Cancel, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task An_agent_that_goes_as_stop_answers_a_debugged_app_leaves_no_port_nor_relay_on_the_emulator()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-android-"));
        DeleteAtEnd(folder);
        string source = Path.Combine(Debuggees.Root, "tests", "AndroidApp");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(source, f).StartsWith("bin", StringComparison.Ordinal)
            && !Path.GetRelativePath(source, f).StartsWith("obj", StringComparison.Ordinal)))
        {
            string copy = Path.Combine(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? problem) =
            await AndroidDevices.ListAsync(Path.Combine(folder, "AndroidApp.csproj"), "net11.0-android", new Dictionary<string, string>(), Cancel);
        if (tools is null)
        {
            Assert.Skip($"There is nothing to run on: {problem}");
        }
        if (tools.Lacks is { } lacks)
        {
            Assert.Skip($"No Android app can be built here: {Refusals.NoAndroidPlatform(lacks)}");
        }
        if ((targets.FirstOrDefault(t => t.Running) ?? targets.FirstOrDefault(t => t.Kind == TargetKind.Emulator)) is not { } target)
        {
            Assert.Skip("No Android device is connected and no emulator is made (an AVD of an installed system image), so there is nothing to run on");
            return;
        }
        if (!((string[])["android-arm64", "android-x64"]).Any(runtime => File.Exists(Path.Combine(AppContext.BaseDirectory, "debugger", "relay", runtime, AndroidApp.RelayFile))))
        {
            Assert.Skip("This build carries no Android debug relay, which `just debug-relay android` makes before a build carries it");
        }
        System.Diagnostics.Process passenger = StartPassenger(["--mcp", folder], languageServer: false);
        McpClient client = await Connect(passenger, ["--mcp", folder]);
        await Waiting.UntilAsync(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == "AndroidApp"),
            TimeSpan.FromMinutes(3), Cancel, TimeSpan.FromMilliseconds(500));
        await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseRunTarget, ["input"] = target.Name });
        JsonElement started = await Call(client, McpTools.DebugStart, new() { ["configuration"] = "AndroidApp", ["waitSeconds"] = 5 });
        Assert.True((await LeftAsync(tools)).Length > 0, $"debug_start put nothing of Passenger's on the emulator: {started}");

        await Call(client, McpTools.Stop);
        await client.DisposeAsync();
        Assert.True(await Waiting.Within(passenger.WaitForExitAsync(Cancel), TimeSpan.FromMinutes(1), Cancel), "Passenger did not end once the agent went");
        string left = await LeftAsync(tools);
        Assert.True(left.Length == 0, $"Passenger went leaving on the emulator {left}: the next start finds them, and the app runs on marked as debugged");
    }

    /// <summary>What a Debug start leaves on the device: a port forwarded to a relay's or an inspector's socket, the relay in
    /// the app's folder, the app marked as debugged; empty once its end took them away.</summary>
    private static async Task<string> LeftAsync(AndroidTools tools)
    {
        string forwards = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["forward", "--list"]))).Output;
        string relay = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb,
            ["shell", $"run-as {Package} ls files/.__override__/arm64-v8a files/.__override__/x86_64 2>/dev/null"]))).Output;
        string debugged = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["shell", "dumpsys activity processes | grep mDebugApp="]))).Output;
        return string.Join("; ", [.. forwards.Split('\n').Where(l => l.Contains("localabstract:passenger-", StringComparison.Ordinal)).Select(l => l.Trim()),
            .. relay.Contains(AndroidApp.RelayFile, StringComparison.Ordinal) ? ["the relay in the app"] : (string[])[],
            .. debugged.Contains($"mDebugApp={Package}/", StringComparison.Ordinal) ? [$"{Package} marked as debugged"] : (string[])[]]);
    }
}

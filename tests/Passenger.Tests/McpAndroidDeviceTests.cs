using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>An agent's run of an Android app (tests/AndroidApp) on an emulator through its own Passenger (<c>--mcp</c>):
/// ide_action app.chooseRunTarget chooses the emulator, and run answers once the app has started there, naming the
/// emulator and the app's process, rather than waiting out its whole wait; stop ends it. Skips, saying why, without the
/// Android workload, its SDK and Java, or an emulator. Alone, as AndroidDeviceTests is, the emulator being one for both.</summary>
[Collection(Alone.Name)]
public sealed class McpAndroidDeviceTests : McpRig
{
    private const string Package = "net.passengeride.tests.androidapp";

    [Fact]
    public async Task An_agents_run_of_an_android_app_on_an_emulator_answers_once_it_has_started_there_naming_the_emulator()
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
            return;
        }
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        await Waiting.UntilAsync(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == "AndroidApp"),
            TimeSpan.FromMinutes(3), Cancel, TimeSpan.FromMilliseconds(500));
        string chose = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseRunTarget, ["input"] = target.Name }));
        Assert.Equal($"AndroidApp runs on {target.Name}", chose);

        // Longer than the build, the install and the start take: the answer comes at the start, not at the wait's end.
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = "AndroidApp", ["waitSeconds"] = 3600 });
        string status = ran.GetProperty("status").GetString() ?? string.Empty;
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"run said the app ended on the emulator: {ran}");
        Assert.True(status.StartsWith($"Running AndroidApp, {Package} started on {target.Name} as process ", StringComparison.Ordinal),
            $"run did not answer once the app started on {target.Name}, naming it and the app's process: {ran}");
        Assert.True(int.TryParse(status[status.LastIndexOf(' ')..], out _), $"run named no process of the app's: {status}");

        await Call(client, McpTools.Stop);
        // The emulator's serial once it ran, else the first an emulator started takes.
        string serial = target.Running ? target.Id : "emulator-5554";
        await Waiting.UntilAsync(async () => (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", Package]))).Output.Trim().Length == 0,
            TimeSpan.FromSeconds(30), Cancel, TimeSpan.FromMilliseconds(500));
    }
}

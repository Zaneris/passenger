using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>An agent's run and debug of an Android app (tests/AndroidApp) on an emulator through its own Passenger
/// (<c>--mcp</c>): ide_action app.chooseRunTarget chooses the emulator, and run, or debug_start once the session runs
/// there, answers once the app has started, naming the emulator and the app's process; stop answers once the app has
/// ended there. An agent that goes as stop answers a debugged app's leaves nothing of Passenger's on the emulator. An
/// Avalonia app's Android head shows its view root as its one window to app_windows asked as run answers; the MAUI
/// template's Android head lists its main page's label and button to app_tree, its picture to app_screenshot, and
/// app_click taps the button. Every adb call names the emulator, ANDROID_SERIAL's where several run. Skips, saying why,
/// without the Android workload, its SDK and Java, or an emulator (and the relays, for the debugged app; Avalonia's
/// Android packages, or MAUI's workload, for their apps). Alone, as AndroidDeviceTests is, the emulator one for both.</summary>
[Collection(Alone.Name)]
public sealed class McpAndroidDeviceTests : McpRig
{
    private const string Package = "net.passengeride.tests.androidapp";

    [Fact]
    public async Task An_agents_run_of_an_android_app_on_an_emulator_answers_once_it_has_started_there_naming_the_emulator_and_stop_once_it_has_ended()
    {
        if (await ChosenAsync() is not ({ } client, { } target, { } tools, { } project))
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

            string serial = await SerialAsync(target, project);
            await Call(client, McpTools.Stop);
            await EndedAsync(serial, tools);
        }
    }

    [Fact]
    public async Task An_agents_debug_start_of_an_android_app_on_an_emulator_answers_once_its_session_runs_there_naming_the_emulator_and_stop_once_it_has_ended()
    {
        if (await ChosenAsync() is not ({ } client, { } target, { } tools, { } project))
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

            string serial = await SerialAsync(target, project);
            await Call(client, McpTools.Stop);
            await EndedAsync(serial, tools);
        }
    }

    /// <summary>An agent's own Passenger over a copy of the Android test app, the emulator chosen for its configuration
    /// with ide_action app.chooseRunTarget, and the app's project; skipped, saying why, where there is nothing to run it on.</summary>
    private async Task<(McpClient? Client, DeviceTarget? Target, AndroidTools? Tools, string? Project)> ChosenAsync()
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
        (DeviceTarget target, AndroidTools tools) = await TargetAsync(project);
        McpClient client = await Start(["--mcp", folder], languageServer: false);
        await ChooseAsync(client, "AndroidApp", target);
        return (client, target, tools, project);
    }

    /// <summary>The device or emulator a project of the folder runs on, as the Android workload lists them, and its tools;
    /// skipped, saying why, where there is nothing to run it on.</summary>
    private static async Task<(DeviceTarget Target, AndroidTools Tools)> TargetAsync(string project)
    {
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
        DeviceTarget? target = MauiApp.Target(targets);
        if (target is null)
        {
            Assert.Skip("No Android device is connected and no emulator is made (an AVD of an installed system image), so there is nothing to run on");
        }
        return (target, tools);
    }

    /// <summary>The agent's Passenger, once it lists the configuration, has it run on the target, through ide_action app.chooseRunTarget.</summary>
    private async Task ChooseAsync(McpClient client, string configuration, DeviceTarget target)
    {
        await Waiting.UntilAsync(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == configuration),
            TimeSpan.FromMinutes(3), Cancel, TimeSpan.FromMilliseconds(500));
        string chose = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ChooseRunTarget, ["input"] = target.Name }));
        Assert.Equal($"{configuration} runs on {target.Name}", chose);
    }

    /// <summary>The serial of the emulator a test runs on: the target's own where it ran when listed, else the one its AVD
    /// runs as once the start booted it.</summary>
    private static async Task<string> SerialAsync(DeviceTarget target, string project)
    {
        if (target.Running)
        {
            return target.Id;
        }
        string? serial = null;
        await Waiting.UntilAsync(async () => (serial = (await AndroidDevices.ListAsync(project, "net11.0-android", new Dictionary<string, string>(), Cancel)).Targets
            .FirstOrDefault(t => t.Running && t.Avd == target.Avd)?.Id) is not null, TimeSpan.FromMinutes(3), Cancel, TimeSpan.FromSeconds(2));
        return serial ?? throw new InvalidOperationException($"{target.Name} was not booted as an emulator the workload lists");
    }

    /// <summary>That the app has ended on the emulator of <paramref name="serial"/> as stop answers: an agent goes on from there.</summary>
    private static async Task EndedAsync(string serial, AndroidTools tools, string package = Package)
    {
        string running = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", package]))).Output.Trim();
        Assert.True(running.Length == 0, $"stop answered while the app still ran on the emulator as {running}, so an agent's next start there may find it");
    }

    [Fact]
    public async Task An_agents_app_windows_on_an_avalonia_app_it_ran_on_an_emulator_names_the_apps_view_root_as_its_window()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-avalonia-android-"));
        DeleteAtEnd(folder);
        (string project, string? unrestored) = await AvaloniaApp.CreateAndroidHeadAsync(folder, "XApp");
        if (unrestored is not null)
        {
            Assert.Skip(unrestored);
        }
        (DeviceTarget target, AndroidTools tools) = await TargetAsync(project);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        await ChooseAsync(client, "XApp.Android", target);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = "XApp.Android", ["waitSeconds"] = 3600 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"run said the app ended on the emulator: {ran}");

        // Asked the moment run answers, as the app has started on the device and before its activity has made its view.
        JsonElement windows = await Call(client, McpTools.AppWindows);
        List<JsonElement> listed = [.. windows.GetProperty("windows").EnumerateArray()];
        Assert.True(listed is [{ } only] && only.GetProperty("type").GetString() == "EmbeddableControlRoot" && only.GetProperty("visible").GetBoolean(),
            $"app_windows did not name the app's view root as its one window, so an agent finds nothing to look at or click: {windows}");
        string id = listed[0].GetProperty("id").GetString()!;
        string tree = Text(await Call(client, McpTools.AppTree, new() { ["window"] = id, ["depth"] = 2 }));
        Assert.True(tree.StartsWith($"EmbeddableControlRoot {id} ", StringComparison.Ordinal) && tree.Contains("MainView", StringComparison.Ordinal),
            $"The window app_windows named is not the one app_tree shows the app's view in: {tree}");

        string serial = await SerialAsync(target, project);
        await Call(client, McpTools.Stop);
        await EndedAsync(serial, tools, "com.CompanyName.XApp");
    }

    [Fact]
    public async Task An_agents_app_tree_on_a_maui_app_it_ran_on_an_emulator_lists_its_main_pages_label_and_button_which_app_click_taps()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-maui-android-"));
        DeleteAtEnd(folder);
        (string project, string? unmade) = await MauiApp.CreateAndroidHeadAsync(folder);
        if (unmade is not null)
        {
            Assert.Skip(unmade);
        }
        (DeviceTarget target, AndroidTools tools) = await TargetAsync(project);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        await ChooseAsync(client, MauiApp.Name, target);
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = MauiApp.Name, ["waitSeconds"] = 3600 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"run said the app ended on the emulator: {ran}");

        JsonElement windows = await Call(client, McpTools.AppWindows);
        Assert.True(windows.GetProperty("framework").GetString()?.StartsWith("MAUI 11.", StringComparison.Ordinal) == true
            && windows.GetProperty("windows").EnumerateArray().SingleOrDefault() is { ValueKind: JsonValueKind.Object } only && only.GetProperty("type").GetString() == "Window",
            $"app_windows did not name the MAUI app's one window: {windows}");
        string tree = Text(await Call(client, McpTools.AppTree));
        string[] lines = tree.Split('\n');
        Assert.True(lines.Any(l => l.TrimStart().StartsWith("MainPage @", StringComparison.Ordinal))
            && lines.Any(l => l.TrimStart().StartsWith("Label \"Hello, World!\" @", StringComparison.Ordinal))
            && lines.Any(l => l.TrimStart().StartsWith("Button#CounterBtn \"Click me\" @", StringComparison.Ordinal)),
            $"app_tree did not list the main page with its label and its button, by its x:Name and text, so an agent finds nothing to click:\n{tree}");

        CallToolResult picture = await Result(client, McpTools.AppScreenshot);
        Assert.True(picture.IsError != true, $"The app's picture was refused: {Text(picture)}");
        ImageContentBlock image = Assert.Single(picture.Content.OfType<ImageContentBlock>());
        using (SkiaSharp.SKBitmap frame = SkiaSharp.SKBitmap.Decode(image.DecodedData.ToArray()))
        {
            Assert.True(frame is { Width: > 200, Height: > 400 }, $"app_screenshot's picture of the app's window is {frame?.Width}x{frame?.Height}: {Text(picture)}");
        }

        string clicked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#CounterBtn" }));
        Assert.True(clicked.StartsWith("Clicked Button#CounterBtn @", StringComparison.Ordinal) && clicked.Contains("it shows \"Clicked 1 time\"", StringComparison.Ordinal),
            $"app_click did not tap the button so its click counted: {clicked}");
        string counted = Text(await Call(client, McpTools.AppTree, new() { ["filter"] = "#CounterBtn" }));
        Assert.True(counted.StartsWith("Button#CounterBtn \"Clicked 1 time\" @", StringComparison.Ordinal), $"The button does not show its click in app_tree: {counted}");

        string serial = await SerialAsync(target, project);
        await Call(client, McpTools.Stop);
        await EndedAsync(serial, tools, MauiApp.Package);
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
        string project = Path.Combine(folder, "AndroidApp.csproj");
        (DeviceTarget target, AndroidTools tools) = await TargetAsync(project);
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
        string serial = await SerialAsync(target, project);
        Assert.True((await LeftAsync(tools, serial)).Length > 0, $"debug_start put nothing of Passenger's on the emulator: {started}");

        await Call(client, McpTools.Stop);
        await client.DisposeAsync();
        Assert.True(await Waiting.Within(passenger.WaitForExitAsync(Cancel), TimeSpan.FromMinutes(1), Cancel), "Passenger did not end once the agent went");
        string left = await LeftAsync(tools, serial);
        Assert.True(left.Length == 0, $"Passenger went leaving on the emulator {left}: the next start finds them, and the app runs on marked as debugged");
    }

    /// <summary>What a Debug start leaves on the emulator of <paramref name="serial"/>: a port forwarded to a relay's or an
    /// inspector's socket, a socket reversed to Passenger, the relay in the app's folder, the app marked as debugged; empty
    /// once its end took them away.</summary>
    private static async Task<string> LeftAsync(AndroidTools tools, string serial)
    {
        string forwards = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "forward", "--list"]))).Output;
        string reverses = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "reverse", "--list"]))).Output;
        string relay = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb,
            ["-s", serial, "shell", $"run-as {Package} ls files/.__override__/arm64-v8a files/.__override__/x86_64 2>/dev/null"]))).Output;
        string debugged = (await ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "dumpsys activity processes | grep mDebugApp="]))).Output;
        return string.Join("; ", [.. forwards.Split('\n').Where(l => l.StartsWith(serial + " ", StringComparison.Ordinal) && l.Contains("localabstract:passenger-", StringComparison.Ordinal))
                .Select(l => l.Trim()),
            .. reverses.Split('\n').Where(l => l.Contains("localabstract:passenger-", StringComparison.Ordinal)).Select(l => l.Trim()),
            .. relay.Contains(AndroidApp.RelayFile, StringComparison.Ordinal) ? ["the relay in the app"] : (string[])[],
            .. debugged.Contains($"mDebugApp={Package}/", StringComparison.Ordinal) ? [$"{Package} marked as debugged"] : (string[])[]]);
    }
}

using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Mobile;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Avalonia's cross-platform solution (<c>dotnet new avalonia.xplat</c>) with its Android head taken to the first
/// .NET whose Android apps run on CoreCLR, run and debugged on an emulator from the window: the head is chosen to run
/// there, Run installs and starts it, and Debug stops at a breakpoint in the shared project's App, its locals shown, the
/// shared library still built for its own framework. Skips, saying why, without the Android workload, its SDK and Java,
/// Avalonia's Android packages in the package cache, an emulator, or the relays. Debugged through the window, so alone.</summary>
[Collection(Alone.Name)]
public sealed class AvaloniaAndroidTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(6);

    private const string Package = "com.CompanyName.XApp";

    private static readonly string Android = $"net{MobilePlatforms.FirstOnCoreClr}.0-android";

    [AvaloniaFact]
    public void An_avalonia_apps_android_head_runs_and_stops_at_a_breakpoint_in_its_shared_code_on_an_emulator()
    {
        string folder = PathNames.Canonical(TestFolders.Create("avalonia-android"));
        Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateCrossPlatformAsync(folder, "XApp")), "dotnet new avalonia.xplat", Slow);
        string project = Path.Combine(folder, "XApp.Android", "XApp.Android.csproj");
        // The template's head is .NET 10's, on Mono; on CoreCLR from .NET 11, whose Android workload takes API 24 at least.
        File.WriteAllText(project, File.ReadAllText(project).Replace("net10.0-android", Android, StringComparison.Ordinal)
            .Replace("<SupportedOSPlatformVersion>23<", "<SupportedOSPlatformVersion>24<", StringComparison.Ordinal));
        ChildResult restored = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], Path.GetDirectoryName(project)!),
            limit: Slow)), "the Android head's restore", Slow);
        if (!restored.Succeeded)
        {
            Assert.Skip($"The app's Android head does not restore here, from the package cache alone (Avalonia.Android {AvaloniaApp.Version} and its AndroidX packages "
                + $"are there once an Avalonia Android app was restored): {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
        }
        (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? problem) = Ui.Await(Waiting.Apart(() =>
            AndroidDevices.ListAsync(project, Android, new Dictionary<string, string>(), CancellationToken.None)), "the Android workload's devices", Slow);
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
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "XApp.Android"), () => $"XApp.Android's run configuration; the window has configurations "
                + $"{string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
            Assert.True(window.SelectConfiguration("XApp.Android"));
            ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
            window.ChooseRunTarget(model, target);
            Ui.Settle();

            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the emulator", Slow);
            Ui.WaitFor(() => Output(window).Contains($"{Package} started as process ", StringComparison.Ordinal),
                () => $"the app's start in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
            Ui.WaitFor(() => Pid(tools, window).Length > 0, "the app to run on the emulator", TimeSpan.FromSeconds(30));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => Pid(tools, window).Length == 0, () => $"the app to end on the emulator once stopped; it runs as {Pid(tools, window)}", TimeSpan.FromSeconds(30));

            string app = Path.Combine(folder, "XApp", "App.axaml.cs");
            window.OpenFile(app);
            Ui.Settle();
            string[] lines = File.ReadAllLines(app);
            int line = Array.FindIndex(lines, l => l.Contains("if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)", StringComparison.Ordinal));
            Assert.True(line >= 0, $"The template's App no longer asks for the desktop's lifetime:\n{string.Join('\n', lines)}");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(line, lines[line].Length - lines[line].TrimStart().Length));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start on the emulator", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is not null,
                () => $"the stop at the breakpoint in the shared project's App with its locals; the debugger is "
                    + $"{window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}\n{Console(window)}", Slow);
            Assert.Equal("null", Shown(window, "desktop"));
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "singleViewFactoryApplicationLifetime") is not null,
                () => $"the step past the desktop's lifetime to the next branch; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: "
                    + Ui.Said(window), Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Pid(tools, window).Length == 0, () => $"the debugged app to end on the emulator once stopped; it runs as {Pid(tools, window)}", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;

    /// <summary>The app's process on the device the window chose, as pidof says it: empty once it has ended.</summary>
    private static string Pid(AndroidTools tools, MainWindow window)
    {
        string serial = window.Session.RunPlan?.Device?.App?.Serial ?? "emulator-5554";
        return Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", Package]))), "pidof on the device")
            .Output.Trim();
    }
}

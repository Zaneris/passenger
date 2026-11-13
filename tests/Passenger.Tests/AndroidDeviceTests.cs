using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>An Android app (tests/AndroidApp) run and debugged on an emulator from the window: chosen beside the run
/// configuration named for the app (its launch settings' only profile is the MAUI template's Windows one), Run installs
/// and starts it there, its log in the Run window, and Stop ends it on the emulator; Debug stops at a breakpoint in its
/// OnCreate through Passenger's relay in the app, its locals shown, and Stop ends it. Skips, saying why, without the
/// Android workload, its SDK and Java, or an emulator. Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class AndroidDeviceTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(6);

    private const string Package = "net.passengeride.tests.androidapp";

    [AvaloniaFact]
    public void An_android_app_runs_and_stops_at_a_breakpoint_on_an_emulator_chosen_beside_its_configuration()
    {
        string folder = TestFolders.Create("android");
        string source = Path.Combine(Debuggees.Root, "tests", "AndroidApp");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(source, f).StartsWith("bin", StringComparison.Ordinal)
            && !Path.GetRelativePath(source, f).StartsWith("obj", StringComparison.Ordinal)))
        {
            string copy = Path.Combine(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        string project = PathNames.Canonical(Path.Combine(folder, "AndroidApp.csproj"));
        (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? problem) = Ui.Await(Waiting.Apart(() =>
            AndroidDevices.ListAsync(project, "net11.0-android", new Dictionary<string, string>(), CancellationToken.None)), "the Android workload's devices", Slow);
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
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "AndroidApp"), () => $"AndroidApp's run configuration; the window has {Projects(window)}, "
                + $"configurations {string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
            Assert.True(window.SelectConfiguration("AndroidApp"));
            ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
            window.ChooseRunTarget(model, target);
            Ui.Settle();
            Assert.True(window.Session.Targets.Of(model)?.Key == target.Key, $"{target.Name} was not kept as where AndroidApp runs");

            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the emulator", Slow);
            Ui.WaitFor(() => Output(window).Contains("Hello from Passenger's test app, 42", StringComparison.Ordinal),
                () => $"the app's log in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => Pid(tools, window).Length == 0, () => $"the app to end on the emulator once stopped; it runs as {Pid(tools, window)}", TimeSpan.FromSeconds(30));

            window.OpenFile(Path.Combine(PathNames.Canonical(folder), "MainActivity.cs"));
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(10, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start on the emulator", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "answer") is not null,
                () => $"the stop at the breakpoint in OnCreate with its locals; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}\n"
                    + Console(window), Slow);
            Assert.Equal("42", Shown(window, "answer"));
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "greeting") is { } said && said.Contains("42", StringComparison.Ordinal),
                () => $"the step over the greeting's line, its value shown; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}, greeting is "
                    + $"{Shown(window, "greeting") ?? "not shown"}: {Ui.Said(window)}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Pid(tools, window).Length == 0, () => $"the debugged app to end on the emulator once stopped; it runs as {Pid(tools, window)}", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => Left(tools).Length == 0, () => $"the debugging's port and debugged app to be let go on the emulator; it still has {Left(tools)}", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>The window's projects as it evaluated them: each with its output type, else why it could not be evaluated.</summary>
    private static string Projects(MainWindow window) => window.Session.Projects.Count == 0 ? "no projects"
        : string.Join("; ", window.Session.Projects.Select(p => $"{p.Name} ({(p.IsEvaluated ? $"output {p.OutputType}" : p.Problem ?? "not evaluated yet")})"));

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;

    /// <summary>What a Debug start left on the device: a port forwarded to a session's relay, the app marked as debugged;
    /// empty once its end took both away.</summary>
    private static string Left(AndroidTools tools)
    {
        string forwards = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["forward", "--list"]))), "adb's forwards").Output;
        string debugged = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["shell", "dumpsys activity processes | grep mDebugApp="]))),
            "the device's debugged app").Output;
        return string.Join("; ", [.. forwards.Split('\n').Where(l => l.Contains("localabstract:passenger-debug-", StringComparison.Ordinal)).Select(l => l.Trim()),
            .. debugged.Contains($"mDebugApp={Package}/", StringComparison.Ordinal) ? [$"{Package} marked as debugged"] : (string[])[]]);
    }

    /// <summary>The app's process on the device the window chose, as pidof says it: empty once it has ended.</summary>
    private static string Pid(AndroidTools tools, MainWindow window)
    {
        string serial = window.Session.RunPlan?.Device?.App?.Serial ?? "emulator-5554";
        return Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", Package]))), "pidof on the device")
            .Output.Trim();
    }
}

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
/// OnCreate through Passenger's relay in the app, its locals shown, and Stop ends it; Run and Debug with Hot Reload take
/// an edit of its code through the relay, its update handler called after it, and under Debug a method the edit added is
/// evaluated. The emulator is the one ANDROID_SERIAL names where several run, and every adb call names it. Skips, saying
/// why, without the Android workload, its SDK and Java, an emulator or the relays. Debugged through the window, so run
/// alone.</summary>
[Collection(Alone.Name)]
public sealed class AndroidDeviceTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(6);

    private const string Package = "net.passengeride.tests.androidapp";

    [AvaloniaFact]
    public void An_android_app_runs_and_stops_at_a_breakpoint_on_an_emulator_chosen_beside_its_configuration()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the emulator", Slow);
            Ui.WaitFor(() => Output(window).Contains("Hello from Passenger's test app, 42", StringComparison.Ordinal),
                () => $"the app's log in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            string serial = AndroidSerials.Of(window, target);
            Ui.WaitFor(() => Pid(tools, serial).Length == 0, () => $"the app to end on the emulator once stopped; it runs as {Pid(tools, serial)}", TimeSpan.FromSeconds(30));

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
            Ui.WaitFor(() => Pid(tools, serial).Length == 0, () => $"the debugged app to end on the emulator once stopped; it runs as {Pid(tools, serial)}", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"the debugging's port and debugged app to be let go on the emulator; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Run with Hot Reload on the emulator puts Passenger's relay in the app and attaches to it quietly as its start
    /// waits there, so an edit of its code applied as it runs lands on the emulator: what it logs each second after is the
    /// edit's, in the Run window, with no debugging session shown, and its update handler is called with the type the edit
    /// changed, as on this machine; Stop ends it and leaves nothing of the relay or the handlers' hook there.</summary>
    [AvaloniaFact]
    public void An_android_app_run_with_hot_reload_on_an_emulator_takes_an_edit_of_its_code_and_runs_its_update_handler()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the emulator with Hot Reload", Slow);
            Ui.WaitFor(() => Output(window).Contains("Ticked as built", StringComparison.Ordinal) && window.Debugger.Running.Count == 1,
                () => $"the app to tick on the emulator and the debugger to attach for Hot Reload; the debugger runs {window.Debugger.Running.Count} quietly: "
                    + $"{Ui.Said(window)}\n{Output(window)}", Slow);
            Assert.False(window.Debugger.IsDebugging, "Run with Hot Reload showed a debugging session");

            window.OpenFile(Path.Combine(PathNames.Canonical(folder), "MainActivity.cs"));
            Typing.Replace(window, "\"Ticked as built\"", "\"Ticked after Hot Reload\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, () => $"Hot Reload to say what it applied: {Ui.Said(window)}", Slow);
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"The app on the emulator did not take the edit: {window.Debugger.LastApplied.Said}\n{Output(window)}");
            Ui.WaitFor(() => Output(window).Contains("Ticked after Hot Reload", StringComparison.Ordinal),
                () => $"the app on the emulator to tick as the edited code does: {Ui.Said(window)}\n{Output(window)}", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Output(window).Contains("Update handler called for MainActivity", StringComparison.Ordinal),
                () => $"the app's update handler to be called on the emulator with the type the edit changed, so an app's own refresh runs: {Ui.Said(window)}\n{Output(window)}",
                TimeSpan.FromMinutes(1));

            string serial = AndroidSerials.Of(window, target);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the quiet session to end with the app");
            Ui.WaitFor(() => Pid(tools, serial).Length == 0, () => $"the app to end on the emulator once stopped; it runs as {Pid(tools, serial)}", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"the relay's port and debugged app to be let go on the emulator; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Debug with Hot Reload on the emulator, stopped at a breakpoint in OnCreate, takes an edit that adds a method:
    /// the method evaluates there, as the debugger reads the app's code as the update made it, and once the app goes on its
    /// update handler is called with the type the edit changed and the edited code runs; Stop ends it and leaves nothing of
    /// the relay or the handlers' hook there.</summary>
    [AvaloniaFact]
    public void An_android_app_debugged_with_hot_reload_on_an_emulator_evaluates_a_method_an_edit_added_and_runs_its_update_handler()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Choose(window, project, target);
            window.OpenFile(Path.Combine(PathNames.Canonical(folder), "MainActivity.cs"));
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(10, 8));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start on the emulator with Hot Reload", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "answer") is not null,
                () => $"the stop at the breakpoint in OnCreate; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}\n"
                    + Console(window), Slow);

            Typing.Replace(window, "    private static string Ticked() => \"Ticked as built\";",
                "    private static string Ticked() => \"Ticked after Hot Reload\";\n\n    private static string Added() => \"Added by Hot Reload\";");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, () => $"Hot Reload to say what it applied: {Ui.Said(window)}", Slow);
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied,
                $"The app on the emulator did not take the edit: {window.Debugger.LastApplied.Said}\n{Console(window)}");
            Dap.EvaluateResponse added = Ui.Await(window.Debugger.Session!.EvaluateAsync("Added()", window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch),
                "the method the edit added, evaluated where the app stopped", Slow);
            Assert.True(added.Result == "\"Added by Hot Reload\"", $"The method the edit added did not evaluate where the app stopped: {added.Result}");

            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Console(window).Contains("Update handler called for MainActivity", StringComparison.Ordinal)
                && Console(window).Contains("Ticked after Hot Reload", StringComparison.Ordinal),
                () => $"the app's update handler to be called with the type the edit changed, and the edited code to run, once the app went on: {Ui.Said(window)}\n{Console(window)}",
                TimeSpan.FromMinutes(1));

            string serial = AndroidSerials.Of(window, target);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Pid(tools, serial).Length == 0, () => $"the debugged app to end on the emulator once stopped; it runs as {Pid(tools, serial)}", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"the debugging's port and debugged app to be let go on the emulator; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A copy of the test app, the emulator or device to run it on and the Android tools; skipped, saying why, where
    /// the workload, its SDK and Java, a target or the relays are missing.</summary>
    private static (string Folder, string Project, DeviceTarget Target, AndroidTools Tools) Prepared()
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
        DeviceTarget? target = MauiApp.Target(targets);
        if (target is null)
        {
            Assert.Skip("No Android device is connected and no emulator is made (an AVD of an installed system image), so there is nothing to run on");
        }
        if (!((string[])["android-arm64", "android-x64"]).Any(runtime => File.Exists(Path.Combine(AppContext.BaseDirectory, "debugger", "relay", runtime, AndroidApp.RelayFile))))
        {
            Assert.Skip("This build carries no Android debug relay, which `just debug-relay android` makes before a build carries it");
        }
        return (folder, project, target, tools);
    }

    /// <summary>The window, once it lists the app's configuration, has it run on the target.</summary>
    private static void Choose(MainWindow window, string project, DeviceTarget target)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "AndroidApp"), () => $"AndroidApp's run configuration; the window has {Projects(window)}, "
            + $"configurations {string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
        Assert.True(window.SelectConfiguration("AndroidApp"));
        ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
        window.ChooseRunTarget(model, target);
        Ui.Settle();
        Assert.True(window.Session.Targets.Of(model)?.Key == target.Key, $"{target.Name} was not kept as where AndroidApp runs");
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>The window's projects as it evaluated them: each with its output type, else why it could not be evaluated.</summary>
    private static string Projects(MainWindow window) => window.Session.Projects.Count == 0 ? "no projects"
        : string.Join("; ", window.Session.Projects.Select(p => $"{p.Name} ({(p.IsEvaluated ? $"output {p.OutputType}" : p.Problem ?? "not evaluated yet")})"));

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;

    /// <summary>What a start left on the emulator of <paramref name="serial"/>: a port forwarded to a session's relay, the
    /// update handlers' socket reversed to Passenger, the app marked as debugged; empty once its end took them away.</summary>
    private static string Left(AndroidTools tools, string serial)
    {
        string forwards = Adb(tools, serial, "forward", "--list");
        string reverses = Adb(tools, serial, "reverse", "--list");
        string debugged = Adb(tools, serial, "shell", "dumpsys activity processes | grep mDebugApp=");
        return string.Join("; ", [.. forwards.Split('\n').Where(l => l.StartsWith(serial + " ", StringComparison.Ordinal) && l.Contains("localabstract:passenger-debug-", StringComparison.Ordinal))
                .Select(l => l.Trim()),
            .. reverses.Split('\n').Where(l => l.Contains("localabstract:passenger-handlers-", StringComparison.Ordinal)).Select(l => l.Trim()),
            .. debugged.Contains($"mDebugApp={Package}/", StringComparison.Ordinal) ? [$"{Package} marked as debugged"] : (string[])[]]);
    }

    /// <summary>The app's process on the emulator of <paramref name="serial"/>, as pidof says it: empty once it has ended.</summary>
    private static string Pid(AndroidTools tools, string serial) => Adb(tools, serial, "shell", "pidof", Package).Trim();

    /// <summary>What adb says to a command for the emulator of <paramref name="serial"/>.</summary>
    private static string Adb(AndroidTools tools, string serial, params string[] command) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, .. command]))), $"adb {string.Join(' ', command)} on {serial}").Output;
}

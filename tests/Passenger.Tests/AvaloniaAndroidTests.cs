using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Mobile;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Avalonia's cross-platform solution (<c>dotnet new avalonia.xplat</c>) with its Android head taken to the first
/// .NET whose Android apps run on CoreCLR, run and debugged on an emulator from the window: the head is chosen to run
/// there, Run installs and starts it with Passenger's inspector in it, whose tree shows the shared view and takes a XAML
/// edit saved, and Debug stops at a breakpoint in the shared project's App, its locals shown, the shared library still
/// built for its own framework; each end leaves nothing of Passenger's on the device and the build's own environment in
/// the app, Debug's after a start left its own environment file in the build's link's place. Skips, saying why, without the
/// Android workload, its SDK and Java, Avalonia's Android packages in the package cache, an emulator, or the relays.
/// Debugged through the window, so alone.</summary>
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
        (string project, string? unrestored) = Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateAndroidHeadAsync(folder, "XApp")),
            "dotnet new avalonia.xplat and the Android head's restore", Slow);
        if (unrestored is not null)
        {
            Assert.Skip(unrestored);
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
            string serial = window.Session.RunPlan!.Device!.App!.Serial;
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected }, () => $"the inspector in the app on the emulator to say hello; it is "
                + $"{window.Session.RunApp?.State.ToString() ?? "not made"} ({window.Session.RunApp?.Problem}): {Ui.Said(window)}\n{Output(window)}", Slow);
            TreeAsked tree = new(window.Session.RunApp!);
            // The app's Application says hello before its activity shows the shared view.
            Ui.WaitFor(() => tree.Holds(e => e.Type == "MainView") && tree.Holds(e => e.Type == "TextBox"),
                () => $"the shared view and its TextBox in the app's tree; it shows {tree.Shown}", Slow);

            // The shared view's XAML edited and saved reaches the app on the emulator.
            window.OpenFile(Path.Combine(folder, "XApp", "Views", "MainView.axaml"));
            Ui.Settle();
            Typing.Replace(window, "<TextBox Text=\"{Binding Greeting}\"", "<TextBlock Text=\"Edited on the emulator\"");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => tree.Holds(e => e.Type == "TextBlock") && !tree.Holds(e => e.Type == "TextBox"),
                () => $"the saved XAML's TextBlock in the app's tree in place of its TextBox; it shows {tree.Shown}: {Ui.Said(window)}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => Pid(tools, window).Length == 0, () => $"the app to end on the emulator once stopped; it runs as {Pid(tools, window)}", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => Left(tools).Length == 0, () => $"the inspector's port and files to be let go on the emulator; it still has {Left(tools)}", TimeSpan.FromSeconds(30));
            (string linked, string built) = AppEnvironment(tools, serial);
            Assert.True(linked == built, $"Run's end left the app's environment linked to '{linked}', not the build's own at {built}: the app's next start reads it");

            // A start Passenger could not end left its own environment file in place of the build's link, which the next start's end puts back.
            InAppsFolder(tools, serial, "cp environment left && rm environment && mv left environment");
            Assert.True(AppEnvironment(tools, serial).Linked.Length == 0, "The app's environment is still a link, so the start after it is no start after an interrupted one");

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
            Ui.WaitFor(() => Left(tools).Length == 0, () => $"the debugging's ports and files to be let go on the emulator; it still has {Left(tools)}", TimeSpan.FromSeconds(30));
            (linked, built) = AppEnvironment(tools, serial);
            Assert.True(linked == built, $"Debug, started where a start left its own environment file, left the app's environment linked to '{linked}', not the build's "
                + $"own at {built}: the app's next start reads no environment of its build's");
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

    /// <summary>The app's tree as its inspector last answered, asked again each time the last answer is read, so a wait
    /// on what it shows never waits inside itself.</summary>
    private sealed class TreeAsked(LiveApp app)
    {
        private Task<Passenger.Inspector.AppTree>? asking;
        private List<Passenger.Inspector.Element> read = [];

        /// <summary>The elements' types as last read.</summary>
        public string Shown => string.Join(", ", read.Select(e => e.Type));

        /// <summary>Whether the last answer holds an element so; the next answer is asked for once this one is read.</summary>
        public bool Holds(Func<Passenger.Inspector.Element, bool> wanted)
        {
            asking ??= app.TreeAsync();
            Task<Passenger.Inspector.AppTree> answered = asking;
            if (answered.IsCompleted)
            {
                asking = null;
                read = [];
                if (answered.IsCompletedSuccessfully)
                {
                    read = [.. Passenger.Inspector.AppTree.Walk(answered.Result.Roots).Select(e => e.Element)];
                }
            }
            return read.Any(wanted);
        }
    }

    /// <summary>What a start left on the device: a port forwarded to a relay's or an inspector's socket, the inspector's
    /// files in the app, the app marked as debugged; empty once its end took them away.</summary>
    private static string Left(AndroidTools tools)
    {
        string forwards = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["forward", "--list"]))), "adb's forwards").Output;
        string files = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["shell", $"run-as {Package} ls -a files"]))), "the app's files").Output;
        string debugged = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["shell", "dumpsys activity processes | grep mDebugApp="]))),
            "the device's debugged app").Output;
        return string.Join("; ", [.. forwards.Split('\n').Where(l => l.Contains("localabstract:passenger-", StringComparison.Ordinal)).Select(l => l.Trim()),
            .. files.Split('\n').Any(l => l.Trim() == ".passenger") ? ["the inspector's files"] : (string[])[],
            .. debugged.Contains($"mDebugApp={Package}/", StringComparison.Ordinal) ? [$"{Package} marked as debugged"] : (string[])[]]);
    }

    /// <summary>Where the app's environment file links to in its fast-deployment folder (empty where it is no link), and
    /// where its build's own is, beside what the links to the build's assemblies point to.</summary>
    private static (string Linked, string Built) AppEnvironment(AndroidTools tools, string serial)
    {
        string assembly = InAppsFolder(tools, serial, "readlink XApp.Android.dll").Trim();
        Assert.True(assembly.StartsWith('/'), $"The build put no link to XApp.Android.dll in the app's folder: {assembly}");
        return (InAppsFolder(tools, serial, "readlink environment || true").Trim(), $"{assembly[..assembly.LastIndexOf('/')]}/environment");
    }

    /// <summary>What the app's own shell says to a command run in its fast-deployment folder for the device's ABI.</summary>
    private static string InAppsFolder(AndroidTools tools, string serial, string command)
    {
        string abi = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "getprop", "ro.product.cpu.abi"]))),
            "the device's ABI").Output.Trim();
        ChildResult said = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb,
            ["-s", serial, "shell", $"run-as {Package} sh -c 'cd files/.__override__/{abi} && {command}'"]))), $"{command} in the app's folder");
        Assert.True(said.Succeeded, $"{command} failed in the app's folder on the device: {said.Output}{said.Error}");
        return said.Output;
    }

    /// <summary>The app's process on the device the window chose, as pidof says it: empty once it has ended.</summary>
    private static string Pid(AndroidTools tools, MainWindow window)
    {
        string serial = window.Session.RunPlan?.Device?.App?.Serial ?? "emulator-5554";
        return Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", Package]))), "pidof on the device")
            .Output.Trim();
    }
}

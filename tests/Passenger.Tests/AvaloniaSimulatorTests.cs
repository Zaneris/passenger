using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Avalonia's cross-platform solution (<c>dotnet new avalonia.xplat</c>) with its iOS head taken to the first .NET
/// whose iOS apps run on CoreCLR, run and debugged on an iOS simulator chosen beside its run configuration: Run builds it for
/// the simulator, boots one that is shut down with no window, installs and starts the app with Passenger's inspector carried
/// into its data container, whose tree shows the shared view and takes a XAML edit saved; Debug stops at a breakpoint in the
/// shared project's App, its locals shown, steps, evaluates and, gone on, shows its tree; each Stop ends the app and takes
/// out what was carried, and a simulator the window booted is shut down as it closes. Skips, saying why, off a Mac, without
/// the iOS workload, Xcode or an iPhone simulator, the simulator's relay, or nuget.org's Avalonia iOS packages. Debugged
/// through the window, so alone; each test's waits end within the run's limit on a test (<see cref="Limits.WithinHang"/>).</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class AvaloniaSimulatorTests
{
    private static TimeSpan Slow => Limits.WithinHang(TimeSpan.FromMinutes(8));

    private const string Name = "XApp";

    private const string Head = $"{Name}.iOS";

    [AvaloniaFact]
    public void An_avalonia_apps_ios_head_run_on_a_simulator_shows_its_tree_and_takes_a_xaml_edit()
    {
        using IDisposable hang = Limits.BeginHang();
        (string folder, string project, DeviceTarget target, AppleTools tools) = Prepared("avalonia-ios-run");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the simulator", Slow);
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected }, () => $"the inspector in the app on the simulator to say hello; it is "
                + $"{window.Session.RunApp?.State.ToString() ?? "not made"} ({window.Session.RunApp?.Problem}): {Ui.Said(window)}\n{Output(window)}", Slow);
            LiveApp app = window.Session.RunApp!;
            Assert.True(app is { Kind: LiveAppKind.Avalonia } && app.Framework.StartsWith("Avalonia 12.", StringComparison.Ordinal), $"The app was taken for {app.Kind}, {app.Framework}");
            string? started = Started(window, window.BottomPanel.Run.Document.Snapshot, debugged: false);
            Assert.True(started is not null, $"simctl did not say the app's process in the Run window:\n{Output(window)}");
            string carried = Carried(window, relayed: false);
            TreeAsked tree = new(app);
            // The app's Application says hello before its scene shows the shared view.
            Ui.WaitFor(() => tree.Holds(e => e.Type == "MainView") && tree.Holds(e => e.Type == "TextBox"),
                () => $"the shared view and its TextBox in the app's tree; it shows {tree.Shown}", Slow);

            // The shared view's XAML edited and saved reaches the app on the simulator.
            window.OpenFile(Path.Combine(folder, Name, "Views", "MainView.axaml"));
            Ui.Settle();
            Typing.Replace(window, "<TextBox Text=\"{Binding Greeting}\"", "<TextBlock Text=\"Edited on the simulator\"");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => tree.Holds(e => e.Type == "TextBlock") && !tree.Holds(e => e.Type == "TextBox"),
                () => $"the saved XAML's TextBlock in the app's tree in place of its TextBox; it shows {tree.Shown}: {Ui.Said(window)}\n{Output(window)}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => !Runs(started!), () => $"the app to end on the simulator once stopped; process {started} still runs", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(carried), () => $"what the run carried into the app to be taken out once stopped; {carried} is still there", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
        LeftAsFound(target, tools);
    }

    [AvaloniaFact]
    public void An_avalonia_apps_ios_head_debugged_on_a_simulator_stops_at_a_breakpoint_in_its_shared_code_and_shows_its_tree()
    {
        using IDisposable hang = Limits.BeginHang();
        (string folder, string project, DeviceTarget target, AppleTools tools) = Prepared("avalonia-ios-debug");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Choose(window, project, target);
            string shared = Path.Combine(folder, Name, "App.axaml.cs");
            window.OpenFile(shared);
            Ui.Settle();
            string[] lines = File.ReadAllLines(shared);
            int line = Array.FindIndex(lines, l => l.Contains("if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)", StringComparison.Ordinal));
            Assert.True(line >= 0, $"The template's App no longer asks for the desktop's lifetime:\n{string.Join('\n', lines)}");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(line, lines[line].Length - lines[line].TrimStart().Length));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the build, the install and the debugged start on the simulator", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is not null,
                () => $"the stop at the breakpoint in the shared project's App with its locals; the debugger is "
                    + $"{window.Debugger.Session?.State.ToString() ?? "not debugging"}: {Ui.Said(window)}\n{Console(window)}", Slow);
            Assert.Equal("null", Shown(window, "desktop"));
            string? started = Started(window, window.BottomPanel.Debug.Console.Document.Snapshot, debugged: true);
            string carried = Carried(window, relayed: true);
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "singleViewFactoryApplicationLifetime") is not null,
                () => $"the step past the desktop's lifetime to the next branch; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: "
                    + Ui.Said(window), Slow);
            Dap.EvaluateResponse lifetime = Ui.Await(window.Debugger.Session!.EvaluateAsync("ApplicationLifetime is ISingleViewApplicationLifetime",
                window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch), "the app's lifetime, evaluated where it stopped", Slow);
            Assert.True(lifetime.Result == "true", $"The app on the simulator was not given a single view's lifetime: {lifetime.Result}");

            // The inspector carried in beside the relay shows the debugged app's tree once it goes on.
            Ui.Run(window, AppActions.Resume);
            LiveApp debugged = window.Debugger.Tabs.LastOrDefault(t => t.Name == Head)?.App is { Kind: LiveAppKind.Avalonia } inspected ? inspected
                : throw new Xunit.Sdk.XunitException("Debug's tab holds no Avalonia app for the Live Visual Tree");
            Ui.WaitFor(() => debugged.State == LiveAppState.Connected, () => $"the inspector in the debugged app to say hello; it is {debugged.State} ({debugged.Problem}): "
                + $"{Ui.Said(window)}\n{Console(window)}", Slow);
            TreeAsked debuggedTree = new(debugged);
            Ui.WaitFor(() => debuggedTree.Holds(e => e.Type == "MainView") && debuggedTree.Holds(e => e.Type == "TextBox"),
                () => $"the shared view and its TextBox in the debugged app's tree; it shows {debuggedTree.Shown}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Assert.True(started is not null, $"simctl did not say the app's process in the Debug console:\n{Console(window)}");
            Ui.WaitFor(() => !Runs(started!), () => $"the debugged app to end on the simulator once stopped; process {started} still runs", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(carried), () => $"what the session carried into the app to be taken out once stopped; {carried} is still there", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
        LeftAsFound(target, tools);
    }

    /// <summary>The template's solution with its iOS head in a folder of its own, and the simulator to run it on: an iPhone
    /// simulator already booted, else the first shut down, which the window boots; or a skip saying why there is none.</summary>
    private static (string Folder, string Project, DeviceTarget Target, AppleTools Tools) Prepared(string name)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone);
        }
        string runtime = $"iossimulator-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";
        if (!File.Exists(CarriedFiles.Relay(runtime, SimulatorApp.RelayFile)))
        {
            Assert.Skip($"This build carries no debug relay for {runtime}, which `just debug-relay apple` makes before a build carries it");
        }
        string folder = PathNames.Canonical(TestFolders.Create(name));
        (string project, string? unrestored) = Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateIosHeadAsync(folder, Name)),
            "dotnet new avalonia.xplat and the iOS head's restore", Slow);
        if (unrestored is not null)
        {
            Assert.Skip(unrestored);
        }
        (IReadOnlyList<DeviceTarget> targets, AppleTools? tools, string? problem) = Ui.Await(Waiting.Apart(() =>
            AppleSimulators.ListAsync(project, MauiApp.Ios, new Dictionary<string, string>(), CancellationToken.None)), "the iOS workload's simulators", Slow);
        if (tools is null)
        {
            Assert.Skip($"There is nothing to run on: {problem}");
        }
        if (targets.Where(t => t is { Platform: TargetPlatform.Ios, Kind: TargetKind.Simulator } && t.Name.StartsWith("iPhone", StringComparison.Ordinal))
            .OrderBy(t => t.Running ? 0 : 1).FirstOrDefault() is not { } target)
        {
            Assert.Skip("The iOS workload lists no iPhone simulator: add one in Xcode, with its iOS simulator runtime");
            return default;
        }
        return (folder, project, target, tools);
    }

    /// <summary>The head's run configuration chosen, and the simulator kept as where its project runs.</summary>
    private static void Choose(MainWindow window, string project, DeviceTarget target)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == Head), () => $"{Head}'s run configuration; the window has configurations "
            + $"{string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
        Assert.True(window.SelectConfiguration(Head));
        ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
        window.ChooseRunTarget(model, target);
        Ui.Settle();
        Assert.True(window.Session.Targets.Of(model)?.Key == target.Key, $"{target.Name} was not kept as where {Head} runs");
    }

    /// <summary>The simulator as the test found it once the window has closed: shut down again where the window booted it,
    /// still booted where it was already.</summary>
    private static void LeftAsFound(DeviceTarget target, AppleTools tools)
    {
        string expected = target.Running ? "(Booted)" : "(Shutdown)";
        Ui.WaitFor(() => State(target, tools).Contains(expected, StringComparison.Ordinal),
            () => $"{target.Name} to be left {expected} as the window closed; simctl lists it as {State(target, tools)}", TimeSpan.FromMinutes(1));
    }

    /// <summary>The app on the simulator the window's last start named: Debug's where <paramref name="debugged"/>, else Run's.</summary>
    private static SimulatorApp App(MainWindow window, bool debugged) =>
        (debugged ? window.Debugger.Tabs.Select(t => t.Plan?.Device?.App).LastOrDefault(a => a is not null) : window.Session.RunPlan?.Device?.App) as SimulatorApp
            ?? throw new InvalidOperationException($"{(debugged ? "Debug" : "Run")} named no app on a simulator");

    /// <summary>The app's process as simctl said it launched it, in a console's text; null until it has.</summary>
    private static string? Started(MainWindow window, TextSnapshot shown, bool debugged) => App(window, debugged).StartedIn(shown);

    /// <summary>Where the start carried what the app loads of Passenger's in its data container, which holds the inspector now,
    /// and the relay where <paramref name="relayed"/>, as Debug's start carries it.</summary>
    private static string Carried(MainWindow window, bool relayed)
    {
        SimulatorApp app = App(window, debugged: relayed);
        Assert.True(File.Exists(Path.Combine(app.Carried, Path.GetFileName(LiveApp.InspectorPath))),
            $"The inspector was not carried into the app's data container ({app.Carried}), where the app reads it");
        Assert.True(File.Exists(Path.Combine(app.Carried, SimulatorApp.RelayFile)) == relayed,
            $"The relay was {(relayed ? "not " : string.Empty)}carried into the app's data container ({app.Carried})");
        return app.Carried;
    }

    /// <summary>Whether a process of this Mac (a simulator's app is one) still runs.</summary>
    private static bool Runs(string process) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("ps", ["-p", process, "-o", "pid="]))), "ps on the app's process").Output.Trim().Length > 0;

    /// <summary>The simulator's line in simctl's listing of devices.</summary>
    private static string State(DeviceTarget target, AppleTools tools) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["list", "devices"]))), "simctl's devices").Output
            .Split('\n').FirstOrDefault(l => l.Contains(target.Id, StringComparison.Ordinal))?.Trim() ?? "not listed";

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;
}

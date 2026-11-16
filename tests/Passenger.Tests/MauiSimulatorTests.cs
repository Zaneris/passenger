using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, its iOS head run and debugged on an iOS simulator chosen beside its run
/// configuration, as the iOS workload lists them: Run builds it for the simulator, boots one that is shut down with no window,
/// installs and starts the app there, its output in the Run window and its MAUI inspector's tree the app's; with Hot Reload
/// an edit lands through Passenger's relay in it and its update handler is called; Debug stops at a breakpoint in its main
/// page, steps, shows and evaluates, and takes an edit there; Stop ends the app, and a simulator the window booted is shut
/// down as the window closes. Skips, saying why, off a Mac, without MAUI's and iOS's workloads, Xcode or an iPhone
/// simulator, or the simulator's relay. Debugged through the window, so alone.</summary>
[Collection(Alone.Name)]
public sealed class MauiSimulatorTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(8);

    /// <summary>What the update handler the test adds to the app writes once Hot Reload has applied an edit.</summary>
    private const string Handled = "Update handler called for MainPage";

    [AvaloniaFact]
    public void A_maui_apps_ios_head_run_on_a_simulator_shows_its_tree_takes_a_hot_reload_edit_and_ends_with_stop()
    {
        (string folder, string project, DeviceTarget target, AppleTools tools) = Prepared("maui-ios-run");
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the simulator with Hot Reload", Slow);
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected } && window.Debugger.Running.Count == 1,
                () => $"the app's inspector to connect and the debugger to attach for Hot Reload; the inspector is {window.Session.RunApp?.State.ToString() ?? "not made"} "
                    + $"({window.Session.RunApp?.Problem}), the debugger runs {window.Debugger.Running.Count} quietly: {Ui.Said(window)}\n{Output(window)}", Slow);
            Assert.False(window.Debugger.IsDebugging, "Run with Hot Reload showed a debugging session");
            LiveApp app = window.Session.RunApp!;
            Assert.True(app is { Kind: LiveAppKind.Maui } && app.Framework.StartsWith("MAUI 11.", StringComparison.Ordinal), $"The app was taken for {app.Kind}, {app.Framework}");
            AppTree tree = Ui.AwaitPumped(app.TreeAsync(), "the app's tree", TimeSpan.FromMinutes(1));
            Assert.Contains(AppTree.Walk(tree.Roots), e => e.Element is { Type: "Button", Name: "CounterBtn" });
            string? started = SimulatorStart(window.BottomPanel.Run.Document.Snapshot, window);
            Assert.True(started is not null, $"simctl did not say the app's process in the Run window:\n{Output(window)}");

            window.OpenFile(Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml.cs"));
            Typing.Replace(window, "$\"Clicked {count} time\"", "$\"Tapped {count} time\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, () => $"Hot Reload to say what it applied: {Ui.Said(window)}", Slow);
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"The app on the simulator did not take the edit: {window.Debugger.LastApplied.Said}\n{Output(window)}");
            Ui.WaitFor(() => Output(window).Contains(Handled, StringComparison.Ordinal),
                () => $"the app's update handler to be called on the simulator with the type the edit changed: {Ui.Said(window)}\n{Output(window)}", TimeSpan.FromMinutes(1));

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the quiet session to end with the app");
            Ui.WaitFor(() => !Runs(started!), () => $"the app to end on the simulator once stopped; process {started} still runs", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
        LeftAsFound(target, tools);
    }

    [AvaloniaFact]
    public void A_maui_apps_ios_head_debugged_on_a_simulator_stops_steps_evaluates_and_takes_a_hot_reload_edit()
    {
        (string folder, string project, DeviceTarget target, AppleTools tools) = Prepared("maui-ios-debug");
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Choose(window, project, target);
            string page = Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml.cs");
            window.OpenFile(page);
            Ui.Settle();
            int line = Array.FindIndex(File.ReadAllLines(page), l => l.Trim() == "InitializeComponent();");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(line, File.ReadAllLines(page)[line].IndexOf('I', StringComparison.Ordinal)));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the build, the install and the debugged start on the simulator", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is not null,
                () => $"the stop at the breakpoint in MainPage's constructor with its locals; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: "
                    + $"{Ui.Said(window)}\n{Console(window)}", Slow);
            Assert.Contains("Content = (null)", Shown(window, "this"), StringComparison.Ordinal);
            string? started = SimulatorStart(window.BottomPanel.Debug.Console.Document.Snapshot, window);

            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is { } shown && shown.Contains("ScrollView", StringComparison.Ordinal),
                () => $"the step over InitializeComponent, the page's content shown made; this is {Shown(window, "this") ?? "not shown"}: {Ui.Said(window)}", Slow);
            Dap.EvaluateResponse count = Ui.Await(window.Debugger.Session!.EvaluateAsync("count + 41", window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch),
                "the page's count, evaluated where the app stopped", Slow);
            Assert.Equal("41", count.Result);

            Typing.Replace(window, "$\"Clicked {count} time\"", "$\"Pressed {count} time\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, () => $"Hot Reload to say what it applied: {Ui.Said(window)}", Slow);
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied,
                $"The app on the simulator did not take the edit: {window.Debugger.LastApplied.Said}\n{Console(window)}");
            Ui.Await(window.Debugger.Session!.EvaluateAsync("OnCounterClicked(null, EventArgs.Empty)", window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch),
                "the click handler the edit changed, called where the app stopped", Slow);
            Dap.EvaluateResponse text = Ui.Await(window.Debugger.Session!.EvaluateAsync("CounterBtn.Text", window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch),
                "the button's text after the edited handler ran", Slow);
            Assert.True(text.Result == "\"Pressed 1 time\"", $"The click handler ran as built, not as edited: the button says {text.Result}");

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => Console(window).Contains(Handled, StringComparison.Ordinal),
                () => $"the app's update handler to be called once the app went on: {Ui.Said(window)}\n{Console(window)}", TimeSpan.FromMinutes(1));
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Assert.True(started is not null, $"simctl did not say the app's process in the Debug console:\n{Console(window)}");
            Ui.WaitFor(() => !Runs(started!), () => $"the debugged app to end on the simulator once stopped; process {started} still runs", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
        LeftAsFound(target, tools);
    }

    /// <summary>The MAUI template's iOS head in a folder of its own, with an update handler that says each type an update
    /// changed, and the simulator to run it on (<see cref="Target"/>), or a skip saying why there is none.</summary>
    private static (string Folder, string Project, DeviceTarget Target, AppleTools Tools) Prepared(string name)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone);
        }
        string runtime = $"iossimulator-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "debugger", "relay", runtime, SimulatorApp.RelayFile)))
        {
            Assert.Skip($"This build carries no debug relay for {runtime}, which `just debug-relay apple` makes before a build carries it");
        }
        string folder = PathNames.Canonical(TestFolders.Create(name));
        (string project, string? unmade) = Ui.AwaitPumped(Waiting.Apart(() => MauiApp.CreateIosHeadAsync(folder)), "dotnet new maui and the iOS head's restore", Slow);
        if (unmade is not null)
        {
            Assert.Skip(unmade);
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "UpdateSaid.cs"), $$"""
            [assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(MauiApp1.UpdateSaid))]

            namespace MauiApp1;

            internal static class UpdateSaid
            {
                public static void UpdateApplication(Type[]? types) => Console.WriteLine($"{{Handled[..^"MainPage".Length]}}{string.Join(", ", (types ?? []).Select(t => t.Name))}");
            }
            """);
        (IReadOnlyList<DeviceTarget> targets, AppleTools? tools, string? problem) = Ui.Await(Waiting.Apart(() =>
            AppleSimulators.ListAsync(project, MauiApp.Ios, new Dictionary<string, string>(), CancellationToken.None)), "the iOS workload's simulators", Slow);
        if (tools is null)
        {
            Assert.Skip($"There is nothing to run on: {problem}");
        }
        if (Target(targets) is not { } target)
        {
            Assert.Skip("The iOS workload lists no iPhone simulator: add one in Xcode, with its iOS simulator runtime");
            return default;
        }
        return (folder, project, target, tools);
    }

    /// <summary>The simulator to run on: an iPhone simulator already booted, else the first shut down, which the window boots;
    /// never a device.</summary>
    private static DeviceTarget? Target(IReadOnlyList<DeviceTarget> targets) =>
        targets.Where(t => t is { Platform: TargetPlatform.Ios, Kind: TargetKind.Simulator } && t.Name.StartsWith("iPhone", StringComparison.Ordinal))
            .OrderBy(t => t.Running ? 0 : 1).FirstOrDefault();

    private static void Choose(MainWindow window, string project, DeviceTarget target)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == MauiApp.Name), () => $"{MauiApp.Name}'s run configuration; the window has configurations "
            + $"{string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
        Assert.True(window.SelectConfiguration(MauiApp.Name));
        ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
        window.ChooseRunTarget(model, target);
        Ui.Settle();
        Assert.True(window.Session.Targets.Of(model)?.Key == target.Key, $"{target.Name} was not kept as where {MauiApp.Name} runs");
    }

    /// <summary>The app's process as simctl said it launched it, in a console's text; null until it has.</summary>
    private static string? SimulatorStart(TextSnapshot shown, MainWindow window) =>
        (window.Session.RunPlan?.Device?.App ?? window.Debugger.Tabs.Select(t => t.Plan?.Device?.App).LastOrDefault(a => a is not null))?.StartedIn(shown);

    /// <summary>Whether a process of this Mac (a simulator's app is one) still runs.</summary>
    private static bool Runs(string process) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("ps", ["-p", process, "-o", "pid="]))), "ps on the app's process").Output.Trim().Length > 0;

    /// <summary>The simulator as the test found it once the window has closed: shut down again where the window booted it,
    /// still booted where it was already.</summary>
    private static void LeftAsFound(DeviceTarget target, AppleTools tools)
    {
        string expected = target.Running ? "(Booted)" : "(Shutdown)";
        Ui.WaitFor(() => State(target, tools).Contains(expected, StringComparison.Ordinal),
            () => $"{target.Name} to be left {expected} as the window closed; simctl lists it as {State(target, tools)}", TimeSpan.FromMinutes(1));
    }

    /// <summary>The simulator's line in simctl's listing of devices.</summary>
    private static string State(DeviceTarget target, AppleTools tools) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Simctl, ["list", "devices"]))), "simctl's devices").Output
            .Split('\n').FirstOrDefault(l => l.Contains(target.Id, StringComparison.Ordinal))?.Trim() ?? "not listed";

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;
}

using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, its iOS head debugged on an iOS simulator chosen beside its run
/// configuration, as the iOS workload lists them, the iOS head's debug path (<see cref="AvaloniaSimulatorTests"/> holds its
/// run path): Debug builds it for the simulator, boots one that is shut down with no window, installs and starts the app
/// with Passenger's relay carried into its data container, stops in its main page and at breakpoints in its XAML, steps,
/// shows, evaluates and takes a Hot Reload edit, whose update handler is called once the app goes on, a tap then stopping
/// and stepping in the edited handler at its edit's lines, then XAML edits, one MAUI patches and one for which the page is
/// made again; Stop ends the app and takes out what was carried, and a simulator the window booted is shut down as it
/// closes. First, the tests of the project <c>dotnet new iostest</c> makes run in its app on the simulator chosen beside
/// its configuration, and one is debugged there. Skips, saying why, where it cannot run (off a Mac, without MAUI's and iOS's
/// workloads, Xcode, an iPhone simulator or the simulator's relay). Debugged through the window, so alone.</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class MauiSimulatorTests
{
    private static TimeSpan Slow => Limits.WithinHang(TimeSpan.FromMinutes(8));

    /// <summary>What the update handler the test adds to the app writes once Hot Reload has applied an edit.</summary>
    private const string Handled = "Update handler called for MainPage";

    [AvaloniaFact]
    public void A_device_test_projects_tests_run_on_a_simulator_and_a_maui_apps_ios_head_debugged_there_stops_steps_evaluates_and_takes_a_hot_reload_edit()
    {
        using IDisposable hang = Limits.BeginHang();
        Flow.Step("an iostest project's tests run in its app on the simulator chosen beside its configuration, each told, and the app ends there",
            An_iostest_projects_tests_run_in_its_app_on_the_simulator_chosen_beside_its_configuration_each_told_and_the_app_ends_there);
        Flow.Step("an iostest project's tests debugged from the Tests window stop at a breakpoint in one on the simulator, then each test is told and the app ends there",
            An_iostest_projects_tests_debugged_from_the_tests_window_stop_at_a_breakpoint_in_one_on_the_simulator_then_each_test_is_told_and_the_app_ends_there);
        Flow.Step("a MAUI app's iOS head debugged on a simulator stops, steps, evaluates and takes a Hot Reload edit",
            A_maui_apps_ios_head_debugged_on_a_simulator_stops_steps_evaluates_and_takes_a_hot_reload_edit);
    }

    /// <summary>The template's three tests run as the Tests window's Run All runs them: built for the simulator, the app
    /// installed and started there for each test host, which connects to Passenger's client on this Mac's loopback.</summary>
    private static void An_iostest_projects_tests_run_in_its_app_on_the_simulator_chosen_beside_its_configuration_each_told_and_the_app_ends_there()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone);
        }
        string folder = PathNames.Canonical(TestFolders.Create("ios-device-tests"));
        try
        {
            (string project, string? unmade) = Ui.AwaitPumped(Waiting.Apart(() => MauiApp.CreateDeviceTestsAsync(folder, "iostest")), "dotnet new iostest and its restore", Slow);
            if (unmade is not null)
            {
                Assert.Skip(unmade);
            }
            (DeviceTarget target, AppleTools tools) = Listed(project);
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                Choose(window, project, target, MauiApp.DeviceTests);
                Assert.True(window.Perform(AppActions.RunAllTests));
                Ui.WaitFor(() => !window.Tests.IsBusy && window.Tests.Tree.Roots.SelectMany(r => r.Methods).Any(m => m.State != TestState.NotRun),
                    () => $"the run of the tests in their app on {target.Name}: {window.BottomPanel.Tests.Status}\n{Ui.Said(window)}", Slow);
                Assert.True(System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, $" in {SaidWhile.Pattern}$", string.Empty) == MauiApp.DeviceTestsRan,
                    $"The tests did not run in their app on {target.Name}, each told: {window.BottomPanel.Tests.Status}");
                TestNode failed = window.Tests.Tree.Roots.SelectMany(r => r.Methods).Single(m => m.State == TestState.Failed);
                Assert.Contains("This test is expected to fail", failed.Details, StringComparison.Ordinal);
                Ui.WaitFor(() => !AppRuns(target), () => $"the tests' app to end on {target.Name} once their run was over", TimeSpan.FromSeconds(30));
            }
            finally
            {
                window.Close();
            }
            LeftAsFound(target, tools);
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The tests of the project <c>dotnet new iostest</c> makes, debugged from the Tests window (<see cref="MauiApp.DebugDeviceTests"/>):
    /// the app started on the simulator through Passenger's relay in it as Debug starts an app there, with the Testing
    /// Platform's server options, stopping at a breakpoint in its first test; once it goes on each test is told as a run tells it,
    /// and the app ends there.</summary>
    private static void An_iostest_projects_tests_debugged_from_the_tests_window_stop_at_a_breakpoint_in_one_on_the_simulator_then_each_test_is_told_and_the_app_ends_there()
    {
        Relayed();
        string folder = PathNames.Canonical(TestFolders.Create("ios-device-tests-debugged"));
        try
        {
            (string project, string? unmade) = Ui.AwaitPumped(Waiting.Apart(() => MauiApp.CreateDeviceTestsAsync(folder, "iostest")), "dotnet new iostest and its restore", Slow);
            if (unmade is not null)
            {
                Assert.Skip(unmade);
            }
            (DeviceTarget target, AppleTools tools) = Listed(project);
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                Choose(window, project, target, MauiApp.DeviceTests);
                MauiApp.DebugDeviceTests(window, project, $"on {target.Name}", Slow);
                Ui.WaitFor(() => !AppRuns(target), () => $"the debugged tests' app to end on {target.Name} once their run was over", TimeSpan.FromSeconds(30));
            }
            finally
            {
                window.Close();
            }
            LeftAsFound(target, tools);
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Whether a process of this Mac runs the device test project's app installed on the simulator.</summary>
    private static bool AppRuns(DeviceTarget target) => Processes.Listed().Values
        .Any(l => l.Contains($"/Devices/{target.Id}/", StringComparison.Ordinal) && l.Contains($"/{MauiApp.DeviceTests}.app/", StringComparison.Ordinal));

    private static void A_maui_apps_ios_head_debugged_on_a_simulator_stops_steps_evaluates_and_takes_a_hot_reload_edit()
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
            string carried = Carried(window);

            // Breakpoints in the page's XAML, bound in the code MAUI's source generator wrote of it.
            (string xaml, int hello, int clickMe) = MauiApp.XamlBreakpoints(window, project);
            Ui.Run(window, AppActions.Resume);
            MauiApp.StopsInXaml(window, xaml, hello, clickMe, Slow);
            Ui.Run(window, AppActions.StepOut);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is { } shown && shown.Contains("ScrollView", StringComparison.Ordinal),
                () => $"the step out of InitializeComponent, the page's content shown made; this is {Shown(window, "this") ?? "not shown"} at {MauiApp.Top(window)}: {Ui.Said(window)}", Slow);
            window.OpenFile(page);
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
            LiveApp app = window.LiveApp ?? throw new Xunit.Sdk.XunitException($"The debugged app has no inspector: {Ui.Said(window)}");
            MauiApp.StopsInEditedHandler(window, app, project, Slow);
            MauiApp.TakesXamlEdits(window, app, project, Slow);
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

    /// <summary>The MAUI template's iOS head in a folder of its own, with an update handler that says each type an update
    /// changed, and the simulator to run it on (<see cref="Target"/>), or a skip saying why there is none.</summary>
    private static (string Folder, string Project, DeviceTarget Target, AppleTools Tools) Prepared(string name)
    {
        Relayed();
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
        (DeviceTarget target, AppleTools tools) = Listed(project);
        return (folder, project, target, tools);
    }

    /// <summary>The simulator a project of iOS runs on (<see cref="Target"/>) and the Xcode the workload lists it with, or a
    /// skip saying why there is none.</summary>
    private static (DeviceTarget Target, AppleTools Tools) Listed(string project)
    {
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
        return (target, tools);
    }

    /// <summary>The simulator to run on: an iPhone simulator already booted, else the first shut down, which the window boots;
    /// never a device.</summary>
    private static DeviceTarget? Target(IReadOnlyList<DeviceTarget> targets) =>
        targets.Where(t => t is { Platform: TargetPlatform.Ios, Kind: TargetKind.Simulator } && t.Name.StartsWith("iPhone", StringComparison.Ordinal))
            .OrderBy(t => t.Running ? 0 : 1).FirstOrDefault();

    private static void Choose(MainWindow window, string project, DeviceTarget target, string name = MauiApp.Name)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == name), () => $"{name}'s run configuration; the window has configurations "
            + $"{string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
        Assert.True(window.SelectConfiguration(name));
        ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
        window.ChooseRunTarget(model, target);
        Ui.Settle();
        Assert.True(window.Session.Targets.Of(model)?.Key == target.Key, $"{target.Name} was not kept as where {name} runs");
    }

    /// <summary>The app's process as simctl said it launched it, in a console's text; null until it has.</summary>
    private static string? SimulatorStart(TextSnapshot shown, MainWindow window) =>
        window.Debugger.Tabs.Select(t => t.Plan?.Device?.App).LastOrDefault(a => a is not null)?.StartedIn(shown);

    /// <summary>Where the start carried what the app loads of Passenger's in its data container, which it has there now.</summary>
    private static string Carried(MainWindow window)
    {
        SimulatorApp app = window.Debugger.Tabs.Select(t => t.Plan?.Device?.App).LastOrDefault(a => a is not null) as SimulatorApp
            ?? throw new InvalidOperationException("Debug named no app on a simulator");
        Assert.True(File.Exists(Path.Combine(app.Carried, SimulatorApp.RelayFile)), $"The relay was not carried into the app's data container ({app.Carried}), where the app reads it");
        return app.Carried;
    }

    /// <summary>Whether a process of this Mac (a simulator's app is one) still runs.</summary>
    private static bool Runs(string process) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("ps", ["-p", process, "-o", "pid="]))), "ps on the app's process").Output.Trim().Length > 0;

    /// <summary>A skip, saying why, off a Mac or where this build carries no debug relay for this Mac's simulators.</summary>
    private static void Relayed()
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
    }

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

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;
}

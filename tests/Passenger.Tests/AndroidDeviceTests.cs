using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Tests.Debugging.Helpers;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>An Android app (tests/AndroidApp) run and debugged on an emulator from the window: chosen beside the run
/// configuration named for the app (its launch settings' only profile is the MAUI template's Windows one), Run installs
/// and starts it there, its log in the Run window, and Stop ends it on the emulator; Debug stops at a breakpoint in its
/// OnCreate through Passenger's relay in the app, its locals shown, and Stop ends it; Run and Debug with Hot Reload take
/// an edit of its code through the relay, its update handler called after it, and under Debug a method the edit added is
/// evaluated; a start whose app run-as does not know is refused as its relay or inspector goes in; MAUI's and Avalonia's
/// heads (<see cref="MauiAndroidTests"/>, <see cref="AvaloniaAndroidTests"/>) are steps too. The emulator is the one ANDROID_SERIAL names where several run, and every
/// adb call names it. Skips, saying why, without the Android workload, its SDK and Java, an emulator or the relays.
/// Debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class AndroidDeviceTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(6);

    private const string Package = "net.passengeride.tests.androidapp";

    /// <summary>An app on an emulator: run and stopped at a breakpoint, its locals shown, an edit taken by Run and by Debug
    /// with Hot Reload and its update handler called, a MAUI app's live tree and an Avalonia app's shared code stopped in.
    /// The steps that need more than the emulator (MAUI's workload, Avalonia's packages) come first, so a skip ends the
    /// flow before any step ran.</summary>
    [AvaloniaFact]
    public void An_app_on_an_emulator_runs_debugs_and_takes_hot_reload_edits_and_its_live_tree_and_shared_code_are_reached()
    {
        Flow.Step("an Avalonia app's Android head runs and stops at a breakpoint in its shared code on an emulator",
            AvaloniaAndroidTests.An_avalonia_apps_android_head_runs_and_stops_at_a_breakpoint_in_its_shared_code_on_an_emulator);
        Flow.Step("a MAUI app's Android head debugged on an emulator shows its main page's label and button in the live tree and takes its XAML's edits",
            MauiAndroidTests.A_maui_apps_android_head_debugged_on_an_emulator_shows_its_main_pages_label_and_button_in_the_live_tree);
        Flow.Step("an Android app runs and stops at a breakpoint on an emulator chosen beside its configuration",
            An_android_app_runs_and_stops_at_a_breakpoint_on_an_emulator_chosen_beside_its_configuration);
        Flow.Step("an Android app run with Hot Reload on an emulator takes an edit of its code and runs its update handler",
            An_android_app_run_with_hot_reload_on_an_emulator_takes_an_edit_of_its_code_and_runs_its_update_handler);
        Flow.Step("an Android app debugged with Hot Reload on an emulator evaluates a method an edit added and runs its update handler",
            An_android_app_debugged_with_hot_reload_on_an_emulator_evaluates_a_method_an_edit_added_and_runs_its_update_handler);
    }

    /// <summary>However a start on an emulator ends, refused, stopped partway, ended from outside or by Passenger's own end,
    /// nothing of its debugging is left there, its app never starts after a Stop, and no later start loads a relay left.</summary>
    [AvaloniaFact]
    public void Nothing_of_a_start_is_left_on_the_emulator_however_it_ends()
    {
        Flow.Step("a start whose app run-as does not know is refused in its words as the relay or inspector goes in",
            A_start_whose_app_run_as_does_not_know_is_refused_in_its_words_as_the_relay_or_inspector_goes_in);
        Flow.Step("a debugged app ended on the emulator from outside ends its session and leaves nothing of its debugging there",
            A_debugged_app_ended_on_the_emulator_from_outside_ends_its_session_and_leaves_nothing_of_its_debugging_there);
        Flow.Step("a debug start stopped once its relay is in the app leaves nothing of it on the emulator",
            A_debug_start_stopped_once_its_relay_is_in_the_app_leaves_nothing_of_it_on_the_emulator);
        Flow.Step("a run stopped once its relay is in the app never starts the app on the emulator",
            A_run_stopped_once_its_relay_is_in_the_app_never_starts_the_app_on_the_emulator);
        Flow.Step("Passenger ending while a debugged app's end on the emulator is held waits until nothing of it is left there",
            Passenger_ending_while_a_debugged_apps_end_on_the_emulator_is_held_waits_until_nothing_of_it_is_left_there);
        Flow.Step("a relay a vanished end left in the app is out before its next run starts it",
            A_relay_a_vanished_end_left_in_the_app_is_out_before_its_next_run_starts_it);
        Flow.Step("a debugged app started from its launcher while its end is held loads no relay",
            A_debugged_app_started_from_its_launcher_while_its_end_is_held_loads_no_relay);
    }

    private static void An_android_app_runs_and_stops_at_a_breakpoint_on_an_emulator_chosen_beside_its_configuration()
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
    private static void An_android_app_run_with_hot_reload_on_an_emulator_takes_an_edit_of_its_code_and_runs_its_update_handler()
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
    private static void An_android_app_debugged_with_hot_reload_on_an_emulator_evaluates_a_method_an_edit_added_and_runs_its_update_handler()
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

    /// <summary>Passenger's relay and its inspector put in an app run-as does not know (one no longer on the device) refuse
    /// the start at once in run-as's own words, where it went on as though they were in, forwarding a port to a relay that
    /// never came and waiting out its hello; what the start staged on the emulator is taken away all the same.</summary>
    private static void A_start_whose_app_run_as_does_not_know_is_refused_in_its_words_as_the_relay_or_inspector_goes_in()
    {
        (string folder, _, DeviceTarget target, AndroidTools tools) = Prepared();
        string serial = target.Running ? target.Id
            : Ui.Await(Waiting.Apart(() => AndroidDevices.BootAsync(tools, target.Avd ?? target.Id, unseen: true, _ => { }, CancellationToken.None)), "the emulator's start", Slow).Serial
                ?? throw new InvalidOperationException($"{target.Name} did not start");
        string abi = Adb(tools, serial, "shell", "getprop", "ro.product.cpu.abi").Trim();
        const string Unknown = "net.passengeride.tests.notinstalled";
        string intermediate = Path.Combine(folder, "obj", "Debug");
        string built = Path.Combine(intermediate, "fastdeploy2-environment", abi, "environment");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllBytes(built, AndroidEnvironment.Write([new("DOTNET_TieredPGO", "0")]));
        string inspectorFile = Path.Combine(folder, "Passenger.Inspector.dll");
        File.WriteAllBytes(inspectorFile, [0x4d, 0x5a]);
        AndroidApp app = new(serial, tools.Adb, Unknown, "crc64.MainActivity", intermediate);
        DeviceStart debugged = new("AndroidApp", app);
        DeviceStart inspected = new("AndroidApp", app);
        try
        {
            (RelaySession? relay, string? unrelayed) = Ui.Await(debugged.PutRelayAsync(null, null, CancellationToken.None), "the relay's put into an app run-as does not know", Slow);
            Assert.True(relay is null && unrelayed is not null && unrelayed.Contains($"run-as: unknown package: {Unknown}", StringComparison.Ordinal),
                $"A Debug start whose relay could not go in was not refused in run-as's words, so it would wait for a relay that is not there: {unrelayed ?? "nothing said"}");
            DeviceInspector inspector = new("passenger-inspector-test", "token", [inspectorFile], []);
            string? uninspected = Ui.Await(inspected.PutInspectorAsync(inspector, CancellationToken.None), "the inspector's put into an app run-as does not know", Slow);
            Assert.True(uninspected is not null && uninspected.Contains($"run-as: unknown package: {Unknown}", StringComparison.Ordinal),
                $"A Run whose inspector could not go in did not say so in run-as's words: {uninspected ?? "nothing said"}");
            string staged = Adb(tools, serial, "shell", $"ls /data/local/tmp | grep -F passenger-{Unknown}-; true").Trim();
            Assert.True(staged.Length == 0, $"What the refused starts staged on the emulator stayed there: {staged}");
            Assert.True(Left(tools, serial).Length == 0, $"A refused start left its debugging on the emulator: {Left(tools, serial)}");
        }
        finally
        {
            foreach (DeviceStart start in (DeviceStart[])[debugged, inspected])
            {
                Ui.Await(start.TakeOutAsync(), "what a start put in taken out", Slow);
                start.Dispose();
            }
        }
    }

    /// <summary>A debugged app ended on the emulator from outside its debugger, as a crash or another program's force-stop
    /// ends it, ends its session here and leaves nothing of its debugging there: the relay's port forward, the relay, the
    /// app's mark as debugged.</summary>
    private static void A_debugged_app_ended_on_the_emulator_from_outside_ends_its_session_and_leaves_nothing_of_its_debugging_there()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start on the emulator", Slow);
            string serial = AndroidSerials.Of(window, target);
            Ui.WaitFor(() => Pid(tools, serial).Length > 0, () => $"the debugged app to run on the emulator: {Ui.Said(window)}\n{Console(window)}", Slow);
            Assert.True(Left(tools, serial).Length > 0, "The debug start put nothing of its own on the emulator, so there is nothing its end must take out");

            Adb(tools, serial, "shell", "am", "force-stop", Package);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, () => $"the debug session to end with its app on the emulator: {Ui.Said(window)}", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"what the debugging put on the emulator to be taken out once its app ended there; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Debug start stopped once Passenger's relay is in the app and a port forwarded to it takes out what it put on
    /// the emulator by then, the relay, its environment and the forward, so a start that never ran leaves the device as it
    /// found it and the app's next start loads no relay.</summary>
    private static void A_debug_start_stopped_once_its_relay_is_in_the_app_leaves_nothing_of_it_on_the_emulator()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Choose(window, project, target);
            DeviceLaunches.HeldPuts = () =>
            {
                reached.TrySetResult();
                return go.Task;
            };
            Task<string?> debugging = window.DebugAsync();
            Ui.PumpUntil(() => reached.Task.IsCompleted || debugging.IsCompleted, "the build, the install and the relay put in the app", Slow);
            if (!reached.Task.IsCompleted)
            {
                Assert.Fail($"The debug start ended before it put its relay in the app: {Ui.AwaitPumped(debugging, "the start's end")}{Ui.BuildErrors(window)}");
            }
            // The start holds the window busy until it goes on, so the key is pressed with no wait for quiet.
            Ui.Run(window, AppActions.Stop, settle: false);
            go.TrySetResult();
            // A start stopped partway ends canceled, with no answer to read.
            Ui.WaitFor(() => debugging.IsCompleted, "the stopped start to end", Slow);
            string serial = Serial(target, project);
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"what the stopped start put on the emulator to be taken out; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            DeviceLaunches.HeldPuts = null;
            go.TrySetResult();
            window.Close();
        }
    }

    /// <summary>Passenger ending while a debugged app's end on the emulator is held past the limit it gives the rest of what
    /// it started waits for it, as it may take force-stop's ten seconds and more on a loaded machine: the process goes only
    /// once the app has ended there and nothing of its debugging is left, the relay's forward, the relay, the app's mark as
    /// debugged.</summary>
    private static void Passenger_ending_while_a_debugged_apps_end_on_the_emulator_is_held_waits_until_nothing_of_it_is_left_there()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closed = false;
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start on the emulator", Slow);
            string serial = AndroidSerials.Of(window, target);
            Ui.WaitFor(() => Pid(tools, serial).Length > 0, () => $"the debugged app to run on the emulator: {Ui.Said(window)}\n{Console(window)}", Slow);
            Assert.True(Left(tools, serial).Length > 0, "The debug start put nothing of its own on the emulator, so there is nothing Passenger's end must take out");

            DeviceLaunches.HeldEnds = () =>
            {
                reached.TrySetResult();
                return go.Task;
            };
            Ui.CloseAsQuit(window);
            closed = true;
            Task ended = window.Ended ?? throw new InvalidOperationException("The window closed with no end to wait for");
            Ui.PumpUntil(() => reached.Task.IsCompleted, "the debugged app's end on the emulator to begin as Passenger ends", Slow);
            Ui.Hold(MainWindow.EndLimit + TimeSpan.FromSeconds(2), "past the limit Passenger's end gives the rest of what it started");
            if (ended.IsCompleted)
            {
                Assert.Fail($"Passenger would have gone with {Left(tools, serial)} still on the emulator, its app's end there held");
            }
            go.TrySetResult();
            Ui.AwaitPumped(ended, "Passenger's end once the app's end on the emulator goes on", Slow);
            string left = Left(tools, serial);
            Assert.True(left.Length == 0, $"Passenger's end was done with {left} still on the emulator");
            string running = Pid(tools, serial);
            Assert.True(running.Length == 0, $"Passenger's end was done with its debugged app still running on the emulator as {running}");
        }
        finally
        {
            DeviceLaunches.HeldEnds = null;
            go.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    /// <summary>Run with Hot Reload stopped once all its start puts in the app is there, the relay and a port forwarded to
    /// it, never starts the app: once Stop and the start have answered, no process of the app runs on the emulator and
    /// nothing of the start is left there, where the start went on to start the app Stop had said it stopped.</summary>
    private static void A_run_stopped_once_its_relay_is_in_the_app_never_starts_the_app_on_the_emulator()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { HotReload = true });
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Choose(window, project, target);
            DeviceLaunches.HeldStarts = () =>
            {
                reached.TrySetResult();
                return go.Task;
            };
            Task<string?> running = window.RunAsync();
            Ui.PumpUntil(() => reached.Task.IsCompleted || running.IsCompleted, "the build, the install and the relay put in the app", Slow);
            if (!reached.Task.IsCompleted)
            {
                Assert.Fail($"The run ended before it put its relay in the app: {Ui.AwaitPumped(running, "the run's end")}{Ui.BuildErrors(window)}");
            }
            Task stopped = window.Builds.StopAllAsync();
            go.TrySetResult();
            Ui.AwaitPumped(stopped, "Stop's answer once the start it stopped has settled", Slow);
            Ui.PumpUntil(() => running.IsCompleted, "the stopped run's answer", Slow);
            string serial = Serial(target, project);
            Ui.Hold(TimeSpan.FromSeconds(5), "a moment in which a start that went on would have started the app");
            string pid = Pid(tools, serial);
            Assert.True(pid.Length == 0, $"The run Stop stopped once its relay was in the app started the app all the same, which runs as {pid}: "
                + $"{(running.IsCompletedSuccessfully ? running.Result : running.Exception?.Message) ?? "it said it runs"}\n{Output(window)}");
            Assert.Equal(Passenger.App.Run.BuildRunController.StoppedStarting("AndroidApp"), Ui.AwaitPumped(running, "the stopped run's answer"));
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"what the stopped run put on the emulator to be taken out; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            DeviceLaunches.HeldStarts = null;
            go.TrySetResult();
            window.Close();
        }
    }

    /// <summary>The relay and an environment naming it, left in the app by a start whose end never reached the emulator (its
    /// device gone as it ended), are out before the app's next Run starts it: the app starts at once without the relay,
    /// where it loaded it and waited a minute for a debugger, and its environment is the build's link again.</summary>
    private static void A_relay_a_vanished_end_left_in_the_app_is_out_before_its_next_run_starts_it()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.RunAsync(), "the build, the install and the start on the emulator", Slow);
            AndroidApp app = window.Session.RunPlan?.Device?.App as AndroidApp ?? throw new InvalidOperationException("The run on the emulator named no app there");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => Pid(tools, app.Serial).Length == 0, () => $"the app to end on the emulator once stopped; it runs as {Pid(tools, app.Serial)}", TimeSpan.FromSeconds(30));
            string abi = LeaveRelay(tools, app);
            Assert.True(Left(tools, app.Serial).Contains("the relay in the app", StringComparison.Ordinal), "The relay was not left in the app as a vanished end leaves it");

            Ui.Launched(window, window.RunAsync(), "the next run's start on the emulator", Slow);
            Ui.WaitFor(() => Output(window).Contains("Hello from Passenger's test app, 42", StringComparison.Ordinal),
                () => $"the app to start at once, its log in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", TimeSpan.FromSeconds(40));
            string pid = Pid(tools, app.Serial);
            string loaded = Adb(tools, app.Serial, "shell", $"run-as {Package} cat /proc/{pid}/maps");
            Assert.False(loaded.Contains(AndroidApp.RelayFile, StringComparison.Ordinal), "The next run's app loaded the relay a vanished end left in it, so its start waits a minute for a debugger");
            Assert.True(Left(tools, app.Serial).Length == 0, $"The next run started the app with {Left(tools, app.Serial)} still in it");
            string environment = Adb(tools, app.Serial, "shell", $"run-as {Package} readlink files/.__override__/{abi}/environment").Trim();
            Assert.True(environment.StartsWith("/data/local/tmp/", StringComparison.Ordinal),
                $"The app's environment is not the build's link again, so its starts run without the build's variables: {environment}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A debugged app ended on the emulator and started from its launcher while its end there is held, as where the
    /// device goes as it ends, loads no relay: the app's environment is the build's again from the relay's hello on, where it
    /// still named the relay, which held the launcher's start a minute for a debugger.</summary>
    private static void A_debugged_app_started_from_its_launcher_while_its_end_is_held_loads_no_relay()
    {
        (string folder, string project, DeviceTarget target, AndroidTools tools) = Prepared();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Choose(window, project, target);
            Ui.Launched(window, window.DebugAsync(), "the debug session's start on the emulator", Slow);
            string serial = AndroidSerials.Of(window, target);
            Ui.WaitFor(() => Pid(tools, serial).Length > 0, () => $"the debugged app to run on the emulator: {Ui.Said(window)}\n{Console(window)}", Slow);

            DeviceLaunches.HeldEnds = () =>
            {
                reached.TrySetResult();
                return go.Task;
            };
            Ui.Run(window, AppActions.Stop, settle: false);
            Ui.PumpUntil(() => reached.Task.IsCompleted, "the debugged app's end on the emulator to begin", Slow);
            Adb(tools, serial, "shell", "am", "force-stop", Package);
            Adb(tools, serial, "shell", "monkey", "-p", Package, "-c", "android.intent.category.LAUNCHER", "1");
            // Its process comes before its runtime: read once it greets, or has the relay, which holds its greeting a minute.
            string pid = string.Empty;
            string loaded = string.Empty;
            Ui.WaitFor(() => (pid = Pid(tools, serial)).Length > 0
                && ((loaded = Adb(tools, serial, "shell", $"run-as {Package} cat /proc/{pid}/maps")).Contains(AndroidApp.RelayFile, StringComparison.Ordinal)
                    || Adb(tools, serial, "logcat", "-d", $"--pid={pid}", "-s", "PassengerTest:I").Contains("Hello from Passenger's test app", StringComparison.Ordinal)),
                () => $"the app started from its launcher to greet, or to load the relay; it runs as {(pid.Length > 0 ? pid : "nothing yet")}", TimeSpan.FromSeconds(30));
            Assert.False(loaded.Contains(AndroidApp.RelayFile, StringComparison.Ordinal),
                "The app started from its launcher loaded the relay of a debugging whose end had not reached the emulator, so its start waits a minute for a debugger");

            go.TrySetResult();
            Ui.WaitFor(() => Left(tools, serial).Length == 0 && Pid(tools, serial).Length == 0,
                () => $"the held end to end the app and take out what the debugging put on the emulator; it still has {Left(tools, serial)}, the app running as {Pid(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            DeviceLaunches.HeldEnds = null;
            go.TrySetResult();
            window.Close();
        }
    }

    /// <summary>Passenger's relay and an environment naming it put in the installed app as a Debug start puts them, and nothing
    /// else of a start (no port forwarded, the app not marked as debugged): what a start's end leaves where its device went
    /// as it ended. The device's ABI, whose folder they are in.</summary>
    private static string LeaveRelay(AndroidTools tools, AndroidApp app)
    {
        string abi = Adb(tools, app.Serial, "shell", "getprop", "ro.product.cpu.abi").Trim();
        string runtime = AndroidApp.RelayRuntime(abi) ?? throw new InvalidOperationException($"No relay is built for the emulator's {abi}");
        string relay = CarriedFiles.Relay(runtime, AndroidApp.RelayFile);
        string environment = Path.Combine(TestFolders.Create("android-left"), "environment");
        File.WriteAllBytes(environment, AndroidEnvironment.Write(AndroidEnvironment.With(AndroidEnvironment.Read(File.ReadAllBytes(app.BuiltEnvironment(abi))),
            app.RelayVariables(abi, "passenger-debug-left", "left"))));
        Adb(tools, app.Serial, "push", relay, app.Staged("relay"));
        Adb(tools, app.Serial, "push", environment, app.Staged("environment"));
        IReadOnlyList<string> put = app.PutIn(abi, relay: true, []);
        ChildResult putIn = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, put))), "the relay put in the app");
        Assert.True(putIn.Succeeded, $"The relay could not be left in the app: {putIn.Said}");
        return abi;
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
        if (!((string[])["android-arm64", "android-x64"]).Any(runtime => File.Exists(CarriedFiles.Relay(runtime, AndroidApp.RelayFile))))
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
    /// update handlers' socket reversed to Passenger, the relay in the app, the app marked as debugged; empty once its end
    /// took them away.</summary>
    private static string Left(AndroidTools tools, string serial)
    {
        string forwards = Adb(tools, serial, "forward", "--list");
        string reverses = Adb(tools, serial, "reverse", "--list");
        string relay = Adb(tools, serial, "shell", $"run-as {Package} ls files/.__override__/arm64-v8a files/.__override__/x86_64 2>/dev/null");
        string debugged = Adb(tools, serial, "shell", "dumpsys activity processes | grep mDebugApp=");
        return string.Join("; ", [.. forwards.Split('\n').Where(l => l.StartsWith(serial + " ", StringComparison.Ordinal) && l.Contains("localabstract:passenger-debug-", StringComparison.Ordinal))
                .Select(l => l.Trim()),
            .. reverses.Split('\n').Where(l => l.Contains("localabstract:passenger-handlers-", StringComparison.Ordinal)).Select(l => l.Trim()),
            .. relay.Contains(AndroidApp.RelayFile, StringComparison.Ordinal) ? ["the relay in the app"] : (string[])[],
            .. debugged.Contains($"mDebugApp={Package}/", StringComparison.Ordinal) ? [$"{Package} marked as debugged"] : (string[])[]]);
    }

    /// <summary>The serial of the emulator a test runs on: the target's own where it ran when listed, else the one its AVD
    /// runs as once a start booted it.</summary>
    private static string Serial(DeviceTarget target, string project) => target.Running ? target.Id
        : Ui.Await(Waiting.Apart(() => AndroidDevices.ListAsync(project, "net11.0-android", new Dictionary<string, string>(), CancellationToken.None)), "the Android workload's devices", Slow)
            .Targets.FirstOrDefault(t => t.Running && t.Avd == target.Avd)?.Id ?? throw new InvalidOperationException($"{target.Name} was not booted as an emulator the workload lists");

    /// <summary>The app's process on the emulator of <paramref name="serial"/>, as pidof says it: empty once it has ended.</summary>
    private static string Pid(AndroidTools tools, string serial) => Adb(tools, serial, "shell", "pidof", Package).Trim();

    /// <summary>What adb says to a command for the emulator of <paramref name="serial"/>.</summary>
    private static string Adb(AndroidTools tools, string serial, params string[] command) =>
        Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, .. command]))), $"adb {string.Join(' ', command)} on {serial}").Output;
}

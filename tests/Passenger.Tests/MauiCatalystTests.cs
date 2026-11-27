using System.Runtime.InteropServices;
using Avalonia.Controls;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Mcp;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, its Mac Catalyst head run and debugged on this Mac, where Run with nothing
/// chosen runs it, which the system opens hidden and never in front, none of the windows it kept reopened, as nobody sees
/// this window: Passenger's relay is carried into the sandboxed app's container, where it listens on a Unix socket. Built
/// with .NET's assemblies whole, Run with Hot Reload carries the inspector and the update handlers' hook in too, so its tree
/// is the app's window, a tap reaches its button's centre, a C# edit lands and its update handler is called, and XAML edits
/// show, patched or the page made again; built as the template has it, which trims them, Debug says they stay out, and stops
/// in its main page, which its interpreter runs, steps, shows, evaluates and takes an edit. Stop ends the app and takes out
/// what was carried, its start's own instance alone where it is debugged and run at once; an agent's Passenger killed with
/// its tree ends the app it opened too. Skips, saying why, off a Mac, without MAUI's and Mac Catalyst's workloads or Xcode,
/// or the Mac Catalyst relay. Debugged through the window, so alone; waits end within the run's limit (<see cref="Limits.WithinHang"/>).</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class MauiCatalystTests : McpRig
{
    private static TimeSpan Slow => Limits.WithinHang(TimeSpan.FromMinutes(8));

    /// <summary>What the update handler the test adds to the app writes once Hot Reload has applied an edit.</summary>
    private const string Handled = "Update handler called for MainPage";

    /// <summary>The MAUI app's Mac Catalyst head built whole, run on this Mac with Hot Reload: its tree, a tap, a C# edit and XAML edits.</summary>
    [AvaloniaFact]
    public void A_maui_apps_mac_catalyst_head_run_with_hot_reload_shows_its_tree_takes_a_tap_and_an_edit_and_ends_with_stop()
    {
        using IDisposable hang = Limits.BeginHang();
        (string folder, string project) = Prepared("maui-catalyst-run", whole: true);
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Chosen(window);
            Assert.Equal(DeviceTargets.Here(TargetPlatform.MacCatalyst).Shown, window.FindControl<TextBlock>("RunTargetName")!.Text);
            Ui.Launched(window, window.RunAsync(), "the Mac Catalyst head's build and its opening with Hot Reload", Slow);
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected } && window.Debugger.Running.Count == 1,
                () => $"the app's inspector to connect and the debugger to attach for Hot Reload; the inspector is {window.Session.RunApp?.State.ToString() ?? "not made"} "
                    + $"({window.Session.RunApp?.Problem}), the debugger runs {window.Debugger.Running.Count} quietly: {Ui.Said(window)}\n{Output(window)}", Slow);
            Assert.False(window.Debugger.IsDebugging, "Run with Hot Reload showed a debugging session");
            MacCatalystApp opened = Opened(window);
            string[] opening = [.. window.Session.RunPlan!.Arguments.Take(window.Session.RunPlan.Leading)];
            Assert.True(opening.Contains("-g") && opening.Contains("-j"), $"The app opened where the person sees it, though nobody sees this window: {string.Join(' ', opening)}");
            LiveApp app = window.Session.RunApp!;
            Assert.True(app is { Kind: LiveAppKind.Maui } && app.Framework.StartsWith("MAUI 11.", StringComparison.Ordinal), $"The app was taken for {app.Kind}, {app.Framework}");
            AppTree tree = new([]);
            Ui.WaitFor(() => AppTree.Walk((tree = Ui.AwaitPumped(app.TreeAsync(), "the app's tree", TimeSpan.FromMinutes(1))).Roots).Any(e => e.Element is { Type: "Button", Name: "CounterBtn" }),
                () => $"the main page's CounterBtn in the app's tree, which holds {string.Join(", ", AppTree.Walk(tree.Roots).Select(e => $"{e.Element.Type}#{e.Element.Name}").Take(40))}",
                TimeSpan.FromMinutes(1));
            Element button = AppTree.Walk(tree.Roots).Select(e => e.Element).First(e => e is { Type: "Button", Name: "CounterBtn" });
            Element placed = AppTree.Walk(Ui.AwaitPumped(app.AgentTreeAsync([]), "the app's tree with where each element is", TimeSpan.FromMinutes(1)).Roots)
                .Select(e => e.Element).First(e => e.Id == button.Id);
            Assert.True(placed is { Bounds: not null, OutOfView: not true }, $"The button is not where its window shows it: at {placed.Bounds}, out of view {placed.OutOfView}");
            InputDone tapped = Ui.AwaitPumped(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "a tap on the button", TimeSpan.FromMinutes(1));
            Assert.True(tapped.Target == button.Id && tapped.Shows == "Clicked 1 time", $"The tap reached {tapped.TargetName}, which shows \"{tapped.Shows}\", not the button counting it");
            Assert.True(Math.Abs(tapped.X!.Value - (placed.Bounds!.X + (placed.Bounds.Width / 2))) < 1 && Math.Abs(tapped.Y!.Value - (placed.Bounds.Y + (placed.Bounds.Height / 2))) < 1,
                $"The tap was aimed at {tapped.X},{tapped.Y}, not the centre of the button at {placed.Bounds}: the window's area and its elements' places are in different points");
            string carried = Carried(opened);

            window.OpenFile(Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml.cs"));
            Typing.Replace(window, "$\"Clicked {count} times\"", "$\"Tapped {count} times\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, () => $"Hot Reload to say what it applied: {Ui.Said(window)}", Slow);
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"The app did not take the edit: {window.Debugger.LastApplied.Said}\n{Output(window)}");
            Ui.WaitFor(() => Output(window).Contains(Handled, StringComparison.Ordinal),
                () => $"the app's update handler to be called with the type the edit changed: {Ui.Said(window)}\n{Output(window)}", TimeSpan.FromMinutes(1));
            InputDone again = Ui.AwaitPumped(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "a tap on the button after the edit", TimeSpan.FromMinutes(1));
            Assert.True(again.Shows == "Tapped 2 times", $"The button's click ran as built, not as edited: it shows \"{again.Shows}\"");

            // The page's XAML, whose code MAUI's source generator writes: in the program's update, then patched into the open page by MAUI.
            window.OpenFile(Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml"));
            Typing.Replace(window, "Text=\"Hello, World!\"", "Text=\"Hello, Passenger!\"");
            MauiApp.XamlApplied(window, "a property's edit", Slow);
            MauiApp.Shows(window, app, "Hello, Passenger!", "the edited label's text");
            Assert.True(Ui.AwaitPumped(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "a tap on the button after the XAML edit",
                TimeSpan.FromMinutes(1)).Shows == "Tapped 3 times", "The page was made again rather than patched: its button lost its count");
            Typing.Replace(window, "            <Button", "            <Label Text=\"Added by Hot Reload\" />\n\n            <Button");
            MauiApp.XamlApplied(window, "an added element", Slow);
            MauiApp.Shows(window, app, "Added by Hot Reload", "the added label");
            Typing.Replace(window, "Style=\"{StaticResource SubHeadline}\"", "Style=\"{StaticResource Headline}\"");
            MauiApp.XamlApplied(window, "a style's edit", Slow);
            MauiApp.Shows(window, app, "Welcome to \n.NET Multi-platform App UI", "the welcome label in the headline's size", ("FontSize", "32"));
            // A binding's and a C# expression's edits, which MAUI's patch may pass over: the page is made again from its XAML.
            Typing.Replace(window, "<Label Text=\"Added by Hot Reload\" />", "<Label Text=\"{Binding Text, Source={x:Reference CounterBtn}}\" />");
            MauiApp.XamlApplied(window, "a binding to the button's text", Slow);
            Assert.True(window.Debugger.LastApplied!.Said.Contains("MainPage made again", StringComparison.Ordinal),
                $"The page was not made again for a binding's edit: {window.Debugger.LastApplied.Said}");
            MauiApp.ShowsButtonsText(window, app, string.Empty, "the label bound to the button's text, made again with the page", "Click me");
            Ui.AwaitPumped(app.InputAsync(new InputAction(InputKinds.Click) { Element = MauiApp.Button(app).Id }), "a tap on the page's button made again", TimeSpan.FromMinutes(1));
            MauiApp.ShowsButtonsText(window, app, string.Empty, "the label bound to the button's text after a tap", "Tapped 4 times");
            Typing.Replace(window, "HorizontalOptions=\"Fill\" />", "HorizontalOptions=\"Fill\" />\n\n<Label Text=\"{CounterBtn.Text + '!'}\" />");
            MauiApp.XamlApplied(window, "an element with a C# expression of the button's text", Slow);
            MauiApp.ShowsButtonsText(window, app, "!", "the added label whose C# expression reads the button's text", "Click me");
            Typing.Replace(window, "Text=\"Hello, Passenger!\"", "Txt=\"Hello, Passenger!\"");
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            HotReloadOutcome? refused = null;
            Ui.WaitFor(() => (refused = window.Debugger.LastApplied) is { Result: not HotReloadResult.Applied }, () => $"Hot Reload to refuse a property the label lacks: {Ui.Said(window)}", Slow);
            Assert.True(refused!.Result == HotReloadResult.Errors && refused.Problems.Any(p => p.Path is { } path && path.EndsWith("MainPage.xaml", StringComparison.Ordinal)),
                $"An edit naming a property the label lacks was not refused at its place in the XAML: {refused.Result}, {refused.Said}");
            MauiApp.Shows(window, app, "Hello, Passenger!", "the label as it was before the refused edit");
            Typing.Replace(window, "Txt=\"Hello, Passenger!\"", "Text=\"Hello, Passenger!\"");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the quiet session to end with the app");
            Ui.WaitFor(() => Running(opened) is null, () => $"the app to end once stopped; process {Running(opened)} still runs it", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(carried), () => $"what the run carried into the app's container to be taken out once stopped; {carried} is still there", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(opened.Pipes), () => $"the killed opener's pipes to be taken out once stopped; {opened.Pipes} is still there", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The MAUI app's Mac Catalyst head as the template builds it, debugged on this Mac, its trimmed assemblies said;
    /// then debugged and run at once, each instance ended alone, from the one build.</summary>
    [AvaloniaFact]
    public void A_maui_apps_mac_catalyst_head_is_debugged_as_the_template_builds_it_and_beside_a_run_each_ending_alone()
    {
        using IDisposable hang = Limits.BeginHang();
        (string folder, string project) = Prepared("maui-catalyst-debug", whole: false);
        try
        {
            Flow.Step("a MAUI app's Mac Catalyst head built as the template has it is debugged, its assemblies trimmed said, and takes a Hot Reload edit",
                () => A_maui_apps_mac_catalyst_head_built_as_the_template_has_it_is_debugged_its_assemblies_trimmed_said_and_takes_a_hot_reload_edit(folder, project));
            Flow.Step("a MAUI app's Mac Catalyst head debugged and run at once ends each instance alone with its own start",
                () => A_maui_apps_mac_catalyst_head_debugged_and_run_at_once_ends_each_instance_alone_with_its_own_start(folder));
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private static void A_maui_apps_mac_catalyst_head_built_as_the_template_has_it_is_debugged_its_assemblies_trimmed_said_and_takes_a_hot_reload_edit(string folder, string project)
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Chosen(window);
            string page = Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml.cs");
            window.OpenFile(page);
            Ui.Settle();
            int line = Array.FindIndex(File.ReadAllLines(page), l => l.Trim() == "InitializeComponent();");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(line, File.ReadAllLines(page)[line].IndexOf('I', StringComparison.Ordinal)));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the Mac Catalyst head's build and its debugged opening", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is not null,
                () => $"the stop at the breakpoint in MainPage's constructor with its locals; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: "
                    + $"{Ui.Said(window)}\n{Console(window)}", Slow);
            Assert.Contains("Content = (null)", Shown(window, "this"), StringComparison.Ordinal);
            Assert.True(Console(window).Contains(MacCatalystApp.Trimmed(MauiApp.Name), StringComparison.Ordinal),
                $"The Debug console did not say the trimmed app takes no inspector or update handlers' hook:\n{Console(window)}");
            MacCatalystApp opened = window.Debugger.Tabs.Select(t => t.Plan?.MacCatalyst?.App).LastOrDefault(a => a is not null)
                ?? throw new Xunit.Sdk.XunitException("The session's plan opens no Mac Catalyst app");
            string carried = Carried(opened);
            Assert.True(Running(opened) is not null, $"No process runs {opened.Executable} while it is debugged");

            // Breakpoints in the page's XAML bind in the code MAUI's source generator wrote of it; AppShell's, inflated as the app runs, say why they cannot.
            (string xaml, int hello, int clickMe) = MauiApp.XamlBreakpoints(window, project);
            string shell = Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "AppShell.xaml");
            window.OpenFile(shell);
            Ui.Settle();
            window.EditorArea.CodeEditor.GoTo(new TextPosition(Array.FindIndex(File.ReadAllLines(shell), l => l.TrimStart().StartsWith("<ShellContent", StringComparison.Ordinal)), 0));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            LineBreakpoint inShell = window.Debugger.Breakpoints.In(shell).Single();
            Ui.WaitFor(() => inShell.Status == BreakpointStatus.Invalid && inShell.Message?.Contains("inflator", StringComparison.Ordinal) == true,
                () => $"AppShell.xaml's breakpoint said not to bind, its page not source-generated; it is {inShell.Status}: {inShell.Message}", Slow);
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
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"The app did not take the edit: {window.Debugger.LastApplied.Said}\n{Console(window)}");
            Ui.Await(window.Debugger.Session!.EvaluateAsync("OnCounterClicked(null, EventArgs.Empty)", window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch),
                "the click handler the edit changed, called where the app stopped", Slow);
            Dap.EvaluateResponse text = Ui.Await(window.Debugger.Session!.EvaluateAsync("CounterBtn.Text", window.BottomPanel.Debug.Frames[0].Id, Dap.EvaluateContexts.Watch),
                "the button's text after the edited handler ran", Slow);
            Assert.True(text.Result == "\"Pressed 1 time\"", $"The click handler ran as built, not as edited: the button says {text.Result}");

            Ui.Run(window, AppActions.Resume);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Running(opened) is null, () => $"the debugged app to end once stopped; process {Running(opened)} still runs it", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(carried), () => $"what the session carried into the app's container to be taken out once stopped; {carried} is still there",
                TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(opened.Pipes), () => $"the killed opener's pipes to be taken out once stopped; {opened.Pipes} is still there", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The same app debugged and run at once, by two configurations of it (Run of the one debugged would end its
    /// session first), each start opening an instance of its own: each is told from the other by its start's own token, so
    /// the debugged one's end ends it alone and Run's runs on until its own Stop, and neither leaves its opener's pipes
    /// behind, though each opener is killed.</summary>
    private static void A_maui_apps_mac_catalyst_head_debugged_and_run_at_once_ends_each_instance_alone_with_its_own_start(string folder)
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = false });
        try
        {
            Chosen(window);
            Ui.Launched(window, window.DebugAsync(), "the Mac Catalyst head's build and its debugged opening", Slow);
            MacCatalystApp? debugged = null;
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Running
                    && (debugged = window.Debugger.Tabs.Select(t => t.Plan?.MacCatalyst?.App).LastOrDefault(a => a is not null)) is { Process.Id: not null },
                () => $"the debugged app to run and its start to find its process; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}, "
                    + $"the start found {debugged?.Process.Id?.ToString() ?? "no process"}: {Ui.Said(window)}\n{Console(window)}", Slow);
            int first = debugged!.Process.Id!.Value;
            Passenger.App.Run.RunConfiguration twin = window.Configurations.Single(c => c.Name == MauiApp.Name) with { Name = $"{MauiApp.Name} twin", Generated = false };
            window.SetConfigurations([twin], twin.Name);
            Ui.Launched(window, window.RunAsync(), "Run's opening of a second instance while the first is debugged", Slow);
            MacCatalystApp ran = Opened(window);
            Ui.WaitFor(() => ran.Process.Id is not null, () => $"Run's start to find its own process: {Ui.Said(window)}\n{Output(window)}", TimeSpan.FromMinutes(1));
            int second = ran.Process.Id!.Value;
            Assert.True(first != second, $"Run's start took the debugged instance, process {first}, for its own");
            Assert.True(Runs(debugged, first) && Runs(ran, second), $"Process {first} or {second} does not run {ran.Executable}");

            Ui.Await(window.Debugger.StopAsync(window.Debugger.Tabs.First(t => t.IsLive)), "the debug session's stop", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => !Runs(debugged, first) && !Directory.Exists(debugged.Pipes),
                () => $"the debugged instance (process {first}) to end once its session stopped and its opener's pipes to be taken out "
                    + $"({(Directory.Exists(debugged.Pipes) ? $"{debugged.Pipes} is still there" : "they are")})", TimeSpan.FromSeconds(30));
            Ui.Hold(TimeSpan.FromSeconds(2), "for the debugged start's end, which has taken out its pipes, to have ended whatever it would");
            Assert.True(Runs(ran, second), $"Run's instance (process {second}) ended with the debugged one's session, though nobody stopped it");
            Assert.True(window.Runner.IsRunning && Directory.Exists(ran.Pipes), "Run's opener ended, or its pipes went, with the debugged one's session");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => !Runs(ran, second) && !Directory.Exists(ran.Pipes),
                () => $"Run's instance (process {second}) to end once stopped and its opener's pipes to be taken out "
                    + $"({(Directory.Exists(ran.Pipes) ? $"{ran.Pipes} is still there" : "they are")})", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Whether process <paramref name="id"/> runs <paramref name="app"/>'s program.</summary>
    private static bool Runs(MacCatalystApp app, int id) => Libc.ExecutableOf(id) is { } program && PathNames.Same(program, app.Executable);

    /// <summary>An agent's Passenger that ran the app is killed as a test runner kills a hung host, every process of its tree
    /// first, the deepest first, then itself: the app, which the system started apart from Passenger, still ends with it, as
    /// its watchdog, in no tree of Passenger's, ends what Passenger took on.</summary>
    [Fact]
    public async Task A_maui_apps_mac_catalyst_head_an_agents_passenger_ran_ends_once_that_passenger_and_its_tree_are_killed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("A .NET MAUI app's Mac Catalyst head runs only on a Mac");
        }
        string folder = PathNames.Canonical(TestFolders.Create("maui-catalyst-killed"));
        DeleteAtEnd(folder);
        (_, string? unmade) = await MauiApp.CreateMacCatalystHeadAsync(folder);
        if (unmade is not null)
        {
            Assert.Skip(unmade);
        }
        int? app = null;
        try
        {
            McpClient client = await Start(["--mcp", folder], languageServer: false);
            // Each program it takes on is in its log as it does.
            await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["logEveryProgram"] = true } });
            await Until(async () => (await Call(client, McpTools.Workspace)).GetProperty("runConfigurations").EnumerateArray().Any(c => c.GetString() == MauiApp.Name),
                "the app's run configuration");
            JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = MauiApp.Name, ["waitSeconds"] = 0 });
            Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"run did not leave the app running: {ran}");
            string log = Path.Combine(OwnState, "logs", "passenger.log");
            await Until(() => (app = Opened(folder)) is int id && File.Exists(log) && Ui.Shared(log).Contains($"Process {id} started", StringComparison.Ordinal),
                "the app to run and Passenger to take it on, as its log says");

            System.Diagnostics.Process passenger = LastPassenger();
            int[] killed = [.. Processes.Descendants(passenger.Id).Reverse(), passenger.Id];
            foreach (int id in killed)
            {
                OwnProcess.Of(id).Kill(alone: true);
            }
            await Until(() => killed.All(id => !Processes.IsRunning(id)), "Passenger and every process of its tree killed");
            await Until(() => Opened(folder) is null, $"the app (process {app}) to end with the Passenger that ran it, though no process of its tree is left");
        }
        finally
        {
            if (app is int left && Opened(folder) == left)
            {
                OwnProcess.Of(left).Kill(alone: true);
            }
        }
    }

    /// <summary>The process running a Mac Catalyst app built in <paramref name="folder"/>; null where none does.</summary>
    private static int? Opened(string folder)
    {
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (Libc.ExecutableOf(process.Id) is { } program && PathNames.IsUnder(program, folder) && Path.GetFileName(program) == MauiApp.Name)
                {
                    return process.Id;
                }
            }
        }
        return null;
    }

    /// <summary>The MAUI template's Mac Catalyst head in a folder of its own, with an update handler that says each type an
    /// update changed, its build keeping .NET's assemblies <paramref name="whole"/> or trimming them as the template's does,
    /// or a skip saying why it cannot be made or run here.</summary>
    private static (string Folder, string Project) Prepared(string name, bool whole)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("A .NET MAUI app's Mac Catalyst head runs only on a Mac");
        }
        string runtime = $"maccatalyst-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";
        if (!File.Exists(CarriedFiles.Relay(runtime, MacCatalystApp.RelayFile)))
        {
            Assert.Skip($"This build carries no debug relay for {runtime}, which `just debug-relay apple` makes before a build carries it");
        }
        string folder = PathNames.Canonical(TestFolders.Create(name));
        (string project, string? unmade) = Ui.AwaitPumped(Waiting.Apart(() => MauiApp.CreateMacCatalystHeadAsync(folder)), "dotnet new maui and the Mac Catalyst head's restore", Slow);
        if (unmade is not null)
        {
            TestFolders.Delete(folder);
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
        if (whole)
        {
            File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "\t<PropertyGroup>\n\t\t<MtouchLink>None</MtouchLink>\n\t</PropertyGroup>\n</Project>",
                StringComparison.Ordinal));
        }
        else
        {
            // The shell inflated as the app runs, which MAUI's source generator writes no code of.
            File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "\t<ItemGroup>\n\t\t<MauiXaml Update=\"AppShell.xaml\" Inflator=\"Runtime\" />\n\t</ItemGroup>\n</Project>",
                StringComparison.Ordinal));
        }
        return (folder, project);
    }

    /// <summary>The app's configuration chosen, which runs on this Mac with nothing chosen, nobody seeing the window.</summary>
    private static void Chosen(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == MauiApp.Name), () => $"{MauiApp.Name}'s run configuration; the window has configurations "
            + $"{string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
        Assert.True(window.SelectConfiguration(MauiApp.Name));
        Assert.True(window.Unseen, "The test's window is one a person sees, so the app would open on their screen");
    }

    /// <summary>The Mac Catalyst app Run opened.</summary>
    private static MacCatalystApp Opened(MainWindow window) =>
        window.Session.RunPlan?.MacCatalyst?.App ?? throw new Xunit.Sdk.XunitException($"Run opened no Mac Catalyst app: it runs {window.Session.RunPlan?.Program}");

    /// <summary>Where the start carried what the app loads of Passenger's, in its container, which it has there now.</summary>
    private static string Carried(MacCatalystApp app)
    {
        Assert.True(app.Carried.Contains("/Library/Containers/" + MauiApp.Package + "/", StringComparison.Ordinal),
            $"The sandboxed app's files were carried outside its container, to {app.Carried}");
        Assert.True(File.Exists(Path.Combine(app.Carried, MacCatalystApp.RelayFile)), $"The relay was not carried into the app's container ({app.Carried}), where the app reads it");
        return app.Carried;
    }

    /// <summary>The process running the app's program; null where none does.</summary>
    private static int? Running(MacCatalystApp app)
    {
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (Libc.ExecutableOf(process.Id) is { } program && PathNames.Same(program, app.Executable))
                {
                    return process.Id;
                }
            }
        }
        return null;
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static string Console(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();

    /// <summary>A local's value as Variables shows it where the program stopped; null before it shows one.</summary>
    private static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;
}

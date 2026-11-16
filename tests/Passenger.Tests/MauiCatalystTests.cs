using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, its Mac Catalyst head run and debugged on this Mac, which the system opens
/// hidden and never in front, none of the windows it kept reopened, as nobody sees this window: Passenger's relay is carried
/// into the sandboxed app's container, where it listens on a Unix socket. Built with .NET's assemblies whole, Run with Hot
/// Reload carries the inspector and the update handlers' hook in too, so its tree is the app's window, a tap reaches its
/// button's centre, an edit lands and its update handler is called; built as the template has it, which trims them, Debug
/// says they stay out, and stops in its main page, which its interpreter runs, steps, shows, evaluates and takes an edit.
/// Stop ends the app and takes out what was carried. Skips, saying why, off a Mac, without MAUI's and Mac Catalyst's
/// workloads or Xcode, or the Mac Catalyst relay. Debugged through the window, so alone.</summary>
[Collection(Alone.Name)]
public sealed class MauiCatalystTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(8);

    /// <summary>What the update handler the test adds to the app writes once Hot Reload has applied an edit.</summary>
    private const string Handled = "Update handler called for MainPage";

    [AvaloniaFact]
    public void A_maui_apps_mac_catalyst_head_run_with_hot_reload_shows_its_tree_takes_a_tap_and_an_edit_and_ends_with_stop()
    {
        (string folder, string project) = Prepared("maui-catalyst-run", whole: true);
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Chosen(window);
            Ui.Launched(window, window.RunAsync(), "the Mac Catalyst head's build and its opening with Hot Reload", Slow);
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected } && window.Debugger.Running.Count == 1,
                () => $"the app's inspector to connect and the debugger to attach for Hot Reload; the inspector is {window.Session.RunApp?.State.ToString() ?? "not made"} "
                    + $"({window.Session.RunApp?.Problem}), the debugger runs {window.Debugger.Running.Count} quietly: {Ui.Said(window)}\n{Output(window)}", Slow);
            Assert.False(window.Debugger.IsDebugging, "Run with Hot Reload showed a debugging session");
            MacCatalystApp opened = Opened(window);
            Assert.True(window.Session.RunPlan!.Arguments.Take(window.Session.RunPlan.Leading).Contains("-g"), "The app opened where the person sees it, though nobody sees this window");
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

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the run to end once stopped");
            Ui.WaitFor(() => window.Debugger.Running.Count == 0, "the quiet session to end with the app");
            Ui.WaitFor(() => Running(opened) is null, () => $"the app to end once stopped; process {Running(opened)} still runs it", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => !Directory.Exists(carried), () => $"what the run carried into the app's container to be taken out once stopped; {carried} is still there", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_maui_apps_mac_catalyst_head_built_as_the_template_has_it_is_debugged_its_assemblies_trimmed_said_and_takes_a_hot_reload_edit()
    {
        (string folder, string project) = Prepared("maui-catalyst-debug", whole: false);
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

            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && Shown(window, "this") is { } shown && shown.Contains("ScrollView", StringComparison.Ordinal),
                () => $"the step over InitializeComponent, the page's content shown made; this is {Shown(window, "this") ?? "not shown"}: {Ui.Said(window)}", Slow);
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
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
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
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "debugger", "relay", runtime, MacCatalystApp.RelayFile)))
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

using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Inspector;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>XAML Hot Reload in a WPF app, as a person uses it: tests/Debuggees/WpfTreeApp built and launched by Debug or Run,
/// its MainWindow.xaml edited in the editor (a colour, a text, a button added that names the window's own handler) and
/// saved, and the app's own window, the same object, showing the edit; a click on the button added runs the handler, which
/// writes to the counter through its x:Name field; an element WPF has not is refused with its line, the window left as it
/// was. WPF's window shows on the screen, and WPF runs on Windows alone, so elsewhere the tests skip; run alone.</summary>
[Collection(Alone.Name)]
public sealed class WpfXamlHotReloadTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string OnlyOnWindows = "WPF runs on Windows alone";

    /// <summary>The counter's element, whole, before which the tests add elements.</summary>
    private const string Counter = "<TextBlock x:Name=\"Counter\" Text=\"Not clicked yet\" />";

    private readonly string folder = string.Empty;

    public WpfXamlHotReloadTests(RestoredFolders folders)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        folder = PathNames.Canonical(folders.Take("wpfreload-", made =>
        {
            foreach (string file in Directory.GetFiles(Debuggees.Source("WpfTreeApp")))
            {
                File.Copy(file, Path.Combine(made, Path.GetFileName(file)));
            }
            return WpfApp.Project(made, "WpfTreeApp");
        }));
    }

    public void Dispose()
    {
        if (folder.Length > 0)
        {
            TestFolders.Delete(folder);
        }
    }

    private string Xaml => Path.Combine(folder, "MainWindow.xaml");

    /// <summary>Under Debug, a save of the window's XAML puts its edits into the window as it runs, the button added runs
    /// the window's own handler, and an element WPF has not is refused at its line in Problems' Hot Reload tab.</summary>
    [AvaloniaFact]
    public void A_debugged_wpf_apps_saved_xaml_shows_in_its_window_and_an_element_wpf_has_not_is_refused_at_its_line()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Ui.OpenFolder(folder, 1400, 900, languageServer: false);
        try
        {
            Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.LiveApp is { State: LiveAppState.Connected }, $"the app's inspector to say hello ({window.Status})", TimeSpan.FromMinutes(2));
            LiveApp app = window.LiveApp!;
            long shell = Ui.Await(app.TreeAsync(), "the app's tree").Roots.Single(r => r.Type == "MainWindow").Id;

            Edit(window);
            HotReloadOutcome? before = window.Debugger.LastApplied;
            Ui.Run(window, AppActions.SaveAll);
            HotReloadOutcome applied = Said(window, before);
            Assert.True(applied.Result == HotReloadResult.Applied, $"The saved XAML did not apply: {applied.Said}");
            Assert.Contains("MainWindow.xaml (MainWindow)", applied.Said, StringComparison.Ordinal);
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element is { Type: "Button", Name: "Added" }), "the Live Visual Tree to show the button added");
            Shows(app, shell);

            Typing.Replace(window, Counter, $"<TextBlok Text=\"typo\" />\n{Counter}");
            before = window.Debugger.LastApplied;
            Ui.Run(window, AppActions.SaveAll);
            HotReloadOutcome refused = Said(window, before);
            Assert.True(refused.Result == HotReloadResult.Errors, $"The misspelt element was not refused as an error: {refused.Said}");
            int line = Array.FindIndex(File.ReadAllLines(Xaml), l => l.Contains("TextBlok", StringComparison.Ordinal));
            Problem problem = Assert.Single(window.BottomPanel.Problems.HotReloadProblems ?? [], p => p.Severity == ProblemSeverity.Error);
            Assert.Equal((Xaml, line), (problem.Path, problem.Position.Line));
            Assert.Contains("TextBlok", problem.Message, StringComparison.Ordinal);
            Assert.Equal("Caption", Find(Ui.Await(app.TreeAsync(), "the app's tree").Roots, "Caption")?.Name);
        }
        finally
        {
            if (window.Debugger.IsDebugging)
            {
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            }
            window.Close();
        }
    }

    /// <summary>Under Run, with no debugger, a save of the window's XAML puts its edits into the running window, said in the
    /// Run window, and the button added runs the window's own handler.</summary>
    [AvaloniaFact]
    public void A_running_wpf_apps_saved_xaml_shows_in_its_window_and_its_new_button_runs_the_windows_handler()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        MainWindow window = Ui.OpenFolder(folder, 1400, 900, languageServer: false);
        try
        {
            Ui.Launched(window, window.RunAsync(), "the app to build and start", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected }, $"the app's inspector to say hello ({window.Status})", TimeSpan.FromMinutes(2));
            LiveApp app = window.Session.RunApp!;
            long shell = Ui.Await(app.TreeAsync(), "the app's tree").Roots.Single(r => r.Type == "MainWindow").Id;

            Edit(window);
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() =>
            {
                OutputView run = window.BottomPanel.Run;
                run.Flush();
                return run.Document.Snapshot.ToString().Contains("Hot Reload: XAML changes applied: MainWindow.xaml (MainWindow)", StringComparison.Ordinal);
            }, () => $"the Run window to say the XAML applied ({window.Status})");
            Shows(app, shell);
        }
        finally
        {
            if (window.Runner.IsRunning)
            {
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => !window.Runner.IsRunning, "the run to end");
            }
            window.Close();
        }
    }

    /// <summary>The window's XAML edited as a person types: the caption's colour, Delete's words, and a button added that
    /// names the window's own handler.</summary>
    private void Edit(MainWindow window)
    {
        window.OpenFile(Xaml);
        Typing.Replace(window, "Foreground=\"SteelBlue\"", "Foreground=\"SeaGreen\"");
        Typing.Replace(window, "Content=\"Delete\"", "Content=\"Remove\"");
        Typing.Replace(window, Counter, $"<Button x:Name=\"Added\" Content=\"Added\" Click=\"OnCount\" />\n{Counter}");
    }

    /// <summary>The app shows the edits in the window it had, and a click on the button added counts through the window's
    /// handler into the counter the XAML made again.</summary>
    private static void Shows(LiveApp app, long shell)
    {
        Element root = Ui.Await(app.AgentTreeAsync(["Foreground"]), "the app's tree").Roots.Single(r => r.Type == "MainWindow");
        Assert.True(root.Id == shell, "The app's window is another than the one it had: the XAML made a new window rather than the one shown again");
        Assert.Contains("2E8B57", Find([root], "Caption")?.Values?.GetValueOrDefault("Foreground") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Remove", Find([root], "Delete")?.Text);
        Element added = Find([root], "Added") ?? throw new InvalidOperationException("The app has no button Added");
        Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = added.Id }), "the click on the button added");
        Ui.WaitFor(() => Find(Ui.Await(app.AgentTreeAsync([]), "the app's tree").Roots, "Counter")?.Text == "Clicked once",
            "the click to count through the window's handler into the counter the XAML made again");
    }

    private static HotReloadOutcome Said(MainWindow window, HotReloadOutcome? before)
    {
        Ui.WaitFor(() => window.Debugger.LastApplied is { } now && now != before, () => $"Hot Reload to say what the XAML came to ({window.Status})");
        return window.Debugger.LastApplied!;
    }

    private static Element? Find(IEnumerable<Element> elements, string name) =>
        elements.Select(e => e.Name == name ? e : Find(e.Children, name)).FirstOrDefault(e => e is not null);
}

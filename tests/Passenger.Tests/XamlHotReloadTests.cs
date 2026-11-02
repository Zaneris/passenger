using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>XAML Hot Reload in an Avalonia app, as a person uses it: tests/Debuggees/LiveTreeApp built and launched by Run or
/// Debug, its MainWindow.axaml edited in the editor (a text, a colour, a control added) and saved, and the app's own window,
/// the same object, showing the edit in its live visual tree; an edit the app's XAML compiler refuses said with its line,
/// the app left as it was; and a handler added in C# with the button that names it in XAML, applied together, C#'s first.
/// The app is headless, so nothing shows on the screen. It runs and is debugged through the window, so the tests run alone.</summary>
[Collection(Alone.Name)]
public sealed class XamlHotReloadTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public XamlHotReloadTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("xamlreload-", made => AvaloniaApp.Debuggee(made, "LiveTreeApp")));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string Xaml => Path.Combine(folder, "MainWindow.axaml");

    /// <summary>The password box's element, whole, before which the tests add elements.</summary>
    private const string Pin = "<TextBox x:Name=\"Pin\" PasswordChar=\"•\" />";

    /// <summary>Under Debug, a save of the window's XAML puts the edits into the window as it runs: its greeting's new text,
    /// Delete's new colour and a button added, in the Live Visual Tree under the same window; then an element the XAML names
    /// that Avalonia has not is refused, said with its line in Problems' Hot Reload tab, and the window keeps what it had.</summary>
    [AvaloniaFact]
    public void A_debugged_apps_saved_xaml_shows_in_its_live_tree_and_an_edit_its_compiler_refuses_is_said_at_its_line()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        try
        {
            Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Delete"),
                $"the Live Visual Tree to show the app's window ({window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));
            LiveApp app = window.LiveApp!;
            long shell = Ui.Await(app.TreeAsync(), "the app's tree").Roots.Single(r => r.Type == "MainWindow").Id;

            Edit(window);
            HotReloadOutcome? before = window.Debugger.LastApplied;
            Ui.Run(window, AppActions.SaveAll);
            HotReloadOutcome applied = Said(window, before);
            Assert.True(applied.Result == HotReloadResult.Applied, $"The saved XAML did not apply: {applied.Said}");
            Assert.Contains("MainWindow.axaml (MainWindow)", applied.Said, StringComparison.Ordinal);
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element is { Type: "Button", Name: "Added" }), "the Live Visual Tree to show the button added");
            Shows(app, shell);

            Typing.Replace(window, Pin, $"<TextBlok Text=\"typo\" />\n{Pin}");
            before = window.Debugger.LastApplied;
            Ui.Run(window, AppActions.SaveAll);
            HotReloadOutcome refused = Said(window, before);
            Refused(window, refused, window.BottomPanel.Problems.HotReloadProblems);
            Shows(app, shell);
            Assert.Contains("TextBlok", window.Status ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>Under Run, with no debugger, a save of the window's XAML puts the edits into the running window, said in the
    /// Run window; an edit the app's compiler refuses is said in the status bar with its line, and listed there in Problems.</summary>
    [AvaloniaFact]
    public void A_running_apps_saved_xaml_shows_in_its_window_and_an_edit_its_compiler_refuses_is_said_at_its_line()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        try
        {
            Ui.Launched(window, window.RunAsync(), "the app to build and start", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.Session.RunApp is { State: LiveAppState.Connected }, $"the app's inspector to say hello ({window.Status})", TimeSpan.FromMinutes(2));
            LiveApp app = window.Session.RunApp!;
            long shell = Ui.Await(app.TreeAsync(), "the app's tree").Roots.Single(r => r.Type == "MainWindow").Id;

            Edit(window);
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => RunSays(window, "Hot Reload: XAML changes applied: MainWindow.axaml (MainWindow)"), () => $"the Run window to say the XAML applied ({window.Status})");
            Shows(app, shell);

            Typing.Replace(window, Pin, $"<TextBlok Text=\"typo\" />\n{Pin}");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => window.Status?.Contains("TextBlok", StringComparison.Ordinal) == true, () => $"the status bar to say why the edit did not apply ({window.Status})");
            Refused(window, null, window.BottomPanel.Problems.HotReloadProblems);
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

    /// <summary>A handler added in C# and a button added in XAML that names it, applied together by Apply Hot Reload Changes:
    /// C#'s first, so the button finds the handler; a click on the new button runs it, and what it does to the greeting
    /// through its x:Name field shows on the greeting the XAML made again.</summary>
    [AvaloniaFact]
    public void A_handler_added_in_csharp_and_the_button_naming_it_in_xaml_apply_together_and_the_click_reaches_the_new_greeting()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        try
        {
            Ui.Launched(window, window.DebugAsync(), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
            Ui.WaitFor(() => window.LiveApp is { State: LiveAppState.Connected }, $"the app's inspector to say hello ({window.Status})", TimeSpan.FromMinutes(2));
            LiveApp app = window.LiveApp!;

            window.OpenFile(Path.Combine(folder, "MainWindow.axaml.cs"));
            Typing.Replace(window, "public MainWindow() => InitializeComponent();",
                "public MainWindow() => InitializeComponent();\n\npublic void OnAdded(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Greeting.Text = \"Clicked\";");
            window.OpenFile(Xaml);
            Typing.Replace(window, Pin, $"<Button x:Name=\"Added\" Content=\"Added\" Click=\"OnAdded\" />\n{Pin}");
            HotReloadOutcome? before = window.Debugger.LastApplied;
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            HotReloadOutcome applied = Said(window, before);
            Assert.True(applied.Result == HotReloadResult.Applied, $"The C# and XAML edits did not apply together: {applied.Said}");
            Assert.True(applied.Said.IndexOf("C# changes applied", StringComparison.Ordinal) is int code and >= 0
                && applied.Said.IndexOf("XAML changes applied", StringComparison.Ordinal) > code, $"C#'s apply was not said first: {applied.Said}");

            Element added = Find(Ui.Await(app.TreeAsync(), "the app's tree").Roots, "Added") ?? throw new InvalidOperationException("The app has no button Added");
            Ui.Await(app.InputAsync(new InputAction(InputKinds.Click) { Element = added.Id }), "the click on the button added");
            Ui.WaitFor(() => Text(app, "Greeting") == "Clicked", () => $"the click to set the greeting through its field ({Text(app, "Greeting")})");
        }
        finally
        {
            End(window);
        }
    }

    /// <summary>The window's XAML edited as a person types: the greeting's text, Delete's colour, and a button added.</summary>
    private void Edit(MainWindow window)
    {
        window.OpenFile(Xaml);
        Typing.Replace(window, "Text=\"Hello\"", "Text=\"Howdy\"");
        Typing.Replace(window, "Background=\"Orange\"", "Background=\"SeaGreen\"");
        Typing.Replace(window, Pin, $"<Button x:Name=\"Added\" Content=\"Added\" />\n{Pin}");
    }

    /// <summary>The app shows the edits in the window it had: the same window, the greeting's text, Delete's colour, the
    /// button added.</summary>
    private static void Shows(LiveApp app, long shell)
    {
        Element root = Ui.Await(app.AgentTreeAsync(["Background"]), "the app's tree").Roots.Single(r => r.Type == "MainWindow");
        Assert.True(root.Id == shell, "The app's window is another than the one it had: the XAML made a new window rather than the one shown again");
        Assert.Equal("Howdy", Find([root], "Greeting")?.Text);
        Assert.Equal("SeaGreen", Find([root], "Delete")?.Values?.GetValueOrDefault("Background"));
        Assert.NotNull(Find([root], "Added"));
    }

    /// <summary>The refused edit said at the editor's line of the element Avalonia has not, the outcome (when the debugger
    /// said it) an error that applied nothing.</summary>
    private void Refused(MainWindow window, HotReloadOutcome? outcome, IReadOnlyList<Problem>? problems)
    {
        if (outcome is not null)
        {
            Assert.True(outcome.Result == HotReloadResult.Errors, $"The misspelt element was not refused as an error: {outcome.Said}");
        }
        int line = Array.FindIndex(File.ReadAllLines(Xaml), l => l.Contains("TextBlok", StringComparison.Ordinal));
        Problem problem = Assert.Single(problems ?? [], p => p.Severity == ProblemSeverity.Error);
        Assert.Equal((Xaml, line), (problem.Path, problem.Position.Line));
        Assert.Contains("TextBlok", problem.Message, StringComparison.Ordinal);
    }

    private static HotReloadOutcome Said(MainWindow window, HotReloadOutcome? before)
    {
        Ui.WaitFor(() => window.Debugger.LastApplied is { } now && now != before, () => $"Hot Reload to say what the XAML came to ({window.Status})");
        return window.Debugger.LastApplied!;
    }

    private static bool RunSays(MainWindow window, string words)
    {
        OutputView run = window.BottomPanel.Run;
        run.Flush();
        return run.Document.Snapshot.ToString().Contains(words, StringComparison.Ordinal);
    }

    private static string? Text(LiveApp app, string name) => Find(Ui.Await(app.AgentTreeAsync([]), "the app's tree").Roots, name)?.Text;

    private static Element? Find(IEnumerable<Element> elements, string name) =>
        elements.Select(e => e.Name == name ? e : Find(e.Children, name)).FirstOrDefault(e => e is not null);

    private static void End(MainWindow window)
    {
        if (window.Debugger.IsDebugging)
        {
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        }
        window.Close();
    }
}

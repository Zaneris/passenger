using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The window's actions as a person finds and runs them: every one Find Action lists runs where it was asked
/// for or says why it cannot, and the shortcuts a window's menus and popups show are the ones its own keys run.</summary>
[AboutMacOS]
public sealed class ActionsTests : IDisposable
{
    /// <summary>What Find Action lists but this test leaves alone: the IDE's end, the system's file and folder pickers, Finder
    /// (Explorer) opened on the log, and Find Action itself, which the test chooses each of the others from.</summary>
    private static readonly string[] LeftAlone = [AppActions.Exit, AppActions.OpenSolution, AppActions.OpenFolder, AppActions.OpenFile, AppActions.ShowLog, AppActions.FindAction];

    private readonly string folder = TestFolders.Create("actions-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Every action Find Action lists runs where it was asked for or says why not, a shortcut kept under a
    /// former id runs the action it is now, and menus and popups show the keys that run each, as one flow, each step
    /// the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void Every_action_runs_where_it_is_asked_for_and_menus_show_the_keys_that_run_it()
    {
        Flow.StepApart<ActionsTests>("every action Find Action lists runs where it was asked for or says why it cannot", t => t.Every_action_Find_Action_lists_runs_where_it_was_asked_for_or_says_why_it_cannot());
        Flow.StepApart<ActionsTests>("a shortcut kept under a service actions former id runs the one action it is now", t => t.A_shortcut_kept_under_a_service_actions_former_id_runs_the_one_action_it_is_now());
        Flow.StepApart<ActionsTests>("a windows branch menu and breakpoint popup show the shortcuts its own keys run", t => t.A_windows_branch_menu_and_breakpoint_popup_show_the_shortcuts_its_own_keys_run());
        Flow.StepApart<ActionsTests>("the quit is named as the system names it and found in Find Action by Quit and by Exit", t => t.The_quit_is_named_as_the_system_names_it_and_found_in_Find_Action_by_Quit_and_by_Exit());
    }

    /// <summary>Also: no key is bound to an action Find Action leaves out, every action is titled by words of its own (a
    /// resource's, keyed by its name in AppActions or EditorActions, never another action's), and what ran last is offered
    /// first next time, what could not run taking no place from it.</summary>
    private void Every_action_Find_Action_lists_runs_where_it_was_asked_for_or_says_why_it_cannot()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int Two() => 2;\n}\n");
        // Every action may run here, Prune and Remove among them: on an engine of no program, never the machine's own.
        MainWindow window = Ui.OpenFolder(folder, engine: new Passenger.App.Containers.ContainerEngine(cli: Path.Combine(folder, "no-docker")));
        window.OpenFile("One.cs");
        Ui.Settle();
        List<string> ran = [];
        window.Actions.Running += (_, running) => ran.Add(running.Action.Id);
        try
        {
            IReadOnlyList<IdeAction> listed = FindAction(window).Rows;
            Ui.Press(window, "ESCAPE");
            string[] unlisted = [.. window.Actions.Keymap.Actions.Where(id => listed.All(a => a.Id != id))];
            Assert.True(unlisted.Length == 0, $"Keys are bound to actions Find Action does not list, and run nothing: {string.Join(", ", unlisted)}");
            Dictionary<string, string> named = typeof(AppActions).GetFields().Concat(typeof(EditorActions).GetFields())
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)).ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);
            // A tool window's own action may be titled by the window's name, ToolWindowTitles' words.
            string[] mistitled = [.. listed.Where(a => window.DockHost.Windows.All(w => w.Action != a.Id || w.Title != a.Title)
                    && (a.Title.IsVerbatim || named.TryGetValue(a.Title.Key, out string? of) && of != a.Id))
                .Select(a => a.Title.IsVerbatim ? $"{a.Id} ('{a.Title.English}', words written in the code)" : $"{a.Id} ('{a.Title.English}', {named[a.Title.Key]}'s title)")];
            Assert.True(mistitled.Length == 0, $"Actions a translation never reaches, or titled by another action's words, which a translation of one changes in both: {string.Join(", ", mistitled)}");

            IdeAction first = listed[0];
            IdeAction? previous = null;
            foreach (IdeAction action in listed.Where(a => !LeftAlone.Contains(a.Id)))
            {
                string? why = window.Actions.WhyNot(action.Id, window.Actions.Context())?.Shown;
                ActionPopup popup = FindAction(window);
                Assert.True(popup.Rows[0].Id == first.Id, (previous is null || previous == first
                    ? $"{first.Title} ({first.Id}) ran from Find Action but is not offered first the next time"
                    : $"{previous.Title} ({previous.Id}) could not run, yet Find Action offers {popup.Rows[0].Title} first rather than {first.Title}, run last")
                    + $": it offers {popup.Rows[0].Title} ({popup.Rows[0].Id}) first; {Said(window)}");
                popup.Query = action.Title.English;
                Ui.Settle();
                int row = popup.Rows.ToList().FindIndex(a => a.Id == action.Id);
                Assert.True(row >= 0, $"Find Action lists {action.Id} by its title, '{action.Title}', with nothing typed, but not once it is typed");
                for (int down = 0; down < row; down++)
                {
                    Ui.Press(window, "DOWN");
                }
                string before = $"before Enter, {(window.Popup == popup ? $"Find Action had {popup.Selected?.Id ?? "no row"} chosen" : $"Find Action was gone, {window.Popup?.GetType().Name ?? "no popup"} open")}";
                ran.Clear();
                Ui.Press(window, "ENTER");
                if (why is null)
                {
                    Assert.True(ran.Contains(action.Id), $"{action.Title} ({action.Id}) was chosen in Find Action, which ran "
                        + $"{(ran.Count == 0 ? "nothing" : string.Join(", ", ran))}; {before}; {Said(window)}");
                    first = action;
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(why), $"{action.Id} cannot run, and gives no reason");
                    Assert.True(window.Status == why, $"{action.Title} ({action.Id}) cannot run here because '{why}', but the status bar says '{window.Status}'");
                }
                previous = action;
                foreach (Window opened in window.OwnedWindows.ToList())
                {
                    opened.Close();
                }
                window.OpenContextMenu?.Close();
                Ui.Settle();
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A shortcut a person kept under a service action's former id (one the Containers or Aspire window gave
    /// Debug) runs Debug Service, which says why it cannot act here, and an agent's ide_action naming a former id
    /// reaches the same action.</summary>
    private void A_shortcut_kept_under_a_service_actions_former_id_runs_the_one_action_it_is_now()
    {
        MainWindow window = Ui.OpenFolder(folder);
        string keys = OperatingSystem.IsMacOS() ? "meta alt shift K" : "control alt shift K";
        window.ApplySettings(window.Settings with { Shortcuts = new() { ["app.aspire.debug"] = [keys] } });
        try
        {
            IReadOnlyList<Shortcut> bound = window.Actions.Keymap.ShortcutsFor(AppActions.ServiceDebug);
            Assert.True(bound.Count == 1 && bound[0].First.Notation() == Shortcut.Parse(keys).First.Notation(),
                $"Debug Service's keys are [{string.Join(", ", bound.Select(s => s.Display()))}], not those kept under app.aspire.debug");
            Ui.Press(window, bound[0].First.Notation());
            Assert.Equal("No container run is under way: Run, Debug or Profile a container configuration first", window.Status);
            Assert.Same(window.Actions[AppActions.ServiceDebug], window.Actions["app.containers.service.debug"]);
            Assert.Same(window.Actions[AppActions.ServiceRun], window.Actions["app.aspire.runWithoutDebugger"]);
        }
        finally
        {
            // The keymap the views without a window read is the process's; the next test starts on the platform's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void A_windows_branch_menu_and_breakpoint_popup_show_the_shortcuts_its_own_keys_run()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int Two() => 2;\n}\n");
        TestGit.Init(folder);
        Keymap platforms = AppActions.Keymap(new Dictionary<string, string[]>());
        // Every action may run here, Prune and Remove among them: on an engine of no program, never the machine's own.
        MainWindow window = Ui.OpenFolder(folder, engine: new Passenger.App.Containers.ContainerEngine(cli: Path.Combine(folder, "no-docker")));
        window.OpenFile("One.cs");
        Ui.Settle();
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");

        string commitKeys = OperatingSystem.IsMacOS() ? "meta alt shift J" : "control alt shift J";
        string breakpointsKeys = OperatingSystem.IsMacOS() ? "meta alt shift U" : "control alt shift U";
        MainWindow other = new() { Width = 1200, Height = 800 };
        other.Show();
        other.ApplySettings(other.Settings with
        {
            Shortcuts = new() { [AppActions.Commit] = [commitKeys], [AppActions.ViewBreakpoints] = [breakpointsKeys] },
        });
        try
        {
            string commit = platforms.ShortcutsFor(AppActions.Commit)[0].Display();
            Assert.True(window.Perform(AppActions.Branches));
            Ui.WaitFor(() => window.OpenContextMenu is not null, "the branch menu");
            Ui.Settle();
            // The keys stand in the item's own column, as every menu's do, written as the keymap writes them.
            MenuItem commitItem = window.OpenContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == "Commit…");
            string[] shown = [.. commitItem.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
            window.OpenContextMenu.Close();
            Assert.True(shown.Contains(commit),
                $"The branch menu does not show Commit's shortcut in this window's keymap, {commit}: [{string.Join(", ", shown)}]");

            Ui.Press(window, platforms.ShortcutsFor(AppActions.Commit)[0].First.Notation());
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Commit), $"{commit}, which the branch menu shows for Commit, did not commit in this window");

            Passenger.App.Debugging.LineBreakpoint breakpoint = Toggled(window);
            window.ShowBreakpointPopup(breakpoint);
            Ui.Settle();
            string viewBreakpoints = platforms.ShortcutsFor(AppActions.ViewBreakpoints)[0].Display();
            string[] buttons = [.. window.Popup!.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string ?? string.Empty)];
            Assert.True(buttons.Contains($"More ({viewBreakpoints})"),
                $"The breakpoint's popup does not show View Breakpoints' shortcut in this window's keymap, {viewBreakpoints}: [{string.Join(", ", buttons)}]");
        }
        finally
        {
            // The keymap the views without a window read is the process's; the next test starts on the platform's own.
            other.ApplySettings(new AppSettings());
            other.Close();
            window.Close();
        }
    }

    /// <summary>Passenger's quit is named in Find Action as the system names it, Quit Passenger on macOS (the application
    /// menu's) and Exit elsewhere (File's), and found by either word.</summary>
    private void The_quit_is_named_as_the_system_names_it_and_found_in_Find_Action_by_Quit_and_by_Exit()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            IdeAction quit = window.Actions[AppActions.Exit]!;
            Assert.Equal(OperatingSystem.IsMacOS() ? "Quit Passenger" : "Exit", quit.Title.English);
            foreach (string asked in (string[])["Quit", "Exit"])
            {
                ActionPopup popup = FindAction(window);
                popup.Query = asked;
                Ui.Settle();
                Assert.True(popup.Rows.Any(a => a.Id == AppActions.Exit), $"Find Action does not list {quit.Title} for '{asked}', only [{string.Join(", ", popup.Rows.Select(a => a.Title))}]");
                Ui.Press(window, "ESCAPE");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What the window shows of the actions run: the status bar's words, those Find Action ran latest first, the
    /// windows it opened and what has its keys.</summary>
    private static string Said(MainWindow window) =>
        $"the status bar says '{window.Status}', Find Action ran last [{string.Join(", ", window.RecentActions)}], "
        + $"the window owns [{string.Join(", ", window.OwnedWindows.Select(w => $"{w.GetType().Name} '{w.Title}'"))}] "
        + $"and its keys are with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}";

    /// <summary>Find Action as its shortcut opens it, waiting with the keys in its query.</summary>
    private static ActionPopup FindAction(MainWindow window)
    {
        Assert.True(window.Perform(AppActions.FindAction));
        Ui.Settle();
        return Assert.IsType<ActionPopup>(window.Popup);
    }

    private static Passenger.App.Debugging.LineBreakpoint Toggled(MainWindow window)
    {
        Ui.Run(window, AppActions.ToggleBreakpoint);
        return Assert.Single(window.Debugger.Breakpoints.In(window.EditorArea.Active!.Path));
    }
}

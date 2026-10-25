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
public sealed class ActionsTests : IDisposable
{
    /// <summary>What Find Action lists but this test leaves alone: the IDE's end, the system's file and folder pickers, Finder
    /// (Explorer) opened on the log, and Find Action itself, which the test chooses each of the others from.</summary>
    private static readonly string[] LeftAlone = [AppActions.Exit, AppActions.Open, AppActions.OpenFolder, AppActions.ShowLog, AppActions.FindAction];

    private readonly string folder = TestFolders.Create("actions-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Also: no key is bound to an action Find Action leaves out, and what ran last is offered first next
    /// time, what could not run taking no place from it.</summary>
    [AvaloniaFact]
    public void Every_action_Find_Action_lists_runs_where_it_was_asked_for_or_says_why_it_cannot()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int Two() => 2;\n}\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("One.cs");
        Ui.Settle();
        try
        {
            IReadOnlyList<IdeAction> listed = FindAction(window).Rows;
            Ui.Press(window, "ESCAPE");
            string[] unlisted = [.. window.Actions.Keymap.Actions.Where(id => listed.All(a => a.Id != id))];
            Assert.True(unlisted.Length == 0, $"Keys are bound to actions Find Action does not list, and run nothing: {string.Join(", ", unlisted)}");

            IdeAction first = listed[0];
            IdeAction? previous = null;
            foreach (IdeAction action in listed.Where(a => !LeftAlone.Contains(a.Id)))
            {
                string? why = window.Actions.WhyNot(action.Id, window.Actions.Context());
                ActionPopup popup = FindAction(window);
                Assert.True(popup.Rows[0].Id == first.Id, previous is null || previous == first
                    ? $"{first.Title} ({first.Id}) ran from Find Action but is not offered first the next time"
                    : $"{previous.Title} ({previous.Id}) could not run, yet Find Action offers {popup.Rows[0].Title} first rather than {first.Title}, run last");
                popup.Query = action.Title;
                Ui.Settle();
                int row = popup.Rows.ToList().FindIndex(a => a.Id == action.Id);
                Assert.True(row >= 0, $"Find Action lists {action.Id} by its title, '{action.Title}', with nothing typed, but not once it is typed");
                for (int down = 0; down < row; down++)
                {
                    Ui.Press(window, "DOWN");
                }
                Ui.Press(window, "ENTER");
                if (why is null)
                {
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
    [AvaloniaFact]
    public void A_shortcut_kept_under_a_service_actions_former_id_runs_the_one_action_it_is_now()
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

    [AvaloniaFact]
    public void A_windows_branch_menu_and_breakpoint_popup_show_the_shortcuts_its_own_keys_run()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int Two() => 2;\n}\n");
        TestGit.Init(folder);
        Keymap platforms = AppActions.Keymap(new Dictionary<string, string[]>());
        MainWindow window = Ui.OpenFolder(folder);
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

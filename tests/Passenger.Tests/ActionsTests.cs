using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
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

    [AvaloniaFact]
    public void Every_action_Find_Action_lists_runs_where_it_was_asked_for_or_says_why_it_cannot()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int Two() => 2;\n}\n");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("One.cs");
        Ui.Settle();
        try
        {
            IReadOnlyList<IdeAction> listed = FindAction(window).Rows;
            Ui.Press(window, "ESCAPE");
            // No key is bound to an action Find Action does not list, which would run nothing.
            string[] unlisted = [.. window.Actions.Keymap.Actions.Where(id => listed.All(a => a.Id != id))];
            Assert.True(unlisted.Length == 0, $"Keys are bound to actions Find Action does not list, and run nothing: {string.Join(", ", unlisted)}");

            // What ran last is offered first next time; what could not run takes no place from it.
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
                // What an action opened over the window (Settings, a context menu) is closed before the next is found.
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

    [AvaloniaFact]
    public void A_windows_branch_menu_and_breakpoint_popup_show_the_shortcuts_its_own_keys_run()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int Two() => 2;\n}\n");
        TestGit.Init(folder);
        Keymap platforms = AppActions.Keymap(new Dictionary<string, string[]>());
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("One.cs");
        Ui.Settle();
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");

        // Another window whose person gave Commit and View Breakpoints keys of their own.
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
            MenuItem commitItem = window.OpenContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == "Commit...");
            string[] shown = [.. commitItem.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
            window.OpenContextMenu.Close();
            Assert.True(shown.Contains(commit),
                $"The branch menu does not show Commit's shortcut in this window's keymap, {commit}: [{string.Join(", ", shown)}]");

            // The keys it shows run Commit here.
            Ui.Press(window, platforms.ShortcutsFor(AppActions.Commit)[0].First.Notation());
            Assert.True(window.DockHost.IsShown("commit"), $"{commit}, which the branch menu shows for Commit, did not commit in this window");

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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Find_Action_keeps_complete_action_details_readable_and_runs_pointer_selected_rows_from_a_narrow_editor_window(bool light)
    {
        File.WriteAllText(Path.Combine(folder, "One.txt"), "A real file for the detached editor's actions\n");
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("One.txt");
        Ui.Settle();
        try
        {
            Ui.Run(window, AppActions.OpenInNewWindow);
            Window owner = Assert.Single(window.EditorArea.Windows).Window;
            owner.SetRenderScaling(2);
            owner.RequestedThemeVariant = window.RequestedThemeVariant;
            owner.Height = 800;
            Ui.Run(owner, AppActions.FindAction);
            ActionPopup popup = Assert.IsType<ActionPopup>(window.Popup);
            owner.KeyTextInput("View Breakpoints and Exceptions");
            Ui.Settle();
            ListBox results = Assert.Single(popup.GetVisualDescendants().OfType<ListBox>());
            IdeAction chosen = Assert.Single(popup.Rows);
            TextBox query = Assert.Single(popup.GetVisualDescendants().OfType<TextBox>());
            foreach (double width in new double[] { 1200, 683, 640, 320, 1200, 320 })
            {
                owner.Width = width;
                Ui.Settle();
                string[] overflow = [.. results.GetVisualDescendants().OfType<ListBoxItem>().SelectMany(item =>
                    item.GetVisualDescendants().OfType<TextBlock>().Where(label => !string.IsNullOrEmpty(label.Text)).SelectMany(label =>
                    {
                        Point at = label.TranslatePoint(default, results)!.Value;
                        List<string> errors = [];
                        if (at.X < -0.5 || at.X + label.Bounds.Width > results.Bounds.Width + 0.5)
                        {
                            errors.Add($"'{label.Text}' escapes the actual {results.Bounds.Width}px action list: {at.X}..{at.X + label.Bounds.Width}");
                        }
                        if (label.TextLayout.Width > label.Bounds.Width + 1)
                        {
                            errors.Add($"'{label.Text}' clips inside its action column");
                        }
                        return errors;
                    }))];
                bool focused = query.IsFocused;
                object? selected = results.SelectedItem;
                Border frame = Assert.Single(popup.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("popup"));
                Point edge = frame.TranslatePoint(default, owner)!.Value;
                bool frameFits = edge.X >= 40 && edge.X + frame.Bounds.Width <= owner.Bounds.Width - 40 + 0.5;
                UiAudit.Capture(owner, $"find-action-details-{width:0}-{(light ? "light" : "dark")}");
                Assert.True(overflow.Length == 0, string.Join("\n", overflow));
                Assert.True(frameFits, "The action popup or its shadow escapes the actual editor window");
                Assert.True(focused);
                Assert.Same(chosen, selected);
            }
            ListBoxItem row = Assert.Single(results.GetVisualDescendants().OfType<ListBoxItem>());
            Control details = Assert.Single(row.GetVisualDescendants().OfType<Control>(), control => ToolTip.GetTip(control) is string);
            string tip = Assert.IsType<string>(ToolTip.GetTip(details));
            Assert.Contains(chosen.Title, tip, StringComparison.Ordinal);
            Assert.Contains(chosen.Group, tip, StringComparison.Ordinal);
            Assert.Contains(window.Actions.Shortcut(chosen.Id)!, tip, StringComparison.Ordinal);
            Hover(details);
            UiAudit.Capture(owner, $"find-action-details-tooltip-320-{(light ? "light" : "dark")}");

            Ui.Press(owner, OperatingSystem.IsMacOS() ? "meta A" : "control A");
            owner.KeyTextInput("Go to File");
            Ui.Settle();
            PointerChoose(AppActions.GoToFile);
            Assert.IsType<GoToFilePopup>(window.Popup);
            Assert.Same(owner, TopLevel.GetTopLevel(window.Popup));
            Ui.Press(owner, "ESCAPE");
            Assert.False(window.IsPopupOpen);
            Assert.True(window.EditorArea.CodeEditor.IsFocused);

            Ui.Run(owner, AppActions.FindAction);
            popup = Assert.IsType<ActionPopup>(window.Popup);
            owner.KeyTextInput("Debug");
            Ui.Settle();
            results = Assert.Single(popup.GetVisualDescendants().OfType<ListBox>());
            IdeAction disabled = Assert.Single(popup.Rows, action => action.Id == AppActions.Debug);
            string why = window.Actions.WhyNot(disabled.Id, window.Actions.Context())!;
            Assert.False(string.IsNullOrWhiteSpace(why));
            ListBoxItem disabledRow = Assert.Single(results.GetVisualDescendants().OfType<ListBoxItem>(), item => item.DataContext is IdeAction action && action.Id == disabled.Id);
            TextBlock title = Assert.Single(disabledRow.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == disabled.Title);
            Assert.True(window.TryFindResource("Passenger.TextMuted", window.ActualThemeVariant, out object? muted));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(muted).Color, Assert.IsAssignableFrom<ISolidColorBrush>(title.Foreground).Color);
            UiAudit.Capture(owner, $"find-action-disabled-320-{(light ? "light" : "dark")}");
            Control disabledDetails = Assert.Single(disabledRow.GetVisualDescendants().OfType<Control>(), control => ToolTip.GetTip(control) is string);
            Assert.Contains(why, Assert.IsType<string>(ToolTip.GetTip(disabledDetails)), StringComparison.Ordinal);
            Hover(disabledDetails);
            UiAudit.Capture(owner, $"find-action-disabled-tooltip-320-{(light ? "light" : "dark")}");
            PointerChoose(AppActions.Debug);
            Assert.False(window.IsPopupOpen);
            Assert.Equal(why, window.Status);

            void Hover(Control control)
            {
                owner.MouseMove(new Point(2, owner.Bounds.Height - 2));
                Ui.Settle();
                Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner)!.Value;
                owner.MouseMove(at);
                string hit = string.Join(" > ", (owner.InputHitTest(at) as Visual)?.GetVisualAncestors().OfType<Control>()
                    .Select(parent => $"{parent.GetType().Name}#{parent.Name}") ?? []);
                Assert.True(control.IsPointerOver, $"The actual pointer missed the tooltip row at {at}; hit {hit}");
                Screenshot.Pump(TimeSpan.FromMilliseconds(800));
                Ui.Settle();
                Assert.True(ToolTip.GetIsOpen(control), $"Hovering the actual tooltip row did not open its details; configured delay {ToolTip.GetShowDelay(control)}ms, hit {hit}");
            }

            void PointerChoose(string id)
            {
                ListBoxItem item = Assert.Single(results.GetVisualDescendants().OfType<ListBoxItem>(), item => item.DataContext is IdeAction action && action.Id == id);
                Point at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), owner)!.Value;
                owner.MouseMove(at);
                owner.MouseDown(at, MouseButton.Left);
                owner.MouseUp(at, MouseButton.Left);
                Ui.Settle();
                Assert.True(results.IsKeyboardFocusWithin);
                Ui.Press(owner, "ENTER");
                Ui.Settle();
            }
        }
        finally
        {
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

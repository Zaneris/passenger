using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The Switcher (Ctrl+Tab): the window's tabs in the order they last came forward, however each came (a click on
/// it, Go to, Back, a search's result), each let go of as it closes, then its tool windows. Pressed and let go at once, it
/// goes back to the tab used before, so a second press comes back; with its modifier held, its popup shows after a moment,
/// Tab moving its choice, and letting the modifier go goes to the one chosen. Asked for from Find Action, its popup waits
/// for Enter. An attached agent's input switches as a press let go at once, never opening the popup.</summary>
public partial class MainWindow
{
    /// <summary>How long the modifier is held before the popup shows, so a quick press switches without it.</summary>
    private static readonly TimeSpan SwitcherShowsAfter = TimeSpan.FromMilliseconds(150);

    private readonly UsedOrder<OpenDocument> tabsUsed = new();

    /// <summary>The Switcher while its modifier is held, until it is let go or the switch is left; null otherwise.</summary>
    private Switching? switching;

    private void AddSwitcher()
    {
        Editors.ActiveChanged += (_, _) =>
        {
            tabsUsed.Keep(Editors.HasTab);
            if (Editors.Active is { } shown)
            {
                tabsUsed.Came(shown);
            }
        };
        Editors.Closed += (_, document) => tabsUsed.Went(document);
        Editors.TabsGone += (_, document) => tabsUsed.Went(document);
        // In no menu: macOS's menu bar would take its keys as a key equivalent, never telling how long they are held.
        Add(A.Switcher, ActionTitles.Switcher, ActionGroups.Ide, c => Switch(c, 1), _ => NothingToSwitchTo());
        Add(A.SwitcherBackward, ActionTitles.SwitcherBackward, ActionGroups.Ide, c => Switch(c, -1), _ => NothingToSwitchTo());
    }

    private string? NothingToSwitchTo() =>
        (Editors.Groups.Any(g => g.Tabs.Count > 0) || Dock.Windows.Any(w => w.Unserved is null && w.Action is not null) ? null : Refusals.NothingToSwitchTo)?.Shown;

    /// <summary>The Switcher's rows as they stand: the tabs open (a file's or a diff's; a tool window's is among the
    /// windows), the latest used first, those not used since the window opened in Recent Files' order, then the tool
    /// windows the folder has, by name.</summary>
    public IReadOnlyList<SwitcherRow> SwitcherRows()
    {
        string? root = Folder;
        IReadOnlyList<string> recent = session.RecentFiles;
        List<OpenDocument> tabs = [.. Editors.Groups.SelectMany(g => g.Tabs).Select(t => t.Document).Where(d => d.Tool is null).Distinct()
            .OrderBy(d => RankIn(recent, d))];
        List<SwitcherRow> rows = [.. tabsUsed.Of(tabs).Select(d => new SwitcherRow(d.Title, d.Diff is null && root is not null ? Path.GetDirectoryName(PathNames.Shown(d.Path, root)) ?? string.Empty : string.Empty, d, null))];
        rows.AddRange(Dock.Windows.Where(w => w.Unserved is null && w.Action is not null).OrderBy(w => w.Title.Shown, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new SwitcherRow(w.Title.Shown, actions!.Shortcut(w.Action!) ?? string.Empty, null, w)));
        return rows;

        static int RankIn(IReadOnlyList<string> recent, OpenDocument document)
        {
            for (int i = 0; i < recent.Count; i++)
            {
                if (PathNames.Same(recent[i], document.Path))
                {
                    return i;
                }
            }
            return int.MaxValue;
        }
    }

    /// <summary>Ctrl+Tab, or Ctrl+Shift+Tab for <paramref name="step"/> back: the choice moved while the Switcher is open, else
    /// the Switcher begun, its choice the row after the tab shown (the tab shown where the editor has not the keys), or the
    /// last row going back.</summary>
    private void Switch(ActionContext context, int step)
    {
        if (switching is { } held)
        {
            held.Move(step);
            return;
        }
        if (popups.Open is SwitcherPopup open)
        {
            open.Move(step);
            return;
        }
        IReadOnlyList<SwitcherRow> rows = SwitcherRows();
        if (rows.Count == 0)
        {
            return;
        }
        int shown = rows[0].Document is { } first && first == Editors.Active && context.ToolWindow is null ? 0 : -1;
        int chosen = step > 0 ? (shown + 1) % rows.Count : rows.Count - 1;
        KeyModifiers modifiers = windowKeys.PressedWith & ~KeyModifiers.Shift;
        if (AgentAsks.Inputting)
        {
            // An agent's key comes and goes at once.
            SwitchTo(rows[chosen]);
        }
        else if (windowKeys.Pressing is { } window && modifiers != KeyModifiers.None)
        {
            switching = new Switching(this, window, modifiers, rows, chosen);
        }
        else
        {
            ShowSwitcher(rows, chosen);
        }
    }

    private SwitcherPopup ShowSwitcher(IReadOnlyList<SwitcherRow> rows, int chosen)
    {
        SwitcherPopup popup = new(rows, chosen);
        popups.Show(popup, row =>
        {
            if (row is not null)
            {
                SwitchTo(row);
            }
        });
        return popup;
    }

    /// <summary>Goes to a row's tab, where it is still open, or a tool window, as its action does.</summary>
    private void SwitchTo(SwitcherRow row)
    {
        if (row.Document is { } document)
        {
            if (Editors.HasTab(document))
            {
                Editors.Activate(document);
            }
        }
        else if (row.Window?.Action is { } action)
        {
            Request(action);
        }
    }

    /// <summary>The Switcher with its modifier held in the window it was pressed in: the choice moves with each press, the
    /// popup shows once the modifier has been held a moment, and letting the modifier go goes to the one chosen; Escape,
    /// the popup closed, or the window left ends it without going anywhere.</summary>
    private sealed class Switching
    {
        private readonly MainWindow owner;
        private readonly TopLevel window;
        private readonly KeyModifiers held;
        private readonly IReadOnlyList<SwitcherRow> rows;
        private readonly Debounce showing;
        private readonly EventHandler<KeyEventArgs> released;
        private readonly EventHandler<KeyEventArgs> pressed;
        private int chosen;
        private SwitcherPopup? popup;

        public Switching(MainWindow owner, TopLevel window, KeyModifiers held, IReadOnlyList<SwitcherRow> rows, int chosen)
        {
            this.owner = owner;
            this.window = window;
            this.held = held;
            this.rows = rows;
            this.chosen = chosen;
            released = (_, e) => OnReleased(e);
            pressed = (_, e) => OnPressed(e);
            window.AddHandler(InputElement.KeyUpEvent, released, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.KeyDownEvent, pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (window is WindowBase left)
            {
                left.Deactivated += OnLeft;
            }
            showing = new Debounce(SwitcherShowsAfter, () =>
            {
                Show();
                return Task.CompletedTask;
            });
            showing.Restart();
        }

        public void Move(int step)
        {
            if (popup is { } shown)
            {
                shown.Move(step);
            }
            else
            {
                chosen = ((chosen + step) % rows.Count + rows.Count) % rows.Count;
            }
        }

        private void Show()
        {
            if (owner.switching != this)
            {
                return;
            }
            popup = owner.ShowSwitcher(rows, chosen);
            popup.DetachedFromVisualTree += (_, _) => End();
        }

        /// <summary>One of the held modifiers let go: the row chosen gone to, through the popup where it shows.</summary>
        private void OnReleased(KeyEventArgs e)
        {
            if (Keystroke.Alone(e.Key, KeyModifiers.None) is not { } modifier || !held.HasFlag(modifier.Modifiers))
            {
                return;
            }
            End();
            if (popup is { } shown)
            {
                shown.Choose();
            }
            else
            {
                owner.SwitchTo(rows[chosen]);
            }
        }

        /// <summary>Escape before the popup shows, which takes its own once it does.</summary>
        private void OnPressed(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && popup is null)
            {
                End();
                e.Handled = true;
            }
        }

        private void OnLeft(object? sender, EventArgs e)
        {
            End();
            if (popup is { } shown && owner.popups.Open == shown)
            {
                owner.popups.Close();
            }
        }

        private void End()
        {
            if (owner.switching != this)
            {
                return;
            }
            owner.switching = null;
            showing.Stop();
            window.RemoveHandler(InputElement.KeyUpEvent, released);
            window.RemoveHandler(InputElement.KeyDownEvent, pressed);
            if (window is WindowBase left)
            {
                left.Deactivated -= OnLeft;
            }
        }
    }
}

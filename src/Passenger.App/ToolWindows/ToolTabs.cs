using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's tab, in the one look every tool window's tabs have (Shell.axaml's <c>ToggleButton.panelTab</c>:
/// its name, the open one underlined in the accent), a press on the open one leaving it open (<see cref="ChoiceToggle"/>),
/// and a × on one that closes (a search's, a shell's, a history's), shown on the open tab and the one under the pointer; a
/// middle-click closes it too. A window's tabs are a <see cref="Strip{TKey}"/>, but for a list's rows (a session's tab),
/// which the list makes and its model keeps one open.</summary>
public static class ToolTabs
{
    /// <summary>Tabs Next Tab and Previous Tab step through, and Close Tool Window Tab closes the one shown of, while what
    /// holds them has the keys: a tool window's (<see cref="Strip{TKey}"/>), the Terminal's shells.</summary>
    public interface ITabbed
    {
        /// <summary>Shows the tab <paramref name="step"/> places on from the one shown (back for a negative step), round at
        /// the ends, as a press on it shows it; false where no other tab shows.</summary>
        bool StepTab(int step);

        /// <summary>Closes the tab shown as its × does; false where it has none.</summary>
        bool CloseTab();
    }

    /// <summary>A tool window's row of tabs, one open: <see cref="ChoiceRow{TKey}"/>'s tabs, each standing for what it opens,
    /// which Next Tab and Previous Tab step through while the window has the keys.</summary>
    public sealed class Strip<TKey> : ChoiceRow<TKey>, ITabbed
        where TKey : notnull
    {
        private readonly Dictionary<TKey, Action> closes = [];

        public bool StepTab(int step) => StepChoice(step);

        public bool CloseTab()
        {
            if (!HasChoice || !closes.TryGetValue(Choice!, out Action? close))
            {
                return false;
            }
            close();
            return true;
        }

        /// <summary>Adds a tab for <paramref name="key"/>, closed, as <see cref="Tab"/> makes it, named <paramref name="name"/>.</summary>
        public ToggleButton Add(TKey key, object title, Action? close = null, string closeTip = "Close", string? name = null)
        {
            if (close is null)
            {
                closes.Remove(key);
            }
            else
            {
                closes[key] = close;
            }
            return Join(key, Tab(title, close, closeTip), name);
        }
    }

    public static ToggleButton Tab(object title, Action? close = null, string closeTip = "Close")
    {
        if (close is null)
        {
            return new ChoiceToggle { Classes = { StyleClasses.PanelTab }, Content = title };
        }
        Control name = title as Control ?? new TextBlock { Text = title.ToString(), VerticalAlignment = VerticalAlignment.Center };
        Button button = ToolButtons.Small(IconKeys.Close, closeTip);
        button.Classes.Add(StyleClasses.TabClose);
        button.Margin = new Thickness(Gaps.Small, 0, 0, 0);
        DockPanel.SetDock(button, Dock.Right);
        button.Click += (_, e) =>
        {
            close();
            e.Handled = true;
        };
        ChoiceToggle tab = new()
        {
            Classes = { StyleClasses.PanelTab },
            // Its name gives way before its ×, ending in an ellipsis, down to a few letters, so every tab stays to choose.
            [ShrinkingRow.LeastProperty] = 88,
            // Given less room than it wants, it takes all of it, its ellipsis where the room ends rather than a letter before.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel { Children = { button, name } },
        };
        // Heard before the toggle, which takes no press but the left button's.
        tab.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed)
            {
                close();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        return tab;
    }
}

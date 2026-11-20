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
    /// <summary>A tool window's row of tabs, one open: <see cref="ChoiceRow{TKey}"/>'s tabs, each standing for what it opens.</summary>
    public sealed class Strip<TKey> : ChoiceRow<TKey>
        where TKey : notnull
    {
        /// <summary>Adds a tab for <paramref name="key"/>, closed, as <see cref="Tab"/> makes it, named <paramref name="name"/>.</summary>
        public ToggleButton Add(TKey key, object title, Action? close = null, string closeTip = "Close", string? name = null) =>
            Join(key, Tab(title, close, closeTip), name);
    }

    public static ToggleButton Tab(object title, Action? close = null, string closeTip = "Close")
    {
        if (close is null)
        {
            return new ChoiceToggle { Classes = { StyleClasses.PanelTab }, Content = title };
        }
        Control name = title as Control ?? new TextBlock { Text = title.ToString(), VerticalAlignment = VerticalAlignment.Center };
        RowIcon glyph = RowIcon.Glyph(IconKeys.Close);
        Button button = new() { Classes = { StyleClasses.TabClose }, Content = glyph, Margin = new Thickness(Gaps.Small, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        ToolButtons.Tip(button, closeTip);
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

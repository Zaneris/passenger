using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's tab, in the one look every tool window's tabs have (Shell.axaml's <c>ToggleButton.panelTab</c>:
/// its name, the open one underlined in the accent), and a × on one that closes (a search's, a shell's, a history's),
/// shown on the open tab and the one under the pointer.</summary>
public static class ToolTabs
{
    public static ToggleButton Tab(object title, bool open, Action? close = null, string closeTip = "Close")
    {
        Control name = title as Control ?? new TextBlock { Text = title.ToString(), VerticalAlignment = VerticalAlignment.Center };
        if (close is null)
        {
            return new ToggleButton { Classes = { "panelTab" }, IsChecked = open, Content = name };
        }
        PathIcon glyph = new() { Classes = { "small" } };
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(IconKeys.Close));
        Button button = new() { Classes = { "tabClose" }, Content = glyph, Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        ToolTip.SetTip(button, closeTip);
        button.Click += (_, e) =>
        {
            close();
            e.Handled = true;
        };
        return new ToggleButton
        {
            Classes = { "panelTab" },
            IsChecked = open,
            // Its name gives way before its ×, ending in an ellipsis, down to a few letters, so every tab stays to choose.
            [ShrinkingRow.LeastProperty] = 88,
            Content = new DockPanel { Children = { button, name } },
        };
    }
}

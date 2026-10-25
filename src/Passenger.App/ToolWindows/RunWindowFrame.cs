using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>The frame the windows that list a run's services (Containers, Aspire) are built on, one layout in both: their
/// toolbar of action buttons along the top (<see cref="ToolBars.Top"/>), the rows on the left with their empty words over
/// them, and on the right the chosen row's tabs, those of <see cref="RunWindows.Tabs"/> the window has in their order, each
/// an action that Find Action reaches and a click runs, over the page the window shows; the tabs scroll where the window is
/// too narrow for them.</summary>
public static class RunWindowFrame
{
    /// <summary>Builds the frame into <paramref name="window"/>: <paramref name="toolbar"/> along its top, <paramref name="rows"/>
    /// beside the tabs over <paramref name="page"/>, each tab named <c>{named}{Tab}Tab</c> and asking for its action
    /// (<paramref name="actionOf"/>) through <paramref name="request"/>. The tabs, by their names.</summary>
    public static IReadOnlyDictionary<string, ToggleButton> Build(DockPanel window, Control toolbar, Control rows, Control page, IEnumerable<string> tabs,
        Func<string, string> actionOf, string named, Action<string> request)
    {
        window.Children.Add(ToolBars.Top(toolbar));
        StackPanel strip = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        Dictionary<string, ToggleButton> made = new(StringComparer.Ordinal);
        HashSet<string> has = [.. tabs];
        foreach (string name in RunWindows.Tabs.Where(has.Contains))
        {
            ToggleButton tab = ToolTabs.Tab(name, false);
            tab.Name = $"{named}{name.Replace(" ", string.Empty, StringComparison.Ordinal)}Tab";
            string id = actionOf(name);
            ActionButtons.Mark(tab, id);
            tab.Click += (_, _) => request(id);
            made[name] = tab;
            strip.Children.Add(tab);
        }
        Border tabBar = ToolBars.Top(new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        DockPanel right = new() { [Grid.ColumnProperty] = 2, Children = { tabBar, page } };
        Grid panes = new() { ColumnDefinitions = new ColumnDefinitions("2*,1,3*") };
        panes.Children.Add(rows);
        panes.Children.Add(new HairlineDivider(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 });
        panes.Children.Add(right);
        window.Children.Add(panes);
        return made;
    }

    /// <summary>The toolbar's row of buttons, each running one of the window's actions (<see cref="ToolButtons"/>), with what
    /// else the bar holds before them.</summary>
    public static StackPanel Tools(params Control[] before)
    {
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        tools.Children.AddRange(before);
        return tools;
    }
}

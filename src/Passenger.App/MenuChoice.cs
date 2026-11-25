using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A menu's item for one value among several (a branch, a run configuration, an encoding, a line separator, a
/// target framework): the current value checked and still enabled, never greyed, so it reads as the one in force;
/// choosing it again changes nothing. Shell.axaml draws the check as a ✓.</summary>
public static class MenuChoice
{
    public static MenuItem Of(object header, bool current, Action choose)
    {
        MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = current };
        item.Click += (_, _) =>
        {
            if (!current)
            {
                choose();
            }
        };
        return item;
    }

    /// <summary>One whose own menu holds what can be done with it (a branch in the branch widget), so choosing it opens
    /// that menu and nothing is done until one of its items is chosen, made as it first opens
    /// (<see cref="ToolWindows.ListMenus.MadeOnOpen"/>). Where given, <paramref name="detail"/> follows its name muted (a
    /// local branch's upstream), giving way first where the menu is narrow; a screen reader names it by its name alone.</summary>
    public static MenuItem Holding(string name, bool current, Func<IReadOnlyList<Control>> items, string? detail = null)
    {
        MenuItem item = new() { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = current };
        if (detail is not null)
        {
            TextBlock after = new() { Text = detail, Classes = { StyleClasses.Caption, StyleClasses.Muted }, Margin = new Avalonia.Thickness(Gaps.Normal, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            item.Header = new ShrinkingRow { Children = { new TextBlock { Text = name }, after } };
            Avalonia.Automation.AutomationProperties.SetName(item, name);
            Avalonia.Automation.AutomationProperties.SetHelpText(item, detail);
        }
        return ToolWindows.ListMenus.MadeOnOpen(item, items);
    }

    /// <summary>The name an item is shown and heard by: its words, or the name given it where its header says more.</summary>
    public static string? NameOf(MenuItem item) => Avalonia.Automation.AutomationProperties.GetName(item) is { Length: > 0 } name ? name : item.Header as string;
}

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
    /// that menu and nothing is done until one of its items is chosen. Its items are made as that menu first opens, so a
    /// value whose menu stays shut costs it nothing.</summary>
    public static MenuItem Holding(object header, bool current, Func<IReadOnlyList<Control>> items)
    {
        // A stand-in until then, so the value shows that it opens a menu and a click or Enter opens it.
        MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = current, ItemsSource = (Control[])[new Separator()] };
        bool made = false;
        item.SubmenuOpened += (_, e) =>
        {
            if (e.Source == item && !made)
            {
                made = true;
                item.ItemsSource = items();
            }
        };
        return item;
    }
}

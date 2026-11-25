using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Passenger.App;

/// <summary>A menu's search, for one listing many named things (the branch menu's branches): letters typed while it has
/// the keys narrow it to the items its listing makes of them, which say at their top what was found
/// (<see cref="DropDownSearch.Said"/>), the first that runs taking the keys; Backspace takes the last letter back and
/// Escape all of them before it closes the menu, as a drop-down's search does.</summary>
public static class MenuSearch
{
    /// <summary>Lists <paramref name="menu"/>'s items as <paramref name="listed"/> makes them of what is typed, nothing typed
    /// to begin with.</summary>
    public static void Attach(ContextMenu menu, Func<string, IReadOnlyList<Control>> listed)
    {
        string typed = string.Empty;
        Show(given: false);
        menu.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            // A space before any letter is the menu's own, as it chooses an item.
            if (e.Text is { Length: > 0 } text && !text.Any(char.IsControl) && (typed.Length > 0 || !string.IsNullOrWhiteSpace(text)))
            {
                typed += text;
                Show(given: true);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        menu.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (typed.Length == 0 || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Back or Key.Escape))
            {
                return;
            }
            typed = e.Key == Key.Back ? typed[..^1] : string.Empty;
            Show(given: true);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // Listed again, the items that had the keys are gone: the first left takes them, as the menu opened takes none.
        void Show(bool given)
        {
            menu.Items.Clear();
            foreach (Control item in listed(typed))
            {
                menu.Items.Add(item);
            }
            if (given && menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEnabled) is { } first)
            {
                first.Focus(NavigationMethod.Directional);
            }
        }
    }
}

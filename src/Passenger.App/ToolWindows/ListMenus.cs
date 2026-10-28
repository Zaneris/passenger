using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's list given a right-click menu for each row: made again for the row chosen as
/// the choice moves, so a right-click or the menu key finds it ready, and none with nothing to offer. One open stays as it
/// opened while the list is read again (its rows made anew, the same one chosen), and is made again as it closes.</summary>
public static class ListMenus
{
    public static void Attach(SelectingItemsControl list, Func<IReadOnlyList<Control>> items)
    {
        void Make()
        {
            if (list.ContextMenu is { IsOpen: true })
            {
                return;
            }
            IReadOnlyList<Control> made = items();
            ContextMenu? menu = made.Count == 0 ? null : new ContextMenu { ItemsSource = made };
            if (menu is not null)
            {
                menu.Closed += (_, _) => Make();
            }
            list.ContextMenu = menu;
        }
        list.SelectionChanged += (_, _) => Make();
        Make();
    }

    /// <summary>An item, with the keys shown that do the same where the list has the focus.</summary>
    public static MenuItem Item(string header, string? keys, Action run, bool enabled = true)
    {
        MenuItem item = new() { Header = header, IsEnabled = enabled, InputGesture = keys is null ? null : KeyGesture.Parse(keys) };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>An item that runs an action of the editor's or the window's its place's own way, with the keys the keymap in
    /// force gives that action.</summary>
    public static MenuItem Keyed(string header, string action, Action run, bool enabled = true)
    {
        MenuItem item = Item(header, null, run, enabled);
        if (AppActions.Keymap().ShortcutsFor(action).FirstOrDefault(s => s.Second is null) is { } keys)
        {
            item.InputGesture = new KeyGesture(keys.First.Key, keys.First.Modifiers);
        }
        return item;
    }

    /// <summary>A submenu of items.</summary>
    public static MenuItem Sub(string header, params Control[] items) => new() { Header = header, ItemsSource = items };

    public static async Task CopyAsync(Visual from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

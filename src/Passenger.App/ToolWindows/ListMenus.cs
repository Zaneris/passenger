using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>Every menu's items, made one way wherever a menu opens (a list's right-click menu, a widget's, a gutter mark's):
/// an item shows only the keys the keymap in force gives its action (<see cref="Keyed"/>) or those its list's row does it
/// by (<see cref="Open"/>, <see cref="Source"/>, <see cref="Edit"/>, <see cref="Remove"/>, through <see cref="ListKeys"/>),
/// never keys written beside it; one greyed says why in its tip (<see cref="Grey"/>); a group's heading and words in place
/// of items are words, never a command (<see cref="Heading"/>, <see cref="Notice"/>). A list's menu is made again for the
/// row chosen as the choice moves (<see cref="Attach"/>).</summary>
public static class ListMenus
{
    /// <summary>F4's item in every list: what goes to the row's place in the code or the file.</summary>
    public const string GoToSource = "Go to Source";

    /// <summary>Gives a list a right-click menu for its chosen row, so a right-click or the menu key finds it ready, and none
    /// with nothing to offer. One open stays as it opened while the list is read again (its rows made anew, the same one
    /// chosen), and is made again as it closes.</summary>
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

    /// <summary>Places a menu the menu key (Shift+F10) opens under what has the keys (a list's chosen row), where one a
    /// right-click opens stands at the pointer. Gives what puts it back where it stood, run as it closes too.</summary>
    public static Action PlaceForKeys(ContextMenu menu, Control at)
    {
        (PlacementMode Placement, Control? Target) was = (menu.Placement, menu.PlacementTarget);
        bool back = false;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.PlacementTarget = at;
        menu.Closed += Closed;
        return Back;

        void Closed(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Back();

        void Back()
        {
            if (!back)
            {
                back = true;
                menu.Closed -= Closed;
                (menu.Placement, menu.PlacementTarget) = was;
            }
        }
    }

    /// <summary>An item, greyed while <paramref name="whyNot"/> says why it cannot run, which its tip says; with
    /// <paramref name="icon"/> (a key of <see cref="IconKeys"/>), that icon before its words.</summary>
    public static MenuItem Item(string header, Action run, Words? whyNot = null, string? icon = null)
    {
        MenuItem item = new() { Header = header };
        if (icon is not null)
        {
            item.Icon = Glyph(icon);
        }
        Grey(item, whyNot);
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>An item that runs an action of the editor's or the window's its place's own way, with the keys the keymap in
    /// force gives that action.</summary>
    public static MenuItem Keyed(string header, string action, Action run, Words? whyNot = null) => KeysOf(Item(header, run, whyNot), action);

    /// <summary>What Enter and a double click do on a list's row (<see cref="ListActs.Open"/>), in the list's words for it.</summary>
    public static MenuItem Open(string header, Action run, Words? whyNot = null) => Act(header, ListKeys.OpenKey, run, whyNot);

    /// <summary>What F4 does on a list's row (<see cref="ListActs.Source"/>): <see cref="GoToSource"/> in every list.</summary>
    public static MenuItem Source(Action run, Words? whyNot = null) => Act(GoToSource, ListKeys.SourceKey, run, whyNot);

    /// <summary>What F2 does on a list's row (<see cref="ListActs.Edit"/>), in the list's words for it.</summary>
    public static MenuItem Edit(string header, Action run, Words? whyNot = null) => Act(header, ListKeys.EditKey, run, whyNot);

    /// <summary>What Delete does on a list's row (<see cref="ListActs.Remove"/>), in the list's words for it.</summary>
    public static MenuItem Remove(string header, Action run, Words? whyNot = null) => Act(header, ListKeys.RemoveKey, run, whyNot);

    private static MenuItem Act(string header, Key key, Action run, Words? whyNot)
    {
        MenuItem item = Item(header, run, whyNot);
        item.InputGesture = new KeyGesture(key);
        return item;
    }

    /// <summary>An item turning something on or off, ticked while it is on, greyed as <see cref="Item"/> is; with
    /// <paramref name="icon"/>, that icon before its words; with <paramref name="action"/>, the keys the keymap gives the
    /// action it runs, as <see cref="Keyed"/>'s.</summary>
    public static MenuItem Toggle(string header, bool on, Action<bool> turn, Words? whyNot = null, string? icon = null, string? action = null)
    {
        MenuItem item = Item(header, () => turn(!on), whyNot, icon);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = on;
        return action is null ? item : KeysOf(item, action);
    }

    /// <summary>A submenu of items.</summary>
    public static MenuItem Sub(string header, params IEnumerable<Control> items) => new() { Header = header, ItemsSource = items.ToArray() };

    /// <summary>A submenu whose items are made as it first opens (<see cref="MadeOnOpen"/>): a remote's branches.</summary>
    public static MenuItem Sub(string header, Func<IReadOnlyList<Control>> made) => MadeOnOpen(new MenuItem { Header = header }, made);

    /// <summary>Gives <paramref name="item"/> a submenu whose items are made as it first opens, so one whose submenu stays shut
    /// costs nothing: a stand-in until then shows it opens one, which a click or Enter opens.</summary>
    public static MenuItem MadeOnOpen(MenuItem item, Func<IReadOnlyList<Control>> items)
    {
        item.ItemsSource = (Control[])[new Separator()];
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

    /// <summary>The heading over a group of a menu's items (a list of branches, of solutions): words at the caption's step,
    /// muted, which the keys and the pointer pass over, and a screen reader hears as a heading.</summary>
    public static MenuItem Heading(string words)
    {
        MenuItem item = new() { Header = words, IsEnabled = false, Classes = { StyleClasses.MenuHeading, StyleClasses.Caption } };
        AutomationProperties.SetHeadingLevel(item, 1);
        return item;
    }

    /// <summary>Words a menu says in place of the items it has none of, or of what they lack (Git is not installed, no
    /// executable projects): muted, wrapping in the menu's width, which the keys and the pointer pass over.</summary>
    public static MenuItem Notice(string words) => new() { Header = words, IsEnabled = false, Classes = { StyleClasses.MenuNotice } };

    /// <summary>Greys an item while it cannot run, its tip saying why (<paramref name="whyNot"/>); null enables it again.</summary>
    public static void Grey(MenuItem item, Words? whyNot)
    {
        item.IsEnabled = whyNot is null;
        ToolTip.SetTip(item, whyNot?.Shown);
    }

    public static async Task CopyAsync(Visual from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static MenuItem KeysOf(MenuItem item, string action)
    {
        if (AppActions.Keymap().ShortcutsFor(action).FirstOrDefault(s => s.Second is null) is { } keys)
        {
            item.InputGesture = new KeyGesture(keys.First.Key, keys.First.Modifiers);
        }
        return item;
    }

    private static PathIcon Glyph(string icon)
    {
        PathIcon glyph = new();
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        return glyph;
    }
}

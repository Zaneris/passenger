using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>The keys of a list, one behaviour in every list. What its chosen row does by Enter, F4, F2, Delete and a double
/// click (<see cref="Act"/>) is heard ahead of its rows, which take Enter and Space for their choice. A search field over a
/// list gives it Up and Down, Page Up and Page Down (<see cref="Search"/>): the choice moves to the row above or below, or a
/// page of rows (those in sight, less one), stopping at the ends, kept in sight, a heading passed over; Left, Right, Home
/// and End stay the field's. A list given the keys has them on its chosen row (<see cref="Hold"/>).</summary>
public static class ListKeys
{
    /// <summary>The key of each act (<see cref="ListActs"/>), as <see cref="Act"/> hears it and a menu's item for the act
    /// shows it (<see cref="ToolWindows.ListMenus"/>).</summary>
    public const Key OpenKey = Key.Enter;

    /// <inheritdoc cref="OpenKey"/>
    public const Key SourceKey = Key.F4;

    /// <inheritdoc cref="OpenKey"/>
    public const Key EditKey = Key.F2;

    /// <summary>The key Remove is shown by: Delete, or on a Mac keyboard the Delete key over Return, which is Backspace.
    /// <see cref="Act"/> hears both.</summary>
    public static Key RemoveKey => OperatingSystem.IsMacOS() ? Key.Back : Key.Delete;

    /// <summary>Gives a list what its chosen row does by the keys and a double click, the one way a list hears them, ahead
    /// of its rows: Enter and a double click on a row run <see cref="ListActs.Open"/> (Enter <see cref="ListActs.Enter"/>
    /// where the list gives one), F4 <see cref="ListActs.Source"/>, F2 <see cref="ListActs.Edit"/>, Delete and Backspace
    /// <see cref="ListActs.Remove"/>, each with no modifier held, and <see cref="ListActs.Keys"/> any key first. A key or a
    /// double click in a field or box inside a row stays that control's.</summary>
    public static void Act(ListBox list, ListActs acts)
    {
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!ReferenceEquals(e.Source, list) && !(e.Source is Control row && ItemsControl.ItemsControlFromItemContainer(row) == list))
            {
                return;
            }
            if (acts.Keys?.Invoke(e) == true)
            {
                e.Handled = true;
                return;
            }
            Action? act = e.KeyModifiers != KeyModifiers.None ? null : e.Key switch
            {
                OpenKey => acts.Enter ?? acts.Open,
                SourceKey => acts.Source,
                EditKey => acts.Edit,
                Key.Delete or Key.Back => acts.Remove,
                _ => null,
            };
            if (act is not null)
            {
                act();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        if (acts.Open is { } open)
        {
            list.AddHandler(InputElement.DoubleTappedEvent, (_, e) =>
            {
                if (OnRow(list, e.Source as Visual))
                {
                    open();
                    e.Handled = true;
                }
            });
        }
    }

    /// <summary>Whether what was pressed is one of the list's rows, or lies in one but not in a field or box of its own.</summary>
    private static bool OnRow(ListBox list, Visual? pressed)
    {
        for (Visual? at = pressed; at is not null && at != list; at = at.GetVisualParent())
        {
            if (at is ListBoxItem item)
            {
                return ItemsControl.ItemsControlFromItemContainer(item) == list;
            }
            if (at is TextBox or ToggleButton or Button)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Gives a list's chosen row the keys, as a click on it does (the first chosen where none is and
    /// <paramref name="chooseFirst"/> asks): the row a Shift range runs from, else the first chosen. False where the list has
    /// no row chosen to give them to.</summary>
    public static bool Hold(ListBox list, bool chooseFirst = false, NavigationMethod method = NavigationMethod.Unspecified) => Hold(list, null, chooseFirst, method);

    /// <summary>Gives the keys back to the row of a list that had them, where it is still shown and chosen, else to the row
    /// of the item it showed, where that is chosen, else as <see cref="Hold(ListBox, bool, NavigationMethod)"/> does: so the next key goes on
    /// from where the person was, a Shift range's end rather than its start.</summary>
    public static bool Hold(RowWithKeys had, NavigationMethod method = NavigationMethod.Unspecified) => Hold(had.List, had, chooseFirst: false, method);

    /// <summary>A row out of sight has no container to take the keys until it is brought into view; given them as by Tab
    /// (<paramref name="method"/>), the row draws the ring round it.</summary>
    private static bool Hold(ListBox list, RowWithKeys? had, bool chooseFirst, NavigationMethod method)
    {
        if (list.SelectedIndex < 0 && chooseFirst && list.ItemCount > 0)
        {
            list.SelectedIndex = 0;
        }
        int index = Chosen(list, had?.Index) ?? Chosen(list, had?.Item is { } item ? list.ItemsView.IndexOf(item) : null)
            ?? Chosen(list, list.Selection.AnchorIndex) ?? list.SelectedIndex;
        if (index < 0 || index >= list.ItemCount)
        {
            return false;
        }
        if (list.ContainerFromIndex(index) is null)
        {
            list.ScrollIntoView(index);
            list.UpdateLayout();
        }
        return list.ContainerFromIndex(index)?.Focus(method) == true;

        static int? Chosen(ListBox list, int? index) => index is int at && at >= 0 && at < list.ItemCount && list.Selection.IsSelected(at) ? at : null;
    }

    /// <summary>Gives a search field the keys of the list under it, ahead of the field's own: Up, Down, Page Up and Page Down
    /// move the list's choice (<see cref="Move"/>), Escape clears a search before it cancels, and Enter runs
    /// <paramref name="enter"/> where one is given.</summary>
    public static void Search(TextBox search, ListBox list, Action? enter = null) => search.AddHandler(InputElement.KeyDownEvent, (_, e) =>
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when Move(list, e):
                e.Handled = true;
                break;
            case Key.Enter when enter is not null:
                enter();
                e.Handled = true;
                break;
            case Key.Escape when search.Text is { Length: > 0 }:
                search.Text = string.Empty;
                e.Handled = true;
                break;
        }
    }, RoutingStrategies.Tunnel);

    /// <summary>Moves the list's choice for a key pressed in its field, with no modifier held; true when the key was the
    /// list's, which the caller marks handled.</summary>
    public static bool Move(ListBox list, KeyEventArgs e, Func<int, bool>? choosable = null)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down or Key.PageUp or Key.PageDown))
        {
            return false;
        }
        int count = list.ItemCount;
        if (count == 0)
        {
            return true;
        }
        int step = e.Key is Key.Up or Key.PageUp ? -1 : 1;
        int by = e.Key is Key.PageUp or Key.PageDown ? Page(list) : 1;
        int from = list.SelectedIndex;
        int to = Math.Clamp(from < 0 && step > 0 ? by - 1 : from + step * by, 0, count - 1);
        // A heading is passed over the way the key goes, else the nearest row back towards where the choice was.
        int chosen = Choosable(to, step) ?? Choosable(to, -step) ?? from;
        if (chosen >= 0 && chosen != from)
        {
            list.SelectedIndex = chosen;
        }
        if (list.SelectedIndex >= 0)
        {
            list.ScrollIntoView(list.SelectedIndex);
        }
        return true;

        int? Choosable(int at, int way)
        {
            for (int i = at; i >= 0 && i < count; i += way)
            {
                if (choosable?.Invoke(i) != false)
                {
                    return i;
                }
            }
            return null;
        }
    }

    /// <summary>How many rows a page is: those the list shows at once, less one, by the height of a row drawn.</summary>
    private static int Page(ListBox list)
    {
        double seen = list.Scroll?.Viewport.Height is > 0 and var viewport ? viewport : list.Bounds.Height;
        double row = list.GetRealizedContainers().Select(c => c.Bounds.Height).FirstOrDefault(h => h > 0);
        if (row <= 0)
        {
            row = ListRow.Height;
        }
        return Math.Max(1, (int)Math.Floor(seen / row) - 1);
    }
}

/// <summary>A list's row as it had the keys: its container and the item it showed then, which the list may show in another
/// container since (its rows made again, or scrolled out of sight and back).</summary>
public sealed record RowWithKeys(ListBox List, Control Row, object? Item)
{
    /// <summary>The list's row <paramref name="had"/> is, with the item it shows now; null where it is no list's row.</summary>
    public static RowWithKeys? Of(Control? had) =>
        had is not null && ItemsControl.ItemsControlFromItemContainer(had) is ListBox list ? new(list, had, list.ItemFromContainer(had)) : null;

    /// <summary>The row's index, where it is still shown and shows the same item; else null.</summary>
    public int? Index => Row.IsEffectivelyVisible && List.IndexFromContainer(Row) is int at and >= 0 && Equals(List.ItemFromContainer(Row), Item) ? at : null;
}

/// <summary>What a list's chosen row does by the keys and a double click (<see cref="ListKeys.Act"/>), each null where its
/// rows do nothing by it; each runs on the list's chosen row.</summary>
public sealed record ListActs
{
    /// <summary>A double click on a row, and Enter: what the row is for (opens it, goes to it, runs it).</summary>
    public Action? Open { get; init; }

    /// <summary>Enter, where it does what a stray double click must never do (sends a request, writes into a file): the
    /// double click goes on doing <see cref="Open"/>.</summary>
    public Action? Enter { get; init; }

    /// <summary>F4: goes to what the row stands for in the code or the file.</summary>
    public Action? Source { get; init; }

    /// <summary>F2: edits the row's value in place.</summary>
    public Action? Edit { get; init; }

    /// <summary>Delete, and Backspace (a Mac keyboard's Delete): takes the chosen row out.</summary>
    public Action? Remove { get; init; }

    /// <summary>Any key, modifiers held or not, before the others: true where it was the list's (Space flipping a row's
    /// box, an action's keys).</summary>
    public Func<KeyEventArgs, bool>? Keys { get; init; }
}

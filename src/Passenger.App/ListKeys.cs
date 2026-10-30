using Avalonia.Controls;
using Avalonia.Input;

namespace Passenger.App;

/// <summary>The keys a search field gives the list under it (Go to File, Find Action, Recent Locations, New Project's
/// templates, Attach to Process): Up and Down choose the row above or below, Page Up and Page Down a page of rows (those
/// in sight, less one, so the row chosen last stays in view), the choice stopping at the ends and kept in sight. Left,
/// Right, Home and End stay the field's, for its text. A row that cannot be chosen (a heading) is passed over. One
/// behaviour for every such list; and a list given the keys has them on its chosen row (<see cref="Hold"/>).</summary>
public static class ListKeys
{
    /// <summary>Gives a list's chosen row the keys, as a click on it does (the first chosen where none is and
    /// <paramref name="chooseFirst"/> asks): the row a Shift range runs from, else the first chosen. False where the list has
    /// no row chosen to give them to.</summary>
    public static bool Hold(ListBox list, bool chooseFirst = false) => Hold(list, null, chooseFirst);

    /// <summary>Gives the keys back to the row of a list that had them, where it is still shown and chosen, else to the row
    /// of the item it showed, where that is chosen, else as <see cref="Hold(ListBox, bool)"/> does: so the next key goes on
    /// from where the person was, a Shift range's end rather than its start.</summary>
    public static bool Hold(RowWithKeys had) => Hold(had.List, had, chooseFirst: false);

    /// <summary>A row out of sight has no container to take the keys until it is brought into view.</summary>
    private static bool Hold(ListBox list, RowWithKeys? had, bool chooseFirst)
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
        return list.ContainerFromIndex(index)?.Focus() == true;

        static int? Chosen(ListBox list, int? index) => index is int at && at >= 0 && at < list.ItemCount && list.Selection.IsSelected(at) ? at : null;
    }

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

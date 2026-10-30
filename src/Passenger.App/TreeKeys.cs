using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Passenger.App;

/// <summary>The keys of a tree a list draws as rows (<see cref="ITreeRow"/>), one behaviour in every such tree: Right opens
/// a closed row, Left closes an open one or, on one closed, goes to the row it sits under; Enter and F4 open what the row
/// stands for, and Enter, on a row that stands for nothing to open, opens or closes it. Right and Left are the tree's on
/// every row, never the list's to scroll by; a value typed in a row keeps its own keys. The list scrolls to its chosen
/// row only as the choice changes (<see cref="FollowChoices"/>), so a row opened or closed stays where it was clicked, and
/// the keys stay on the chosen row, a Shift range running from the row it was begun on, however the rows change above or
/// among them.</summary>
public static class TreeKeys
{
    /// <summary>Gives a list of rows the tree's keys: <paramref name="toggle"/> opens or closes a row, <paramref name="open"/>
    /// opens what it stands for, false where it stands for nothing.</summary>
    public static void Attach<T>(ListBox list, Action<T> toggle, Func<T, bool>? open = null)
        where T : class, ITreeRow =>
        Attach<T>(list, row => row, toggle, open);

    /// <summary>The same for a list whose items hold their rows, read by <paramref name="rowOf"/>.</summary>
    public static void Attach<T>(ListBox list, Func<T, ITreeRow> rowOf, Action<T> toggle, Func<T, bool>? open = null)
        where T : class
    {
        FollowChoices(list);
        // Ahead of the list's own keys and its rows', which take the arrows and Enter before a handler of the list hears them.
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox && list.SelectedItem is T item && Take(list, e.Key, item, rowOf, toggle, open))
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Brings the row a choice was made on (the anchor a Shift range runs from) into view as it changes, once the
    /// list shows if it is hidden then, and at no other time: rows opened, closed, added or taken out above it carry it
    /// with them, out of sight if they push it there, so the view stays where the person opened or closed a row. A list
    /// that has the keys keeps them on its chosen row however its rows change, the view left where it is, and one that
    /// hasn't never takes them.</summary>
    private static void FollowChoices(ListBox list)
    {
        list.AutoScrollToSelectedItem = false;
        ISelectionModel selection = list.Selection;
        object? anchored = null;
        bool owed = false;
        bool keys = false;
        void Pay()
        {
            if (owed || keys)
            {
                UiThread.Post(() =>
                {
                    bool give = keys;
                    keys = false;
                    // Hidden, or not laid out yet, the list stays owed its scroll until it shows, and never takes the keys.
                    if (list.Presenter is null || !list.IsEffectivelyVisible || TopLevel.GetTopLevel(list) is not { } top)
                    {
                        return;
                    }
                    bool scrolled = owed && selection.AnchorIndex >= 0;
                    owed = false;
                    if (scrolled)
                    {
                        list.ScrollIntoView(selection.AnchorIndex);
                    }
                    int chosen = selection.AnchorIndex >= 0 ? selection.AnchorIndex : selection.SelectedIndex;
                    if (give && chosen >= 0 && Loose(list, top, chosen))
                    {
                        Hold(list, chosen, stay: !scrolled);
                    }
                });
            }
        }
        selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ISelectionModel.AnchorIndex))
            {
                return;
            }
            // The anchor's index moves with rows put in or taken out above it, its row the same.
            int at = selection.AnchorIndex;
            object? row = at >= 0 && at < list.ItemCount ? list.Items[at] : null;
            if (!ReferenceEquals(row, anchored))
            {
                anchored = row;
                owed = row is not null;
                // A row chosen by code while another has the keys takes them (one a click or a key chose has them already).
                keys |= row is not null && Keeper(list) is not null;
                Pay();
            }
            else if (Keeper(list) is { } keeper)
            {
                // The list put the keys' place at the anchor's new index before its panel moved there, which would drop this row.
                KeyboardNavigation.SetTabOnceActiveElement(list, keeper);
            }
        };
        bool checking = false;
        selection.IndexesChanged += (_, _) =>
        {
            if (checking || anchored is null)
            {
                return;
            }
            checking = true;
            // A row put in among the rows chosen, below the first of them, leaves the anchor's index behind its row.
            UiThread.Post(() =>
            {
                checking = false;
                int at = selection.AnchorIndex;
                if (anchored is not { } row)
                {
                    return;
                }
                int place = Place(list, row, at);
                if (place < 0)
                {
                    anchored = at >= 0 && at < list.ItemCount ? list.Items[at] : null;
                }
                else if (place != at)
                {
                    selection.AnchorIndex = place;
                }
            });
        };
        Control? holder = null;
        list.GotFocus += (_, e) =>
        {
            if (e.Source is Control row && ItemsControl.ItemsControlFromItemContainer(row) == list)
            {
                holder = row;
            }
        };
        list.AttachedToVisualTree += (_, _) => Pay();
        list.TemplateApplied += (_, _) => Pay();
        list.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                Pay();
            }
            else if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false && holder is { } gone
                && (!gone.IsVisible || TopLevel.GetTopLevel(gone) is null))
            {
                // The keys went with the row that had them, let go of or made again; taken elsewhere, they stay there.
                keys = true;
                Pay();
            }
        };
    }

    /// <summary>The row of the list that has the keys, if one has.</summary>
    private static Control? Keeper(ListBox list) =>
        TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Control row && ItemsControl.ItemsControlFromItemContainer(row) == list ? row : null;

    /// <summary>Whether the keys are the list's to give to its chosen row, its window in front: nothing has them, or another
    /// of its rows has.</summary>
    private static bool Loose(ListBox list, TopLevel top, int chosen) =>
        top is not WindowBase { IsActive: false } && top.FocusManager?.GetFocusedElement() switch
        {
            null => true,
            Control row when ItemsControl.ItemsControlFromItemContainer(row) == list => list.IndexFromContainer(row) != chosen,
            _ => false,
        };

    /// <summary>Gives a row the keys, as a click on it does, the view kept where it is when <paramref name="stay"/>: a row
    /// out of sight has no container to take them until it is laid out in view, and the view goes back once it has.</summary>
    private static void Hold(ListBox list, int index, bool stay)
    {
        Vector offset = list.Scroll?.Offset ?? default;
        if (list.ContainerFromIndex(index) is null)
        {
            list.ScrollIntoView(index);
            list.UpdateLayout();
        }
        list.ContainerFromIndex(index)?.Focus();
        if (stay && list.Scroll is { } scroll && scroll.Offset != offset)
        {
            scroll.Offset = offset;
        }
    }

    private static bool Take<T>(ListBox list, Key key, T item, Func<T, ITreeRow> rowOf, Action<T> toggle, Func<T, bool>? open)
        where T : class
    {
        ITreeRow row = rowOf(item);
        switch (key)
        {
            case Key.Right:
                if (row.Expands && !row.IsExpanded)
                {
                    toggle(item);
                }
                return true;
            case Key.Left when row.IsExpanded:
                toggle(item);
                return true;
            case Key.Left:
                if (Holder(list, item, rowOf) is int holder)
                {
                    Choose(list, holder);
                }
                return true;
            case Key.Enter:
                if (open?.Invoke(item) != true && row.Expands)
                {
                    toggle(item);
                }
                return true;
            case Key.F4:
                return open?.Invoke(item) == true;
            default:
                return false;
        }
    }

    /// <summary>The index of the row an item's row sits under: the nearest above the item's own place, less deep.</summary>
    private static int? Holder<T>(ListBox list, T item, Func<T, ITreeRow> rowOf)
        where T : class
    {
        int depth = rowOf(item).Depth;
        for (int at = Place(list, item, list.SelectedIndex) - 1; at >= 0; at--)
        {
            if (list.Items[at] is T above && rowOf(above).Depth < depth)
            {
                return at;
            }
        }
        return null;
    }

    /// <summary>Where an item stands among the list's rows, found by the item itself, -1 where it is gone: the index it
    /// was last known at (<paramref name="near"/>) is taken only once seen to hold it.</summary>
    private static int Place(ListBox list, object item, int near)
    {
        if (near >= 0 && near < list.ItemCount && ReferenceEquals(list.Items[near], item))
        {
            return near;
        }
        for (int at = 0; at < list.ItemCount; at++)
        {
            if (ReferenceEquals(list.Items[at], item))
            {
                return at;
            }
        }
        return -1;
    }

    /// <summary>Chooses a row and gives it the keys, so the next key moves on from it.</summary>
    private static void Choose(ListBox list, int index)
    {
        list.SelectedIndex = index;
        list.ScrollIntoView(index);
        if (list.ContainerFromIndex(index) is null)
        {
            list.UpdateLayout();
        }
        list.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }
}

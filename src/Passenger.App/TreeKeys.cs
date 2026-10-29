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
/// row only as the choice changes (<see cref="FollowChoices"/>), so a row opened or closed stays where it was clicked.</summary>
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
    /// with them, out of sight if they push it there, so the view stays where the person opened or closed a row.</summary>
    private static void FollowChoices(ListBox list)
    {
        list.AutoScrollToSelectedItem = false;
        ISelectionModel selection = list.Selection;
        object? anchored = null;
        bool owed = false;
        void Pay()
        {
            if (owed)
            {
                UiThread.Post(() =>
                {
                    // Hidden, or not laid out yet, the list stays owed its scroll until it shows.
                    if (owed && list.Presenter is not null && list.IsEffectivelyVisible && TopLevel.GetTopLevel(list) is not null)
                    {
                        owed = false;
                        if (selection.AnchorIndex >= 0)
                        {
                            list.ScrollIntoView(selection.AnchorIndex);
                        }
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
                Pay();
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
        };
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
                if (Holder(list, rowOf, row) is int holder)
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

    /// <summary>The index of the row a row sits under: the nearest above the chosen one, less deep.</summary>
    private static int? Holder<T>(ListBox list, Func<T, ITreeRow> rowOf, ITreeRow row)
        where T : class
    {
        for (int at = list.SelectedIndex - 1; at >= 0; at--)
        {
            if (list.Items[at] is T above && rowOf(above).Depth < row.Depth)
            {
                return at;
            }
        }
        return null;
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

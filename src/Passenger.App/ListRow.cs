using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A row a person picks from, one height in every list, tree, menu and popup: Shell.axaml's list items and
/// menu items take it, and a row a view builds itself is as tall. A row takes its height from here, never a number of
/// its own.</summary>
public static class ListRow
{
    public const double Height = 24;

    /// <summary>Where a list's rows' words start inside it, past its padding and theirs (Shell.axaml's <c>ListBox.tree</c>, a
    /// tight step, and its rows', a small one): what stands over or under a list in line with its words starts here.</summary>
    public const double TextStart = Gaps.Tight + Gaps.Small;

    /// <summary>A row's own room at its sides, where it lays itself out (a popup's rows, a list's laid by hand).</summary>
    public static readonly Thickness Inset = new(Gaps.Tight, 0);

    /// <summary>A row's detail at its end (a path, a kind, keys): a wide step from the words before it, a tight one from the
    /// row's end.</summary>
    public static readonly Thickness Detail = new(Gaps.Wide, 0, Gaps.Tight, 0);

    /// <summary>A row's detail as every row shows it: muted at the caption's size, docked at the row's end
    /// (<see cref="Detail"/>), cut with an ellipsis where the row has no room for it.</summary>
    public static TextBlock DetailText() => new()
    {
        Classes = { StyleClasses.Muted, StyleClasses.Caption },
        VerticalAlignment = VerticalAlignment.Center,
        Margin = Detail,
        TextTrimming = TextTrimming.CharacterEllipsis,
        [DockPanel.DockProperty] = Dock.Right,
    };

    /// <summary>The heading over a group of a list's rows (Recent, Other processes) as every list draws it: muted strong
    /// words at the caption's size, a small step above them and a hair below, wrapping where the list is narrow.</summary>
    public static TextBlock Heading() => new()
    {
        Classes = { StyleClasses.Muted, StyleClasses.Caption, StyleClasses.Strong },
        Margin = new Thickness(Gaps.Tight, Gaps.Small, 0, Gaps.Hair),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Has a list's heading rows never chosen, by the pointer or the list's own keys: each container showing an item
    /// <paramref name="isHeading"/> takes for one is disabled, and enabled again as it shows another. Keys moving the choice
    /// from a field pass over them through <see cref="ListKeys.Move"/>.</summary>
    public static void Headings(ListBox list, Func<object?, bool> isHeading) =>
        list.ContainerPrepared += (_, e) => e.Container.IsEnabled = !isHeading(e.Container.DataContext);

    /// <summary>The room above and below a list's rows inside a popup of its own.</summary>
    public static readonly Thickness PopupList = new(0, Gaps.Tight);

    /// <summary>How faint a row stands that is there but off: one that can't be chosen, a feed not used, a file cut to move,
    /// a frame of no interest, an element hidden, a style that does not apply.</summary>
    public const double Dimmed = 0.55;

    /// <summary>Draws a row, or a part of one, faint while it is off (<see cref="Dimmed"/>), whole while it is not.</summary>
    public static void Dim(Visual row, bool off) => row.Opacity = off ? Dimmed : 1;
}

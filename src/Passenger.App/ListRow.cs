using Avalonia;

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

    /// <summary>The room above and below a list's rows inside a popup of its own.</summary>
    public static readonly Thickness PopupList = new(0, Gaps.Tight);
}

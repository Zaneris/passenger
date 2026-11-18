using Avalonia;

namespace Passenger.App;

/// <summary>A search field's room inside its edge (Shell.axaml's <c>TextBox.search</c>), and where its words start, for
/// what stands in line with them below it (Settings' page names).</summary>
public static class SearchFields
{
    /// <summary>The field's padding: its words a step in from its edge, its height a control's.</summary>
    public static readonly Thickness Padding = new(Gaps.Normal, Gaps.Tight, Gaps.Small, Gaps.Tight);

    /// <summary>Where the field's words start, from its outer edge.</summary>
    public static readonly double TextStart = Padding.Left + Hairlines.Width;

    /// <summary>A row's padding that starts its words in line with a search field's above it.</summary>
    public static readonly Thickness InLine = new(TextStart, 0);
}

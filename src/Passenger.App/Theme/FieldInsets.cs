using Avalonia;

namespace Passenger.App;

/// <summary>A field's room inside its edge (Shell.axaml's base <c>TextBox</c>, every field's), and where its words start,
/// for what stands in line with them: a drop-down's and a number box's words, Settings' page names under its search.</summary>
public static class FieldInsets
{
    /// <summary>The field's padding: its words a step in from its edge, its height a control's.</summary>
    public static readonly Thickness Padding = new(Gaps.Normal, Gaps.Tight, Gaps.Small, Gaps.Tight);

    /// <summary>Where the field's words start, from its outer edge.</summary>
    public static readonly double TextStart = Padding.Left + Hairlines.Width;

    /// <summary>A drop-down's padding: its words where a field's start, its arrow its own.</summary>
    public static readonly Thickness DropDown = new(Padding.Left, 0, 0, 0);

    /// <summary>A row's padding that starts its words in line with a field's above it, or a search's magnifier.</summary>
    public static readonly Thickness InLine = new(TextStart, 0);
}

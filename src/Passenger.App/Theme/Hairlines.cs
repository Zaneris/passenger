using Avalonia;

namespace Passenger.App;

/// <summary>The one line weight a border draws between areas or round a box, on whichever sides it stands: a Border's
/// BorderThickness is one of these, never a number of its own (UiRulesTests), its brush the style's.</summary>
public static class Hairlines
{
    /// <summary>A line's weight.</summary>
    public const double Width = 1;

    /// <summary>A line along the foot, under a header or a row.</summary>
    public static readonly Thickness Below = new(0, 0, 0, Width);

    /// <summary>A line along the top, over a footer or a pane below another.</summary>
    public static readonly Thickness Above = new(0, Width, 0, 0);

    /// <summary>A line down the right, between a list and the page beside it.</summary>
    public static readonly Thickness After = new(0, 0, Width, 0);

    /// <summary>A line round all four sides.</summary>
    public static readonly Thickness Round = new(Width);
}

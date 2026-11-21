using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>Words to read and copy (code, a Markdown page's, a value in Details), laid out a rounding error wider than
/// the width they measured where they are given that width: the text's formatter sums a line's advances with an error of
/// its own, so given its own measured width (as a box sized to them gives it wherever that width falls on a pixel at the
/// scale in force, code's at 1.25 and 2) it wraps the line's last word to a line there is no height for, cutting the words
/// short.</summary>
public class SelectableWords : SelectableTextBlock
{
    /// <summary>A millionth of a unit: far above a line's error in summing its advances, far below a pixel at any scale.</summary>
    private const double RoundingError = 1e-6;

    /// <summary>The width the words last measured, before the layout rounded it.</summary>
    private double measured;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        measured = size.Width;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize) =>
        base.ArrangeOverride(Math.Abs(finalSize.Width - measured) < RoundingError ? finalSize.WithWidth(measured + RoundingError) : finalSize);
}

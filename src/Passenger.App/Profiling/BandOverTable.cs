using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Passenger.App.Profiling;

/// <summary>A Frames page: its band (the legend, the frames' chart and what they cost) over its table's column titles and
/// rows. The band takes the room it asks for while that leaves the table its titles and <see cref="RowsKept"/> rows; in a
/// pane too short for both it gives way, what it holds scrolling over its divider, so the rows a person reads and opens stay
/// in view however short the pane.</summary>
public sealed class BandOverTable : Panel
{
    /// <summary>The rows the table keeps in view under its titles, however much the band asks.</summary>
    public const int RowsKept = 3;

    private readonly Border band;
    private readonly Control titles;
    private readonly Control table;

    public BandOverTable(Control said, Control titles, Control table)
    {
        band = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer { Content = said, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Padding = new Thickness(0, 4, 0, 0),
        };
        band.Bind(Border.BorderBrushProperty, band.GetResourceObservable(TokenKeys.Divider));
        this.titles = titles;
        this.table = table;
        Children.AddRange([band, titles, table]);
    }

    /// <summary>The most the band may stand in a page of a height: what leaves the table its titles and rows.</summary>
    private double Room(double height) => Math.Max(0, height - titles.DesiredSize.Height - (RowsKept * ListRow.Height));

    protected override Size MeasureOverride(Size availableSize)
    {
        titles.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        band.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        if (band.DesiredSize.Height > Room(availableSize.Height))
        {
            // Measured to its room it scrolls; measured whole, what it holds growing asks the page again.
            band.Measure(new Size(availableSize.Width, Room(availableSize.Height)));
        }
        double over = band.DesiredSize.Height + titles.DesiredSize.Height;
        table.Measure(new Size(availableSize.Width, Math.Max(0, availableSize.Height - over)));
        double width = Math.Max(band.DesiredSize.Width, Math.Max(titles.DesiredSize.Width, table.DesiredSize.Width));
        return new Size(width, Math.Min(availableSize.Height, over + table.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double height = Math.Min(band.DesiredSize.Height, Room(finalSize.Height));
        band.Arrange(new Rect(0, 0, finalSize.Width, height));
        titles.Arrange(new Rect(0, height, finalSize.Width, titles.DesiredSize.Height));
        double top = height + titles.DesiredSize.Height;
        table.Arrange(new Rect(0, top, finalSize.Width, Math.Max(0, finalSize.Height - top)));
        return finalSize;
    }
}

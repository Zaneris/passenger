using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's two panes: side by side, and in a window narrow and tall, as a side dock is, the first over the
/// second, where side by side would leave neither room for its words. Each way keeps its own shares, which its divider
/// moves.</summary>
public sealed class PaneSplit : Grid
{
    private readonly Control first;
    private readonly Control second;
    private readonly HairlineDivider beside = new(GridResizeDirection.Columns) { [ColumnProperty] = 1, [RowSpanProperty] = 3 };
    private readonly HairlineDivider under = new(GridResizeDirection.Rows) { [RowProperty] = 1, [ColumnSpanProperty] = 3, IsVisible = false };

    /// <param name="first">The left pane, or the upper.</param>
    /// <param name="second">The right pane, or the lower.</param>
    /// <param name="across">Their columns side by side, the divider's between them ("2*,1,3*").</param>
    /// <param name="down">Their rows one over the other; those of <paramref name="across"/> by default.</param>
    public PaneSplit(Control first, Control second, string across, string? down = null)
    {
        this.first = first;
        this.second = second;
        ColumnDefinitions = new ColumnDefinitions(across);
        RowDefinitions = new RowDefinitions(down ?? across);
        Children.AddRange([first, beside, under, second]);
        Place(first, 0);
        Place(second, 2);
    }

    /// <summary>Whether the panes stand one over the other.</summary>
    public bool IsStacked => under.IsVisible;

    /// <summary>The divider between the panes as they stand.</summary>
    public HairlineDivider Divider => IsStacked ? under : beside;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ToolLayout.Stacks(availableSize.Width, availableSize.Height) != IsStacked)
        {
            under.IsVisible = !IsStacked;
            beside.IsVisible = !under.IsVisible;
            Place(first, 0);
            Place(second, 2);
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>A pane in its column and every row, or its row and every column.</summary>
    private void Place(Control pane, int at)
    {
        SetColumn(pane, IsStacked ? 0 : at);
        SetColumnSpan(pane, IsStacked ? 3 : 1);
        SetRow(pane, IsStacked ? at : 0);
        SetRowSpan(pane, IsStacked ? 1 : 3);
    }
}

using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.ToolWindows;

/// <summary>Two panes, a tool window's or an editor's and its file's own (a preview): side by side, and in a place narrow
/// and tall, as a side dock or the editors beside it are, the first over the second, where side by side would leave
/// neither room for its words. Each way keeps its own shares, which its divider moves. With no second pane the first
/// takes it all.</summary>
public sealed class PaneSplit : Grid
{
    private readonly Control first;
    private Control? second;
    private readonly string across;
    private readonly string down;
    private readonly HairlineDivider beside = new(GridResizeDirection.Columns) { [ColumnProperty] = 1, [RowSpanProperty] = 3 };
    private readonly HairlineDivider under = new(GridResizeDirection.Rows) { [RowProperty] = 1, [ColumnSpanProperty] = 3, IsVisible = false };
    private bool stacked;

    /// <param name="first">The left pane, or the upper.</param>
    /// <param name="second">The right pane, or the lower; null for none yet.</param>
    /// <param name="across">Their columns side by side, the divider's between them ("2*,1,3*").</param>
    /// <param name="down">Their rows one over the other; those of <paramref name="across"/> by default.</param>
    public PaneSplit(Control first, Control? second, string across, string? down = null)
    {
        this.first = first;
        this.across = across;
        this.down = down ?? across;
        ColumnDefinitions = new ColumnDefinitions(across);
        RowDefinitions = new RowDefinitions(this.down);
        this.second = second;
        Children.AddRange(second is null ? [first, beside, under] : [first, beside, under, second]);
        Lay();
    }

    /// <summary>The right pane, or the lower; null while the first stands alone. One coming where none was takes the first
    /// shares again; one in another's place keeps the shares its divider moved.</summary>
    public Control? Second
    {
        get => second;
        set
        {
            if (value == second)
            {
                return;
            }
            if (second is not null)
            {
                Children.Remove(second);
            }
            else if (value is not null)
            {
                ColumnDefinitions = new ColumnDefinitions(across);
                RowDefinitions = new RowDefinitions(down);
            }
            second = value;
            if (value is not null)
            {
                Children.Add(value);
            }
            Lay();
        }
    }

    /// <summary>Whether the panes stand one over the other.</summary>
    public bool IsStacked => stacked;

    /// <summary>The divider between the panes as they stand.</summary>
    public HairlineDivider Divider => stacked ? under : beside;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ToolLayout.Stacks(availableSize.Width, availableSize.Height) != stacked)
        {
            stacked = !stacked;
            Lay();
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>Each pane in its place and the divider between them as they stand, or the first alone over every cell.</summary>
    private void Lay()
    {
        beside.IsVisible = second is not null && !stacked;
        under.IsVisible = second is not null && stacked;
        Place(first, 0, second is null);
        if (second is not null)
        {
            Place(second, 2, alone: false);
        }
    }

    /// <summary>A pane in its column and every row, or its row and every column; alone, in every one.</summary>
    private void Place(Control pane, int at, bool alone)
    {
        bool rows = stacked || alone;
        bool columns = !stacked || alone;
        SetColumn(pane, rows ? 0 : at);
        SetColumnSpan(pane, rows ? 3 : 1);
        SetRow(pane, columns ? 0 : at);
        SetRowSpan(pane, columns ? 3 : 1);
    }
}

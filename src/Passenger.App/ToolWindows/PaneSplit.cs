using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.ToolWindows;

/// <summary>Two panes, a tool window's or an editor's and its file's own (a preview): side by side, and in a place narrow
/// and tall, as a side dock or the editors beside it are, the first over the second, where side by side would leave
/// neither room for its words; or either way whatever the shape, as its user sets them (<see cref="SetPresentation"/>).
/// Each way keeps its own shares, which its divider moves. With no second pane the first takes it all, no divider of its
/// own standing in the tree; the second may stand alone too, the first hidden.</summary>
public sealed class PaneSplit : Grid
{
    private readonly Control first;
    private Control? second;
    private readonly string across;
    private readonly string down;
    private readonly HairlineDivider beside = new(GridResizeDirection.Columns) { [ColumnProperty] = 1, [RowSpanProperty] = 3 };
    private readonly HairlineDivider under = new(GridResizeDirection.Rows) { [RowProperty] = 1, [ColumnSpanProperty] = 3, IsVisible = false };
    private bool stacked;

    /// <summary>One over the other (true) or side by side (false) whatever the place's shape; null as the shape stands them.</summary>
    private bool? stackedOverride;

    /// <summary>Whether the second pane, while there is one, stands alone over every cell, the first hidden.</summary>
    private bool secondOnly;

    /// <summary>Whether the first pane is hidden by <see cref="secondOnly"/>.</summary>
    private bool firstHidden;

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
        Children.AddRange(second is null ? [first] : [first, beside, under, second]);
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
            else
            {
                ColumnDefinitions = new ColumnDefinitions(across);
                RowDefinitions = new RowDefinitions(down);
                Children.AddRange([beside, under]);
            }
            second = value;
            if (value is not null)
            {
                Children.Add(value);
            }
            else
            {
                Children.RemoveAll([beside, under]);
            }
            Lay();
        }
    }

    /// <summary>Whether the panes stand one over the other.</summary>
    public bool IsStacked => stacked;

    /// <summary>The divider between the panes as they stand.</summary>
    public HairlineDivider Divider => stacked ? under : beside;

    /// <summary>The panes turned, one over the other or side by side.</summary>
    internal event EventHandler? DirectionChanged;

    /// <summary>The panes stand as <paramref name="stack"/> says whatever the place's shape (null: as the shape stands
    /// them), and with <paramref name="onlySecond"/> the second alone while there is one.</summary>
    internal void SetPresentation(bool? stack, bool onlySecond)
    {
        if (stackedOverride == stack && secondOnly == onlySecond)
        {
            return;
        }
        stackedOverride = stack;
        secondOnly = onlySecond;
        Lay();
        InvalidateMeasure();
    }

    /// <summary>The way to stand the panes turned from how they stand: one over the other (true) or side by side (false),
    /// null where that is how the place's shape stands them.</summary>
    internal bool? Turned => !stacked == ToolLayout.Stacks(Bounds.Width, Bounds.Height) ? null : !stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        if ((stackedOverride ?? ToolLayout.Stacks(availableSize.Width, availableSize.Height)) != stacked)
        {
            stacked = !stacked;
            Lay();
            DirectionChanged?.Invoke(this, EventArgs.Empty);
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>Each pane in its place and the divider between them as they stand, or either alone over every cell.</summary>
    private void Lay()
    {
        bool alone = secondOnly && second is not null;
        beside.IsVisible = second is not null && !alone && !stacked;
        under.IsVisible = second is not null && !alone && stacked;
        if (alone != firstHidden)
        {
            // Only this split's own hiding is undone, never the first pane's own (a log's branches turned off).
            firstHidden = alone;
            first.IsVisible = !alone;
        }
        Place(first, 0, second is null);
        if (second is not null)
        {
            Place(second, 2, alone);
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

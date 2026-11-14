using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Passenger.App;

/// <summary>A row whose children take their own widths while they fit and, when the row is narrower, give way from the
/// last one back, each down to its MinWidth (its Width when it has one, its item's when it holds a list's): a list's
/// row (a frame's place before its method's name, a branch's upstream before its name) or the main toolbar narrowing
/// (the branch's name before the project's). Its words end in an ellipsis (Shell.axaml), so none is cut
/// mid-letter. A child that goes whole (<see cref="GoesWholeProperty"/>) goes where the row cannot hold every child at
/// its least.</summary>
public sealed class ShrinkingRow : Panel
{
    private double[] widths = [];

    /// <summary>The children gone whole as the row was last measured, by their places.</summary>
    private ulong gone;

    /// <summary>The room between two children shown.</summary>
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<ShrinkingRow, double>(nameof(Spacing));

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The least the row gives way to, its children each at its least, known once it has been measured.</summary>
    public double LeastWidth { get; private set; }

    static ShrinkingRow()
    {
        AffectsMeasure<ShrinkingRow>(SpacingProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        widths = new double[Children.Count];
        double total = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            Control child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths[i] = child.IsVisible ? child.DesiredSize.Width : 0;
            total += widths[i];
        }
        gone = 0;
        int shown = 0;
        int staying = 0;
        double least = 0;
        double staysLeast = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i].IsVisible)
            {
                double its = LeastOf(Children[i], i);
                least += its;
                shown++;
                if (!GetGoesWhole(Children[i]))
                {
                    staysLeast += its;
                    staying++;
                }
            }
        }
        double gaps = Spacing * Math.Max(0, shown - 1);
        least += gaps;
        LeastWidth = staysLeast + Spacing * Math.Max(0, staying - 1);
        // Those that go whole go before any other is cut below its least, the last first.
        for (int i = Math.Min(Children.Count, 64) - 1; i >= 0 && least > availableSize.Width; i--)
        {
            if (Children[i].IsVisible && GetGoesWhole(Children[i]))
            {
                least -= LeastOf(Children[i], i) + (gaps > 0 ? Spacing : 0);
                gone |= 1UL << i;
                gaps = Math.Max(0, gaps - Spacing);
                total -= widths[i];
                widths[i] = 0;
            }
        }
        double room = Math.Max(0, availableSize.Width - gaps);
        double excess = double.IsInfinity(room) ? 0 : total - room;
        // One that gives way first does so before any other gives a pixel, and before the row takes the room it frees.
        double free = Children.OfType<IGivesWayFirst>().Sum(c => c.LeavesFree);
        double before = double.IsInfinity(room) ? 0 : total - Math.Max(0, room - free);
        for (int i = Children.Count - 1; i >= 0 && before > 0; i--)
        {
            if (Children[i] is IGivesWayFirst first)
            {
                double give = Math.Min(before, Math.Max(0, widths[i] - (first.LeastWidth + Children[i].Margin.Left + Children[i].Margin.Right)));
                widths[i] -= give;
                before -= give;
                excess -= give;
            }
        }
        for (int i = Children.Count - 1; i >= 0 && excess > 0; i--)
        {
            if (Children[i] is IGivesWayFirst)
            {
                continue;
            }
            double give = Math.Min(excess, Math.Max(0, widths[i] - Least(Children[i], i)));
            widths[i] -= give;
            excess -= give;
        }
        // Told the room left over too, so a child that grows by itself (a tab strip) is measured again within it.
        double slack = double.IsInfinity(room) ? 0 : Math.Max(0, room - widths.Sum());
        double height = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(widths[i] + (Children[i] is IGivesWayFirst || Gone(i) ? 0 : slack), availableSize.Height));
            height = Math.Max(height, Children[i].DesiredSize.Height);
            if (Children[i] is IGivesWayFirst)
            {
                // It gives way by whole parts (a menu's title), so it may want less than it was given: no gap after it.
                widths[i] = Math.Min(widths[i], Children[i].DesiredSize.Width);
            }
        }
        return new Size(widths.Sum() + gaps, height);
    }

    /// <summary>The least a child shown takes in the row, at most what it wants.</summary>
    private double LeastOf(Control child, int i) => child is IGivesWayFirst first
        ? Math.Min(widths[i], first.LeastWidth + child.Margin.Left + child.Margin.Right) : Math.Min(widths[i], Least(child, i));

    /// <summary>The least a child gives way to in a row, past its MinWidth, which leaves it as it is anywhere else: a closable
    /// tab's few letters, its ellipsis and its ×.</summary>
    public static readonly AttachedProperty<double> LeastProperty = AvaloniaProperty.RegisterAttached<ShrinkingRow, Control, double>("Least");

    private double Least(Control child, int i) => !double.IsNaN(child.Width) ? widths[i]
        : Math.Max(Math.Max(child.MinWidth, child.GetValue(LeastProperty)), ItemOf(child) is { } item ? Math.Max(item.MinWidth, item.GetValue(LeastProperty)) + item.Margin.Left + item.Margin.Right : 0)
            + child.Margin.Left + child.Margin.Right;

    /// <summary>A child that goes whole where the row cannot hold every child at its least, the last first, before any is
    /// cut below its least: unseen, taking no pointer and no Tab, as the main menu's titles go behind its button.</summary>
    public static readonly AttachedProperty<bool> GoesWholeProperty = AvaloniaProperty.RegisterAttached<ShrinkingRow, Control, bool>("GoesWhole");

    public static bool GetGoesWhole(Control child) => child.GetValue(GoesWholeProperty);

    public static void SetGoesWhole(Control child, bool value) => child.SetValue(GoesWholeProperty, value);

    /// <summary>Whether a child has gone whole as its row was last measured, so what it shows is shown elsewhere.</summary>
    public static bool HasGone(Control child) => child.Parent is ShrinkingRow row && row.Gone(row.Children.IndexOf(child));

    private bool Gone(int i) => i is >= 0 and < 64 && (gone & (1UL << i)) != 0;

    /// <summary>An item a list's container holds (a row's panel of an ItemsControl's tabs), whose least width is its container's.</summary>
    private static Control? ItemOf(Control child) => child is Avalonia.Controls.Presenters.ContentPresenter { Child: { } item } ? item : null;

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < Children.Count && i < widths.Length; i++)
        {
            Control child = Children[i];
            if (GetGoesWhole(child))
            {
                Show(child, !Gone(i));
            }
            if (Gone(i))
            {
                child.Arrange(default);
                continue;
            }
            child.Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i] + (child.IsVisible ? Spacing : 0);
        }
        return finalSize;
    }

    /// <summary>A child that goes whole shown, or laid out to nothing unseen (a MinWidth keeps its bounds, so it is hidden
    /// by its opacity).</summary>
    private static void Show(Control child, bool shown)
    {
        if (shown)
        {
            child.ClearValue(OpacityProperty);
            child.ClearValue(IsHitTestVisibleProperty);
            child.ClearValue(KeyboardNavigation.IsTabStopProperty);
            return;
        }
        child.Opacity = 0;
        child.IsHitTestVisible = false;
        KeyboardNavigation.SetIsTabStop(child, false);
    }
}

/// <summary>A child of a <see cref="ShrinkingRow"/> that gives way before the others do, down to its least: the main
/// menu's bar, its menus going behind its button one by one before the project's and branch's names narrow.</summary>
public interface IGivesWayFirst
{
    /// <summary>The least it takes, known once it has been measured.</summary>
    double LeastWidth { get; }

    /// <summary>The room beside the row it gives way to keep free while it has more to give (the title bar's caption,
    /// which moves the window).</summary>
    double LeavesFree { get; }
}

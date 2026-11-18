using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.Docking;

/// <summary>A place's titled tabs in a row that shows each tab whole or not at all: all of them where they fit, else the
/// open one (<see cref="Kept"/>) and the others in their order while they fit beside it, the rest gone and listed by the
/// button the row ends with (<see cref="More"/>), shown only while some are gone. A tab gone is laid out past the row's
/// end, which clips it, so it is neither seen nor pressed.</summary>
internal sealed class FittingTabs : Panel
{
    private readonly HashSet<Control> gone = [];
    private Control? kept;

    public FittingTabs(Control more)
    {
        More = more;
        Children.Add(more);
        ClipToBounds = true;
    }

    /// <summary>The button listing the tabs gone, the row's last child.</summary>
    public Control More { get; }

    /// <summary>The open window's tab, which stays whatever else goes.</summary>
    public Control? Kept
    {
        get => kept;
        set
        {
            if (value != kept)
            {
                kept = value;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>The tabs, in their order.</summary>
    public IEnumerable<Control> Tabs => Children.Where(c => c != More);

    /// <summary>Puts these tabs in the row, before its button, unless it holds them already in this order.</summary>
    public void Hold(IReadOnlyList<Control> tabs)
    {
        if (Tabs.SequenceEqual(tabs))
        {
            return;
        }
        Children.Clear();
        Children.AddRange(tabs);
        Children.Add(More);
    }

    /// <summary>Whether a tab is gone for want of room, as the row was last measured.</summary>
    public bool IsGone(Control tab) => gone.Contains(tab);

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        gone.Clear();
        List<Control> tabs = [.. Tabs];
        double total = tabs.Sum(t => t.DesiredSize.Width);
        if (total <= availableSize.Width)
        {
            return new Size(total, height);
        }
        double room = availableSize.Width - More.DesiredSize.Width - (kept is not null && tabs.Contains(kept) ? kept.DesiredSize.Width : 0);
        bool full = false;
        foreach (Control tab in tabs.Where(t => t != kept))
        {
            full = full || tab.DesiredSize.Width > room;
            if (full)
            {
                gone.Add(tab);
            }
            else
            {
                room -= tab.DesiredSize.Width;
            }
        }
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (Control tab in Tabs)
        {
            if (gone.Contains(tab))
            {
                tab.Arrange(new Rect(finalSize.Width, 0, tab.DesiredSize.Width, finalSize.Height));
                continue;
            }
            tab.Arrange(new Rect(x, 0, tab.DesiredSize.Width, finalSize.Height));
            x += tab.DesiredSize.Width;
        }
        More.Arrange(gone.Count > 0 ? new Rect(finalSize.Width - More.DesiredSize.Width, 0, More.DesiredSize.Width, finalSize.Height)
            : new Rect(finalSize.Width, 0, More.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}

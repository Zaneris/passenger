using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The main toolbar's row: at its right the window's buttons and the end group (docked right, the first
/// rightmost), at its left the start group. The start group's least is kept for it before the end group is measured, so
/// the start group's names give way first and the end group's (the run configuration's) only once those are at their
/// least: neither group draws over the other.</summary>
public sealed class ToolbarRow : Panel
{
    private Control? Start => Children.FirstOrDefault(c => DockPanel.GetDock(c) != Dock.Right);

    private IEnumerable<Control> Ends => Children.Where(c => DockPanel.GetDock(c) == Dock.Right);

    protected override Size MeasureOverride(Size availableSize)
    {
        Control? start = Start;
        double least = 0;
        if (start is not null)
        {
            start.Measure(availableSize);
            least = start is ShrinkingRow row ? row.LeastWidth + start.Margin.Left + start.Margin.Right : start.DesiredSize.Width;
        }
        double ends = 0;
        double height = 0;
        foreach (Control end in Ends)
        {
            end.Measure(availableSize.WithWidth(Math.Max(0, availableSize.Width - least - ends)));
            ends += end.DesiredSize.Width;
            height = Math.Max(height, end.DesiredSize.Height);
        }
        if (start is not null)
        {
            start.Measure(availableSize.WithWidth(Math.Max(0, availableSize.Width - ends)));
            height = Math.Max(height, start.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? ends + (start?.DesiredSize.Width ?? 0) : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double right = finalSize.Width;
        foreach (Control end in Ends)
        {
            right -= end.DesiredSize.Width;
            end.Arrange(new Rect(right, 0, end.DesiredSize.Width, finalSize.Height));
        }
        Start?.Arrange(new Rect(0, 0, Math.Max(0, right), finalSize.Height));
        return finalSize;
    }
}

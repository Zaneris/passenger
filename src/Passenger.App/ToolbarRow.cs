using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A toolbar's row (the main toolbar's, a tool window's header, the status bar's): at its right the end groups
/// (docked right, the first rightmost: the window's buttons, the run widget, the status bar's widgets), at its left the
/// start group. The start group's least is kept for it before the end groups are measured, so the start group's names
/// give way first and the end groups' (the run configuration's, a header's buttons, the status bar's branch) only once
/// those are at their least: neither group draws over the other.</summary>
public sealed class ToolbarRow : Panel
{
    private Control? Start => Children.FirstOrDefault(c => DockPanel.GetDock(c) != Dock.Right);

    private IEnumerable<Control> Ends => Children.Where(c => DockPanel.GetDock(c) == Dock.Right);

    /// <summary>The least the row holds without drawing one group over another, every group at its least, known once it
    /// has been measured.</summary>
    public double LeastWidth { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        Control? start = Start;
        double least = 0;
        if (start is not null)
        {
            start.Measure(availableSize);
            least = LeastOf(start);
        }
        double ends = 0;
        double endsLeast = 0;
        double height = 0;
        foreach (Control end in Ends)
        {
            double room = Math.Max(0, availableSize.Width - least - ends);
            end.Measure(availableSize.WithWidth(room));
            if (LeastOf(end) > room)
            {
                // Held below its least (the status bar's widgets, which keep their widths past the branch's), it keeps that least.
                end.Measure(availableSize.WithWidth(Math.Max(0, Math.Min(availableSize.Width - ends, LeastOf(end)))));
            }
            ends += end.DesiredSize.Width;
            endsLeast += LeastOf(end);
            height = Math.Max(height, end.DesiredSize.Height);
        }
        LeastWidth = least + endsLeast;
        if (start is not null)
        {
            start.Measure(availableSize.WithWidth(Math.Max(0, availableSize.Width - ends)));
            height = Math.Max(height, start.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? ends + (start?.DesiredSize.Width ?? 0) : availableSize.Width, height);
    }

    private static double LeastOf(Control group) => group switch
    {
        ShrinkingRow row => row.LeastWidth + group.Margin.Left + group.Margin.Right,
        StatusLine => StatusLine.LeastWidth + group.Margin.Left + group.Margin.Right,
        _ => group.DesiredSize.Width,
    };

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

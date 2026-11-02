using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The status bar's line left of its widgets: the file's path, then what the bar says. What it says takes the
/// room it needs first, so a message (a refusal, a stage of work) is read whole while the path gives way, shortened to
/// end a gap before the message (<see cref="PathCrumbs"/>); with nothing said, the path has the line. The path is measured
/// whole, never against the room it is given, so a part come to it since is measured as drawn and the line laid out
/// anew.</summary>
public sealed class StatusLine : Panel
{
    /// <summary>The room between a cut path and the message after it, as between the bar's widgets.</summary>
    private const double Gap = 18;

    /// <summary>The path's width as the line lays it, and whether that cuts it.</summary>
    private (double Width, bool Cut) laid;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [Control path, Control message])
        {
            return default;
        }
        message.Measure(availableSize);
        path.Measure(availableSize.WithWidth(double.PositiveInfinity));
        double room = Math.Max(0, availableSize.Width - message.DesiredSize.Width);
        laid = path.DesiredSize.Width <= room ? (path.DesiredSize.Width, false) : (Math.Max(0, room - Gap), true);
        return new Size(Math.Min(availableSize.Width, laid.Width + (laid.Cut ? Gap : 0) + message.DesiredSize.Width),
            Math.Max(path.DesiredSize.Height, message.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is [Control path, Control message])
        {
            double shown = Math.Min(laid.Width, finalSize.Width);
            path.Arrange(new Rect(0, 0, shown, finalSize.Height));
            double after = Math.Min(finalSize.Width, shown + (laid.Cut ? Gap : 0));
            message.Arrange(new Rect(after, 0, finalSize.Width - after, finalSize.Height));
        }
        return finalSize;
    }
}

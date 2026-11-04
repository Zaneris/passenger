using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The status bar's line left of its widgets: the file's path, then what the bar says. The path keeps its room up
/// to <see cref="PathShare"/> of the line, and a message too long for the rest is cut with an ellipsis, its whole text on
/// hover (<see cref="StatusBar"/>); a path longer than its share gives way to a message, shortened to end a gap before it
/// (<see cref="PathCrumbs"/>), and with nothing said the path has the line. The path is measured whole, never against the
/// room it is given, so a part come to it since is measured as drawn and the line laid out anew.</summary>
public sealed class StatusLine : Panel
{
    /// <summary>The room between a cut path and the message after it, as between the bar's widgets.</summary>
    private const double Gap = 18;

    /// <summary>The share of the line the path keeps, whatever the message's length.</summary>
    public const double PathShare = 0.5;

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
        double whole = path.DesiredSize.Width;
        double room = Math.Max(0, availableSize.Width - message.DesiredSize.Width);
        double kept = Math.Max(Math.Min(whole, availableSize.Width * PathShare), room - Gap);
        laid = whole <= room ? (whole, false) : kept >= whole ? (whole, false) : (Math.Max(0, kept), true);
        // The message measured again in what the path leaves it, so its words end in an ellipsis there.
        message.Measure(availableSize.WithWidth(Math.Max(0, availableSize.Width - laid.Width - (laid.Cut ? Gap : 0))));
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

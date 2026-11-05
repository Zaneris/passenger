using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The status bar's line left of its widgets: the file's path, then what the bar says. The path keeps its room up
/// to <see cref="PathShare"/> of the line, short of <see cref="MessageLeast"/> for the message, and a message too long for
/// the rest is cut with an ellipsis, its whole text on hover (<see cref="StatusBar"/>); a path longer than its share gives
/// way to a message, shortened to end a gap before it (<see cref="PathCrumbs"/>), and with nothing said the path has the
/// line. The path is measured whole, never against the room it is given, so a part come to it since is measured as drawn
/// and the line laid out anew.</summary>
public sealed class StatusLine : Panel
{
    /// <summary>The room between a cut path and the message after it, as between the bar's widgets.</summary>
    private const double Gap = 18;

    /// <summary>The share of the line the path keeps, whatever the message's length, where that leaves the message its
    /// <see cref="MessageLeast"/>.</summary>
    public const double PathShare = 0.5;

    /// <summary>The room a message keeps beside any path: a sentence of the bar's (a refusal, a build's outcome), so the
    /// narrow line of a small window gives it the room its path's share would take.</summary>
    private const double MessageLeast = 300;

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
        double width = availableSize.Width;
        double whole = path.DesiredSize.Width;
        double said = message.DesiredSize.Width;
        double room = Math.Max(0, width - said);
        double share = Math.Min(whole, Math.Max(0, Math.Min(width * PathShare, width - MessageLeast - Gap)));
        if (whole <= room)
        {
            laid = (whole, false);
        }
        else if (said <= width - share - Gap)
        {
            laid = (room - Gap, true);
        }
        else
        {
            laid = (share, share < whole);
            // The message measured again in what the path leaves it, so its words end in an ellipsis there.
            message.Measure(availableSize.WithWidth(Math.Max(0, width - share - (laid.Cut ? Gap : 0))));
        }
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

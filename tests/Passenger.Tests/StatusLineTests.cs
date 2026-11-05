using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>The status bar's line of a path and a message: the path keeps its room up to its share of the line and a long
/// message is cut there with an ellipsis; a path longer than its share is cut at it beside a long message, and gives way to
/// a short one, which is read whole.</summary>
public sealed class StatusLineTests
{
    private const string Long = "Snapshot of Greenhouse.Cli (greenhouse): 912 samples, kept on the Performance Profiler's Snapshots page, "
        + "where it opens again from its row";

    [AvaloniaFact]
    public void A_long_message_is_cut_with_an_ellipsis_while_the_path_keeps_its_room_up_to_its_share()
    {
        (TextBlock path, TextBlock message) = Laid("src › Greenhouse.Core › Forecast.cs", Long, 700);
        Assert.Equal(path.DesiredSize.Width, path.Bounds.Width, 0.5);
        Assert.True(message.Bounds.Right <= 700.5, $"The message runs to {message.Bounds.Right:0.#}, past the line's 700");
        Assert.True(message.TextLayout.TextLines.Any(line => line.HasCollapsed), "The long message is not cut with an ellipsis");
    }

    [AvaloniaFact]
    public void A_path_longer_than_its_share_is_cut_at_it_beside_a_long_message_and_gives_way_to_a_short_one()
    {
        string deep = string.Join(" › ", Enumerable.Repeat("Processing", 12)) + " › RefundCalculator.cs";
        (TextBlock path, TextBlock message) = Laid(deep, Long, 700);
        Assert.Equal(700 * StatusLine.PathShare, path.Bounds.Width, 0.5);
        Assert.True(message.TextLayout.TextLines.Any(line => line.HasCollapsed), "The long message beside a long path is not cut with an ellipsis");

        (TextBlock shortened, TextBlock whole) = Laid(deep, "Built", 700);
        Assert.True(shortened.Bounds.Width > 700 * StatusLine.PathShare, $"A long path beside a short message kept only {shortened.Bounds.Width:0.#} of 700");
        Assert.False(whole.TextLayout.TextLines.Any(line => line.HasCollapsed), "A short message beside a long path was cut");
        Assert.True(whole.Bounds.Right <= 700.5, $"The short message runs to {whole.Bounds.Right:0.#}, past the line's 700");
    }

    /// <summary>A line of the path's words and the message's laid out at a width.</summary>
    private static (TextBlock Path, TextBlock Message) Laid(string path, string message, double width)
    {
        TextBlock crumbs = new() { Text = path };
        TextBlock said = new() { Text = message, TextTrimming = TextTrimming.CharacterEllipsis };
        StatusLine line = new() { Children = { crumbs, said } };
        line.Measure(new Size(width, 24));
        line.Arrange(new Rect(0, 0, width, 24));
        return (crumbs, said);
    }
}

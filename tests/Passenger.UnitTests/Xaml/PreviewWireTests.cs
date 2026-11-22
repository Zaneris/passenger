using System.Globalization;
using Passenger.Core;

namespace Passenger.UnitTests.Xaml;

/// <summary>What every previewer shares of its wire: the newest request read is the one drawn, those it replaced never,
/// and a design larger than a frame is drawn at is said, never cut.</summary>
public class PreviewWireTests
{
    [Fact]
    public void Of_requests_read_while_one_is_drawn_only_the_newest_waits_and_a_design_past_the_largest_frame_is_too_large() => Cases.All(
        ("of requests read while one is drawn only the newest waits and the end says none", Of_requests_read_while_one_is_drawn_only_the_newest_waits_and_the_end_says_none),
        ("a design larger than the largest frame is said too large", () => Cases.Each<(double, double, double, bool)>(
        [
            (2048, 2048, 4, false),
            (2049, 100, 4, true),
            (100, 8193, 1, true),
        ], row => A_design_larger_than_the_largest_frame_is_said_too_large(row.Item1, row.Item2, row.Item3, row.Item4))));

    private void Of_requests_read_while_one_is_drawn_only_the_newest_waits_and_the_end_says_none()
    {
        PreviewRequests<Asked> requests = new();
        int arrived = 0;
        requests.Read(new StringReader("""
            {"sequence":1,"path":"/App/Page.xaml","xaml":"<Grid />"}
            not a request

            {"sequence":2,"path":"/App/Page.xaml","xaml":"<Grid Width=\"2\" />","scale":2}
            {"sequence":3,"path":"/App/Page.xaml","xaml":"<Grid Width=\"3\" />","scale":2}

            """), () => arrived++);

        Assert.Equal(3, arrived);
        Assert.True(requests.Waiting, "The newest request read is not waiting to be drawn");
        Asked? next = requests.Next();
        Assert.Equal((3, "<Grid Width=\"3\" />", 2.0), (next?.Sequence, next?.Xaml, next?.Scale));
        Assert.Null(requests.Take());
        Assert.True(requests.Next() is null, "A drawing waiting on requests that have ended waits on");
    }

    private void A_design_larger_than_the_largest_frame_is_said_too_large(double width, double height, double scale, bool large)
    {
        string? said = PreviewWire.TooLarge(width, height, scale);

        Assert.True((said is not null) == large, $"A {width} × {height} design at {scale}× was {(said is null ? "drawn" : said)}");
        Assert.True(said is null || said.Contains(string.Create(CultureInfo.InvariantCulture, $"{width:0} × {height:0}"), StringComparison.Ordinal), $"The design's size is missing from {said}");
    }

    private sealed record Asked : PreviewRequest;
}

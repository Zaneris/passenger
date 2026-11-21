using System.Globalization;
using Passenger.Core;

namespace Passenger.UnitTests.Xaml;

/// <summary>What every previewer shares of its wire: the newest request read is the one drawn, those it replaced never,
/// and a design larger than a frame is drawn at is said, never cut.</summary>
public class PreviewWireTests
{
    [Fact]
    public void Of_requests_read_while_one_is_drawn_only_the_newest_waits_and_the_end_says_none()
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

    [Theory]
    [InlineData(2048, 2048, 4, false)]
    [InlineData(2049, 100, 4, true)]
    [InlineData(100, 8193, 1, true)]
    [InlineData(1280, 800, 4, false)]
    public void A_design_larger_than_the_largest_frame_is_said_too_large(double width, double height, double scale, bool large)
    {
        string? said = PreviewWire.TooLarge(width, height, scale);

        Assert.True((said is not null) == large, $"A {width} × {height} design at {scale}× was {(said is null ? "drawn" : said)}");
        Assert.True(said is null || said.Contains(string.Create(CultureInfo.InvariantCulture, $"{width:0} × {height:0}"), StringComparison.Ordinal), $"The design's size is missing from {said}");
    }

    private sealed record Asked : PreviewRequest;
}

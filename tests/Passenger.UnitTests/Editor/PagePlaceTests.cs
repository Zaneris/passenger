using System.Globalization;
using Passenger.App.Editors;

namespace Passenger.UnitTests.Editor;

/// <summary>Where an HTML preview's page is scrolled to, read from each system's web view's answer to the script that asks
/// it, bare (WKWebView, WebKitGTK) or as a JSON string (WebView2), an answer naming no place read as none, and written back
/// into the script that puts it back in numbers JavaScript reads whatever the culture.</summary>
public class PagePlaceTests
{
    [Theory]
    [InlineData("0,1500", 0, 1500)]
    [InlineData("\"0,1500\"", 0, 1500)]
    [InlineData(" \"12.5,1500.75\" ", 12.5, 1500.75)]
    [InlineData("-240,30", -240, 30)]
    public void A_place_is_read_from_each_web_views_answer(string answer, double x, double y) =>
        Assert.Equal(new PagePlace(x, y), PagePlace.Of(answer));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("1500")]
    [InlineData(",1500")]
    [InlineData("left,top")]
    [InlineData("NaN,0")]
    [InlineData("0,Infinity")]
    public void An_answer_naming_no_place_reads_as_none(string? answer) =>
        Assert.True(PagePlace.Of(answer) is null, $"The answer '{answer}' was read as the place {PagePlace.Of(answer)}");

    [Fact]
    public void Only_a_place_away_from_the_pages_start_is_put_back()
    {
        Assert.False(new PagePlace(0, 0).Away, "A page at its start was given a place to put back");
        Assert.True(new PagePlace(0, 1).Away && new PagePlace(-1, 0).Away, "A page scrolled down or across had no place to put back");
    }

    [Fact]
    public void The_script_putting_a_place_back_writes_it_in_numbers_JavaScript_reads_in_any_culture()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string restore = new PagePlace(12.5, 1500.75).Restore;
            Assert.True(restore.Contains("})(12.5, 1500.75);", StringComparison.Ordinal),
                $"A German culture's decimal commas would give the script other numbers than the place: {restore}");
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}

using System.Globalization;
using Passenger.App.Editors;

namespace Passenger.UnitTests.Editor;

/// <summary>Where an HTML preview's page is scrolled to, read from each system's web view's answer to the script that asks
/// it, bare (WKWebView, WebKitGTK) or as a JSON string (WebView2), an answer naming no place read as none, and written back
/// into the script that puts it back in numbers JavaScript reads whatever the culture.</summary>
public class PagePlaceTests
{
    /// <summary>A page's place is read from each web view's answer and put back in numbers JavaScript reads.</summary>
    [Fact]
    public void A_pages_place_is_read_and_put_back_as_the_web_view_says() => Cases.All(
        (nameof(A_place_is_read_from_each_web_views_answer), A_place_is_read_from_each_web_views_answer),
        (nameof(An_answer_naming_no_place_reads_as_none), An_answer_naming_no_place_reads_as_none),
        (nameof(The_script_putting_a_place_back_writes_it_in_numbers_JavaScript_reads_in_any_culture),
            The_script_putting_a_place_back_writes_it_in_numbers_JavaScript_reads_in_any_culture));

    private void A_place_is_read_from_each_web_views_answer() => Cases.Each<(string Answer, double X, double Y)>(
    [
        ("0,1500", 0, 1500),
        ("\"0,1500\"", 0, 1500),
        (" \"12.5,1500.75\" ", 12.5, 1500.75),
        ("-240,30", -240, 30),
    ], row => A_place_is_read_from_each_web_views_answer(row.Answer, row.X, row.Y));

    private void A_place_is_read_from_each_web_views_answer(string answer, double x, double y) =>
        Assert.Equal(new PagePlace(x, y), PagePlace.Of(answer));

    private void An_answer_naming_no_place_reads_as_none() => Cases.Each<string?>(
    [
        null,
        "null",
        "1500",
        ",1500",
        "NaN,0",
    ], An_answer_naming_no_place_reads_as_none);

    private void An_answer_naming_no_place_reads_as_none(string? answer) =>
        Assert.True(PagePlace.Of(answer) is null, $"The answer '{answer}' was read as the place {PagePlace.Of(answer)}");

    private void The_script_putting_a_place_back_writes_it_in_numbers_JavaScript_reads_in_any_culture()
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

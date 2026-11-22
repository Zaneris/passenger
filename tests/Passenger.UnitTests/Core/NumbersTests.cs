using System.Globalization;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A number reads one way wherever the IDE says one, in its own digits whatever the person's culture: thousands
/// set apart by a comma and a fraction by a point, a count whole, a measure to at most or exactly its places.</summary>
public sealed class NumbersTests
{
    [Fact]
    public void A_number_reads_in_the_ides_own_digits_whatever_the_culture() => Cases.All(
        ("A count sets its thousands apart by a comma", A_count_sets_its_thousands_apart_by_a_comma),
        ("A measure is said to at most its places", A_measure_is_said_to_at_most_its_places),
        ("A measure in a column is written to exactly its places", A_measure_in_a_column_is_written_to_exactly_its_places),
        ("Numbers counts sizes and whiles are said in the ides digits under a culture writing a decimal comma", Numbers_counts_sizes_and_whiles_are_said_in_the_ides_digits_under_a_culture_writing_a_decimal_comma));

    private void A_count_sets_its_thousands_apart_by_a_comma() => Cases.Each<(long count, string said)>(
    [
        (0L, "0"),
        (999L, "999"),
        (1234L, "1,234"),
        (1_234_567L, "1,234,567"),
        (-1234L, "-1,234"),
    ], row =>
    {
        (long count, string said) = row;
        string written = Numbers.Said(count);
        Assert.True(written == said, $"{count} was said as {written}, not {said}: a count reads one way in one place and another elsewhere");
    });

    private void A_measure_is_said_to_at_most_its_places() => Cases.Each<(double value, int places, string said)>(
    [
        (1.5, 1, "1.5"),
        (2.0, 1, "2"),
        (1234.25, 2, "1,234.25"),
        (0.004, 2, "0"),
        (12.345, 0, "12"),
    ], row =>
    {
        (double value, int places, string said) = row;
        string written = Numbers.Said(value, places);
        Assert.True(written == said, $"{value} to at most {places} places was said as {written}, not {said}");
    });

    private void A_measure_in_a_column_is_written_to_exactly_its_places() => Cases.Each<(double value, int places, string said)>(
    [
        (12.0, 1, "12.0"),
        (1234.56, 1, "1,234.6"),
        (0.04, 2, "0.04"),
        (1599.5, 0, "1,600"),
    ], row =>
    {
        (double value, int places, string said) = row;
        string written = Numbers.Fixed(value, places);
        Assert.True(written == said, $"{value} to {places} places was written as {written}, not {said}: a column's points would not line up");
    });

    private void Numbers_counts_sizes_and_whiles_are_said_in_the_ides_digits_under_a_culture_writing_a_decimal_comma()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            string[] said = [Numbers.Said(1234), Numbers.Said(1234.5, 1), Numbers.Fixed(1234.5, 2), Plural.Of(1500, "object"), Sizes.Said(1536),
                Durations.Said(TimeSpan.FromMilliseconds(1500))];
            Assert.True(said is ["1,234", "1,234.5", "1,234.50", "1,500 objects", "1.5 KB", "1.5 s"],
                $"Under a German culture numbers were said as [{string.Join(", ", said)}]: the profiler's columns would read one way, the Tests "
                + "window's counts another and agents' answers a third");
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}

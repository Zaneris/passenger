using System.Globalization;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A while reads one way wherever it is said, in the IDE's digits whatever the person's culture: milliseconds
/// under a second, seconds under a minute, minutes under an hour, then hours, to a tenth in the largest unit it reaches once
/// rounded, a minute or an hour alone said as one.</summary>
public sealed class DurationsTests
{
    [Fact]
    public void A_while_is_said_one_way_wherever_it_is_said() => Cases.All(
        ("A while is said in its largest unit to a tenth", A_while_is_said_in_its_largest_unit_to_a_tenth),
        ("A while is said in the ides digits under a culture writing a decimal comma", A_while_is_said_in_the_ides_digits_under_a_culture_writing_a_decimal_comma));

    private void A_while_is_said_in_its_largest_unit_to_a_tenth() => Cases.Each<(double milliseconds, string said)>(
    [
        (0d, "0 ms"),
        (250d, "250 ms"),
        (1500d, "1.5 s"),
        (90_000d, "1.5 minutes"),
        (3_600_000d, "1 hour"),
        (-1500d, "-1.5 s"),
    ], row =>
    {
        (double milliseconds, string said) = row;
        string written = Durations.Said(TimeSpan.FromMilliseconds(milliseconds));
        Assert.True(written == said, $"{milliseconds} ms were said as {written}, not {said}: a while reads one way in one place and another elsewhere");
    });

    private void A_while_is_said_in_the_ides_digits_under_a_culture_writing_a_decimal_comma()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            string[] said = [Durations.Said(TimeSpan.FromMilliseconds(1500)), Durations.Said(TimeSpan.FromSeconds(90)), Durations.Said(TimeSpan.FromHours(1500))];
            Assert.True(said is ["1.5 s", "1.5 minutes", "1,500 hours"], $"Under a German culture a while was said as [{string.Join(", ", said)}]: the Tests window would read one way and agents' answers another");
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}

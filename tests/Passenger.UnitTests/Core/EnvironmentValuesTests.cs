using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A variable that gives a time in milliseconds: a whole number above 0 is taken, unset or empty gives the
/// default, and anything else is fatal, saying what the variable takes, as a malformed flag is.</summary>
public class EnvironmentValuesTests
{
    private const string Variable = "SOME_WAIT_MS";
    private static readonly TimeSpan Otherwise = TimeSpan.FromSeconds(7);

    [Fact]
    public void A_whole_number_of_milliseconds_above_0_is_taken_and_none_given_is_the_default()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), EnvironmentValues.Milliseconds(Variable, "1500", Otherwise));
        Assert.Equal(TimeSpan.FromMilliseconds(1), EnvironmentValues.Milliseconds(Variable, "1", Otherwise));
        Assert.Equal(Otherwise, EnvironmentValues.Milliseconds(Variable, null, Otherwise));
        Assert.Equal(Otherwise, EnvironmentValues.Milliseconds(Variable, "", Otherwise));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1000")]
    [InlineData("5s")]
    [InlineData("1.5")]
    [InlineData(" 100")]
    [InlineData("99999999999")]
    public void Any_other_value_is_fatal_saying_what_the_variable_takes(string given)
    {
        FormatException refused = Assert.Throws<FormatException>(() => EnvironmentValues.Milliseconds(Variable, given, Otherwise));
        Assert.Equal($"{Variable} takes a whole number of milliseconds above 0, not '{given}'", refused.Message);
    }
}

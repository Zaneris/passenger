using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A variable that gives a time in milliseconds: a whole number above 0 is taken, unset or empty gives the
/// default, and anything else is fatal, saying what the variable takes, as a malformed flag is. One naming a stand-in for a
/// host takes https, or http to this machine alone.</summary>
public class EnvironmentValuesTests
{
    private const string Variable = "SOME_WAIT_MS";
    private static readonly TimeSpan Otherwise = TimeSpan.FromSeconds(7);

    [Fact]
    public void A_variables_value_is_taken_where_it_is_what_the_variable_takes_and_otherwise_fatal() => Cases.All(
        ("A whole number of milliseconds above 0 is taken and none given is the default", A_whole_number_of_milliseconds_above_0_is_taken_and_none_given_is_the_default),
        ("Any other value is fatal saying what the variable takes", Any_other_value_is_fatal_saying_what_the_variable_takes),
        ("A stand in at https or on this machine is taken and none given is none", A_stand_in_at_https_or_on_this_machine_is_taken_and_none_given_is_none),
        ("A stand in that is no url or plain http to another machine is fatal saying what the variable takes", A_stand_in_that_is_no_url_or_plain_http_to_another_machine_is_fatal_saying_what_the_variable_takes));

    private void A_whole_number_of_milliseconds_above_0_is_taken_and_none_given_is_the_default()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), EnvironmentValues.Milliseconds(Variable, "1500", Otherwise));
        Assert.Equal(TimeSpan.FromMilliseconds(1), EnvironmentValues.Milliseconds(Variable, "1", Otherwise));
        Assert.Equal(Otherwise, EnvironmentValues.Milliseconds(Variable, null, Otherwise));
        Assert.Equal(Otherwise, EnvironmentValues.Milliseconds(Variable, "", Otherwise));
    }

    private void Any_other_value_is_fatal_saying_what_the_variable_takes() => Cases.Each<string>(
    [
        "0",
        "-1000",
        "5s",
        "1.5",
        " 100",
        "99999999999",
    ], given =>
    {
        FormatException refused = Assert.Throws<FormatException>(() => EnvironmentValues.Milliseconds(Variable, given, Otherwise));
        Assert.Equal($"{Variable} takes a whole number of milliseconds above 0, not '{given}'", refused.Message);
    });

    private const string Place = "SOME_HOST";
    private const string What = "the host is asked at instead";

    private void A_stand_in_at_https_or_on_this_machine_is_taken_and_none_given_is_none() => Cases.Each<string>(
    [
        "https://mirror.example.com/feed/",
        "http://127.0.0.1:9/",
        "http://localhost:5000/repository2-3.xml",
        "http://[::1]:5000",
    ], given =>
    {
        Assert.Equal(new Uri(given), EnvironmentValues.Address(Place, given, What));
        Assert.Null(EnvironmentValues.Address(Place, null, What));
        Assert.Null(EnvironmentValues.Address(Place, "", What));
    });

    private void A_stand_in_that_is_no_url_or_plain_http_to_another_machine_is_fatal_saying_what_the_variable_takes() => Cases.Each<string>(
    [
        "http://192.0.2.10/",
        "http://localhost.example.com/",
        "ftp://127.0.0.1/",
        "repository2-3.xml",
        "/feed/",
        "127.0.0.1:9",
    ], given =>
    {
        FormatException refused = Assert.Throws<FormatException>(() => EnvironmentValues.Address(Place, given, What));
        Assert.Equal($"{Place} takes the https address (http on this machine) {What}, not '{given}'", refused.Message);
    });
}

using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A live reading's start that failed (the Stats tab's metrics, the Requests window's requests) is asked again only
/// while its program's runtime has yet to open its port, and only within the time a runtime is given to open it: a runtime
/// that took no session in its time and a program gone are said at once, never asked again for minutes.</summary>
public sealed class SessionStartsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_failed_live_reading_is_asked_again_only_while_its_port_may_yet_open() => Cases.All(
        ("a port not yet open is asked again within its time and not past it", () =>
        {
            ProfilingException notOpen = new("Its metrics could not be read: Unable to connect to Process 7.") { NotYetOpen = true };
            Assert.True(SessionStarts.AskAgain(notOpen, TimeSpan.Zero, Limit));
            Assert.True(SessionStarts.AskAgain(notOpen, TimeSpan.FromSeconds(9.9), Limit));
            Assert.False(SessionStarts.AskAgain(notOpen, Limit, Limit));
        }),
        ("a start that cannot succeed is never asked again", () => Cases.Each(
        [
            "Its metrics could not be read: its runtime took no session in 10 s (process 7 runs, and its diagnostic port is there but answered no other request either in 2 s)",
            "Its metrics could not be read: Process 7 is not running.",
        ], said => Assert.False(SessionStarts.AskAgain(new ProfilingException(said), TimeSpan.Zero, Limit)))));

}

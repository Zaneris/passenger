using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>What a VSTest run broken off by its test host's crash says, from what VSTest gave: whichever it heard first as
/// the host ended, its exit (the run's end then carrying the cancellation of VSTest's read of their connection, as a loaded
/// Mac's did) or the connection's end (no error), the crash VSTest logged and the test running then; an error of VSTest's
/// own said in its place, each of an aggregate's errors by its words.</summary>
public class BrokenRunsTests
{
    private const string Crashed = "The active test run was aborted. Reason: Test host process crashed : Process path: /Users/runner/.dotnet/dotnet";

    private const string Running = "Sums.Tests.ExitingTests.Exits";

    /// <summary>The error VSTest gave a run's end as its test host's exit came before the end of their connection, as it
    /// was on a loaded Mac.</summary>
    private static AggregateException HostEndedFirst() => new([new OperationCanceledException()]);

    [Fact]
    public void A_broken_run_is_said_naming_the_tests_running_and_why() => Cases.All(
        ("a crash heard by the host's exit first is said as one heard by the connection's end naming the test running", () =>
        {
            Assert.Equal("One or more errors occurred. (The operation was canceled.)", HostEndedFirst().Message);
            string said = $"{Crashed}, while {Running} ran";
            Assert.Equal(said, BrokenRuns.Said(null, Crashed, [Running]));
            Assert.Equal(said, BrokenRuns.Said(HostEndedFirst(), Crashed, [Running]));
            Assert.Equal(said, BrokenRuns.Said(new OperationCanceledException(), Crashed, [Running]));
        }),
        ("an error of VSTest's own is said in place of what it logged each of an aggregate's by its words with the test running", () =>
        {
            AggregateException failed = new([new InvalidOperationException("Unable to read the test host's message"), new OperationCanceledException()]);
            Assert.Equal($"Unable to read the test host's message; The operation was canceled., while {Running} ran", BrokenRuns.Said(failed, Crashed, [Running]));
            Assert.Equal("Unable to read the test host's message", BrokenRuns.Said(new InvalidOperationException("Unable to read the test host's message"), Crashed, []));
        }),
        ("a run broken off with nothing said names only the tests running and with none running only why", () =>
        {
            Assert.Equal("while A.One, A.Two ran", BrokenRuns.Said(HostEndedFirst(), null, ["A.One", "A.Two"]));
            Assert.Equal(Crashed, BrokenRuns.Said(HostEndedFirst(), Crashed, []));
            Assert.Null(BrokenRuns.Said(null, null, []));
            Assert.Null(BrokenRuns.Told(HostEndedFirst()));
        }));

}

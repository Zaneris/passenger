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
    public void A_crash_heard_by_the_hosts_exit_first_is_said_as_one_heard_by_the_connections_end_naming_the_test_running()
    {
        Assert.Equal("One or more errors occurred. (The operation was canceled.)", HostEndedFirst().Message);
        string said = $"{Crashed}, while {Running} ran";
        Assert.Equal(said, BrokenRuns.Said(null, Crashed, [Running]));
        Assert.Equal(said, BrokenRuns.Said(HostEndedFirst(), Crashed, [Running]));
        Assert.Equal(said, BrokenRuns.Said(new OperationCanceledException(), Crashed, [Running]));
    }

    [Fact]
    public void An_error_of_vstests_own_is_said_in_place_of_what_it_logged_each_of_an_aggregates_by_its_words_with_the_test_running()
    {
        AggregateException failed = new([new InvalidOperationException("Unable to read the test host's message"), new OperationCanceledException()]);
        Assert.Equal($"Unable to read the test host's message; The operation was canceled., while {Running} ran", BrokenRuns.Said(failed, Crashed, [Running]));
        Assert.Equal("Unable to read the test host's message", BrokenRuns.Said(new InvalidOperationException("Unable to read the test host's message"), Crashed, []));
    }

    [Fact]
    public void A_run_broken_off_with_nothing_said_names_only_the_tests_running_and_with_none_running_only_why()
    {
        Assert.Equal("while A.One, A.Two ran", BrokenRuns.Said(HostEndedFirst(), null, ["A.One", "A.Two"]));
        Assert.Equal(Crashed, BrokenRuns.Said(HostEndedFirst(), Crashed, []));
        Assert.Null(BrokenRuns.Said(null, null, []));
        Assert.Null(BrokenRuns.Told(HostEndedFirst()));
    }
}

using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Telemetry;

namespace Passenger.Tests;

/// <summary>The Stats tabs' charts of a program whose runtime answers no session's start: one the debugger holds paused said
/// to be, with no reading begun, so nothing waits on it meanwhile, read once it runs, and said to be paused over its charts
/// while the debugger holds it again; one that takes no session said to once its start's time runs out, and left, and asked
/// nothing more once the pane lets it go as its start waits. The program read is this test's own process.</summary>
public sealed class MetricsPaneTests
{
    [AvaloniaFact]
    public void A_program_paused_in_the_debugger_is_said_to_be_and_read_once_it_runs()
    {
        bool paused = true;
        MetricsPane pane = new() { Paused = (_, _) => paused };
        try
        {
            pane.Show("api", Environment.ProcessId);
            Ui.Settle();
            Assert.Equal(Refusals.MetricsWhilePaused("api"), pane.Said);
            Assert.Empty(pane.Charted);

            paused = false;
            pane.DebuggerChanged();
            Assert.Equal("Waiting for api's first metrics…", pane.Said);
            Ui.WaitFor(() => pane.Charted.Count > 0, () => $"the program's metrics charted once it runs (the pane says {pane.Said})", TimeSpan.FromSeconds(30));

            paused = true;
            pane.DebuggerChanged();
            Assert.Equal(Refusals.MetricsWhilePaused("api"), pane.Said);
            paused = false;
            pane.DebuggerChanged();
            Assert.Equal(string.Empty, pane.Said);
        }
        finally
        {
            pane.Stop();
        }
    }

    /// <summary>A program whose runtime takes no session (a silent port in front of it) is said to, with what its port showed,
    /// once its start's time has run out, and asked no more: never left saying it waits while it asks again for minutes.
    /// What asks is counted in this test's process: another process of the machine may reach the port too.</summary>
    [AvaloniaFact]
    public void A_runtime_that_takes_no_session_is_said_to_and_asked_no_more()
    {
        MetricsPane pane = new() { Paused = (_, _) => false };
        using SilentPort port = new(Environment.ProcessId);
        try
        {
            pane.Show("api", Environment.ProcessId);
            string began = $"began at {port.Time} saying \"{pane.Said}\"";
            Ui.WaitFor(() => pane.Said.StartsWith("api: Its metrics could not be read: its runtime took no session in ", StringComparison.Ordinal),
                () => $"the pane to say api's runtime took no session (it says {pane.Said}; the port took {port.Account()})", TimeSpan.FromMinutes(1));
            string said = $"said so by {port.Time}";
            Ui.Settle();
            Assert.Contains("answered no other request either", pane.Said, StringComparison.Ordinal);
            if (port.TakenFromElsewhere)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"Other processes reached the silent port too: {port.Account()}");
            }
            Assert.True(port.TakenHere == 2, $"The pane's start and its account of the port were to be the two connections this process made to it, where it made "
                + $"{port.TakenHere}. The pane {began}, {said} and was quiet by {port.Time}; the port took {port.Account()}");
            Assert.Empty(pane.Charted);
        }
        finally
        {
            pane.Stop();
        }
    }

    /// <summary>A pane let go of while its start waits at a runtime that takes no session gives the start up at once: it
    /// asks no account of the port that it would only throw away, and says nothing.</summary>
    [AvaloniaFact]
    public void A_pane_let_go_of_while_its_start_waits_asks_its_runtime_nothing_more()
    {
        MetricsPane pane = new() { Paused = (_, _) => false };
        using SilentPort port = new(Environment.ProcessId);
        try
        {
            pane.Show("api", Environment.ProcessId);
            Ui.PumpUntil(() => port.AskedHere.Count > 0, "the pane's start to reach the silent port", TimeSpan.FromSeconds(30));
            Assert.Contains("System.Diagnostics.Metrics", port.AskedHere[0], StringComparison.Ordinal);
            pane.Stop();
            string stopped = $"stopped at {port.Time}";
            Ui.Settle();
            Assert.Equal(string.Empty, pane.Said);
            Assert.True(port.TakenHere == 1, $"The pane's start was to be all this process asked of the port, where it made {port.TakenHere} connections. "
                + $"The pane {stopped} and was quiet by {port.Time}; the port took {port.Account()}");
        }
        finally
        {
            pane.Stop();
        }
    }
}

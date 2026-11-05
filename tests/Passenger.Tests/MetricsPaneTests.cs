using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Telemetry;

namespace Passenger.Tests;

/// <summary>The Stats tabs' charts of a program the debugger holds paused, whose runtime answers no session's start: said to
/// be paused, with no reading begun, so nothing waits on it meanwhile; read once it runs, and said to be paused over its
/// charts while the debugger holds it again. The program read is this test's own process.</summary>
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
}

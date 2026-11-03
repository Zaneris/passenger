using Passenger.App.Aspire;

namespace Passenger.App.ToolWindows;

/// <summary>The views of the tool windows that start at the bottom (Build, Run, Debug, Unit Tests, Problems). Each is
/// a tool window of its own, which the person can move anywhere the dock has a place.</summary>
public sealed class ToolViews
{
    public ToolViews()
    {
        Tests = new TestsView(TestTree);
        RunWindow = new RunPane(Run);
    }

    public OutputView Build { get; } = new();
    public OutputView Run { get; } = new() { RerunsWhileRunning = true };

    /// <summary>The Run window: its console, with its run's Telemetry beside it while the run collects logs and traces.</summary>
    public RunPane RunWindow { get; }
    /// <summary>What the Problems window lists, kept while its view is not built.</summary>
    public ProblemsModel Problems { get; } = new();
    /// <summary>What the Debug window shows, kept while its view is not built.</summary>
    public DebugModel Debug { get; } = new();

    /// <summary>What the Aspire window shows of the session's AppHosts, kept while its view is not built: made anew with each
    /// session, whose AppHosts write to it.</summary>
    public AspireModel Aspire { get; private set; } = new();

    internal void ResetAspire(AspireClient client)
    {
        Aspire.Dispose();
        Aspire = new AspireModel(client);
    }

    /// <summary>The Unit Tests window, over the tree the test runner fills.</summary>
    public TestsView Tests { get; }

    public Testing.TestTree TestTree { get; } = new();
}

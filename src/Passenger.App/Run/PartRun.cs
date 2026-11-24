using System.Diagnostics;
using Passenger.App.ToolWindows;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Where a part of a run of several parts stands, as its tab in the Run window says it.</summary>
public enum PartState
{
    /// <summary>Waiting for the part before it to be ready.</summary>
    Waiting,
    Starting,

    /// <summary>Running, and ready as far as the parts after it are concerned.</summary>
    Running,
    Ended,
}

/// <summary>A part of a run of several parts in a tab of the Run window: its own console, the program running in it (a
/// ProcessRunner of its own, which Stop and Restart end and start again), what it runs and where it stands. Its tab's Rerun
/// restarts it alone and its Stop ends it alone; the run's Stop ends every part. A UI-thread object.</summary>
public sealed class PartRun : INamedRun, IDisposable
{
    public PartRun(string name, string run, int place, RunPart part)
    {
        Name = name;
        Run = run;
        Place = place;
        Part = part;
        Console = new OutputView { RerunsWhileRunning = true };
        Runner = new ProcessRunner(Console, takesInput: true);
        Runner.Exited += (_, code) =>
        {
            exitCode = code;
            Set(PartState.Ended);
        };
    }

    private int? exitCode;

    /// <summary>What its tab and the status bar call it.</summary>
    public string Name { get; }

    /// <summary>The run of several parts it is a part of, by its configuration's name.</summary>
    public string Run { get; }

    /// <summary>Its place in the run, from one.</summary>
    public int Place { get; }

    public RunPart Part { get; }

    /// <summary>For a .NET project, the configuration it starts as, which a restart builds and starts again.</summary>
    public RunConfiguration? Project { get; init; }

    public OutputView Console { get; }

    public ProcessRunner Runner { get; }

    /// <summary>The agent whose call started it, by its connection; null for a person's run.</summary>
    public Mcp.AgentConnection? StartedBy { get; set; }

    public PartState State { get; private set; } = PartState.Waiting;

    /// <summary>Where it stands changed, for its tab's words.</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning => Runner.IsRunning;

    public int? ProcessId => Runner.ProcessId;

    /// <summary>The end of its program last started, with its exit code; -1 for one that never started.</summary>
    public Task<int> Ended => Runner.Ended;

    public bool WasStopped => Runner.WasStopped;

    public void Stop() => Runner.Stop();

    /// <summary>Starts its program in its console, which says what is started below what the last start wrote; null once it
    /// runs, else why not.</summary>
    public async Task<string?> StartAsync(ProcessStartInfo start, IReadOnlyDictionary<string, string>? environment)
    {
        exitCode = null;
        Set(PartState.Starting);
        Console.SetRunning(true);
        string? refused = await Runner.StartAsync(start, environment);
        if (refused is not null)
        {
            Set(PartState.Ended);
        }
        return refused;
    }

    /// <summary>It stands as said now.</summary>
    public void Set(PartState state)
    {
        if (state == State)
        {
            return;
        }
        State = state;
        if (state == PartState.Ended)
        {
            Console.SetRunning(false);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Its tab's words beside its name: where it stands, its exit code once it has ended.</summary>
    public string Said => State switch
    {
        PartState.Waiting => "waiting",
        PartState.Starting => "starting",
        PartState.Running => "running",
        _ => exitCode is { } code ? $"ended {code}" : "ended",
    };

    public void Dispose() => Runner.Dispose();
}

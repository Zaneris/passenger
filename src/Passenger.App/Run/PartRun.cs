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

/// <summary>A part of a run of several parts, or a copy of a game its configuration runs several of (<see cref="Copy"/>), in
/// a tab of the Run window: its own console, the program running in it (a ProcessRunner of its own, which Stop and Restart
/// end and start again), what it runs and where it stands; a Compose part shows the Run window's own console, where its
/// run's log is followed (<see cref="OfContainers"/>), as a game Run started there does once a copy runs beside it
/// (<see cref="OfConsole"/>). Its tab's Rerun restarts it alone and its Stop ends it alone; the run's Stop ends every part.
/// A UI-thread object.</summary>
public sealed class PartRun : INamedRun, IDisposable
{
    public PartRun(string name, string run, int place, RunPart part)
        : this(name, run, place, part, new OutputView { RerunsWhileRunning = true }, null)
    {
    }

    private PartRun(string name, string run, int place, RunPart part, OutputView console, ProcessRunner? runner)
    {
        Name = name;
        Run = run;
        Place = place;
        Part = part;
        Console = console;
        owned = runner is null;
        Runner = runner ?? new ProcessRunner(Console, takesInput: true);
        Runner.Exited += OnExited;
    }

    /// <summary>A Compose part, whose containers' log the Run window's console follows through its runner while
    /// <paramref name="ours"/> says the folder's container run is this part's; <paramref name="stop"/> takes them down.</summary>
    public static PartRun OfContainers(string name, string run, int place, RunPart part, OutputView console, ProcessRunner runner, Func<bool> ours, Action stop) =>
        new(name, run, place, part, console, runner) { ours = ours, stopContainers = stop };

    /// <summary>The game the Run window's console runs, through its runner, while <paramref name="ours"/> says the console's
    /// program is its: shown as the first of its configuration's copies (<paramref name="copy"/>) once another runs beside it.</summary>
    public static PartRun OfConsole(string name, RunPart part, OutputView console, ProcessRunner runner, Func<bool> ours, RunConfiguration project, GameCopy copy) =>
        new(name, copy.Configuration, copy.Place, part, console, runner) { ours = ours, Project = project, Copy = copy };

    private readonly bool owned;
    private Func<bool> ours = () => true;
    private Action? stopContainers;
    private int? exitCode;

    private void OnExited(object? sender, int code)
    {
        exitCode = code;
        Set(PartState.Ended);
    }

    /// <summary>What its tab and the status bar call it.</summary>
    public string Name { get; }

    /// <summary>The run of several parts it is a part of, by its configuration's name.</summary>
    public string Run { get; }

    /// <summary>Its place in the run, from one.</summary>
    public int Place { get; }

    public RunPart Part { get; }

    /// <summary>For a .NET project or a game's copy, the configuration it starts as, which a restart builds and starts again.</summary>
    public RunConfiguration? Project { get; init; }

    /// <summary>For a copy of a game its configuration runs several of, which copy it is; null for a part of a run.</summary>
    public GameCopy? Copy { get; init; }

    public OutputView Console { get; }

    public ProcessRunner Runner { get; }

    /// <summary>The agent whose call started it, by its connection; null for a person's run.</summary>
    public Mcp.AgentConnection? StartedBy { get; set; }

    public PartState State { get; private set; } = PartState.Waiting;

    /// <summary>Where it stands changed, for its tab's words.</summary>
    public event EventHandler? StateChanged;

    public bool IsRunning => Runner.IsRunning && ours();

    public int? ProcessId => Runner.ProcessId;

    /// <summary>The end of its program last started, with its exit code; -1 for one that never started.</summary>
    public Task<int> Ended => Runner.Ended;

    public bool WasStopped => Runner.WasStopped;

    public void Stop()
    {
        if (stopContainers is { } down)
        {
            down();
            return;
        }
        Runner.Stop();
    }

    /// <summary>Whether it shows the Run window's own console, a Compose part's.</summary>
    public bool IsContainers => stopContainers is not null;

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

    public void Dispose()
    {
        Runner.Exited -= OnExited;
        if (owned)
        {
            Runner.Dispose();
        }
    }
}

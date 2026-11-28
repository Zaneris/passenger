using Passenger.App.LiveTree;
using Passenger.App.ToolWindows;

namespace Passenger.App.Run;

/// <summary>The program Run started, wherever it runs: the Run console's (<see cref="ProcessRunner"/>) or a tab of the
/// Terminal window's (<see cref="TerminalRun"/>), read as <see cref="ProjectSession.RunProgram"/>.</summary>
public interface IRunningProgram
{
    bool IsRunning { get; }

    /// <summary>Its process once it has started, else null.</summary>
    int? ProcessId { get; }

    /// <summary>Its exit code once its end has landed where it ran.</summary>
    Task<int> Ended { get; }

    /// <summary>Ends it and all it started.</summary>
    void Stop();
}

/// <summary>A program run in a tab of its own, of the Terminal window (<see cref="TerminalRun"/>) or the Run window
/// (<see cref="PartRun"/>), by the name its tab shows.</summary>
public interface INamedRun : IRunningProgram
{
    string Name { get; }

    /// <summary>Whether it ended because the IDE ended it.</summary>
    bool WasStopped { get; }
}

/// <summary>How a program of the folder came to run.</summary>
public enum RunningKind
{
    Run,
    Profile,
    Debug,

    /// <summary>A .NET service of a run of services (a container run's, an AppHost's), whose program its run finds.</summary>
    Service,
}

/// <summary>One program the folder runs now (<see cref="ProjectSession.Running"/>): how it came to run, its name as the
/// window says it, its project, and what is known of it here: its process on this machine, the plan it was launched by,
/// its inspected app, its Debug tab, or its run of services and service there.</summary>
public sealed record RunningProgram(RunningKind Kind, string Name, string? Project)
{
    /// <summary>Its process on this machine, once known; null for one elsewhere, one a debugger has yet to start, and a
    /// service, whose program its run finds (<see cref="ServiceRun.ProgramAsync"/>).</summary>
    public int? Process { get; init; }

    /// <summary>The debugger's word of its process, for a debugged program on this machine whose start is under way.</summary>
    public Task<int>? Starting { get; init; }

    /// <summary>What Run or Debug launched it as, where it serves: null for one attached to, and a service.</summary>
    public LaunchPlan? Plan { get; init; }

    public LiveApp? App { get; init; }

    public DebugTab? Tab { get; init; }

    public ServiceRun? Services { get; init; }

    public IRunService? Service { get; init; }
}

/// <summary>An app with Passenger's inspector in it (<see cref="ProjectSession.Inspecting"/>): how it came to run, and its
/// name as an agent names it (run, profile, its Debug tab's, or its service's).</summary>
public sealed record InspectedApp(LiveApp App, RunningKind Kind, string Name)
{
    public DebugTab? Tab { get; init; }
}

namespace Passenger.Profiler;

/// <summary>The words Passenger's container tools take on their command line, as they read them and the IDE writes them:
/// a mode first, then its flags.</summary>
[IdentifierHome]
public static class ContainerToolsWords
{
    /// <summary>Passenger's debug adapter, its own flags after.</summary>
    public const string Dap = "dap";

    /// <summary>The container's .NET programs, as JSON.</summary>
    public const string Processes = "processes";

    /// <summary>A program's EventPipe session (<see cref="InContainer.TraceAsync"/>), with <see cref="Pid"/>.</summary>
    public const string Trace = "trace";

    /// <summary>A program's memory snapshots (<see cref="InContainer.HeapAsync"/>), with <see cref="Pid"/>.</summary>
    public const string Heap = "heap";

    /// <summary>Methods' source files and lines (<see cref="InContainer.Sources"/>).</summary>
    public const string Sources = "sources";

    /// <summary>The tools' version.</summary>
    public const string Version = "version";

    /// <summary>The program's process id, next.</summary>
    public const string Pid = "--pid";

    /// <summary><see cref="Trace"/>'s: a program held at its start let go once recording.</summary>
    public const string Resume = "--resume";

    /// <summary><see cref="Trace"/>'s: a web app's requests recorded (<see cref="LiveRequests"/>).</summary>
    public const string Requests = "--requests";
}

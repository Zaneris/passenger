using System.Diagnostics;
using Passenger.App.Containers;

namespace Passenger.App.Debugging;

/// <summary>Where a session's debug adapter runs when not beside the app: the command that starts it there, given the
/// adapter's flags (<c>docker exec -i CONTAINER …/passenger-tools dap</c>, the protocol over its standard input and
/// output), the folder it keeps decompiled symbols in there, how paths cross at the protocol's edge (a container run's
/// <see cref="ContainerPaths"/>; a path the map does not cover crosses as it is, but one coming back that names a file of
/// the folder's <see cref="Sources"/>), and what to call the place (the container's name, which docker exec reaches it
/// by). A session with one is <see cref="DebugSession.Elsewhere"/>: its program's process id is the container's, never
/// held or killed here. Ending the command (Stop, Detach, the IDE's end, the CLI killed) closes the adapter's standard
/// input there, which lets the program go.</summary>
public sealed record AdapterPlace(Func<IReadOnlyList<string>, ProcessStartInfo> Command, string SymbolCache, ContainerPaths Paths, string Where)
{
    /// <summary>The program debugged there, by its name as the container's processes name it (its .dll's, else its
    /// executable's); null where the place does not know it.</summary>
    public string? Program { get; init; }

    /// <summary>What follows the program's output from outside, shown in its session's console as a launch's is (a container
    /// run's service: <c>docker logs --follow</c> of its container from its start), where an attach gets none through the
    /// protocol; null for a program attached to as it already ran, whose output goes where it always went.</summary>
    public Func<ProcessStartInfo>? Output { get; init; }

    /// <summary>Whether the place still runs (its container), asked when its adapter ends with a failure no one asked for: a
    /// container that stopped took docker exec and the adapter in it with the program, which is the program's end and not
    /// the debugger's (<see cref="Runs"/>). Null where nothing can tell, such an end then taken for the debugger's.</summary>
    public Func<Task<bool>>? Running { get; init; }

    /// <summary>Whether a container runs now, as the engine says: what a container's place gives as its <see cref="Running"/>.</summary>
    public static Func<Task<bool>> Runs(ContainerEngine engine, string container) => async () =>
        await engine.RunAsync(["inspect", "-f", "{{.State.Running}}", container]).ConfigureAwait(false) is { Succeeded: true } said
        && said.Output.Trim() == "true";

    /// <summary>A path of this machine as the program's names it.</summary>
    public string ToProgram(string path) => Paths.ToContainer(path) ?? path;

    /// <summary>The open folder's files a path of the program's no map covers may name (a program attached to, built
    /// elsewhere), or null where the map says all there is.</summary>
    public WorkspaceSources? Sources { get; init; }

    /// <summary>A path the program names as this machine's.</summary>
    public string ToIde(string path) => Paths.ToHost(path) ?? Sources?.FileOf(path) ?? path;
}

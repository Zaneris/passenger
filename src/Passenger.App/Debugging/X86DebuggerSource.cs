using Passenger.App.Updates;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>Where the debugger's 32-bit host comes from for this window: passenger-helper built for win-x86 with its own x86
/// runtime and dbgshim, which runs the debug adapter for a program that runs as a 32-bit one on Windows, as a runtime's
/// debugging library loads only into a process of its program's processor. A build of one's own carries it beside the app
/// (<see cref="Carried"/>, the app project's IncludeX86AdapterHost); a release carries none, and downloads its own
/// version's release file into the state's cache the first time a 32-bit program is debugged (<see cref="ReleaseAssets"/>).</summary>
public sealed record X86DebuggerSource(string Carried, ReleaseFeed Releases, string Cache)
{
    /// <summary>The release's file of the host, by the fixed name the release workflow gives it.</summary>
    public const string Asset = "Passenger-debugger-win-x86.zip";

    /// <summary>What the person is told it is.</summary>
    public const string Named = "Passenger's 32-bit debugger";

    /// <summary>The host's program, which a folder holding the host has.</summary>
    private const string Program = ChildProcesses.HelperHost + ".exe";

    /// <summary>debugger/x86 beside the app, and the state's cache/debugger/VERSION/win-x86, downloaded from
    /// <paramref name="releases"/>.</summary>
    public static X86DebuggerSource Default(ReleaseFeed releases) =>
        new(Path.Combine(AppContext.BaseDirectory, "debugger", "x86"), releases, Path.Combine(State.StateStore.DefaultRoot, "cache", "debugger"));

    /// <summary>The folder this version's host is downloaded into.</summary>
    public string Downloaded => Path.Combine(Cache, Product.Version, "win-x86");

    /// <summary>The host's folder as it stands: the one the build carries, else this version's downloaded; null with
    /// neither.</summary>
    public string? Here => File.Exists(Path.Combine(Carried, Program)) ? Carried : File.Exists(Path.Combine(Downloaded, Program)) ? Downloaded : null;

    /// <summary>The host's folder: <see cref="Here"/>, else downloaded now, <paramref name="say"/> telling its progress. Throws
    /// <see cref="ReleaseAssetException"/> saying why there is none.</summary>
    public async Task<string> FolderAsync(Action<string>? say, CancellationToken cancel) =>
        Here ?? await ReleaseAssets.FolderAsync(Releases, Asset, Downloaded, Program, Named, say, cancel).ConfigureAwait(false);
}

using System.Globalization;

namespace Passenger.Core;

/// <summary>What a .NET program's runtime makes in its temporary folder on macOS and Linux and takes away only as it ends of
/// itself: its diagnostic port's socket and its debugger's two pipes, each named for its id and its start
/// (<c>dotnet-diagnostic-ID-START-socket</c>, <c>clr-debug-pipe-ID-START-in</c> and <c>-out</c>). One killed, or ended by its
/// debugger, leaves them, and the folder, which every diagnostics client lists, fills. Taken while the program runs, by its
/// id, and removed once it has gone by those names alone, never by a pattern, which would take another program's.</summary>
public sealed record RuntimeFiles(int Id, long Started, IReadOnlyList<string> Paths)
{
    /// <summary>The files of process <paramref name="id"/>'s runtime, in the temporary folder its environment names, else
    /// <paramref name="temporary"/>, else this process's: null on Windows, whose pipes go with their process, and for one
    /// gone or unreadable.</summary>
    public static RuntimeFiles? Of(int id, string? temporary = null)
    {
        if (DotnetProcesses.StartKey(id) is not long started)
        {
            return null;
        }
        string folder = DotnetProcesses.VariableOf(id, "TMPDIR") is { Length: > 0 } own ? own : temporary ?? Path.GetTempPath();
        string Named(string prefix, string suffix) => Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{prefix}-{id}-{started}-{suffix}"));
        return new RuntimeFiles(id, started, [Named("dotnet-diagnostic", "socket"), Named("clr-debug-pipe", "in"), Named("clr-debug-pipe", "out")]);
    }

    /// <summary>The diagnostic port's socket, where its runtime makes it (<see cref="DiagnosticPorts"/>).</summary>
    public string Socket => Paths[0];

    /// <summary>Removes those still there once their process has gone: whether it has (its id and start no longer one
    /// running process's), so they are taken now; false while it runs, nothing taken.</summary>
    public bool Remove()
    {
        if (DotnetProcesses.StartKey(Id) == Started && DotnetProcesses.Runs(Id))
        {
            return false;
        }
        foreach (string path in Paths)
        {
            try
            {
                if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
                {
                    File.Delete(path);
                }
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                // Another's, or gone meanwhile: nothing of this process's to take.
            }
        }
        return true;
    }
}

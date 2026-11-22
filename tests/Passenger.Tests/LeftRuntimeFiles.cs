using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What the run's .NET programs leave in its temporary folder (<see cref="TestRun.Temporary"/>): each runtime makes
/// its diagnostic socket and its debugger's pipes there, named for its process and its start, and takes them away only as
/// it ends of itself, so Passenger takes away those of a program it ends (<see cref="RuntimeFiles"/>), and the run leaves
/// none. Looked at after each test (<see cref="TestsLeaveNothingBehindAttribute"/>): files whose program has gone fail the
/// test they came in, or one beside it, each named and removed.</summary>
public static partial class LeftRuntimeFiles
{
    /// <summary>A runtime's files, a debugger's start pipes (dbgshim's on macOS) among them, by process and start.</summary>
    [GeneratedRegex(@"^(?:dotnet-diagnostic-(?<id>\d+)-(?<started>\d+)-socket|clr-debug-pipe-(?<id>\d+)-(?<started>\d+)-(?:in|out|co|st))$")]
    private static partial Regex Named();

    /// <summary>How long removals under way may take to land: a kill's once its programs have gone, a debug session's once its
    /// program's end is heard.</summary>
    private static readonly TimeSpan Landing = TimeSpan.FromSeconds(10);

    /// <summary>Why <paramref name="test"/> fails where programs that have gone left their runtime files in the run's folder
    /// (removed now); null where none did, and where the run's programs share the person's folder, which it never touches.</summary>
    public static string? LeftBy(string test)
    {
        if (OperatingSystem.IsWindows() || TestRun.Temporary == TestRun.PersonsTemporary || Gone() is [])
        {
            return null;
        }
        Waiting.Until(() => Gone() is [], Landing);
        List<string> left = Gone();
        if (left.Count == 0)
        {
            return null;
        }
        foreach (string path in left)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                // Gone meanwhile.
            }
        }
        return $"{test}, or a class run beside it, left programs' runtime files in the run's temporary folder after the programs had gone, removed "
            + $"now: a .NET program killed leaves its diagnostic socket and debugger pipes, which pile up in the person's folder and slow every "
            + $"diagnostics connection there, so Passenger takes them away for a program it ends (RuntimeFiles):\n{string.Join('\n', left.Select(Path.GetFileName))}";
    }

    /// <summary>The runtime files of a process and of all it started, read while they run, for a test's own kill of them
    /// to take once they have gone (<see cref="TakeOnceGone"/>); none on Windows.</summary>
    public static RuntimeFiles[] Of(int id) =>
        OperatingSystem.IsWindows() ? [] : [.. Processes.Descendants(id).Prepend(id).Select(each => RuntimeFiles.Of(each)).OfType<RuntimeFiles>()];

    /// <summary>Removes each of <paramref name="files"/> once its process has gone, waiting a few seconds at most: what a
    /// test kills itself leaves them, as what Passenger kills would, which the run holds against Passenger alone.</summary>
    public static void TakeOnceGone(RuntimeFiles[] files)
    {
        List<RuntimeFiles> waiting = [.. files];
        Waiting.Until(() =>
        {
            waiting.RemoveAll(each => each.Remove());
            return waiting.Count == 0;
        }, TimeSpan.FromSeconds(5), every: TimeSpan.FromMilliseconds(20));
    }

    /// <summary>The runtime files in the run's folder whose process has gone, or is another of the same id.</summary>
    private static List<string> Gone()
    {
        List<string> gone = [];
        Dictionary<int, long?> starts = [];
        foreach (string path in Directory.EnumerateFiles(TestRun.Temporary))
        {
            // A name whose numbers no process and start could have (a test's stand-in) is no runtime's.
            if (Named().Match(Path.GetFileName(path)) is not { Success: true } named
                || !int.TryParse(named.Groups["id"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out int id)
                || !long.TryParse(named.Groups["started"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out long started))
            {
                continue;
            }
            if (!starts.TryGetValue(id, out long? running))
            {
                running = RuntimeFiles.Of(id)?.Started;
                starts[id] = running;
            }
            if (running != started)
            {
                gone.Add(path);
            }
        }
        return gone;
    }
}

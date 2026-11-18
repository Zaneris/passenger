using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Workspace;

/// <summary>Files another program brought into the folder, held back from what reads them until the folders they came in
/// have stood still for <see cref="Still"/>: on Windows a folder with a file under it open does not move, so a read of a
/// file just come would refuse a move of its folder that follows at once (a checkout's, a person's next move). Each change
/// heard in such a folder, or of it, puts the reads off again, up to <see cref="Longest"/>; then the files are given on,
/// once, together.
/// A UI-thread object.</summary>
public sealed class StandingStill
{
    /// <summary>How long the folders files came in stand still before the files are read.</summary>
    public static readonly TimeSpan Still = TimeSpan.FromSeconds(1);

    /// <summary>The longest the reads are put off, from the first file waiting, so a folder another program keeps writing in
    /// (a log beside the files that came) holds them back no longer.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(10);

    /// <summary>When the first file now waiting came, a timestamp of <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>'s.</summary>
    private long since;

    private readonly Action<IReadOnlyList<string>> read;
    private readonly Debounce pause;
    private readonly HashSet<string> waiting = new(PathNames.Comparer);

    /// <summary>The folders the files waiting came in.</summary>
    private readonly HashSet<string> folders = new(PathNames.Comparer);

    /// <summary>Gives <paramref name="read"/> the files that came once their folders have stood still.</summary>
    public StandingStill(Action<IReadOnlyList<string>> read)
    {
        this.read = read;
        pause = new Debounce(Still, Land);
    }

    /// <summary>Files that came, read once their folders have stood still.</summary>
    public void Came(IEnumerable<string> files)
    {
        UiThread.Check();
        bool first = waiting.Count == 0;
        foreach (string file in files)
        {
            if (waiting.Add(file) && Path.GetDirectoryName(file) is { } folder)
            {
                folders.Add(folder);
            }
        }
        if (waiting.Count > 0)
        {
            since = first ? System.Diagnostics.Stopwatch.GetTimestamp() : since;
            PutOff();
        }
    }

    /// <summary>What the folder's watcher heard: a change in a folder files wait in, or of it, puts their reads off again.</summary>
    public void Heard(IEnumerable<string> paths)
    {
        UiThread.Check();
        if (waiting.Count > 0 && paths.Any(path => folders.Contains(path) || Path.GetDirectoryName(path) is { } folder && folders.Contains(folder)))
        {
            PutOff();
        }
    }

    /// <summary>The reads put off for <see cref="Still"/> from now, unless they have waited <see cref="Longest"/> already.</summary>
    private void PutOff()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(since) < Longest || !pause.IsWaiting)
        {
            pause.Restart();
        }
    }

    /// <summary>The reads put off until let go, for a test of what the folder does meanwhile.</summary>
    public IDisposable HoldOpen() => pause.HoldOpen();

    /// <summary>Gives on the files that waited, those gone meanwhile among them, which the reads pass over as they find them.</summary>
    private Task Land()
    {
        List<string> came = [.. waiting];
        waiting.Clear();
        folders.Clear();
        if (came.Count > 0)
        {
            read(came);
        }
        return Task.CompletedTask;
    }
}

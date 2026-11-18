using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>Whether macOS's watcher hands changes over in time: under a load of changes its daemon falls seconds to
/// minutes behind, saying nothing, and every watcher hears nothing meanwhile. While anything follows <see cref="Behind"/>,
/// a file of the process's own in the user's temporary folder is written at each <see cref="Beat"/> and its change
/// awaited: one unheard <see cref="Late"/> after it was written has the watchers look for themselves at each beat, and
/// once more as changes have been heard in time for <see cref="Settled"/>. Other systems' watchers say when they lose
/// changes, so nothing is written there.</summary>
public static class WatchLag
{
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(10);
    private const string CanaryName = "beat";

    /// <summary>What begins the name of each process's folder of the beat, its id after it.</summary>
    private const string Prefix = "passenger-watch-";

    private static readonly Lock Gate = new();
    private static Action<DateTime>? behind;
    private static FileSystemWatcher? canary;
    private static string? folder;
    private static CancellationTokenSource? beating;
    private static bool exitHooked;

    /// <summary>When the earliest write of the beat still unheard was made, a timestamp of <see cref="Stopwatch.GetTimestamp"/>'s,
    /// or 0; when one was last found late, or 0; and since when, on the wall clock, changes may have gone unheard.</summary>
    private static long unheard;
    private static long lastLate;
    private static DateTime? since;

    /// <summary>Raised off the UI thread at each beat while the system's watcher is behind, and once as it has caught up,
    /// with the moment on the wall clock from which changes may have gone unheard; following it keeps the beat going.</summary>
    public static event Action<DateTime>? Behind
    {
        add
        {
            lock (Gate)
            {
                behind += value;
                if (OperatingSystem.IsMacOS() && beating is null)
                {
                    Start();
                }
            }
        }
        remove
        {
            FileSystemWatcher? stopped = null;
            lock (Gate)
            {
                behind -= value;
                if (behind is null)
                {
                    stopped = Stop();
                }
            }
            // Off the gate, which the system's watcher takes as it hears the beat.
            stopped?.Dispose();
        }
    }

    /// <summary>The system's watcher of the beat's file, while anything follows <see cref="Behind"/> on macOS: a test stops
    /// it to have the system's watcher lag.</summary>
    public static FileSystemWatcher? Canary
    {
        get
        {
            lock (Gate)
            {
                return canary;
            }
        }
    }

    /// <summary>Whether the system's watcher is taken to be behind now.</summary>
    public static bool IsBehind
    {
        get
        {
            lock (Gate)
            {
                return since is not null;
            }
        }
    }

    private static void Start()
    {
        string own = Path.Combine(UserTemporary.Folder(), Prefix + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            ClearLeft();
            Directory.CreateDirectory(own);
            FileSystemWatcher watcher = new(own, CanaryName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            watcher.Changed += (_, _) => Heard();
            watcher.Created += (_, _) => Heard();
            watcher.Renamed += (_, _) => Heard();
            watcher.EnableRaisingEvents = true;
            (folder, canary) = (own, watcher);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            IdeErrors.Write($"The system's watcher is not checked for lagging behind ({error.Message}): a change it drops is found as a window comes in front");
            return;
        }
        if (!exitHooked)
        {
            exitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(own);
        }
        CancellationTokenSource ending = new();
        beating = ending;
        // Unheld: the beat goes on for as long as anything is watched, which no screenshot or test waits out.
        IdeErrors.Observe(BackgroundWork.Unheld(() => BeatAsync(ending.Token)));
    }

    /// <summary>The beat ended and its file deleted: the system's watcher of it, to dispose of off the gate.</summary>
    private static FileSystemWatcher? Stop()
    {
        beating?.Cancel();
        beating = null;
        FileSystemWatcher? stopped = canary;
        canary = null;
        if (folder is { } own)
        {
            Delete(own);
        }
        folder = null;
        (unheard, lastLate, since) = (0, 0, null);
        return stopped;
    }

    /// <summary>The beat's folder taken away, or left for the system's clean-up of its temporary folder where it cannot be.</summary>
    private static void Delete(string own)
    {
        try
        {
            Directory.Delete(own, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"The system's watcher's beat left {own} ({error.Message})");
        }
    }

    /// <summary>The beats' folders of processes no longer running, which ended before they could take theirs away.</summary>
    private static void ClearLeft()
    {
        foreach (string left in Directory.EnumerateDirectories(UserTemporary.Folder(), Prefix + "*"))
        {
            if (int.TryParse(Path.GetFileName(left.AsSpan())[Prefix.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id)
                && id != Environment.ProcessId && !DotnetProcesses.Runs(id))
            {
                Delete(left);
            }
        }
    }

    private static async Task BeatAsync(CancellationToken ending)
    {
        long count = 0;
        while (!ending.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Beat, ending);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (Check(ending, ++count) is { } from)
            {
                Action<DateTime>? following;
                lock (Gate)
                {
                    following = behind;
                }
                following?.Invoke(from);
            }
        }
    }

    /// <summary>One beat: whether the last write went unheard too long, whether the watcher has caught up, and the beat's
    /// file written again; the moment the watchers look from, or null while the system's watcher keeps up.</summary>
    private static DateTime? Check(CancellationToken ending, long count)
    {
        string? file;
        DateTime? look;
        lock (Gate)
        {
            if (ending.IsCancellationRequested || folder is null)
            {
                return null;
            }
            long now = Stopwatch.GetTimestamp();
            if (unheard != 0 && Stopwatch.GetElapsedTime(unheard, now) >= Late)
            {
                if (since is null)
                {
                    // Anything since the beat before the one unheard may have gone unheard too.
                    since = DateTime.UtcNow - Stopwatch.GetElapsedTime(unheard, now) - Beat - Late;
                    IdeErrors.Write($"The system's watcher has not handed over a change in {Durations.Said(Stopwatch.GetElapsedTime(unheard, now))}: the watchers look for themselves until it keeps up");
                }
                lastLate = now;
            }
            look = since;
            if (since is not null && Stopwatch.GetElapsedTime(lastLate, now) >= Settled)
            {
                IdeErrors.Write("The system's watcher keeps up again: the watchers look once more and follow it");
                since = null;
            }
            if (unheard == 0)
            {
                unheard = now;
            }
            file = Path.Combine(folder, CanaryName);
        }
        try
        {
            // Made again where the system's clean-up of its temporary folder took it.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            AtomicFile.Write(file, System.Text.Encoding.ASCII.GetBytes(count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"The system's watcher's beat could not be written ({error.Message}): the next one is awaited");
            lock (Gate)
            {
                unheard = 0;
            }
        }
        return look;
    }

    private static void Heard()
    {
        lock (Gate)
        {
            unheard = 0;
        }
    }
}

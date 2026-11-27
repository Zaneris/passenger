using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>Whether macOS's watcher hands changes over in time: under a load of changes its daemon falls seconds to
/// minutes behind, saying nothing, and every watcher hears nothing meanwhile. While anything follows <see cref="Behind"/>
/// and a window holds the beat (<see cref="Hold"/>: one in front, or one nobody sees), a file of the process's own in the
/// user's temporary folder is written at each <see cref="Beat"/> and its change awaited: one unheard <see cref="Late"/>
/// after it was written has the watchers look for themselves at each beat, and once more as changes have been heard in
/// time for <see cref="Settled"/>, a beat ended meanwhile and begun again still behind. Other systems' watchers say when
/// they lose changes, so nothing is written there.</summary>
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
    private static WatchedFile? canary;
    private static string? folder;
    private static CancellationTokenSource? beating;
    private static bool exitHooked;

    /// <summary>How many holds on the beat are let go of not yet (<see cref="Hold"/>).</summary>
    private static int holds;

    /// <summary>When the earliest write of the beat still unheard was made, a timestamp of <see cref="Stopwatch.GetTimestamp"/>'s,
    /// or 0; when one was last found late, or 0; and since when, on the wall clock, changes may have gone unheard.</summary>
    private static long unheard;
    private static long lastLate;
    private static DateTime? since;

    /// <summary>Raised off the UI thread at each beat while the system's watcher is behind, and once as it has caught up,
    /// with the moment on the wall clock from which changes may have gone unheard; following it keeps the beat going.</summary>
    public static event Action<DateTime>? Behind
    {
        add => Follow(() => behind += value);
        remove => Follow(() => behind -= value);
    }

    /// <summary>Holds the beat going while anything follows <see cref="Behind"/>, until let go of: a window's while it is in
    /// front, as a person works there, and while it is open where nobody sees it (an agent's, a test's), whose changes come
    /// from programs beside it. A window come in front looks again itself for what was dropped while none held it.</summary>
    public static IDisposable Hold()
    {
        Follow(() => holds++);
        return new Held();
    }

    /// <summary>A hold on the beat, let go of once.</summary>
    private sealed class Held : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Follow(() => holds--);
            }
        }
    }

    /// <summary>How many holds on the beat are let go of not yet.</summary>
    public static int Holds
    {
        get
        {
            lock (Gate)
            {
                return holds;
            }
        }
    }

    /// <summary>Whether the beat goes on now.</summary>
    public static bool IsBeating
    {
        get
        {
            lock (Gate)
            {
                return beating is not null;
            }
        }
    }

    /// <summary>How many times the beat's file was written in this process.</summary>
    public static long Beats => Interlocked.Read(ref beats);

    /// <summary>How many changes of the beat's file the system's watcher handed over in this process.</summary>
    public static long HeardBeats => Interlocked.Read(ref heardBeats);

    private static long beats;
    private static long heardBeats;

    /// <summary>Changes what follows or holds the beat under the gate, then begins or ends it as they now ask: it goes on
    /// on macOS while anything follows it and anything holds it.</summary>
    private static void Follow(Action change)
    {
        WatchedFile? stopped = null;
        lock (Gate)
        {
            change();
            bool wanted = OperatingSystem.IsMacOS() && behind is not null && holds > 0;
            if (wanted && beating is null)
            {
                Start();
            }
            else if (!wanted && beating is not null)
            {
                stopped = Stop();
            }
        }
        // Off the gate, which the system's watcher takes as it hears the beat.
        stopped?.Dispose();
    }

    /// <summary>The watch on the beat's file, while the beat goes on: a test deafens it to have the system's watcher lag.</summary>
    public static WatchedFile? Canary
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
            // Not following the lag itself: a look at the beat's file while behind would take the beat for heard.
            WatchedFile watch = new(Path.Combine(own, CanaryName), "The system's watcher's beat", _ =>
            {
                Heard();
                return true;
            }, followsLag: false);
            (folder, canary) = (own, watch);
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
        if (since is not null)
        {
            // Begun again while behind, whatever was heard before it ended: in time only once heard so again.
            lastLate = Stopwatch.GetTimestamp();
        }
        CancellationTokenSource ending = new();
        beating = ending;
        // Unheld: the beat goes on for as long as anything is watched, which no screenshot or test waits out.
        IdeErrors.Observe(BackgroundWork.Unheld(() => BeatAsync(ending.Token)));
    }

    /// <summary>The beat ended and its file deleted: the watch on it, to dispose of off the gate.</summary>
    private static WatchedFile? Stop()
    {
        beating?.Cancel();
        beating = null;
        WatchedFile? stopped = canary;
        canary = null;
        if (folder is { } own)
        {
            Delete(own);
        }
        folder = null;
        // Behind it may still be: a beat begun again goes on from when it fell behind.
        unheard = 0;
        return stopped;
    }

    /// <summary>The beat's folder taken away, or left for the system's clean-up of its temporary folder where it cannot be;
    /// one gone already (the beat ended before the process) is no failure to log.</summary>
    private static void Delete(string own)
    {
        try
        {
            Directory.Delete(own, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Taken away already.
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
                    // Anything a while before the beat unheard may have gone unheard too: the system hands changes over out of turn.
                    since = DateTime.UtcNow - Stopwatch.GetElapsedTime(unheard, now) - Beat - Late - Settled;
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
            MarkerFiles.Write(file, System.Text.Encoding.ASCII.GetBytes(count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Interlocked.Increment(ref beats);
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
        Interlocked.Increment(ref heardBeats);
        lock (Gate)
        {
            unheard = 0;
        }
    }
}

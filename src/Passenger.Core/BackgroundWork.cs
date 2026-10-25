using System.Runtime.CompilerServices;

namespace Passenger.Core;

/// <summary>Counts background work still to land on the UI thread, so a screenshot can wait for it rather than capture
/// a half-finished frame. Anything that runs off the UI thread and changes what is drawn enters here, best through
/// <see cref="Scope"/>, <see cref="Track"/> or <see cref="Run{T}(Func{T}, CancellationToken, string, int)"/>, which no
/// path out can forget. The process's, as its one UI thread is: with several windows a screenshot or a test waits for
/// all of their work, which can only make it wait longer, never take a frame half-finished. While
/// <see cref="InFlight.Watched"/>, each hold is named by the file and line that took it, for a wait that gives up to
/// say what never finished (<see cref="Held"/>).</summary>
public static class BackgroundWork
{
    /// <summary>The count of holds in the low half, and in the high half the generation they belong to, which
    /// <see cref="Forget"/> moves on: one value, so a hold and the generation it was counted in are taken together.</summary>
    private static long state;

    /// <summary>The holds named while watched: each scope by its own, and Begin's by file, since an End knows only its
    /// file (a pull begun in one method ends in the next); each with the line that took it and when.</summary>
    private static readonly HashSet<Hold> Scopes = [];
    private static readonly Dictionary<string, List<(int Line, long Since)>> Begun = new(StringComparer.Ordinal);

    private static volatile Action<Action>? toUiThread;

    public static int Pending => (int)Volatile.Read(ref state);

    public static void Begin([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Interlocked.Increment(ref state);
        if (InFlight.Watched)
        {
            lock (Scopes)
            {
                if (!Begun.TryGetValue(file, out List<(int Line, long Since)>? lines))
                {
                    Begun[file] = lines = [];
                }
                lines.Add((line, DateTime.UtcNow.Ticks));
            }
        }
    }

    /// <summary>Lets go of one hold, on the UI thread that began it: work that ends on another thread holds a
    /// <see cref="Scope"/>, which a later generation's count never loses to. Never below none.</summary>
    public static void End([CallerFilePath] string file = "")
    {
        Release(null);
        if (InFlight.Watched)
        {
            lock (Scopes)
            {
                if (Begun.TryGetValue(file, out List<(int Line, long Since)>? lines) && lines.Count > 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                Unnamed();
            }
        }
    }

    /// <summary>Each hold now, as named while watched: the file and line that took it and when (UTC), the oldest first;
    /// the count is <see cref="Pending"/>'s, the names as near as an End that names only its file lets them be.</summary>
    public static IReadOnlyList<(string What, DateTime Since)> Held()
    {
        lock (Scopes)
        {
            return [.. Scopes.Select(s => (s.Where, s.Since))
                .Concat(Begun.SelectMany(b => b.Value.Select(l => (Where: $"{Path.GetFileName(b.Key)}:{l.Line}", l.Since))))
                .OrderBy(h => h.Since)
                .Select(h => ($"background work begun at {h.Where}", new DateTime(h.Since, DateTimeKind.Utc)))];
        }
    }

    /// <summary>Drops every name once nothing is held, so an End that named another file than its Begin leaves none behind.</summary>
    private static void Unnamed()
    {
        if (Pending == 0)
        {
            Scopes.Clear();
            Begun.Clear();
        }
    }

    /// <summary>Lets go of every hold, for a UI thread that is gone (a headless test's, once it is over): what would have
    /// ended them (a callback, an await's continuation) was to run on its dispatcher, which never runs again.</summary>
    public static void Forget()
    {
        lock (Scopes)
        {
            Scopes.Clear();
            Begun.Clear();
        }
        long seen = Volatile.Read(ref state);
        while (true)
        {
            long was = Interlocked.CompareExchange(ref state, ((seen >> 32) + 1) << 32, seen);
            if (was == seen)
            {
                return;
            }
            seen = was;
        }
    }

    /// <summary>Work held until the scope is disposed, once however often it is, and not at all once forgotten.</summary>
    public static IDisposable Scope([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Hold held = new(Interlocked.Increment(ref state) >> 32, file, line);
        if (InFlight.Watched)
        {
            lock (Scopes)
            {
                Scopes.Add(held);
            }
        }
        return held;
    }

    /// <summary>Work held until a task ends, however it ends; released after what the UI thread was given to do on it.</summary>
    public static void Track(Task task, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Action<Action> landing = toUiThread
            ?? throw new InvalidOperationException("BackgroundWork.Track needs the UI thread's post, which the app gives as it starts (BackgroundWork.Install)");
        IDisposable held = Scope(file, line);
        UnawaitedWork.Observe(task.ContinueWith(_ => landing(held.Dispose), TaskScheduler.Default));
    }

    /// <summary>Gives <see cref="Track"/> the way onto the UI thread, which only the app knows: what it is given runs
    /// there later, after what the UI thread was given to do before it.</summary>
    public static void Install(Action<Action> postToUiThread) => toUiThread = postToUiThread;

    /// <summary>Work for the thread pool (Task.Run's), held until its answer is back on the caller's thread: an await
    /// on the UI thread lets go only there, so what it then draws is in the frame a screenshot waits for. Not for what
    /// the UI thread may block on (the window's end), which it could never come back to.</summary>
    public static async Task<T> Run<T>(Func<T> work, CancellationToken cancel = default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        using (Scope(file, line))
        {
            return await Task.Run(work, cancel);
        }
    }

    /// <inheritdoc cref="Run{T}(Func{T}, CancellationToken, string, int)"/>
    public static async Task<T> Run<T>(Func<Task<T>> work, CancellationToken cancel = default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        using (Scope(file, line))
        {
            return await Task.Run(work, cancel);
        }
    }

    /// <inheritdoc cref="Run{T}(Func{T}, CancellationToken, string, int)"/>
    public static async Task Run(Action work, CancellationToken cancel = default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        using (Scope(file, line))
        {
            await Task.Run(work, cancel);
        }
    }

    /// <inheritdoc cref="Run{T}(Func{T}, CancellationToken, string, int)"/>
    public static async Task Run(Func<Task> work, CancellationToken cancel = default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        using (Scope(file, line))
        {
            await Task.Run(work, cancel);
        }
    }

    /// <summary>Takes one hold off the count, if there is one and, for a scope's, the count is still of its generation.</summary>
    private static void Release(long? generation)
    {
        long seen = Volatile.Read(ref state);
        while ((int)seen > 0 && (generation is null || seen >> 32 == generation))
        {
            long was = Interlocked.CompareExchange(ref state, seen - 1, seen);
            if (was == seen)
            {
                return;
            }
            seen = was;
        }
    }

    private sealed class Hold(long generation, string file, int line) : IDisposable
    {
        private int ended;

        public long Since { get; } = InFlight.Watched ? DateTime.UtcNow.Ticks : 0;

        public string Where => $"{Path.GetFileName(file)}:{line}";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                Release(generation);
                if (InFlight.Watched)
                {
                    lock (Scopes)
                    {
                        Scopes.Remove(this);
                        Unnamed();
                    }
                }
            }
        }
    }
}

using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>The folders the IDE moves or deletes now, which no watcher of the process reads under meanwhile: Windows refuses
/// to move or delete a folder while a file under it is open, even for a look at whether it is there, as a watcher looks at
/// what a change it heard came to (the save just before a delete among them). A watcher's read touching one waits, in turn,
/// until the folder has gone or moved (<see cref="Begin"/>), and the move begins once the reads touching it under way have
/// ended (<see cref="Hold"/>). Elsewhere an open file keeps no folder from moving, and nothing is held.</summary>
internal static class MovingFolders
{
    private static readonly Lock Gate = new();

    /// <summary>The folders held, each hold's own.</summary>
    private static readonly List<Holding> Held = [];

    /// <summary>The watchers' reads under way, each by the path it reads at or under.</summary>
    private static readonly List<Read> Reading = [];

    /// <summary>What each read that waits does once a hold is let go of: asks again.</summary>
    private static readonly List<Action> Waiting = [];

    /// <summary>A read where nothing is held, never among those under way.</summary>
    private static readonly Read Unread = new(string.Empty);
    private static readonly Task<IDisposable> Unheld = Task.FromResult<IDisposable>(Unread);

    /// <summary>Whether a folder is held from its watchers as it moves: on Windows.</summary>
    public static bool Holds => OperatingSystem.IsWindows();

    /// <summary>Holds the folders from every watcher's reads until disposed (<c>Held</c>): <c>Quiet</c> is done once the reads
    /// touching them under way have ended. Nothing is held where <see cref="Holds"/> is false.</summary>
    public static (IDisposable Held, Task Quiet) Hold(IReadOnlyList<string> folders)
    {
        Holding hold = new(folders);
        if (!Holds)
        {
            return (hold, Task.CompletedTask);
        }
        lock (Gate)
        {
            Held.Add(hold);
            hold.Quiet();
        }
        return (hold, hold.Ended.Task);
    }

    /// <summary>A watcher's read at or under a path (an event's, or a whole folder's walk), until disposed; null where a
    /// folder held touches it, <paramref name="again"/> then run once a hold is let go of, to ask again.</summary>
    public static IDisposable? Begin(string path, Action again)
    {
        if (!Holds)
        {
            return Unread;
        }
        lock (Gate)
        {
            if (Held.Any(h => h.Touches(path)))
            {
                Waiting.Add(again);
                return null;
            }
            Read read = new(path);
            Reading.Add(read);
            return read;
        }
    }

    /// <summary>A watcher's read at or under a path as <see cref="Begin"/> takes it, once no folder held touches it.</summary>
    public static Task<IDisposable> BeginAsync(string path)
    {
        if (!Holds)
        {
            return Unheld;
        }
        TaskCompletionSource<IDisposable> begun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Ask();
        return begun.Task;

        void Ask()
        {
            if (Begin(path, Ask) is { } read)
            {
                begun.TrySetResult(read);
            }
        }
    }

    /// <summary>Whether a folder held touches a path now.</summary>
    public static bool Touched(string path)
    {
        lock (Gate)
        {
            return Held.Any(h => h.Touches(path));
        }
    }

    /// <summary>Folders held as they move, until disposed; <see cref="Ended"/> once no read touching them is under way.</summary>
    private sealed class Holding(IReadOnlyList<string> folders) : IDisposable
    {
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether a read at or under a path touches a folder held: it lies in one, or one lies in what it reads.</summary>
        public bool Touches(string path) => folders.Any(f => PathNames.Within(path, f) || PathNames.Within(f, path));

        /// <summary>Ends the wait once no read touching the folders is under way; under the gate.</summary>
        public void Quiet()
        {
            if (!Reading.Any(r => Touches(r.Path)))
            {
                Ended.TrySetResult();
            }
        }

        public void Dispose()
        {
            List<Action> asking;
            lock (Gate)
            {
                if (!Held.Remove(this))
                {
                    return;
                }
                asking = [.. Waiting];
                Waiting.Clear();
            }
            asking.ForEach(ask => ask());
        }
    }

    /// <summary>A watcher's read under way, until disposed.</summary>
    private sealed class Read(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            lock (Gate)
            {
                if (Reading.Remove(this))
                {
                    Held.ForEach(h => h.Quiet());
                }
            }
        }
    }
}

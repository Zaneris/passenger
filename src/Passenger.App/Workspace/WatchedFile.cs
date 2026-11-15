using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>One file watched alone (settings.json, vimrc), where a folder is watched through <see cref="WatchedTree"/>:
/// <see cref="Moved"/> is raised each time the file is found moved since it was last seen, by its <see cref="FileStamp"/>
/// through its links. It is looked at as its watcher hears it written, made, deleted or renamed onto or from its name; as
/// the watcher fails, since a change it lost is in the file; and as <see cref="LookAgain"/> asks, as a window comes in
/// front, for a change the system dropped without a word (macOS's watcher passes over its word that it did) or made to a
/// link's file elsewhere, which no watcher here hears. The first look always tells.</summary>
public sealed class WatchedFile : IDisposable
{
    private readonly Lock stamped = new();
    private bool disposed;

    /// <summary>Whether the file was seen once, under <see cref="stamped"/>.</summary>
    private bool looked;

    /// <summary>The file's stamp as last seen, under <see cref="stamped"/>; null for a file not there.</summary>
    private FileStamp? seen;

    /// <summary>Watches a file, its folder made where it is not there; one that cannot be watched is said in the log, as
    /// <paramref name="owner"/>'s, and looked at only as <see cref="LookAgain"/> asks.</summary>
    public WatchedFile(string path, string owner)
    {
        Path = path;
        try
        {
            string folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            Directory.CreateDirectory(folder);
            Watcher = new FileSystemWatcher(folder, System.IO.Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            Watcher.Changed += (_, _) => Look();
            Watcher.Created += (_, _) => Look();
            Watcher.Deleted += (_, _) => Look();
            // Heard for a rename from the name as well as onto it, by the filter: either moves the file.
            Watcher.Renamed += (_, _) => Look();
            Watcher.Error += (_, _) => Look();
            Watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            IdeErrors.Write($"{owner}: {path} is not watched for changes ({error.Message}), only looked at as a window comes in front");
            Watcher?.Dispose();
            Watcher = null;
        }
    }

    public string Path { get; }

    /// <summary>The system's watcher of the file; null where it could not be watched.</summary>
    public FileSystemWatcher? Watcher { get; }

    /// <summary>The file found moved since it was last seen, with its stamp now (null: not there); raised off the UI thread,
    /// on the thread that looked.</summary>
    public event Action<FileStamp?>? Moved;

    /// <summary>The file as its owner has just read or written it, so a look finding it so tells nothing; any thread.</summary>
    public void Saw(FileStamp? stamp)
    {
        lock (stamped)
        {
            (looked, seen) = (true, stamp);
        }
    }

    /// <summary>The file told again at the next look however it stands, where what it was told for could not be read.</summary>
    public void Unseen()
    {
        lock (stamped)
        {
            looked = false;
        }
    }

    /// <summary>Looks off the UI thread whether the file moved since it was last seen.</summary>
    public void LookAgain()
    {
        UiThread.Check();
        if (!disposed)
        {
            IdeErrors.Observe(BackgroundWork.Run(Look));
        }
    }

    public void Dispose()
    {
        disposed = true;
        Watcher?.Dispose();
    }

    private void Look()
    {
        if (disposed)
        {
            return;
        }
        FileStamp? now = FileStamp.Of(Path);
        lock (stamped)
        {
            if (looked && now == seen)
            {
                return;
            }
            (looked, seen) = (true, now);
        }
        Moved?.Invoke(now);
    }
}

using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>Vim mode's mappings file, vimrc in the state folder beside settings.json: read once Vim mode is on, and again
/// as it changes on disk, heard by its watcher or found moved as a window comes in front (<see cref="LookAgain"/>); each
/// reading handed to <see cref="Read"/> on the UI thread. A file that is not there maps nothing.</summary>
internal sealed class VimMappingsFile : IDisposable
{
    public const string FileName = "vimrc";

    private readonly FileSystemWatcher? watcher;
    private readonly Lock stamped = new();
    private bool disposed;

    /// <summary>Whether the file was read once, under <see cref="stamped"/>.</summary>
    private bool looked;

    /// <summary>The file's last write and length as last read, under <see cref="stamped"/>; none before the first read.</summary>
    private (DateTime Written, long Length)? seen;

    public VimMappingsFile(string folder)
    {
        Path = System.IO.Path.Combine(folder, FileName);
        try
        {
            Directory.CreateDirectory(folder);
            watcher = new FileSystemWatcher(folder, FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, _) => Look();
            watcher.Created += (_, _) => Look();
            watcher.Deleted += (_, _) => Look();
            watcher.Renamed += (_, _) => Look();
            watcher.Error += (_, _) => Look();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // Unwatched, a change is still read as a window comes in front.
            IdeErrors.Write($"Vim mode: {Path} is not watched for changes ({error.Message})");
            watcher?.Dispose();
            watcher = null;
        }
    }

    public string Path { get; }

    /// <summary>The file read, on the UI thread, each time it moved since it was last read.</summary>
    public event EventHandler<VimMappings>? Read;

    /// <summary>Reads the file off the UI thread where it moved since it was last read (or never was).</summary>
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
        watcher?.Dispose();
    }

    /// <summary>On any thread: the file read and handed on where it moved since it was last read.</summary>
    private void Look()
    {
        (DateTime Written, long Length)? now = Stamp();
        lock (stamped)
        {
            if (looked && now == seen)
            {
                return;
            }
            (looked, seen) = (true, now);
        }
        string text = string.Empty;
        try
        {
            text = now is null ? string.Empty : File.ReadAllText(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Being written: the write's own change reads it again.
            lock (stamped)
            {
                looked = false;
            }
            return;
        }
        VimMappings mappings = VimMappings.Read(text);
        UiThread.Post(() =>
        {
            if (!disposed)
            {
                Read?.Invoke(this, mappings);
            }
        });
    }

    private (DateTime Written, long Length)? Stamp()
    {
        FileInfo file = new(Path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }
}

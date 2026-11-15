using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>Vim mode's mappings file, vimrc in the state folder beside settings.json: read once Vim mode is on, and again
/// as it changes on disk, heard by its watcher or found moved as a window comes in front (<see cref="LookAgain"/>); each
/// reading handed to <see cref="Read"/> on the UI thread. A file that is not there maps nothing.</summary>
internal sealed class VimMappingsFile : IDisposable
{
    public const string FileName = "vimrc";

    private readonly WatchedFile watched;
    private bool disposed;

    public VimMappingsFile(string folder)
    {
        watched = new WatchedFile(System.IO.Path.Combine(folder, FileName), "Vim mode");
        watched.Moved += ReadMoved;
    }

    public string Path => watched.Path;

    /// <summary>The file read, on the UI thread, each time it moved since it was last read.</summary>
    public event EventHandler<VimMappings>? Read;

    /// <summary>Reads the file off the UI thread where it moved since it was last read (or never was).</summary>
    public void LookAgain()
    {
        UiThread.Check();
        if (!disposed)
        {
            watched.LookAgain();
        }
    }

    public void Dispose()
    {
        disposed = true;
        watched.Dispose();
    }

    /// <summary>Off the UI thread: the file as it moved to read and handed on.</summary>
    private void ReadMoved(FileStamp? now)
    {
        string text;
        try
        {
            text = now is null ? string.Empty : File.ReadAllText(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Being written: the write's own change reads it again.
            watched.Unseen();
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
}

using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>The files a web app's pages were last told of, each as it stood then, so a file unchanged since is passed over.
/// Used one change at a time.</summary>
internal sealed class ToldFiles
{
    /// <summary>Each file as it stood when the pages were last told of it; null for one gone.</summary>
    private readonly Dictionary<string, FileStamp?> told = new(StringComparer.Ordinal);

    /// <summary>Whether a file there now was written since the pages were last told of it.</summary>
    public bool Written(string path) => File.Exists(path) && WrittenOrGone(path);

    /// <summary>Whether a file was written or has gone since the pages were last told of it: one gone is news once, and one
    /// come back is news as it stands.</summary>
    public bool WrittenOrGone(string path)
    {
        FileStamp? now = FileStamp.Of(path);
        if (told.TryGetValue(path, out FileStamp? last) && last == now)
        {
            return false;
        }
        told[path] = now;
        return true;
    }
}

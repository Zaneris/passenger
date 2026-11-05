namespace Passenger.App.Debugging;

/// <summary>The files a web app's pages were last told of, each as it stood then, so a file unchanged since is passed over.
/// Used one change at a time.</summary>
internal sealed class ToldFiles
{
    /// <summary>What a file gone is told as: no file's stamp.</summary>
    private static readonly (DateTime Written, long Length) Gone = (DateTime.MinValue, -1);

    private readonly Dictionary<string, (DateTime Written, long Length)> told = new(StringComparer.Ordinal);

    /// <summary>Whether a file there now was written since the pages were last told of it.</summary>
    public bool Written(string path) => File.Exists(path) && WrittenOrGone(path);

    /// <summary>Whether a file was written or has gone since the pages were last told of it: one gone is news once, and one
    /// come back is news as it stands.</summary>
    public bool WrittenOrGone(string path)
    {
        FileInfo file = new(path);
        (DateTime, long) now = file.Exists ? (file.LastWriteTimeUtc, file.Length) : Gone;
        if (told.TryGetValue(path, out (DateTime, long) last) && last == now)
        {
            return false;
        }
        told[path] = now;
        return true;
    }
}

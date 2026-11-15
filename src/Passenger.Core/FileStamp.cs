namespace Passenger.Core;

/// <summary>What tells a file as it stands from itself as it stood without reading it: its length and last write. A path
/// that is a symbolic link (a settings file kept in a dotfiles folder) is stamped as the file at the end of its links,
/// which a read of the path reads and a write to it writes (<see cref="AtomicFile.Target"/>): the link's own stamp stays
/// as it was made whatever that file comes to hold.</summary>
public readonly record struct FileStamp(long Length, DateTime Written)
{
    /// <summary>The stamp of the file a path names, through its links; null where it is not there, or its links lead to
    /// nothing a read could open.</summary>
    public static FileStamp? Of(string path)
    {
        FileInfo file = new(path);
        try
        {
            if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0 && file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
            {
                file = target;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Links that go round, or through a folder that cannot be read: nothing a read could open.
            return null;
        }
        return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : null;
    }

    /// <summary>When the file a path names was last written, through its links; the earliest time where it is not there.</summary>
    public static DateTime WrittenAt(string path) => Of(path)?.Written ?? DateTime.MinValue;
}

namespace Passenger.Core;

/// <summary>Which file a path names, as the file system tells files apart: a file written in place stays the same one, and
/// one put in its place by a rename (an atomic save, Godot's safe save) is another. macOS and Linux tell it; Windows does
/// not here.</summary>
public static class FileIdentity
{
    /// <summary>The regular file's identity at a path; null where there is none, on Windows, or where it cannot be read.</summary>
    public static ulong? Of(string path) => UnixFileMetadata.Of(path) is { Inode: > 0 } file ? file.Inode : null;

    /// <summary>Whether this system tells files apart (<see cref="Of"/>).</summary>
    public static bool Told => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
}

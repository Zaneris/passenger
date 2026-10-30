namespace Passenger.Core;

/// <summary>What a path names: a regular file, a folder, or one of what only looks like a file and may never end being
/// read (a FIFO's open waits for a writer, a terminal's read for a key).</summary>
public enum FileKind
{
    File,
    Folder,
    Fifo,
    Device,
    Socket,
    Other,
}

/// <summary>What a path names on macOS or Linux, read from its status, links followed, without opening it.</summary>
public static class FileKinds
{
    /// <summary>What <paramref name="path"/> names; null where nothing is there, on Windows (where opening a pipe or a
    /// device never waits, and its stream cannot seek), and on a Linux whose C library has no statx.</summary>
    public static FileKind? Of(string path) => UnixFileMetadata.TypeOf(path) switch
    {
        null => null,
        0x8000 => FileKind.File,
        0x4000 => FileKind.Folder,
        0x1000 => FileKind.Fifo,
        0x2000 or 0x6000 => FileKind.Device,
        0xC000 => FileKind.Socket,
        _ => FileKind.Other,
    };
}

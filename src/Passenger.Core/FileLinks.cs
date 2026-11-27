using System.IO.Enumeration;

namespace Passenger.Core;

/// <summary>Whether a file or folder is a link, told one way everywhere: on Windows by the reparse point in its attributes,
/// which a listing carries with each entry (junctions and other reparse points counted with links); elsewhere by asking
/// where it leads, never by its attributes, whose read there costs a question of the directory service for each one another
/// user owns (a walk of the root-owned .NET packs took seconds on macOS). Called on any thread.</summary>
public static class FileLinks
{
    /// <summary>Whether an entry a listing reached is a link.</summary>
    public static bool Is(ref FileSystemEntry entry) => OperatingSystem.IsWindows()
        ? (entry.Attributes & FileAttributes.ReparsePoint) != 0
        : entry.ToFileSystemInfo().LinkTarget is not null;

    /// <summary>Whether a file or folder is a link, whichever of the two the info was made as: false for one not there, whose
    /// attributes read as every one set.</summary>
    public static bool Is(FileSystemInfo info) => OperatingSystem.IsWindows()
        ? info.Attributes is var attributes && attributes != NotThere && (attributes & FileAttributes.ReparsePoint) != 0
        : info.LinkTarget is not null;

    private const FileAttributes NotThere = (FileAttributes)(-1);
}

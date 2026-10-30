namespace Passenger.Core;

/// <summary>A folder moved whole: renamed where it lies on the volume it goes to, else carried there as it is, every entry
/// (build output, hidden and system files too), each link made again as a link to where it led, each file's last write
/// time kept, and only then deleted where it was.</summary>
public static class FolderMoves
{
    /// <summary>How a carry lists a folder: every entry, hidden and system ones too, a folder's own entries alone.</summary>
    private static readonly EnumerationOptions Listing = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary>Moves a folder to a path not there yet: at once on one volume, else carried (<see cref="Carry"/>).</summary>
    public static void Move(string from, string to)
    {
        if (!Renamed(from, to))
        {
            Carry(from, to);
        }
    }

    /// <summary>Moves a folder to a path not there yet by renaming it, at once, where it lies on the volume it goes to: false,
    /// nothing moved, where it must be carried (<see cref="Carry"/>).</summary>
    public static bool Renamed(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            Directory.Move(from, to);
            return true;
        }
        catch (IOException) when (Directory.Exists(from) && !Directory.Exists(to))
        {
            // A rename moves nothing when it fails, so the folder is carried, as it must be from one volume to another.
            return false;
        }
    }

    /// <summary>Moves a folder by copy, as from one volume to another: renamed beside itself first, which a folder a process
    /// holds a file in or works in refuses with nothing changed, then copied whole, then deleted. A copy or delete refused
    /// puts the folder back as it was and throws, naming the folder by its own path: nothing is lost unless all of it moved.</summary>
    public static void Carry(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        string beside = Path.Join(Path.GetDirectoryName(from), $".{Path.GetFileName(from)}.{Guid.NewGuid():N}");
        Directory.Move(from, beside);
        try
        {
            Copy(beside, to, there: false);
            Directory.Delete(beside, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw PutBack(beside, from, to, error);
        }
    }

    /// <summary>A folder renamed beside itself to be moved put back where it was, once a copy or delete was refused: what
    /// the delete took copied back from the copy made, then the copy deleted. The refusal to throw, naming the folder by
    /// its own path, or, where it could not be put back, where its files are.</summary>
    private static IOException PutBack(string beside, string from, string copy, Exception refused)
    {
        string why = refused.Message.Replace(PathNames.Canonical(beside), from, StringComparison.Ordinal).Replace(beside, from, StringComparison.Ordinal);
        try
        {
            if (Directory.Exists(copy))
            {
                Copy(copy, beside, there: true);
            }
            Directory.Move(beside, from);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new IOException($"{why}; it could not be put back ({error.Message}): its files are in {beside} and {copy}", refused);
        }
        try
        {
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new IOException($"{why}; a copy of it is left in {copy} ({error.Message})", refused);
        }
        return new IOException(why, refused);
    }

    /// <summary>Copies a folder's entries into another, a link as a link to the same place and a file with its last write
    /// time; with <paramref name="there"/>, only those the other no longer has.</summary>
    private static void Copy(string from, string to, bool there)
    {
        Directory.CreateDirectory(to);
        foreach (FileSystemInfo entry in new DirectoryInfo(from).EnumerateFileSystemInfos("*", Listing))
        {
            string into = Path.Join(to, entry.Name);
            if (entry.LinkTarget is { } target)
            {
                if (there && Exists(into))
                {
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(into, target);
                }
                else
                {
                    File.CreateSymbolicLink(into, target);
                }
            }
            else if (entry is DirectoryInfo)
            {
                Copy(entry.FullName, into, there);
            }
            else if (!(there && Exists(into)))
            {
                File.Copy(entry.FullName, into);
                File.SetLastWriteTimeUtc(into, entry.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>Whether an entry is there, a link to nothing too.</summary>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
}

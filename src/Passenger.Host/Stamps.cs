using Passenger.Core;

namespace Passenger.Host;

/// <summary>What says an evaluation still stands, one definition for both ends (the app compiles this file too): a file's
/// last write and length, a folder's names, which change only when something in it comes, goes or is renamed, and a
/// file-based program's directives. A folder's own last write would change with a build's obj and bin folders made in it,
/// which no wildcard takes in, so its names are hashed instead, those left out the same as the folders a wildcard never
/// enters.</summary>
public static class Stamps
{
    /// <summary>Names in a folder that say nothing of what a project holds: build output and dependencies.</summary>
    private static readonly HashSet<string> Passed = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    /// <summary>Whether a folder's entry is one a wildcard may take in or enter: not build output, not a hidden folder, not
    /// the system's own file (<see cref="PathNames.IsSystemFile"/>).</summary>
    public static bool Counts(string name, bool folder) => !Passed.Contains(name) && !(folder && name.StartsWith('.')) && !PathNames.IsSystemFile(name);

    public static Stamp File(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? new Stamp(path, file.LastWriteTimeUtc.Ticks, file.Length) : new Stamp(path, 0, -1);
    }

    /// <summary>A program's C# file (<see cref="ProgramFiles"/>) by its head alone, the directives its project is made of,
    /// hashed, with how many: an edit of its code changes nothing MSBuild reads of it.</summary>
    public static Stamp Head(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return new Stamp(path, 0, -1, Head: true);
        }
        List<string> head = ProgramFiles.Head(path);
        return new Stamp(path, Hash(head), head.Count, Head: true);
    }

    /// <summary>A project's own file as its evaluation reads it: a program's by its head, any other whole.</summary>
    public static Stamp Project(string path) => ProgramFiles.IsProgram(path) ? Head(path) : File(path);

    /// <summary>A folder's names as a hash, with how many there are; a folder not there has none.</summary>
    public static Stamp Folder(string path)
    {
        List<string> names = [];
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                bool folder = entry is DirectoryInfo;
                if (Counts(entry.Name, folder))
                {
                    names.Add(folder ? entry.Name + "/" : entry.Name);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Stamp(path, 0, -1, Folder: true);
        }
        names.Sort(StringComparer.Ordinal);
        return new Stamp(path, Hash(names), names.Count, Folder: true);
    }

    /// <summary>FNV-1a, the same in every process, over each text and a separator.</summary>
    private static long Hash(List<string> texts)
    {
        ulong hash = 14695981039346656037;
        foreach (string text in texts)
        {
            foreach (char c in text)
            {
                hash = (hash ^ c) * 1099511628211;
            }
            hash = (hash ^ '\n') * 1099511628211;
        }
        return unchecked((long)hash);
    }

    /// <summary>Whether what was stamped is as it was.</summary>
    public static bool Holds(Stamp stamp) => (stamp.Folder ? Folder(stamp.Path) : stamp.Head ? Head(stamp.Path) : File(stamp.Path)) == stamp;
}

namespace Passenger.Host;

/// <summary>What says an evaluation still stands, one definition for both ends (the app compiles this file too): a file's
/// last write and length, and a folder's names, which change only when something in it comes, goes or is renamed. A
/// folder's own last write would change with a build's obj and bin folders made in it, which no wildcard takes in, so its
/// names are hashed instead, those left out the same as the folders a wildcard never enters.</summary>
public static class Stamps
{
    /// <summary>Names in a folder that say nothing of what a project holds: build output, dependencies, the system's own.</summary>
    private static readonly HashSet<string> Passed = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".DS_Store", "Thumbs.db" };

    /// <summary>Whether a folder's entry is one a wildcard may take in or enter: not build output, not a hidden folder.</summary>
    public static bool Counts(string name, bool folder) => !Passed.Contains(name) && !(folder && name.StartsWith('.'));

    public static Stamp File(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? new Stamp(path, file.LastWriteTimeUtc.Ticks, file.Length) : new Stamp(path, 0, -1);
    }

    /// <summary>A folder's names as a hash, with how many there are; a folder not there has none.</summary>
    public static Stamp Folder(string path) => Folder(path, null);

    /// <summary>Only names matching an import wildcard, leaving unrelated compiler output churn out of its identity.</summary>
    public static Stamp Folder(string path, string? pattern)
    {
        List<string> names = [];
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos(pattern ?? "*"))
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
            return new Stamp(path, 0, -1, Folder: true, Pattern: pattern);
        }
        names.Sort(StringComparer.Ordinal);
        // FNV-1a, the same in every process, over each name and a separator.
        ulong hash = 14695981039346656037;
        foreach (string name in names)
        {
            foreach (char c in name)
            {
                hash = (hash ^ c) * 1099511628211;
            }
            hash = (hash ^ '\n') * 1099511628211;
        }
        return new Stamp(path, unchecked((long)hash), names.Count, Folder: true, Pattern: pattern);
    }

    /// <summary>Whether what was stamped is as it was.</summary>
    public static bool Holds(Stamp stamp) => (stamp.Folder ? Folder(stamp.Path, stamp.Pattern) : File(stamp.Path)) == stamp;
}

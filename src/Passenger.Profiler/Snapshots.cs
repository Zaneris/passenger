namespace Passenger.Profiler;

/// <summary>A snapshot kept on disk: its .nettrace file, named for its program and when it ran, and its size.</summary>
public sealed record SnapshotFile(string Path, string Name, DateTime Written, long Bytes);

/// <summary>A folder of snapshots: the newest first, and only the newest <see cref="Kept"/> runs, nor more than
/// <see cref="KeptBytes"/> of them together, kept. Past either, the oldest go first; the newest stays whatever its
/// size.</summary>
public static class Snapshots
{
    public const int Kept = 10;

    public const long KeptBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The folder's snapshots, the newest first; none when it is not there or cannot be read.</summary>
    public static IReadOnlyList<SnapshotFile> List(string folder)
    {
        try
        {
            DirectoryInfo found = new(folder);
            return !found.Exists ? [] : [.. found.EnumerateFiles("*.nettrace")
                .Select(f => new SnapshotFile(f.FullName, System.IO.Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTimeUtc, f.Length))
                .OrderByDescending(f => f.Written)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Removes the oldest snapshots past the cap, and says which went. One another program holds open (a viewer's,
    /// on Windows) stays until the next time.</summary>
    public static IReadOnlyList<SnapshotFile> Trim(string folder)
    {
        List<SnapshotFile> removed = [];
        int kept = 0;
        long bytes = 0;
        bool full = false;
        foreach (SnapshotFile file in List(folder))
        {
            full |= kept > 0 && (kept == Kept || bytes + file.Bytes > KeptBytes);
            if (!full)
            {
                kept++;
                bytes += file.Bytes;
                continue;
            }
            try
            {
                File.Delete(file.Path);
                removed.Add(file);
                // A game's frames, kept beside its trace, go with it.
                File.Delete(GameProfile.PathOf(file.Path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }
}

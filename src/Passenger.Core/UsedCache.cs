namespace Passenger.Core;

/// <summary>What a <see cref="UsedCache"/> keeps: files with one extension, or folders, this many levels under its
/// folder. A folder may keep a mark, a file inside it whose time is the folder's last use; else the folder's own time
/// is.</summary>
public sealed record UsedEntries(int Depth, string? Extension, string? Mark)
{
    public static UsedEntries Files(int depth, string extension) => new(depth, extension, null);

    public static UsedEntries Folders(int depth, string? mark = null) => new(depth, null, mark);
}

/// <summary>How long a <see cref="UsedCache"/> keeps what it holds.</summary>
/// <param name="UnusedFor">An entry unused this long is removed.</param>
/// <param name="InUse">An entry used this recently is never removed: another process may be reading it.</param>
/// <param name="MostBytes">The most the entries may take, past which the least recently used go; null for no most.</param>
/// <param name="Abandoned">A temporary file or folder this old is a write or a copy that never finished.</param>
public sealed record UsedLimits(TimeSpan UnusedFor, TimeSpan InUse, long? MostBytes, TimeSpan Abandoned);

/// <summary>A cache under one folder kept by last use, each entry's time its use, touched as it is used. Pruned of what no
/// one needs: temporary files and folders (<see cref="AtomicFile.Temporary"/>) of writes and copies that never finished,
/// entries unused for their limit, then the least recently used while the rest take more than the most, never one used
/// lately; then the folders above the entries left empty. A folder entry is renamed out of its name before it is
/// removed, so no reader takes it for whole as it goes. Its files are written whole, through <see cref="AtomicFile"/>.</summary>
public sealed class UsedCache(string folder, UsedEntries entries, UsedLimits limits, Action<string>? log = null)
{
    /// <summary>What a folder entry renamed to be removed is called, until it is.</summary>
    private const string Going = ".gone";

    public string Folder { get; } = Path.GetFullPath(folder);

    /// <summary>Marks an entry used now; one pruned or held a moment ago is left to whatever reads it next.</summary>
    public void Touch(string entry)
    {
        try
        {
            if (entries.Extension is not null)
            {
                File.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
            }
            else if (entries.Mark is not { } mark)
            {
                Directory.SetLastWriteTimeUtc(entry, DateTime.UtcNow);
            }
            else if (File.Exists(Path.Combine(entry, mark)))
            {
                File.SetLastWriteTimeUtc(Path.Combine(entry, mark), DateTime.UtcNow);
            }
            else
            {
                AtomicFile.Write(Path.Combine(entry, mark), []);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unmarked, it is kept by the time it last had.
        }
    }

    /// <summary>Writes a file of the cache whole as its bytes are made, its folder made first, and made again once should
    /// another process's prune take the folder away between the two. A file another process put there first, held open
    /// by a reader (Windows refuses the rename then), is as whole as this one, so it stays.</summary>
    public void Write(string path, Action<Stream> fill)
    {
        for (int attempt = 0; ; attempt++)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                AtomicFile.Write(path, fill, null, AtomicFile.Temporary, Replace);
                return;
            }
            catch (DirectoryNotFoundException) when (attempt == 0)
            {
            }
        }
    }

    /// <summary>Writes a file of the cache whole, as <see cref="Write(string, Action{Stream})"/> does.</summary>
    public void Write(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));

    /// <summary>Removes an entry now, or a temporary folder of the cache's; false when it could not be (held, on Windows).</summary>
    public bool Remove(string entry) => entries.Extension is not null ? DeleteFile(entry) : RemoveFolder(entry);

    /// <summary>Removes what no one needs (see the type), never <paramref name="kept"/>, the entry its caller is about to
    /// use. What it cannot remove waits for the next prune, and the cache is used all the same.</summary>
    public void Prune(DateTime now, string? kept = null)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }
            foreach (FileInfo file in new DirectoryInfo(Folder).EnumerateFiles("*" + AtomicFile.Temporary, SearchOption.AllDirectories))
            {
                if (now - file.LastWriteTimeUtc > limits.Abandoned)
                {
                    DeleteFile(file.FullName);
                }
            }
            // The folders above the entries, a level at a time: the cache's own, then each level's under the last.
            List<List<DirectoryInfo>> above = [[new DirectoryInfo(Folder)]];
            while (above.Count < entries.Depth)
            {
                above.Add([.. above[^1].SelectMany(each => each.EnumerateDirectories())]);
            }
            List<(string Path, DateTime Used, long Bytes)> found = [.. above[^1].SelectMany(each => Entries(each, now))];
            long total = found.Sum(entry => entry.Bytes);
            int removed = 0;
            foreach ((string path, DateTime used, long bytes) in found.OrderBy(entry => entry.Used))
            {
                TimeSpan unused = now - used;
                if (unused < limits.InUse)
                {
                    break;
                }
                if ((kept is null || !PathNames.Same(path, kept)) && (unused > limits.UnusedFor || total > limits.MostBytes) && Remove(path))
                {
                    total -= bytes;
                    removed++;
                }
            }
            // The deepest first, so a name's folder is empty once its versions' are; never the cache's own.
            foreach (DirectoryInfo each in above.Skip(1).Reverse().SelectMany(level => level))
            {
                DeleteEmpty(each);
            }
            if (removed > 0)
            {
                log?.Invoke(limits.MostBytes is null ? $"{removed} entries pruned from {Folder}" : $"{removed} entries pruned, {total / (1024 * 1024)} MB kept in {Folder}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"{Folder} could not be pruned: {error.Message}");
        }
    }

    /// <summary>The entries in one folder of the level above them, each with its last use and, where the cache has a most,
    /// its size; a folder left by a removal that never finished removed, and a temporary one long abandoned.</summary>
    private IEnumerable<(string Path, DateTime Used, long Bytes)> Entries(DirectoryInfo parent, DateTime now)
    {
        if (entries.Extension is { } extension)
        {
            foreach (FileInfo file in parent.EnumerateFiles())
            {
                if (file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (file.FullName, file.LastWriteTimeUtc, file.Length);
                }
            }
            yield break;
        }
        foreach (DirectoryInfo each in parent.EnumerateDirectories().ToList())
        {
            if (each.Name.EndsWith(Going, StringComparison.Ordinal) || (each.Name.EndsWith(AtomicFile.Temporary, StringComparison.Ordinal)
                && now - each.LastWriteTimeUtc > limits.Abandoned))
            {
                RemoveFolder(each.FullName);
            }
            else if (!each.Name.EndsWith(AtomicFile.Temporary, StringComparison.Ordinal))
            {
                DateTime used = entries.Mark is { } mark && new FileInfo(Path.Combine(each.FullName, mark)) is { Exists: true } marked
                    ? marked.LastWriteTimeUtc : each.LastWriteTimeUtc;
                long bytes = limits.MostBytes is null ? 0 : each.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                yield return (each.FullName, used, bytes);
            }
        }
    }

    /// <summary>Gives its owner write in a folder and every folder under it, whatever modes they were copied with (a
    /// system install's, read-only), so what is made in it can be written and it can be removed whole.</summary>
    public static void OwnFolders(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        foreach (string each in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder))
        {
            UnixFileMode mode = File.GetUnixFileMode(each);
            UnixFileMode owners = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if ((mode & owners) != owners)
            {
                File.SetUnixFileMode(each, mode | owners);
            }
        }
    }

    /// <summary>Renames a folder out of its name (unless it is a temporary one, whose name no reader takes), then removes it.</summary>
    private static bool RemoveFolder(string folder)
    {
        try
        {
            string going = folder;
            if (!Path.GetFileName(folder).StartsWith('.'))
            {
                going = Path.Combine(Path.GetDirectoryName(folder)!, $".{Guid.NewGuid():N}{Going}");
                Directory.Move(folder, going);
            }
            OwnFolders(going);
            Directory.Delete(going, recursive: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Held by a reader (Windows), or gone already: a later prune takes what is left.
            return false;
        }
    }

    private static void Replace(string temporary, string path)
    {
        try
        {
            AtomicFile.Move(temporary, path);
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && File.Exists(path))
        {
            DeleteFile(temporary);
        }
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteEmpty(DirectoryInfo folder)
    {
        try
        {
            if (folder.Exists && !folder.EnumerateFileSystemInfos().Any())
            {
                folder.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Written to since it was looked at.
        }
    }
}

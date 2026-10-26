using System.Collections.Concurrent;

namespace Passenger.Core;

/// <summary>What files' parses made, each kept by its file's stamp (its last write and length) and its path as paths are
/// compared (<see cref="PathNames.Comparer"/>): asked of a file as it was, the parse kept answers; a file changed since is
/// parsed again, once however many ask at once; a gone file's parse is let go as it is asked for. Any thread.</summary>
public sealed class FileMemo<T> where T : class
{
    private readonly ConcurrentDictionary<string, (long Written, long Length, Lazy<T?> Parsed)> kept = new(PathNames.Comparer);

    /// <summary>A file's parse as the file stands, made by <paramref name="parse"/> unless kept for it as it is; null for a
    /// file not there, one that cannot be read meanwhile (not kept, so asked again), or what the parse made of it.</summary>
    public T? Get(string path, Func<string, T?> parse)
    {
        FileInfo file = new(path);
        if (!file.Exists)
        {
            kept.TryRemove(path, out _);
            return null;
        }
        long written = file.LastWriteTimeUtc.Ticks;
        long length = file.Length;
        (long Written, long Length, Lazy<T?> Parsed) entry = kept.TryGetValue(path, out (long Written, long Length, Lazy<T?> Parsed) held)
            && held.Written == written && held.Length == length ? held
            : kept.AddOrUpdate(path, static (_, made) => made,
                static (_, held, made) => held.Written == made.Written && held.Length == made.Length ? held : made, (Written: written, Length: length, Parsed: Parsing(path, parse)));
        try
        {
            return entry.Parsed.Value;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            kept.TryRemove(KeyValuePair.Create(path, entry));
            return null;
        }
    }

    /// <summary>A file's parse made once however many ask for it, made apart so a parse kept costs no closure.</summary>
    private static Lazy<T?> Parsing(string path, Func<string, T?> parse) => new(() => parse(path));

    /// <summary>The parse last kept for a file, whether or not it still stands; null for none.</summary>
    public T? Kept(string path)
    {
        if (!kept.TryGetValue(path, out (long Written, long Length, Lazy<T?> Parsed) entry))
        {
            return null;
        }
        try
        {
            return entry.Parsed.Value;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Lets every parse go.</summary>
    public void Clear() => kept.Clear();
}

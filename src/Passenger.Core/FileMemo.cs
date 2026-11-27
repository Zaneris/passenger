using System.Collections.Concurrent;

namespace Passenger.Core;

/// <summary>What files' parses made, each kept by its file's stamp (<see cref="FileStamp"/>) and its path as paths are
/// compared (<see cref="PathNames.Comparer"/>): asked of a file as it was, the parse kept answers; a file changed since is
/// parsed again, once however many ask at once; a gone file's parse is let go as it is asked for. Any thread.</summary>
public sealed class FileMemo<T> where T : class
{
    private readonly ConcurrentDictionary<string, (FileStamp Stamp, Lazy<T?> Parsed)> kept = new(PathNames.Comparer);

    /// <summary>A file's parse as <see cref="Get"/> makes it, but within a look (<see cref="FileLook"/>) the parse last kept
    /// (<see cref="Kept"/>) without a word to the disk, the file asked again as the look is taken again.</summary>
    public T? Read(string path, Func<string, T?> parse)
    {
        if (FileLook.Current is not { } look)
        {
            return Get(path, parse);
        }
        T? kept = Kept(path);
        look.Asked(() => !ReferenceEquals(Get(path, parse), kept));
        return kept;
    }

    /// <summary>A file's parse as the file stands, made by <paramref name="parse"/> unless kept for it as it is; null for a
    /// file not there, one that cannot be read meanwhile (not kept, so asked again), or what the parse made of it.</summary>
    public T? Get(string path, Func<string, T?> parse)
    {
        FileLook.AskedOnThisThread++;
        if (FileStamp.Of(path) is not { } stamp)
        {
            kept.TryRemove(path, out _);
            return null;
        }
        (FileStamp Stamp, Lazy<T?> Parsed) entry = kept.TryGetValue(path, out (FileStamp Stamp, Lazy<T?> Parsed) held) && held.Stamp == stamp ? held
            : kept.AddOrUpdate(path, static (_, made) => made, static (_, held, made) => held.Stamp == made.Stamp ? held : made, (Stamp: stamp, Parsed: Parsing(path, parse)));
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
        if (!kept.TryGetValue(path, out (FileStamp Stamp, Lazy<T?> Parsed) entry))
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

    /// <summary>Lets a file's parse go, so the next asked of it is made anew.</summary>
    public void Forget(string path) => kept.TryRemove(path, out _);

    /// <summary>Lets every parse go.</summary>
    public void Clear() => kept.Clear();
}

/// <summary>A turn of reads through memos (<see cref="FileMemo{T}.Read"/>) that answers each file's parse as last kept and asks
/// the disk nothing, as a list made on the window's thread reads: what each asked is looked at again apart (<see cref="Again"/>),
/// off that thread, and what changed since makes the list again there. One at a time on a thread, until disposed.</summary>
public sealed class FileLook : IDisposable
{
    [ThreadStatic]
    private static FileLook? current;

    [ThreadStatic]
    private static int asked;

    private readonly FileLook? outer;
    private readonly List<Func<bool>> looks = [];

    private FileLook(FileLook? outer) => this.outer = outer;

    /// <summary>The look under way on this thread, if any.</summary>
    internal static FileLook? Current => current;

    /// <summary>How many times this thread has asked the disk of a memo's file, for a test of a list made from kept parses.</summary>
    public static int AskedOnThisThread
    {
        get => asked;
        internal set => asked = value;
    }

    /// <summary>A look on this thread, until disposed.</summary>
    public static FileLook Begin() => current = new FileLook(current);

    internal void Asked(Func<bool> again) => looks.Add(again);

    public void Dispose() => current = outer;

    /// <summary>Each file the look asked of, asked again of the disk as it stands now: whether any parse differs from the one
    /// the look answered. Any thread but the look's, once it is disposed.</summary>
    public bool Again()
    {
        bool changed = false;
        foreach (Func<bool> again in looks)
        {
            changed |= again();
        }
        return changed;
    }
}

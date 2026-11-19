namespace Passenger.Workspace;

/// <summary>What one of macOS's file events says, by its FSEvents flags, as a watcher of a folder takes it: a path's
/// content changed, a path made, moved in or gone, the stream's word that it dropped changes (a reader behind, the kernel's
/// queue full, a folder to scan again, a volume mounted or unmounted), or the folder itself moved or deleted. A stream reads
/// a folder and all below it, so one stream may read for many watchers (<see cref="Reaches"/>), the folder it reads from
/// shared where watchers are many (<see cref="Shared"/>).</summary>
public static class MacFileEventFlags
{
    public const uint MustScanSubDirs = 0x1;
    public const uint UserDropped = 0x2;
    public const uint KernelDropped = 0x4;
    public const uint RootChanged = 0x20;
    public const uint Mount = 0x40;
    public const uint Unmount = 0x80;
    public const uint ItemCreated = 0x100;
    public const uint ItemRemoved = 0x200;
    public const uint ItemInodeMetaMod = 0x400;
    public const uint ItemRenamed = 0x800;
    public const uint ItemModified = 0x1000;
    public const uint ItemFinderInfoMod = 0x2000;
    public const uint ItemChangeOwner = 0x4000;
    public const uint ItemXattrMod = 0x8000;
    public const uint ItemIsFile = 0x10000;
    public const uint ItemIsDir = 0x20000;

    private const uint Dropping = MustScanSubDirs | UserDropped | KernelDropped | Mount | Unmount;
    private const uint Changing = ItemInodeMetaMod | ItemModified | ItemFinderInfoMod | ItemChangeOwner | ItemXattrMod;

    /// <summary>The least depth, in names, of a folder a stream reads from for more than one watcher: never the root, a
    /// volume's (/Volumes/Backup), the folder all homes are in, nor a home's own children, where every program writes.</summary>
    private const int SharedDepth = 4;

    /// <summary>Adds to <paramref name="into"/> what an event at <paramref name="path"/>, of a stream reading from <paramref
    /// name="from"/>, is to a watcher of <paramref name="folder"/>, below its children only where <paramref name="recursive"/>:
    /// a drop whatever its path, the folder gone (itself, or one above it below <paramref name="from"/>, renamed or deleted
    /// and not there now), else each change in turn, nothing else of the folder or above it. A path renamed is moved in or
    /// gone as <paramref name="there"/> finds it now (given the path and whether the event says it is a file).</summary>
    public static void Read(string path, uint flags, string folder, bool recursive, string from, Func<string, bool, bool> there, List<MacFileEvent> into)
    {
        if ((flags & Dropping) != 0)
        {
            into.Add(new MacFileEvent(path, MacFileEventKind.Dropped));
            return;
        }
        if ((flags & RootChanged) != 0)
        {
            into.Add(new MacFileEvent(folder, MacFileEventKind.Gone));
            return;
        }
        if (Holds(path, folder) && Under(path, from))
        {
            if ((flags & (ItemRemoved | ItemRenamed)) != 0 && (flags & ItemIsFile) == 0 && !there(folder, false))
            {
                into.Add(new MacFileEvent(folder, MacFileEventKind.Gone));
            }
            return;
        }
        if (!Under(path, folder) || !recursive && path.AsSpan(folder.AsSpan().TrimEnd('/').Length + 1).Contains('/'))
        {
            return;
        }
        if ((flags & Changing) != 0)
        {
            into.Add(new MacFileEvent(path, MacFileEventKind.Changed));
        }
        if ((flags & ItemCreated) != 0)
        {
            into.Add(new MacFileEvent(path, MacFileEventKind.Made));
        }
        if ((flags & ItemRemoved) != 0)
        {
            into.Add(new MacFileEvent(path, MacFileEventKind.Deleted));
        }
        if ((flags & ItemRenamed) != 0)
        {
            if (there(path, (flags & ItemIsFile) != 0))
            {
                if ((flags & ItemCreated) == 0)
                {
                    into.Add(new MacFileEvent(path, MacFileEventKind.MovedIn));
                }
            }
            else if ((flags & ItemRemoved) == 0)
            {
                into.Add(new MacFileEvent(path, MacFileEventKind.Deleted));
            }
        }
    }

    /// <summary>Whether an event at <paramref name="path"/> of a stream may be something to a watcher of <paramref
    /// name="folder"/> read through it, past what the folders above the path say (each change below it): a drop at a path
    /// in the folder or over it, the stream's own folder moved, or a folder holding the watcher's renamed or deleted.</summary>
    public static bool Reaches(string path, uint flags, string folder)
    {
        if ((flags & Dropping) != 0)
        {
            return Holds(path, folder) || Under(path, folder);
        }
        return (flags & RootChanged) != 0 || (flags & (ItemRemoved | ItemRenamed)) != 0 && (flags & ItemIsFile) == 0 && Holds(path, folder);
    }

    /// <summary>Whether a drop said for every watcher, which only a stream's own words carry (<see cref="Dropping"/>).</summary>
    public static bool Drops(uint flags) => (flags & Dropping) != 0;

    /// <summary>The folder a stream reads from to read both <paramref name="folder"/> and <paramref name="other"/>
    /// (canonical, so their names compare as written): the deepest holding both, unless it is above <see
    /// cref="SharedDepth"/> names, or <paramref name="home"/> or above it, where every program writes all the time; null
    /// then.</summary>
    public static string? Shared(string folder, string other, string home)
    {
        int common = 0;
        int names = 0;
        for (int at = 1; at <= Math.Min(folder.Length, other.Length); at++)
        {
            bool ends = at == folder.Length || folder[at] == '/';
            if (ends && (at == other.Length || other[at] == '/') && folder.AsSpan(0, at).SequenceEqual(other.AsSpan(0, at)))
            {
                (common, names) = (at, names + 1);
            }
            else if (folder[at - 1] != other[at - 1])
            {
                break;
            }
        }
        string shared = folder[..common];
        return names < SharedDepth || Holds(shared, home) ? null : shared;
    }

    /// <summary>Whether a path lies below a folder, the root's included, as macOS's events name it, case aside.</summary>
    private static bool Under(string path, string folder)
    {
        ReadOnlySpan<char> top = folder.AsSpan().TrimEnd('/');
        return path.Length > top.Length + 1 && path[top.Length] == '/' && path.AsSpan(0, top.Length).Equals(top, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a path is the folder or holds it.</summary>
    private static bool Holds(string path, string folder) =>
        path.AsSpan().TrimEnd('/').Equals(folder.AsSpan().TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || Under(folder, path);
}

/// <summary>What a macOS file event is to a watcher.</summary>
public enum MacFileEventKind
{
    /// <summary>A path's content or attributes changed.</summary>
    Changed,

    /// <summary>A path made there.</summary>
    Made,

    /// <summary>A path moved there from elsewhere.</summary>
    MovedIn,

    /// <summary>A path deleted or moved away.</summary>
    Deleted,

    /// <summary>The system dropped changes it says nothing more of.</summary>
    Dropped,

    /// <summary>The folder watched was itself deleted or moved.</summary>
    Gone,
}

/// <summary>A macOS file event as a watcher takes it: what happened, and where.</summary>
public readonly record struct MacFileEvent(string Path, MacFileEventKind Kind);

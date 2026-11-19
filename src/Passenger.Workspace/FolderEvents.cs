namespace Passenger.Workspace;

/// <summary>What one of the system's file events says, as a watcher of a folder takes it: a path's content changed, a path
/// made, moved in or gone, the system's word that it dropped changes (a reader behind, the kernel's queue full, a folder to
/// scan again, a volume mounted or unmounted), the folder itself moved or deleted, or the watch failed. Events are read by
/// FSEvents' flags, macOS's own, into which Windows' are read too. A watcher of a folder reads it and all below it, so one
/// may read for many folders (<see cref="Reaches"/>), the folder it reads from shared where they are many (<see
/// cref="Shared"/>).</summary>
public static class FolderEvents
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

    /// <summary>The watch failed other than by dropping changes (its folder unreachable): a flag of Passenger's own, which
    /// FSEvents never sets.</summary>
    public const uint Failed = 0x8000_0000;

    private const uint Dropping = MustScanSubDirs | UserDropped | KernelDropped | Mount | Unmount;
    private const uint Changing = ItemInodeMetaMod | ItemModified | ItemFinderInfoMod | ItemChangeOwner | ItemXattrMod;

    /// <summary>The least depth, in names below its root, of a folder read from for more than one watcher: never a disk's
    /// root, a volume's (/Volumes/Backup), the folder all homes are in, nor a home's own children, where every program writes.</summary>
    private const int SharedDepth = 4;

    /// <summary>Adds to <paramref name="into"/> what an event at <paramref name="path"/>, of a watcher reading from <paramref
    /// name="from"/>, is to a watcher of <paramref name="folder"/>, below its children only where <paramref name="recursive"/>:
    /// a drop or a failure whatever its path, the folder gone (itself, or one above it below <paramref name="from"/>, renamed or
    /// deleted and not there now), else each change in turn, nothing else of the folder or above it. A path renamed is moved
    /// in or gone as <paramref name="there"/> finds it now (given the path and whether the event says it is a file).</summary>
    public static void Read(string path, uint flags, string folder, bool recursive, string from, Func<string, bool, bool> there, List<FolderEvent> into)
    {
        if ((flags & Failed) != 0)
        {
            into.Add(new FolderEvent(path, FolderEventKind.Failed));
            return;
        }
        if ((flags & Dropping) != 0)
        {
            into.Add(new FolderEvent(path, FolderEventKind.Dropped));
            return;
        }
        if ((flags & RootChanged) != 0)
        {
            into.Add(new FolderEvent(folder, FolderEventKind.Gone));
            return;
        }
        if (Holds(path, folder) && Under(path, from))
        {
            if ((flags & (ItemRemoved | ItemRenamed)) != 0 && (flags & ItemIsFile) == 0 && !there(folder, false))
            {
                into.Add(new FolderEvent(folder, FolderEventKind.Gone));
            }
            return;
        }
        if (!Under(path, folder) || !recursive && path.AsSpan(Trimmed(folder).Length + 1).IndexOfAny(Separators) >= 0)
        {
            return;
        }
        if ((flags & Changing) != 0)
        {
            into.Add(new FolderEvent(path, FolderEventKind.Changed));
        }
        if ((flags & ItemCreated) != 0)
        {
            into.Add(new FolderEvent(path, FolderEventKind.Made));
        }
        if ((flags & ItemRemoved) != 0)
        {
            into.Add(new FolderEvent(path, FolderEventKind.Deleted));
        }
        if ((flags & ItemRenamed) != 0)
        {
            if (there(path, (flags & ItemIsFile) != 0))
            {
                if ((flags & ItemCreated) == 0)
                {
                    into.Add(new FolderEvent(path, FolderEventKind.MovedIn));
                }
            }
            else if ((flags & ItemRemoved) == 0)
            {
                into.Add(new FolderEvent(path, FolderEventKind.Deleted));
            }
        }
    }

    /// <summary>Whether an event at <paramref name="path"/> may be something to a watcher of <paramref name="folder"/> read
    /// through a shared watcher, past what the folders above the path say (each change below it): a drop at a path in the
    /// folder or over it, the shared watcher's own folder moved or its watch failed, or a folder holding the folder renamed
    /// or deleted.</summary>
    public static bool Reaches(string path, uint flags, string folder)
    {
        if ((flags & Dropping) != 0)
        {
            return Holds(path, folder) || Under(path, folder);
        }
        return (flags & (RootChanged | Failed)) != 0 || (flags & (ItemRemoved | ItemRenamed)) != 0 && (flags & ItemIsFile) == 0 && Holds(path, folder);
    }

    /// <summary>Whether an event says a watcher's own word, for every folder it reaches rather than for those above its path:
    /// a drop, its folder moved, or its watch failed.</summary>
    public static bool Says(uint flags) => (flags & (Dropping | RootChanged | Failed)) != 0;

    /// <summary>The folder a watcher reads from to read both <paramref name="folder"/> and <paramref name="other"/>
    /// (canonical, so their names compare as written): the deepest holding both, unless it is above <see
    /// cref="SharedDepth"/> names below its root, or <paramref name="home"/> or above it, where every program writes all the
    /// time; null then.</summary>
    public static string? Shared(string folder, string other, string home)
    {
        int root = Path.GetPathRoot(folder.AsSpan()).Length;
        if (root == 0 || Path.GetPathRoot(other.AsSpan()).Length != root || !folder.AsSpan(0, root).SequenceEqual(other.AsSpan(0, root)))
        {
            return null;
        }
        int common = 0;
        int names = 0;
        for (int at = root + 1; at <= Math.Min(folder.Length, other.Length); at++)
        {
            bool ends = at == folder.Length || Separators.Contains(folder[at]);
            if (ends && (at == other.Length || Separators.Contains(other[at])) && folder.AsSpan(root, at - root).SequenceEqual(other.AsSpan(root, at - root)))
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

    /// <summary>What separates a path's names here.</summary>
    private static ReadOnlySpan<char> Separators => OperatingSystem.IsWindows() ? @"\/" : "/";

    /// <summary>A folder without the separator it may end in.</summary>
    private static ReadOnlySpan<char> Trimmed(string folder) => folder.AsSpan().TrimEnd(Separators);

    /// <summary>Whether a path lies below a folder, the root's included, as the system's events name it, case aside.</summary>
    private static bool Under(string path, string folder)
    {
        ReadOnlySpan<char> top = Trimmed(folder);
        return path.Length > top.Length + 1 && Separators.Contains(path[top.Length]) && path.AsSpan(0, top.Length).Equals(top, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a path is the folder or holds it.</summary>
    private static bool Holds(string path, string folder) =>
        path.AsSpan().TrimEnd(Separators).Equals(Trimmed(folder), StringComparison.OrdinalIgnoreCase) || Under(folder, path);
}

/// <summary>What a file event is to a watcher.</summary>
public enum FolderEventKind
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

    /// <summary>The watch failed, what it would have heard not known.</summary>
    Failed,
}

/// <summary>A file event as a watcher takes it: what happened, and where.</summary>
public readonly record struct FolderEvent(string Path, FolderEventKind Kind);

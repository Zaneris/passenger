namespace Passenger.Workspace;

/// <summary>What one of macOS's file events says, by its FSEvents flags, as a watcher of a folder takes it: a path's
/// content changed, a path made, moved in or gone, the stream's word that it dropped changes (a reader behind, the kernel's
/// queue full, a folder to scan again, a volume mounted or unmounted), or the folder itself moved or deleted.</summary>
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

    private const uint Dropping = MustScanSubDirs | UserDropped | KernelDropped | Mount | Unmount;
    private const uint Changing = ItemInodeMetaMod | ItemModified | ItemFinderInfoMod | ItemChangeOwner | ItemXattrMod;

    /// <summary>Adds to <paramref name="into"/> what an event at <paramref name="path"/> is to a watcher of <paramref
    /// name="folder"/>, below its own children only where <paramref name="recursive"/>: a drop whatever its path, the folder
    /// gone, else each change in turn, nothing for the folder itself or above it. A path renamed is moved in or gone as
    /// <paramref name="there"/> finds it now (given the path and whether the event says it is a file).</summary>
    public static void Read(string path, uint flags, string folder, bool recursive, Func<string, bool, bool> there, List<MacFileEvent> into)
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
        int top = Path.TrimEndingDirectorySeparator(folder.AsSpan()).Length;
        if (path.Length <= top + 1 || path[top] != '/' || !path.AsSpan(0, top).Equals(Path.TrimEndingDirectorySeparator(folder.AsSpan()), StringComparison.OrdinalIgnoreCase)
            || !recursive && path.AsSpan(top + 1).Contains('/'))
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

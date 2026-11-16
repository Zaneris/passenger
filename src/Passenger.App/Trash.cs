using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The platform's Trash (the Recycle Bin on Windows), where a deleted file can still be found: an item moved there
/// is told where it went (<see cref="Trashed"/>), from where <see cref="PutBack"/> moves it back as it was, links, modes
/// and empty folders too, as Undo of a delete does.</summary>
public static class Trash
{
    /// <summary>A folder to use as the Trash instead of the platform's. The process's, as the system's Trash is every
    /// window's.</summary>
    public static string? Folder { get; set; }

    /// <summary>Moves a file or folder to the Trash: where it went there, or null where the Trash keeps it nowhere Passenger
    /// can find (a Recycle Bin that deleted it outright, as one does an item too large for it). What the system refuses is
    /// an IOException or UnauthorizedAccessException, nothing moved.</summary>
    public static Trashed? Move(string path)
    {
        if (OperatingSystem.IsWindows() && Folder is null)
        {
            return MoveToRecycleBin(path);
        }
        if (OperatingSystem.IsLinux() && Folder is null)
        {
            return MoveToFreedesktopTrash(path);
        }
        if (OperatingSystem.IsMacOS() && Folder is null)
        {
            return MoveToMacTrash(path);
        }
        string bin = Folder ?? throw new PlatformNotSupportedException("Passenger knows no Trash on this system");
        Directory.CreateDirectory(bin);
        string name = Path.GetFileName(path);
        string target = Path.Combine(bin, name);
        // The Trash keeps what it already holds: a second one of a name takes the time beside it.
        if (Stands(target))
        {
            target = Path.Combine(bin, $"{Path.GetFileNameWithoutExtension(name)} {DateTime.Now:HH-mm-ss-fff}{Path.GetExtension(name)}");
        }
        MoveTo(path, target);
        return new Trashed(path, target, null);
    }

    /// <summary>Moves an item the Trash took back where it was, renamed as it is, its folder made again where it is gone,
    /// and lets go of the record the Trash kept of it. An IOException or UnauthorizedAccessException says why not, nothing
    /// moved: something is there now, or the item is no longer in the Trash.</summary>
    public static void PutBack(Trashed trashed)
    {
        string name = Path.GetFileName(trashed.From);
        if (Stands(trashed.From))
        {
            throw new IOException($"{name} is already there");
        }
        if (!Stands(trashed.At))
        {
            throw new IOException($"{name} is no longer in the Trash");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(trashed.From)!);
        MoveTo(trashed.At, trashed.From);
        if (trashed.Record is not { } record)
        {
            return;
        }
        try
        {
            File.Delete(record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The item is back: a record left names only what the Trash no longer holds.
            IdeLog.Write(IdeLog.Ide, $"The Trash's record of {trashed.From} could not be deleted, {record}: {error.Message}");
        }
    }

    /// <summary>Whether an item is in the Trash where it went.</summary>
    public static bool Holds(Trashed trashed) => Stands(trashed.At);

    /// <summary>The Recycle Bin, through the shell's own delete, which says nothing of where it put the item: that is found
    /// among the records it made meanwhile (<see cref="RecycleBin.Find"/>).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Trashed? MoveToRecycleBin(string path)
    {
        string full = Path.GetFullPath(path);
        HashSet<string> before = RecycleBin.Records(full);
        if (Directory.Exists(full))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        return RecycleBin.Find(full, before) is { } found ? new Trashed(path, found.Item, found.Record) : null;
    }

    /// <summary>Foundation, where NSFileManager is: the runtime's own native library loads it today, which nothing promises.</summary>
    private static readonly Lazy<nint> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"));

    /// <summary>The Trash as Finder fills it, through NSFileManager: it records where the item was, so Finder's Put Back
    /// returns it, and an item on another volume goes to that volume's own Trash rather than across, under the name it
    /// answers. What the system refuses is an IOException in its own words.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static Trashed? MoveToMacTrash(string path)
    {
        _ = Foundation.Value;
        // Its own pool, so what the calls autorelease goes now on any thread, with or without AppKit's run loop.
        nint pool = MacObjC.Send(MacObjC.Class("NSAutoreleasePool"), MacObjC.Selector("new"));
        try
        {
            nint named = MacObjC.Send(MacObjC.Class("NSString"), MacObjC.Selector("stringWithUTF8String:"), path);
            nint url = MacObjC.Send(MacObjC.Class("NSURL"), MacObjC.Selector("fileURLWithPath:"), named);
            nint manager = MacObjC.Send(MacObjC.Class("NSFileManager"), MacObjC.Selector("defaultManager"));
            nint went = 0;
            nint error = 0;
            if (!MacObjC.Send(manager, MacObjC.Selector("trashItemAtURL:resultingItemURL:error:"), url, ref went, ref error))
            {
                string? said = error == 0 ? null : Utf8(MacObjC.Send(error, MacObjC.Selector("localizedDescription")));
                throw new IOException(said ?? $"The Trash refused {Path.GetFileName(path)}");
            }
            return went != 0 && Utf8(MacObjC.Send(went, MacObjC.Selector("path"))) is { Length: > 0 } at ? new Trashed(path, at, null) : null;
        }
        finally
        {
            MacObjC.Send(pool, MacObjC.Selector("drain"));
        }
    }

    /// <summary>An NSString's text, copied out before its pool drains.</summary>
    private static string? Utf8(nint text) => text == 0 ? null : Marshal.PtrToStringUTF8(MacObjC.Send(text, MacObjC.Selector("UTF8String")));

    /// <summary>The freedesktop.org Trash in the person's data folder (~/.local/share/Trash), as Linux's file managers
    /// keep it: the item in files/ under a name of its own, beside a record in info/ of where it was and when it went,
    /// from which they put it back. A folder on another file system is refused, as a move across them cannot be made.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static Trashed MoveToFreedesktopTrash(string path)
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        string trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trash");
        Directory.CreateDirectory(trash, Private);
        string files = Directory.CreateDirectory(Path.Combine(trash, "files"), Private).FullName;
        string info = Directory.CreateDirectory(Path.Combine(trash, "info"), Private).FullName;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string name = Path.GetFileName(full);
        string where = string.Join('/', full.Split('/').Select(Uri.EscapeDataString));
        for (int copy = 1; ; copy++)
        {
            string kept = copy == 1 ? name : $"{name}.{copy}";
            string target = Path.Combine(files, kept);
            string record = Path.Combine(info, kept + ".trashinfo");
            if (Path.Exists(target))
            {
                continue;
            }
            // Made only when no record has the name, so two programs trashing at once never share one.
            try
            {
                using FileStream claimed = new(record, FileMode.CreateNew, FileAccess.Write);
                using StreamWriter writer = new(claimed);
                writer.Write($"[Trash Info]\nPath={where}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
            }
            catch (IOException) when (File.Exists(record))
            {
                continue;
            }
            try
            {
                MoveTo(full, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                File.Delete(record);
                throw;
            }
            return new Trashed(path, target, record);
        }
    }

    /// <summary>Renames an item, a link as itself, never what it leads to.</summary>
    private static void MoveTo(string path, string target)
    {
        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }
    }

    /// <summary>Whether an item is there, a link to nothing too.</summary>
    private static bool Stands(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
}

/// <summary>An item the Trash took: where it was, where it is in the Trash, and the record the Trash keeps of it apart
/// from it (freedesktop.org's info file, the Recycle Bin's $I file), null where there is none.</summary>
public sealed record Trashed(string From, string At, string? Record);

using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Passenger.Core;

/// <summary>A path as the file system itself names it: links and junctions resolved, each name in the case it has on
/// disk, and on Windows its long name for an 8.3 one (C:\Users\RUNNER~1). Git, MSBuild and Roslyn report paths so (macOS's
/// /var is /private/var), so every path the IDE compares with another goes through <see cref="Canonical"/> first.</summary>
public static partial class PathNames
{
    /// <summary>FILE_FLAG_BACKUP_SEMANTICS, without which a folder cannot be opened.</summary>
    private const uint BackupSemantics = 0x02000000;

    /// <summary>How two paths compare: ignoring case where the file system does, on Windows and macOS. A canonical path
    /// has the disk's case already; a name not made yet keeps the case it was given.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary><see cref="Comparison"/> as a set's or a dictionary's: every collection keyed by paths (or by names in one
    /// folder) takes this one.</summary>
    public static StringComparer Comparer { get; } = StringComparer.FromComparison(Comparison);

    /// <summary>The path as the file system names it. Of a path that is not there (yet, or any more), the part that is
    /// there is resolved and the names below it are kept as given.</summary>
    public static string Canonical(string path)
    {
        string full = Path.GetFullPath(path);
        // A device (a pipe, a console) is no file to resolve; opening one to ask would connect to it.
        if (OperatingSystem.IsWindows() && full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return full;
        }
        for (string? at = full; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Resolved(at) is { } resolved)
            {
                return at.Length == full.Length ? resolved : Path.Join(resolved, full.AsSpan(at.Length).TrimStart(Separators));
            }
        }
        return full;
    }

    /// <summary>Whether two paths name the same place, compared as the file system compares names.</summary>
    public static bool Same(string path, string other) => string.Equals(path, other, Comparison);

    /// <summary>Whether a path lies inside a folder, at any depth: the folder's name and then a separator begin it, so
    /// /x/app2/Foo.cs is not under /x/app. The folder itself is not under itself.</summary>
    public static bool IsUnder(string path, string folder) => IsUnder(path.AsSpan(), folder);

    /// <inheritdoc cref="IsUnder(string, string)"/>
    public static bool IsUnder(ReadOnlySpan<char> path, string folder)
    {
        ReadOnlySpan<char> top = folder.AsSpan().TrimEnd(Separators);
        return path.Length > top.Length + 1 && Separators.Contains(path[top.Length]) && path.StartsWith(top, Comparison);
    }

    /// <summary>The names from a folder down to a path under it ("src/App/App.cs"), as the path spells them; empty for the
    /// folder itself or a path outside it.</summary>
    public static ReadOnlySpan<char> Below(ReadOnlySpan<char> path, string folder) =>
        IsUnder(path, folder) ? path[(folder.AsSpan().TrimEnd(Separators).Length + 1)..] : [];

    /// <summary>Whether a path is a file or folder, or under it.</summary>
    public static bool Within(string path, string place) => Same(path, place) || IsUnder(path, place);

    /// <summary>Where a path is once a file or folder moved: the new place when it was that or under it, else itself.</summary>
    public static string Moved(string path, string from, string to) => Within(path, from) ? to + path[from.Length..] : path;

    /// <summary>A path as the IDE shows it to a person: from the open folder when it is inside it ("src/App/App.cs"), the
    /// folder itself by its name, and anywhere else (or with no folder open) whole, which says where it is.</summary>
    public static string Shown(string path, string? folder)
    {
        if (folder is not { Length: > 0 })
        {
            return path;
        }
        if (IsUnder(path, folder))
        {
            return Path.GetRelativePath(folder, path);
        }
        ReadOnlySpan<char> top = folder.AsSpan().TrimEnd(Separators);
        return path.AsSpan().TrimEnd(Separators).Equals(top, Comparison) ? Path.GetFileName(top).ToString() : path;
    }

    /// <summary>A folder and the folders under it that <paramref name="enters"/> lets the walk into, nearest first,
    /// each once by its canonical path whatever links reach it (named as first reached), and none a link goes up to
    /// (the first folder or one above it), which would have the walk go round the tree for good. A folder that cannot
    /// be read is passed over.</summary>
    public static IEnumerable<string> FoldersUnder(string start, Func<string, bool> enters) =>
        Walk(start, (sub, _) => enters(sub), inside: false, named: null).Select(f => f.At);

    /// <summary>The files in a folder and in the folders under it that <paramref name="enters"/> lets the walk into (asked of
    /// each by its canonical path), folder by folder, nearest first, all by their canonical paths: each folder once
    /// whatever links reach it and none a link leads to outside the first, so no walk loops or leaves it; the system's own
    /// files (<see cref="IsSystemFile"/>) passed over.</summary>
    public static IEnumerable<(string Folder, IReadOnlyList<string> Files)> FilesUnder(string start, Func<string, bool> enters, FileNamed? named = null) =>
        Walk(start, (_, canonical) => enters(canonical), inside: true, named ?? Joined).Select(f => (f.Canonical, f.Files));

    /// <summary>How a walk names a file of a folder it reached (by the folder's canonical path): its name after a part the
    /// folder's files share, so they sort as their names do; null passes the file over.</summary>
    public delegate string? FileNamed(string folder, ReadOnlySpan<char> name);

    private static string Joined(string folder, ReadOnlySpan<char> name) => Path.Join(folder, name);

    /// <summary>Whether a file is the system's own record of its folder (Finder's .DS_Store, Explorer's Thumbs.db), which no
    /// walk or listing of a folder shows and no project means to hold.</summary>
    public static bool IsSystemFile(string name) => IsSystemFile(name.AsSpan());

    /// <inheritdoc cref="IsSystemFile(string)"/>
    public static bool IsSystemFile(ReadOnlySpan<char> name)
    {
        foreach (string system in SystemFiles)
        {
            if (name.Equals(system, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] SystemFiles = [".DS_Store", "Thumbs.db"];

    /// <summary>How a walk lists a folder: every entry, hidden and system ones too.</summary>
    private static readonly EnumerationOptions Listing = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    /// <summary>The walk under <see cref="FoldersUnder"/> and <see cref="FilesUnder"/>, breadth first, each folder's folders
    /// and files in the order of their names, read from the system's entries without an object for each; files only where
    /// <paramref name="named"/> names them, and <paramref name="inside"/> enters no folder outside the first.</summary>
    private static IEnumerable<(string At, string Canonical, IReadOnlyList<string> Files)> Walk(string start, Func<string, string, bool> enters, bool inside, FileNamed? named)
    {
        string root = Canonical(start);
        HashSet<string> walked = new(Comparer) { root };
        Queue<(string At, string Canonical)> waiting = new([(start, root)]);
        List<(string At, bool Link)> subs = [];
        while (waiting.TryDequeue(out (string At, string Canonical) folder))
        {
            List<string> found = [];
            subs.Clear();
            try
            {
                foreach ((string? path, bool isFolder, bool link) in Entries(folder.At, folder.Canonical, named))
                {
                    if (isFolder)
                    {
                        subs.Add((path!, link));
                    }
                    else if (path is not null)
                    {
                        found.Add(path);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or shut: it holds nothing to walk.
                found.Clear();
                subs.Clear();
            }
            found.Sort(StringComparer.Ordinal);
            subs.Sort(static (a, b) => string.CompareOrdinal(a.At, b.At));
            foreach ((string at, bool link) in subs)
            {
                // Only a link (or another reparse point) is asked where it leads: any other folder is its name in its parent.
                string canonical = link ? Canonical(at) : string.Equals(folder.At, folder.Canonical, StringComparison.Ordinal) ? at : Path.Join(folder.Canonical, Path.GetFileName(at.AsSpan()));
                if ((inside ? IsUnder(canonical, root) : !Within(root, canonical)) && Enters(at, canonical) && walked.Add(canonical))
                {
                    waiting.Enqueue((at, canonical));
                }
            }
            yield return (folder.At, folder.Canonical, found);
        }

        bool Enters(string sub, string canonical)
        {
            try
            {
                return enters(sub, canonical);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>A folder's entries: each folder by its path and whether it is a link, each file as <paramref name="named"/>
    /// names it (null for one passed over, the system's own files among them); no files without it.</summary>
    private static FileSystemEnumerable<(string? Path, bool Folder, bool Link)> Entries(string at, string canonical, FileNamed? named) =>
        new(at, (ref FileSystemEntry entry) => entry.IsDirectory
            ? (entry.ToFullPath(), true, (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            : (named is null || IsSystemFile(entry.FileName) ? null : named(canonical, entry.FileName), false, false), Listing)
        {
            ShouldIncludePredicate = named is null ? static (ref FileSystemEntry entry) => entry.IsDirectory : null,
        };

    /// <summary>The characters that part a path's names: both slashes on Windows.</summary>
    public static ReadOnlySpan<char> Separators => OperatingSystem.IsWindows() ? @"\/" : "/";

    /// <summary>An existing file or folder's name as the system resolves it; null for one that is not there.</summary>
    private static string? Resolved(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return FinalPath(path);
        }
        IntPtr resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    /// <summary>Windows' own name for a file or folder, asked of a handle opened for nothing but that (any other program
    /// may still read, write or delete it meanwhile). The name comes as \\?\C:\... or \\?\UNC\server\share\..., which
    /// .NET and every tool spell without the prefix.</summary>
    private static unsafe string? FinalPath(string path)
    {
        // Past MAX_PATH a name opens only with the prefix that turns Win32's parsing of it off.
        string named = path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        using SafeFileHandle handle = CreateFile(named, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }
        char[] buffer = new char[path.Length + 64];
        while (true)
        {
            uint length;
            fixed (char* into = buffer)
            {
                length = GetFinalPathNameByHandle(handle, into, (uint)buffer.Length, 0);
            }
            if (length == 0)
            {
                return null;
            }
            if (length >= buffer.Length)
            {
                // Too small: the length asked for counts the terminating null.
                buffer = new char[length];
                continue;
            }
            string final = new(buffer, 0, (int)length);
            return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
                : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
                : final;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint length, uint flags);

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);
}

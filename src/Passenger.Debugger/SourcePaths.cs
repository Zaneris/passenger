using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>How well a breakpoint's source path names a document a PDB records. The same file is the best match, by the
/// name the file system gives it (macOS's /var is /private/var, Windows' C:\Users\RUNNER~1 a long name). A document
/// recorded under another root (a deterministic build's "/_/", a build on another machine) is not on this disk, and
/// matches by the folders and file name it shares from the end, at least the file and its folder. A document that is on
/// this disk as another file never matches while the breakpoint's file is on this disk too, so a same-named file in
/// another folder or project does not take a breakpoint; a breakpoint's file that is not here (the IDE's, this adapter
/// in a container) matches a document that is (one built in the image) by what they share from the end. Paths compare as
/// their folders tell letter case (<see cref="PathNames.Comparer"/>), a Windows path (C:\…, \\server\…) read on another
/// system ignoring it, as Windows names files.</summary>
public static class SourcePaths
{
    public const int Same = int.MaxValue;

    /// <summary>How many files' canonical names are kept before all are let go.</summary>
    private const int Kept = 10_000;

    /// <summary>The canonical names of files found, by the name asked. A name with no file is not kept, so a file written
    /// since its first look (a generated file, a checkout) is found by its canonical name.</summary>
    private static readonly ConcurrentDictionary<string, string> Canonicals = new();

    public static int Match(string requested, string document) => Match(requested, document, PathNames.Comparer, Path.Exists);

    /// <summary>The match, two paths compared by <paramref name="paths"/>, and whether one is on this disk asked of
    /// <paramref name="exists"/>.</summary>
    internal static int Match(string requested, string document, StringComparer paths, Func<string, bool> exists)
    {
        if (!OperatingSystem.IsWindows() && (IsWindows(requested) || IsWindows(document)))
        {
            paths = StringComparer.OrdinalIgnoreCase;
        }
        string want = Normalize(requested);
        string have = Normalize(document);
        if (paths.Equals(want, have))
        {
            return Same;
        }
        if (Canonical(have) is { } real && Canonical(want) is { } wanted)
        {
            return paths.Equals(wanted, real) ? Same : 0;
        }
        string[] had = have.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int shared = 0;
        int end = want.Length;
        while (shared < had.Length && (end = want.AsSpan(0, end).TrimEnd('/').Length) > 0)
        {
            int start = want.LastIndexOf('/', end - 1) + 1;
            if (!Alike(want[..start], want[start..end], had[^(shared + 1)], paths, exists))
            {
                break;
            }
            shared++;
            end = start;
        }
        return shared >= 2 ? shared : 0;
    }

    /// <summary>Whether a name of the breakpoint's path, in <paramref name="folder"/>, names the document's: the same, or
    /// alike but for case where the folder ignores case or holds no file of the document's spelling (a PDB built where case
    /// was ignored).</summary>
    private static bool Alike(string folder, string name, string other, StringComparer paths, Func<string, bool> exists) =>
        string.Equals(name, other, StringComparison.Ordinal)
        || string.Equals(name, other, StringComparison.OrdinalIgnoreCase) && (paths.Equals(folder + name, folder + other) || !exists(folder + other));

    /// <summary>Whether a breakpoint's file is on this disk, so matched by its canonical name: one not here yet (a generated
    /// file, a branch checked out later) is matched so once it is written.</summary>
    internal static bool OnDisk(string requested) => Canonical(Normalize(requested)) is not null;

    /// <summary>A path a Windows machine gave, a drive's (C:\ or C:/) or a share's (\\server\share), on any system.</summary>
    private static bool IsWindows(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        string slashed = path.Replace('\\', '/');
        // "folder/../Program.cs" names Program.cs; a rooted path resolves its dots.
        return Path.IsPathRooted(slashed) && !slashed.StartsWith("/_/", StringComparison.Ordinal) ? Path.GetFullPath(slashed).Replace('\\', '/') : slashed;
    }

    /// <summary>A file on this disk as the file system names it, as the IDE names the breakpoints' paths it sends
    /// (<see cref="PathNames.Canonical"/>); null when there is no such file.</summary>
    private static string? Canonical(string path)
    {
        if (Canonicals.TryGetValue(path, out string? known))
        {
            return known;
        }
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return null;
        }
        if (Canonicals.Count >= Kept)
        {
            Canonicals.Clear();
        }
        return Canonicals[path] = PathNames.Canonical(path).Replace('\\', '/');
    }
}

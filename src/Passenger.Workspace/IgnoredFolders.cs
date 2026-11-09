using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>The folders passed over wherever the open folder is walked (<see cref="FolderWalk"/>) or watched, and dimmed
/// in the Explorer: version control's own stores, build output, dependencies and other IDEs' state. A project's build
/// output and dependencies are read as their folder reads names, as its own folders are (<see cref="ProjectFolders.Passed"/>);
/// every other name only as written, so a folder of the person's named Artifacts or Packages is their code. Go to File and
/// Find in Files never search them, a folder with no solution never takes its projects from them, and the folder watcher
/// never hears of them (on Linux never watches them) but for a restore's assets in obj, kept apart, so what the index lists
/// is what the watcher keeps current.</summary>
public static class IgnoredFolders
{
    /// <summary>NuGet's restore folder for packages.config, and as often a JavaScript monorepo's source (packages/*/package.json),
    /// so passed over only when it holds what a restore leaves (<see cref="IsRestoreFolder"/>).</summary>
    public const string Packages = "packages";

    /// <summary>How many of a packages folder's folders, and of each one's, are looked in: every folder of a restore's is a
    /// package, so the first few tell, and a monorepo of hundreds is not read whole each time its folder changes.</summary>
    private const int Looked = 4;

    /// <summary>How long after a packages folder last changed its answer is found anew each time, not kept: a restore makes
    /// a package's folder and then writes its .nupkg in it, which changes that folder's time, not the packages folder's.</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(2);

    /// <summary>Godot's own cache and build output beside its project.godot (imports, the editor's state, Godot.NET.Sdk's
    /// <c>mono/temp</c> bin and obj).</summary>
    public const string GodotCache = ".godot";

    /// <summary>The test runner GdUnit4's adapter writes into a Godot project as its tests run, build output to the person,
    /// whose files Git's view leaves out too while git leaves them unversioned (<see cref="InToolOutput"/>).</summary>
    public const string GdUnit4Runner = "gdunit4_testadapter_v5";

    /// <summary>The names passed over as written, beside a project's build output and dependencies.</summary>
    private static readonly HashSet<string> Written = [".git", ".hg", ".svn", "artifacts", ".vs", ".idea", "TestResults", GodotCache, GdUnit4Runner];

    /// <summary><see cref="Written"/> asked of a name inside a path, which takes no string of it.</summary>
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> WrittenIn = Written.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The names passed over wherever they are, whatever they hold: a project's build output and dependencies
    /// (<see cref="ProjectFolders.Passed"/>) among them.</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>([.. ProjectFolders.Passed.Names, .. Written], StringComparer.Ordinal);

    /// <summary>Whether a file lies in a folder a test framework writes into the person's project as its own output
    /// (<see cref="GdUnit4Runner"/>), which no one means to commit. Its folders are read in place: every file of a status
    /// is asked.</summary>
    public static bool InToolOutput(string path)
    {
        for (ReadOnlySpan<char> folder = Path.GetDirectoryName(path.AsSpan()); !folder.IsEmpty; folder = Path.GetDirectoryName(folder))
        {
            if (Path.GetFileName(folder).SequenceEqual(GdUnit4Runner))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>What each packages folder was found to be, as of its last change: a check costs a look at its time, not a
    /// read, as each of the watcher's changes in a monorepo's packages is checked.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Written, bool Restored)> Found = new(PathNames.Comparer);

    /// <summary>Whether a folder, by its path, is passed over: one of <see cref="Names"/>, or a packages folder NuGet
    /// restored into.</summary>
    public static bool Is(string folder)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(folder);
        string parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        ReadOnlySpan<char> name = Path.GetFileName(trimmed.AsSpan());
        return ProjectFolders.Passed.Holds(name, parent, []) || WrittenIn.Contains(name) || name.SequenceEqual(Packages) && IsRestoreFolder(trimmed);
    }

    /// <summary>Whether any of the names along a path from <paramref name="root"/> ("src/obj/Debug") is passed over, each
    /// read in place.</summary>
    public static bool AnyIn(string root, ReadOnlySpan<char> relative)
    {
        for (int start = 0; start <= relative.Length;)
        {
            int end = relative[start..].IndexOfAny(PathNames.Separators) is >= 0 and int at ? start + at : relative.Length;
            ReadOnlySpan<char> name = relative[start..end];
            ReadOnlySpan<char> above = start == 0 ? [] : relative[..(start - 1)];
            if (ProjectFolders.Passed.Holds(name, root, above) || WrittenIn.Contains(name) || name.SequenceEqual(Packages) && IsRestoreFolder(Path.Join(root, relative[..end])))
            {
                return true;
            }
            start = end + 1;
        }
        return false;
    }

    /// <summary>Whether any of the names along a path from a folder (<see cref="AnyIn"/>'s) is hidden, beginning with a dot.</summary>
    public static bool AnyHidden(ReadOnlySpan<char> relative)
    {
        for (int start = 0; start < relative.Length;)
        {
            if (relative[start] == '.')
            {
                return true;
            }
            int at = relative[start..].IndexOfAny(PathNames.Separators);
            start = at < 0 ? relative.Length : start + at + 1;
        }
        return false;
    }

    /// <summary>Whether a packages folder holds what NuGet's restore leaves, as no folder of source does: packages.config's
    /// layout (Id.Version/Id.Version.nupkg), the global packages folder's when a repository sets its folder there
    /// (id/version/.nupkg.metadata), or NuGet 2's repositories.config. One empty, or of package.json packages, is code.</summary>
    private static bool IsRestoreFolder(string folder)
    {
        try
        {
            DateTime written = Directory.GetLastWriteTimeUtc(folder);
            if (Found.TryGetValue(folder, out (DateTime Written, bool Restored) seen) && seen.Written == written)
            {
                return seen.Restored;
            }
            bool restored = HoldsRestore(folder);
            if (restored || DateTime.UtcNow - written > Settling)
            {
                Found[folder] = (written, restored);
            }
            return restored;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Gone or shut: nothing in it is walked either way.
            return false;
        }
    }

    private static bool HoldsRestore(string folder)
    {
        if (File.Exists(Path.Join(folder, "repositories.config")))
        {
            return true;
        }
        foreach (DirectoryInfo package in new DirectoryInfo(folder).EnumerateDirectories().Take(Looked))
        {
            if (package.EnumerateFiles("*.nupkg").Any()
                || package.EnumerateDirectories().Take(Looked).Any(version => File.Exists(Path.Join(version.FullName, ".nupkg.metadata"))))
            {
                return true;
            }
        }
        return false;
    }
}

using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>The folders passed over wherever the open folder is walked (<see cref="FolderWalk"/>) or watched, and dimmed
/// in the Explorer: version control's own stores, build output, dependencies and other IDEs' state. Go to File and Find in
/// Files never search them, a folder with no solution never takes its projects from them, and the folder watcher never
/// hears of them (on Linux never watches them) but for a restore's assets in obj, kept apart, so what the index lists is
/// what the watcher keeps current.</summary>
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

    /// <summary>The names passed over wherever they are, whatever they hold.</summary>
    public static IReadOnlySet<string> Names { get; } =
        new HashSet<string>([".git", ".hg", ".svn", "bin", "obj", "artifacts", "node_modules", ".vs", ".idea", "TestResults"]);

    /// <summary>What each packages folder was found to be, as of its last change: a check costs a look at its time, not a
    /// read, as each of the watcher's changes in a monorepo's packages is checked.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Written, bool Restored)> Found = new(PathNames.Comparer);

    /// <summary>Whether a folder, by its path, is passed over: one of <see cref="Names"/>, or a packages folder NuGet
    /// restored into.</summary>
    public static bool Is(string folder)
    {
        string name = Path.GetFileName(folder);
        return Names.Contains(name) || name == Packages && IsRestoreFolder(folder);
    }

    /// <summary>Whether any of the folders named is passed over, the first a folder in <paramref name="root"/>, each next
    /// one in the one before.</summary>
    public static bool AnyOf(string root, ReadOnlySpan<string> names)
    {
        for (int at = 0; at < names.Length; at++)
        {
            if (Names.Contains(names[at]) || names[at] == Packages && IsRestoreFolder(Path.Join(root, Path.Join(names[..(at + 1)]))))
            {
                return true;
            }
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

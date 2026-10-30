namespace Passenger.Core;

/// <summary>Which folders under a project's are its own: those its SDK's wildcards enter, passing over its build output
/// (bin, obj), its dependencies (node_modules) and hidden folders, and of those, for what the project itself holds, none
/// holding a project of its own. A name differing from bin, obj or node_modules only in letter case is one of them where
/// its folder ignores case, as the build then writes into it. Framework-free, since the project host compiles it in.</summary>
public static class ProjectFolders
{
    /// <summary>The names of a project's build output and dependencies, passed over wherever they are under it.</summary>
    public static IReadOnlyList<string> Passed { get; } = ["bin", "obj", "node_modules"];

    /// <summary>Whether a project's wildcards enter a folder under its own: not its build output, its dependencies or a
    /// hidden folder.</summary>
    public static bool Enters(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (name.StartsWith('.'))
        {
            return false;
        }
        foreach (string passed in Passed)
        {
            if (name == passed)
            {
                return false;
            }
            if (name.Equals(passed, StringComparison.OrdinalIgnoreCase))
            {
                return !PathNames.CaseIgnoredIn(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? folder);
            }
        }
        return true;
    }

    /// <summary>Whether a folder under a project's is the project's own: one its wildcards enter (<see cref="Enters"/>)
    /// that holds no project of its own.</summary>
    public static bool IsOwn(string folder) => Enters(folder) && !HoldsProject(folder);

    /// <summary>A project's own folders: its folder and every one under it that is its own (<see cref="IsOwn"/>), through
    /// <see cref="PathNames.FoldersUnder"/>.</summary>
    public static IEnumerable<string> Under(string projectFolder) => PathNames.FoldersUnder(projectFolder, IsOwn);

    /// <summary>Whether a file, there now or not, lies in a project's own folders: under its folder, each folder between
    /// its own (<see cref="IsOwn"/>).</summary>
    public static bool Holds(string projectFolder, string path)
    {
        if (!PathNames.Within(path, projectFolder))
        {
            return false;
        }
        string top = Path.TrimEndingDirectorySeparator(projectFolder);
        for (string? at = Path.GetDirectoryName(path); at is not null && at.Length > top.Length; at = Path.GetDirectoryName(at))
        {
            if (!IsOwn(at))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a folder holds a project file (<c>*.csproj</c>, <c>*.fsproj</c> and every other <c>*.*proj</c>);
    /// false for one not there or that cannot be read.</summary>
    public static bool HoldsProject(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.*proj").Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

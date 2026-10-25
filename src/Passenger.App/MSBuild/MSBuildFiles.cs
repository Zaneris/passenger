using Passenger.Core;

namespace Passenger.App.MSBuild;

/// <summary>Which files are MSBuild's, and which of them are installed rather than a project's own: those under the .NET
/// install and the package folders the project host's evaluations name, which an editor opens read-only. The folders
/// are the machine's, the same for every window, so the process keeps them.</summary>
public static class MSBuildFiles
{
    private static readonly HashSet<string> Installed = new(StringComparer.Ordinal);

    /// <summary>Whether a path is an MSBuild file the code help reads: a project, or a .props or .targets.</summary>
    public static bool Is(string path) => Languages.Language.ForPath(path) == Languages.Language.MSBuild;

    /// <summary>Whether a path is a project file, evaluated as itself.</summary>
    public static bool IsProject(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".proj";

    /// <summary>The folders an evaluation named as installed, each made canonical.</summary>
    public static void Learn(IEnumerable<string> folders)
    {
        foreach (string folder in folders)
        {
            string canonical = Path.TrimEndingDirectorySeparator(PathNames.Canonical(folder));
            lock (Installed)
            {
                Installed.Add(canonical);
            }
        }
    }

    /// <summary>Whether a file (a canonical path) is one an install put there: the SDK's targets, a package's build files.</summary>
    public static bool IsInstalled(string path)
    {
        lock (Installed)
        {
            return Installed.Count > 0 && Is(path) && Installed.Any(folder => PathNames.IsUnder(path, folder));
        }
    }
}

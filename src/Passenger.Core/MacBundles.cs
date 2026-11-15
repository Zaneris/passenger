namespace Passenger.Core;

/// <summary>macOS's app bundles, read from paths alone: a bundle is a folder whose name ends in .app (in any letter case),
/// its programs in its Contents/MacOS folder and everything it holds under Contents, which its seal covers.</summary>
public static class MacBundles
{
    /// <summary>Whether a folder is named as a bundle is: its name ends in .app.</summary>
    public static bool Named(string folder) => Path.TrimEndingDirectorySeparator(folder).EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bundle a file is a program of (<c>X.app/Contents/MacOS/file</c>); null for any other, or none.</summary>
    public static string? Of(string? program) =>
        Path.GetDirectoryName(program) is { } programs && Path.GetFileName(programs) == "MacOS"
        && Path.GetDirectoryName(programs) is { } contents && Path.GetFileName(contents) == "Contents"
        && Path.GetDirectoryName(contents) is { } bundle && Named(bundle)
            ? bundle : null;

    /// <summary>Whether a path lies in a bundle's Contents (<c>X.app/Contents/…</c>), or is that folder, where a file added
    /// breaks the bundle's seal.</summary>
    public static bool Holds(string path)
    {
        for (string? at = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
        {
            if (Path.GetFileName(at) == "Contents" && Path.GetDirectoryName(at) is { } bundle && Named(bundle))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The folder a bundle keeps its programs in.</summary>
    public static string Programs(string bundle) => Path.Combine(bundle, "Contents", "MacOS");
}

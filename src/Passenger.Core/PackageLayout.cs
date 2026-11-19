namespace Passenger.Core;

/// <summary>NuGet's layout of a package folder (the global packages folder, a fallback folder): each package version in
/// its id's and version's folders below it, holding .nupkg.metadata or its nuspec once NuGet laid it out, as no folder of
/// source does, so a project naming an ancestor of its own source its package folder keeps its source its own.
/// Framework-free, since the project host compiles it in.</summary>
public static class PackageLayout
{
    /// <summary>The folder of the package version a path lies in below a package folder, with its id's and version's folder
    /// names: of a file or folder below that folder, or with <paramref name="orItself"/> that folder itself too; null where
    /// the path is in no version's folder NuGet laid out.</summary>
    public static (string Folder, string Id, string Version)? VersionFolder(string path, string packageFolder, bool orItself = false)
    {
        ReadOnlySpan<char> below = PathNames.Below(path, packageFolder);
        int id = below.IndexOfAny('/', Path.DirectorySeparatorChar);
        ReadOnlySpan<char> rest = id > 0 ? below[(id + 1)..] : [];
        int version = rest.IndexOfAny('/', Path.DirectorySeparatorChar);
        int length = version > 0 && version + 1 < rest.Length ? version : orItself && version < 0 ? rest.Length : 0;
        if (length == 0)
        {
            return null;
        }
        string name = below[..id].ToString();
        string at = rest[..length].ToString();
        string folder = Path.Combine(packageFolder, name, at);
        return File.Exists(Path.Combine(folder, ".nupkg.metadata")) || File.Exists(Path.Combine(folder, $"{name}.nuspec")) ? (folder, name, at) : null;
    }
}

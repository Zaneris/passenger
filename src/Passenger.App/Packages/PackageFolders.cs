using System.Collections.Concurrent;
using System.Xml;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Packages;

/// <summary>The NuGet package folders the projects' restores resolved their packages from, as each evaluation names them
/// (its root and its fallback folders), whose files an editor opens read-only: a package's file there is every project's
/// copy of that version, which no restore writes again. The folders are the machine's, the same for every window, so the
/// process keeps them.</summary>
public static class PackageFolders
{
    private static readonly ConcurrentDictionary<string, string> Shown = new(PathNames.Comparer);
    private static readonly HashSet<string> Named = new(StringComparer.Ordinal);
    private static readonly Lock Learning = new();
    private static volatile string[] folders = [];

    /// <summary>The package folders a project's evaluation names, each made canonical the first time it is named; whether
    /// any was not known before.</summary>
    public static bool Learn(ProjectModel project) => Learn(new[] { project.Property(Host.CarriedProperties.NuGetPackageRoot) }.OfType<string>()
        .Concat(project.Property(Host.CarriedProperties.NuGetPackageFolders)?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []));

    /// <summary>Package folders as an evaluation names them; whether any was not known before.</summary>
    public static bool Learn(IEnumerable<string> named)
    {
        bool learned = false;
        lock (Learning)
        {
            foreach (string folder in named)
            {
                if (Named.Add(folder) && Path.IsPathFullyQualified(folder)
                    && Path.TrimEndingDirectorySeparator(PathNames.Canonical(folder)) is { } canonical && !folders.Contains(canonical, PathNames.Comparer))
                {
                    folders = [.. folders, canonical];
                    learned = true;
                }
            }
        }
        return learned;
    }

    /// <summary>Whether a file (a canonical path) is a package's, in a package folder below folders NuGet laid out as its id's
    /// and version's: a project may name an ancestor of its own source its package folder, whose other files are no package's.</summary>
    public static bool Holds(string path) => PackageFolder(path) is not null;

    /// <summary>The package a file (a canonical path) is part of, by its id and version as its nuspec gives them ("Kit
    /// 1.0.0"), or as its folders do where that cannot be read; null for a file of no package folder.</summary>
    public static string? PackageOf(string path) => PackageFolder(path) is { } package
        ? Shown.GetOrAdd(package.Folder, _ => Read(package.Folder, package.Id) ?? $"{package.Id} {package.Version}")
        : null;

    /// <summary>The folder of the package version a file lies in, with that id's and version's folder names.</summary>
    private static (string Folder, string Id, string Version)? PackageFolder(string path)
    {
        foreach (string folder in folders)
        {
            ReadOnlySpan<char> below = PathNames.Below(path, folder);
            int id = below.IndexOfAny('/', Path.DirectorySeparatorChar);
            int version = id < 0 ? -1 : below[(id + 1)..].IndexOfAny('/', Path.DirectorySeparatorChar);
            if (id > 0 && version > 0 && id + 1 + version + 1 < below.Length)
            {
                string name = below[..id].ToString();
                string at = below.Slice(id + 1, version).ToString();
                string package = Path.Combine(folder, name, at);
                if (File.Exists(Path.Combine(package, ".nupkg.metadata")) || File.Exists(Path.Combine(package, $"{name}.nuspec")))
                {
                    return (package, name, at);
                }
            }
        }
        return null;
    }

    /// <summary>A package's id and version as its nuspec writes them; null where it cannot be read.</summary>
    private static string? Read(string folder, string id)
    {
        try
        {
            using XmlReader reader = XmlReader.Create(Path.Combine(folder, $"{id}.nuspec"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            string? named = null;
            string? version = null;
            reader.MoveToContent();
            while ((named is null || version is null) && !reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 2 && reader.LocalName is "id" or "version")
                {
                    bool isId = reader.LocalName == "id";
                    string value = reader.ReadElementContentAsString().Trim();
                    (named, version) = isId ? (value, version) : (named, value);
                }
                else
                {
                    reader.Read();
                }
            }
            return named is { Length: > 0 } && version is { Length: > 0 } ? $"{named} {version}" : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }
}

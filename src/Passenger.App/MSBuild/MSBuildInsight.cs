using Passenger.Core;
using Passenger.Host;

namespace Passenger.App.MSBuild;

/// <summary>A place an evaluation names: a file (canonical) and a line and column from 1, as MSBuild counts them.</summary>
public readonly record struct MSBuildPlace(string Path, int Line, int Column);

/// <summary>A project's evaluation as the code help asks it (<see cref="ProjectInsight"/>, from the project host), its
/// properties, item types and targets by name and its places as canonical paths. Immutable, so any thread may ask it.</summary>
public sealed class MSBuildInsight
{
    private readonly Dictionary<string, InsightProperty> properties;
    private readonly Dictionary<string, InsightItemType> itemTypes;
    private readonly Dictionary<string, InsightTarget> targets;
    private readonly string[] paths;
    private readonly HashSet<string> walked;

    public MSBuildInsight(ProjectInsight insight)
    {
        Raw = insight;
        properties = insight.Properties.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        itemTypes = insight.ItemTypes.DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        targets = insight.Targets.DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        paths = [.. insight.Files.Select(f => f.Length == 0 ? f : PathNames.Canonical(f))];
        Project = PathNames.Canonical(insight.Project);
        walked = new HashSet<string>(insight.Folders.Select(f => f.Path), PathNames.Comparer);
    }

    public ProjectInsight Raw { get; }

    /// <summary>The project evaluated, canonical.</summary>
    public string Project { get; }

    public bool Evaluated => Raw.Evaluated;

    public IReadOnlyCollection<InsightProperty> Properties => properties.Values;

    public IReadOnlyCollection<InsightItemType> ItemTypes => itemTypes.Values;

    public IReadOnlyCollection<InsightTarget> Targets => targets.Values;

    public InsightProperty? Property(string name) => properties.GetValueOrDefault(name);

    public InsightItemType? ItemType(string name) => itemTypes.GetValueOrDefault(name);

    public InsightTarget? Target(string name) => targets.GetValueOrDefault(name);

    public string Value(string name) => Property(name)?.Value ?? string.Empty;

    /// <summary>Whether a file or folder come or gone may change what the evaluation's wildcards took in: it lies in a
    /// folder they walked, or above the project (the open folder, named when what changed is not known).</summary>
    public bool Walked(string path) => Path.GetDirectoryName(path) is { } folder && walked.Contains(folder) || PathNames.IsUnder(Project, path);

    public MSBuildPlace Place(InsightPlace place) => new(place.File >= 0 && place.File < paths.Length ? paths[place.File] : string.Empty, place.Line, place.Column);

    /// <summary>The Directory.Packages.props central package management reads, when it is on (<see cref="CentralPackages.PropsOf(string?, string?)"/>).</summary>
    public string? PackagesProps => CentralPackages.PropsOf(Value(CarriedProperties.ManagePackageVersionsCentrally), Value(CarriedProperties.DirectoryPackagesPropsPath));

    /// <summary>The items an element of a file made, by the place of its <c>&lt;</c>.</summary>
    public InsightItem? ItemsAt(string path, int line, int column) =>
        Raw.Items.FirstOrDefault(i => i.Element.Line == line && i.Element.Column == column && PathNames.Same(Place(i.Element).Path, path));

    /// <summary>What an import element of a file brought in, by the place of its <c>&lt;</c>.</summary>
    public IReadOnlyList<string> ImportsAt(string path, int line, int column) =>
        [.. Raw.Imports.Where(i => i.Sdk is null && i.Element.Line == line && i.Element.Column == column && PathNames.Same(Place(i.Element).Path, path))
            .Select(i => PathNames.Canonical(i.Imported))];

    /// <summary>What an SDK named in the project file brought in: its Sdk.props and Sdk.targets.</summary>
    public IReadOnlyList<string> SdkImports(string sdk) =>
        [.. Raw.Imports.Where(i => string.Equals(i.Sdk, sdk, StringComparison.OrdinalIgnoreCase)).Select(i => PathNames.Canonical(i.Imported)).Distinct(StringComparer.Ordinal)];

    /// <summary>The central versions: each PackageVersion's include and version, where central package management is on.</summary>
    public IReadOnlyDictionary<string, (string Version, MSBuildPlace Place)> CentralVersions()
    {
        Dictionary<string, (string, MSBuildPlace)> versions = new(StringComparer.OrdinalIgnoreCase);
        foreach (InsightItem item in Raw.Items.Where(i => i.Type.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (string include in item.Includes)
            {
                versions[include] = (item.Metadata.GetValueOrDefault("Version") ?? string.Empty, Place(item.Element));
            }
        }
        return versions;
    }
}

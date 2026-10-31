using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.MSBuild;

/// <summary>Central package management as a project's evaluation has it on (<see cref="MSBuildInsight.PackagesProps"/>):
/// the Directory.Packages.props that holds its versions, those versions by package, and whether a reference may override
/// one (CentralPackageVersionOverrideEnabled). A project made or given packages there takes its form
/// (<see cref="ProjectEdits.Centralized"/>), each version it pins placed as a package chosen in the editor places its own.</summary>
public sealed record CentralPackages(string Props, IReadOnlyDictionary<string, string> Versions, bool Overrides)
{
    /// <summary>The central package management of a project, its text (when given) read in place of the disk's, so a
    /// project not yet written is evaluated where it is to be; null where it is off, names no Directory.Packages.props, or
    /// the project does not evaluate.</summary>
    public static async Task<CentralPackages?> OfAsync(string project, string? text, CancellationToken cancel = default)
    {
        string? folder = Path.GetDirectoryName(project);
        while (folder is not null && !Directory.Exists(folder))
        {
            folder = Path.GetDirectoryName(folder);
        }
        Dictionary<string, string> texts = text is null ? [] : new(PathNames.Comparer) { [project] = text };
        MSBuildInsight insight;
        try
        {
            ProjectHost host = await ProjectHosts.ForAsync(folder ?? Path.GetDirectoryName(project)!, cancel);
            insight = await BackgroundWork.Run(async () => new MSBuildInsight(await host.InspectAsync(project, new Dictionary<string, string>(), texts, cancel)), cancel);
        }
        catch (ProjectHostFailed failed)
        {
            IdeLog.Write(IdeLog.Ide, $"Whether {Path.GetFileName(project)} manages its packages centrally could not be read: {failed.Reason}");
            return null;
        }
        if (!insight.Evaluated || insight.PackagesProps is not { } props)
        {
            return null;
        }
        return new CentralPackages(props, insight.CentralVersions().ToDictionary(v => v.Key, v => v.Value.Version, StringComparer.OrdinalIgnoreCase),
            !insight.Value("CentralPackageVersionOverrideEnabled").Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Directory.Packages.props a project's central package management reads and a package change writes, as
    /// its evaluation names it; null where central package management is off, or before the project is evaluated.</summary>
    public static string? PropsOf(ProjectModel project) =>
        PropsOf(project.Property(CarriedProperties.ManagePackageVersionsCentrally), project.Property(CarriedProperties.DirectoryPackagesPropsPath));

    /// <summary>The Directory.Packages.props of a project (<see cref="PropsOf(ProjectModel)"/>) that dotnet add package cannot
    /// change, where its evaluation's DirectoryPackagesPropsPath spells it otherwise than its full path (eng/Versions.props
    /// on Windows, a path through ".."): the command looks for the file by that spelling, and fails; else null.</summary>
    public static string? UnchangedByDotnet(ProjectModel project) =>
        PropsOf(project) is { } props && project.Property(CarriedProperties.DirectoryPackagesPropsPath) is { } spelled
            && !string.Equals(Path.GetFullPath(spelled), spelled, StringComparison.Ordinal) ? props : null;

    /// <summary>The file an evaluation's DirectoryPackagesPropsPath names, canonical, where its ManagePackageVersionsCentrally
    /// (<paramref name="managed"/>) is true; else null.</summary>
    public static string? PropsOf(string? managed, string? path) =>
        string.Equals(managed, "true", StringComparison.OrdinalIgnoreCase) && path is { Length: > 0 } ? PathNames.Canonical(path) : null;

    /// <summary>Directory.Packages.props' text with a PackageVersion for each package pinned that it lacks, in turn.</summary>
    public static string Pinned(string text, IEnumerable<(string Id, string Version)> pins)
    {
        foreach ((string id, string version) in pins)
        {
            if (MSBuildAssist.CentralVersion(text, new PackageChoice(id, version)) is { } edit)
            {
                text = string.Concat(text.AsSpan(0, edit.Start), edit.Text, text.AsSpan(edit.Start + edit.Length));
            }
        }
        return text;
    }
}

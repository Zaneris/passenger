using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Host;

namespace Passenger.App.Workspace;

/// <summary>A folder's projects as MSBuild last evaluated them, kept in the folder's place in the store, so the folder
/// opens on them before any is evaluated again: each with what its evaluation read (the project file, its imports
/// outside the SDK and package folders, the folders its wildcards walked, each as it was), so one whose inputs changed
/// since is evaluated again and the rest are not; and the SDK they were evaluated with, a change of which evaluates
/// them all.</summary>
public sealed record ProjectModelCache : IStateDocument<ProjectModelCache>
{
    /// <summary>The folder of the SDK whose MSBuild evaluated them.</summary>
    public string? Sdk { get; init; }

    /// <summary>Each project's evaluation, complete, by its path.</summary>
    public Dictionary<string, EvaluatedProject> Projects { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, ForgetWindowsTargeted, ForgetAll, ForgetUncarried, ForgetUnpublishable, ForgetAll,
        ForgetUnrestored, ForgetUndefined, ForgetUnversioned, ForgetUnserved, ForgetUncentral];

    /// <summary>Every evaluation, kept before they carried the Directory.Packages.props a package change writes.</summary>
    private static void ForgetUncentral(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they carried whether a web SDK builds the project, by which its wwwroot shows
    /// as its web root.</summary>
    private static void ForgetUnserved(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they carried each framework's C# version, which its C# files are parsed with.</summary>
    private static void ForgetUnversioned(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they carried each framework's preprocessor symbols, which its C# files are
    /// parsed with.</summary>
    private static void ForgetUndefined(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they carried where a project's restore and build output are, by which its XAML
    /// finds its packages and its preview its build.</summary>
    private static void ForgetUnrestored(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they carried IsPublishable, by which Publish offers a library that asks for it.</summary>
    private static void ForgetUnpublishable(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they stamped the Directory.Build.props, .targets and Directory.Packages.props
    /// MSBuild looks for above a project and the props and targets a restore writes, which may have come since unseen.</summary>
    private static void ForgetAll(JsonObject document) => document.Remove("projects");

    /// <summary>Every evaluation, kept before they carried the properties a project's container run configurations are found
    /// by (<see cref="CarriedProperties"/>): shown from the cache, such a project would have none until it changed.</summary>
    private static void ForgetUncarried(JsonObject document) => document.Remove("projects");

    /// <summary>The evaluations of projects that target Windows, kept before they said which frameworks they reference
    /// (whether one runs only on Windows), are evaluated again.</summary>
    private static void ForgetWindowsTargeted(JsonObject document)
    {
        if (document["projects"] is not JsonObject projects)
        {
            return;
        }
        foreach (string path in projects.Where(p => p.Value?["targetFrameworks"] is JsonArray frameworks
            && frameworks.Any(f => f?.GetValueKind() == System.Text.Json.JsonValueKind.String && f.GetValue<string>().Contains("-windows", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Key).ToList())
        {
            projects.Remove(path);
        }
    }

    /// <summary>An entry left out that has no path, or is not complete (a multi-targeted project's outer evaluation alone).</summary>
    public ProjectModelCache Clamped() => this with
    {
        Projects = (Projects ?? []).Where(p => p.Value is { Path.Length: > 0, Complete: true }).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
    };
}

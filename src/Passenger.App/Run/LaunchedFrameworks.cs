using NuGet.Frameworks;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Which of a project's frameworks a launch builds, by the rule a project reference goes by, for all that reads what
/// that build made.</summary>
public static class LaunchedFrameworks
{
    /// <summary>Which of a project's several frameworks a launch of <paramref name="launched"/> builds: that one, else the
    /// nearest it can reference, or with no launch's named the first. Null for a project of one, whose restore and build name
    /// none, and where it has none the launch can take.</summary>
    internal static string? Of(IReadOnlyList<string> frameworks, string? launched)
    {
        if (frameworks.Count <= 1)
        {
            return null;
        }
        if (launched is null)
        {
            return frameworks[0];
        }
        return frameworks.FirstOrDefault(f => f.Equals(launched, StringComparison.OrdinalIgnoreCase))
            ?? NuGetFrameworkUtility.GetNearest(frameworks, NuGetFramework.ParseFolder(launched), NuGetFramework.ParseFolder);
    }

    /// <summary>The projects a project references among <paramref name="projects"/>, through others too, each with what a launch
    /// of <paramref name="launched"/> builds of it (<see cref="Of"/>).</summary>
    public static IReadOnlyList<(string Project, string? Framework)> Referenced(ProjectModel project, string? launched, IReadOnlyList<ProjectModel> projects) =>
        [.. project.WithReferenced(projects).Skip(1).Select(p => (p.Path, Of(p.TargetFrameworks, launched)))];
}

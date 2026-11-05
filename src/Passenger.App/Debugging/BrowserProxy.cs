using NuGet.Frameworks;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Debugging;

/// <summary>.NET's browser debug proxy as a project's restore brought it for the framework a launch runs: each framework of a
/// project restores its own version of the runtime's packages, and the proxy goes with the runtime the page loads.</summary>
public static class BrowserProxy
{
    public const string FileName = "BrowserDebugHost.dll";

    /// <summary>The proxy in the first of <paramref name="packages"/> a project's restore resolved for what
    /// <paramref name="launched"/> builds of it, under the folder given; null where none is there.</summary>
    public static string? Of(ProjectModel project, string? launched, IReadOnlyList<string> packages, string folder)
    {
        if (project.Property(Host.CarriedProperties.NuGetPackageRoot) is not { Length: > 0 } root
            || (project.Property(Host.CarriedProperties.ProjectAssetsFile) ?? Passenger.Xaml.Projects.ProjectInputs.FindAssets(project.Path)) is not { } assets)
        {
            return null;
        }
        RestoredVersions restored;
        try
        {
            using FileStream stream = new(assets, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            restored = RestoredVersions.Read(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        string? framework = FrameworkOf(project.TargetFrameworks, launched);
        foreach (string package in packages)
        {
            string? version = restored.Of(framework, package);
            if (version is not null && Path.Combine(root, package.ToLowerInvariant(), version.ToLowerInvariant(), folder, FileName) is var path && File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>Which of a project's several frameworks a launch of <paramref name="launched"/> builds: that one, else the
    /// nearest it can reference, as a project reference takes one, or with no launch's named the first. Null for a project
    /// of one, whose restore names none, and where it has none the launch can take.</summary>
    internal static string? FrameworkOf(IReadOnlyList<string> frameworks, string? launched)
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
}

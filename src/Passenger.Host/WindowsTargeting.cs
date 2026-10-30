using Microsoft.Build.Evaluation;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>Off Windows, a project that references a framework the SDK marks Windows-only (WindowsDesktop's, for WPF and
/// Windows Forms; the Windows SDK's projection) builds only with EnableWindowsTargeting, as NETSDK1100 says: the host
/// evaluates and builds it with that as a global property when the project leaves it unset, so its design-time builds see
/// the framework's reference assemblies. Never written into the project; on Windows, or for a project that references no
/// Windows-only framework, nothing changes.</summary>
internal static class WindowsTargeting
{
    /// <summary>A project loaded with these global properties, loaded again with EnableWindowsTargeting when it needs it here.</summary>
    public static Project Load(Func<Dictionary<string, string>, Project> load, IReadOnlyDictionary<string, string> global, ProjectCollection collection)
    {
        Dictionary<string, string> properties = new(global, StringComparer.OrdinalIgnoreCase);
        Project project = load(properties);
        if (!Needed(project))
        {
            return project;
        }
        collection.UnloadProject(project);
        properties[MSBuildNames.EnableWindowsTargeting] = "true";
        return load(properties);
    }

    /// <summary>Whether the host gave this evaluation EnableWindowsTargeting.</summary>
    public static bool Given(Project project) =>
        project.GlobalProperties.TryGetValue(MSBuildNames.EnableWindowsTargeting, out string? value)
        && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Off Windows, one that references a Windows-only framework and leaves the property unset (in its files and
    /// the environment alike), so its restore and build would fail with NETSDK1100.</summary>
    private static bool Needed(Project project) =>
        !OperatingSystem.IsWindows() && project.GetPropertyValue(MSBuildNames.EnableWindowsTargeting).Length == 0 && WindowsOnly(project);

    /// <summary>Whether a framework it references is one the SDK's KnownFrameworkReference marks IsWindowsOnly.</summary>
    private static bool WindowsOnly(Project project)
    {
        ICollection<ProjectItem> referenced = project.GetItems("FrameworkReference");
        if (referenced.Count == 0)
        {
            return false;
        }
        HashSet<string> windowsOnly = new(project.GetItems("KnownFrameworkReference")
            .Where(k => k.GetMetadataValue("IsWindowsOnly").Equals("true", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.EvaluatedInclude), StringComparer.OrdinalIgnoreCase);
        return referenced.Any(r => windowsOnly.Contains(r.EvaluatedInclude));
    }
}

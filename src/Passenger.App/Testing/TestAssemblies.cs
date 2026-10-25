using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>A test project's assembly for one of its target frameworks, where its build puts it.</summary>
public sealed record TestAssembly(ProjectModel Project, string Framework, string Path);

/// <summary>Where the test projects build their assemblies, one per target framework, asked of the warm project host with
/// the global properties a build of each is given: each project's target frameworks as MSBuild evaluates them (from a
/// property, an import, Directory.Build.props), then, for a project of several, each framework's TargetPath as its inner
/// build sees it (GetTargetFrameworks, GetTargetPath).</summary>
public static class TestAssemblies
{
    /// <summary>The assemblies, built or not, the projects' in their order; a project MSBuild cannot evaluate is logged and
    /// left out.</summary>
    public static async Task<IReadOnlyList<TestAssembly>> ResolveAsync(IReadOnlyList<ProjectModel> projects, string folder, WorkspaceModel? workspace,
        Action<string> log, CancellationToken cancel)
    {
        if (projects.Count == 0)
        {
            return [];
        }
        ProjectHost host;
        try
        {
            host = await ProjectHosts.ForAsync(folder, cancel);
        }
        catch (ProjectHostFailed failed)
        {
            throw new InvalidOperationException($"MSBuild could not say where the test projects build: {failed.Reason}", failed);
        }
        (IReadOnlyList<TestAssembly> Assemblies, string? Problem)[] each = await Task.WhenAll(projects.Select(p => OfAsync(host, p, workspace, cancel)));
        List<string> problems = [.. each.Select(e => e.Problem).OfType<string>()];
        foreach (string problem in problems)
        {
            log(problem);
        }
        List<TestAssembly> found = [.. each.SelectMany(e => e.Assemblies)];
        if (found.Count == 0 && problems.Count > 0)
        {
            throw new InvalidOperationException($"MSBuild could not say where the test projects build: {string.Join("; ", problems)}");
        }
        return found;
    }

    /// <summary>One project's assemblies, a framework's at a time in the order its TargetFrameworks gives them, or why MSBuild
    /// could not say. A project of one framework is asked as its build builds it, with no TargetFramework of the request's.</summary>
    private static async Task<(IReadOnlyList<TestAssembly> Assemblies, string? Problem)> OfAsync(ProjectHost host, ProjectModel project, WorkspaceModel? workspace,
        CancellationToken cancel)
    {
        Dictionary<string, string> globals = ProjectModels.Properties(workspace, project);
        try
        {
            PropertiesAnswer outer = await host.PropertiesAsync(project.Path, globals, ["TargetFramework", "TargetFrameworks", "TargetPath"], cancel);
            if (outer.Problem is { } problem)
            {
                return ([], $"{project.Name} could not be evaluated: {problem}");
            }
            string? single = outer.Values.GetValueOrDefault("TargetFramework");
            string[] frameworks = [.. (outer.Values.GetValueOrDefault("TargetFrameworks") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
            // A TargetFramework of its own, or none to choose among, is a project MSBuild builds once (no cross-targeting).
            if (single is { Length: > 0 } || frameworks.Length == 0)
            {
                return single is { Length: > 0 } && outer.Values.GetValueOrDefault("TargetPath") is { Length: > 0 } path
                    ? ([new TestAssembly(project, single, PathNames.Canonical(path))], null)
                    : ([], null);
            }
            PropertiesAnswer[] inner = await Task.WhenAll(frameworks.Select(framework =>
                host.PropertiesAsync(project.Path, new Dictionary<string, string>(globals, StringComparer.OrdinalIgnoreCase) { ["TargetFramework"] = framework }, ["TargetPath"], cancel)));
            List<TestAssembly> assemblies = [];
            List<string> problems = [];
            for (int i = 0; i < frameworks.Length; i++)
            {
                if (inner[i].Problem is { } failed)
                {
                    problems.Add($"{project.Name} ({frameworks[i]}) could not be evaluated: {failed}");
                }
                else if (inner[i].Values.GetValueOrDefault("TargetPath") is { Length: > 0 } path)
                {
                    assemblies.Add(new TestAssembly(project, frameworks[i], PathNames.Canonical(path)));
                }
            }
            return (assemblies, problems.Count == 0 ? null : string.Join("; ", problems));
        }
        catch (ProjectHostFailed failed)
        {
            return ([], $"{project.Name} could not be evaluated: {failed.Reason}");
        }
    }
}

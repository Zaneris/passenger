using Microsoft.Build.Construction;
using Microsoft.Build.Exceptions;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>How a build of a solution builds its projects, read by the SDK's own MSBuild as `dotnet build` of it reads it:
/// the solution configuration it takes when given none (its default, or what the environment's Configuration and Platform
/// name, which MSBuild takes for properties), and the configuration and platform that configuration maps each project to.
/// A project mapped to x64 builds into bin/x64/..., where an evaluation given neither looks in bin/....</summary>
internal static class Solutions
{
    public static SolutionBuild Run(SolutionParams asked)
    {
        try
        {
            SolutionFile solution = SolutionFile.Parse(asked.Path);
            string configuration = Environment.GetEnvironmentVariable(MSBuildNames.Configuration) is { Length: > 0 } named ? named : solution.GetDefaultConfigurationName();
            string platform = Environment.GetEnvironmentVariable(MSBuildNames.Platform) is { Length: > 0 } given ? given : solution.GetDefaultPlatformName();
            string chosen = $"{configuration}|{platform}";
            List<SolutionBuilt> built = [];
            foreach (ProjectInSolution project in solution.ProjectsInOrder)
            {
                if (project.ProjectType != SolutionProjectType.SolutionFolder
                    && project.ProjectConfigurations.TryGetValue(chosen, out ProjectConfigurationInSolution? its) && its.IncludeInBuild)
                {
                    built.Add(new SolutionBuilt(PathNames.Canonical(project.AbsolutePath), its.ConfigurationName, its.PlatformName));
                }
            }
            return new SolutionBuild(built, null);
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new SolutionBuild([], error.Message);
        }
    }
}

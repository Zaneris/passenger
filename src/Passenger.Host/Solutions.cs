using Microsoft.Build.Construction;
using Microsoft.Build.Exceptions;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>How a build of a solution builds its projects, read by the SDK's own MSBuild as `dotnet build` of it reads it:
/// the solution configurations it declares, the one it takes when given none (its default, or what the environment's
/// Configuration and Platform name, which MSBuild takes for properties), and the configuration and platform the one asked
/// maps each project to, built or not. A project mapped to x64 builds into bin/x64/..., where an evaluation given neither
/// looks in bin/....</summary>
internal static class Solutions
{
    public static SolutionBuild Run(SolutionParams asked)
    {
        try
        {
            SolutionFile solution = SolutionFile.Parse(asked.Path);
            string configuration = asked.Configuration is { Length: > 0 } chosen ? chosen
                : Environment.GetEnvironmentVariable(MSBuildNames.Configuration) is { Length: > 0 } named ? named : solution.GetDefaultConfigurationName();
            string platform = asked.Platform is { Length: > 0 } chosenPlatform ? chosenPlatform
                : Environment.GetEnvironmentVariable(MSBuildNames.Platform) is { Length: > 0 } given ? given : solution.GetDefaultPlatformName();
            string full = $"{configuration}|{platform}";
            List<SolutionBuilt> built = [];
            List<SolutionBuilt> mapped = [];
            foreach (ProjectInSolution project in solution.ProjectsInOrder)
            {
                if (project.ProjectType != SolutionProjectType.SolutionFolder && project.ProjectConfigurations.TryGetValue(full, out ProjectConfigurationInSolution? its))
                {
                    SolutionBuilt each = new(PathNames.Canonical(project.AbsolutePath), its.ConfigurationName, its.PlatformName);
                    mapped.Add(each);
                    if (its.IncludeInBuild)
                    {
                        built.Add(each);
                    }
                }
            }
            return new SolutionBuild(built, null)
            {
                Declared = [.. solution.SolutionConfigurations.Select(c => new SolutionConfiguration(c.ConfigurationName, c.PlatformName))],
                Default = new SolutionConfiguration(solution.GetDefaultConfigurationName(), solution.GetDefaultPlatformName()),
                Taken = new SolutionConfiguration(configuration, platform),
                Mapped = mapped,
            };
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new SolutionBuild([], error.Message);
        }
    }
}

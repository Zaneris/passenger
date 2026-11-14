using Passenger.Core;
using Passenger.Host;

namespace Passenger.Workspace;

/// <summary>A build configuration and platform as a folder offers them: a solution configuration (Release, Any CPU) as its
/// solution names it, or, with no solution, a configuration and platform its projects declare (Release, AnyCPU).</summary>
public sealed record BuildConfiguration(string Configuration, string Platform)
{
    /// <summary>How it is kept and named to an agent: Release|Any CPU.</summary>
    public string Key => $"{Configuration}|{Platform}";

    /// <summary>How it is shown: its configuration alone where every one offered has the same platform, else both.</summary>
    public string Shown(IReadOnlyList<BuildConfiguration> offered) =>
        offered.Select(o => o.Platform).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any() ? $"{Configuration} | {Platform}" : Configuration;

    /// <summary>Whether it is the configuration and platform another names, as MSBuild compares them (in any letter case).</summary>
    public bool Is(BuildConfiguration other) =>
        Configuration.Equals(other.Configuration, StringComparison.OrdinalIgnoreCase) && Platform.Equals(other.Platform, StringComparison.OrdinalIgnoreCase);

    /// <summary>One read back from its <see cref="Key"/>; null for text that names none.</summary>
    public static BuildConfiguration? Of(string? key) =>
        key?.Split('|') is [{ Length: > 0 } configuration, { Length: > 0 } platform] ? new BuildConfiguration(configuration.Trim(), platform.Trim()) : null;
}

/// <summary>What a folder's builds may be given: the configurations and platforms its solution declares, or with none what
/// its projects declare (<see cref="CarriedProperties.Configurations"/>, <see cref="CarriedProperties.Platforms"/>), and the
/// one MSBuild takes when given none, which every build is given nothing for.</summary>
public static class BuildConfigurations
{
    /// <summary>The platform a project builds for unless given another, which its build is never given.</summary>
    public const string AnyCpu = "AnyCPU";

    /// <summary>What projects declare, with no solution: each configuration any of them declares, in the order first
    /// declared, with each platform any declares (Debug and Release, AnyCPU, for projects that declare nothing).</summary>
    public static IReadOnlyList<BuildConfiguration> Declared(IEnumerable<(string? Configurations, string? Platforms)> projects)
    {
        List<string> configurations = [];
        List<string> platforms = [];
        foreach ((string? declared, string? declaredPlatforms) in projects)
        {
            Add(configurations, declared);
            Add(platforms, declaredPlatforms);
        }
        if (configurations.Count == 0)
        {
            configurations.AddRange(["Debug", "Release"]);
        }
        if (platforms.Count == 0)
        {
            platforms.Add(AnyCpu);
        }
        return [.. configurations.SelectMany(configuration => platforms.Select(platform => new BuildConfiguration(configuration, platform)))];

        static void Add(List<string> into, string? list)
        {
            foreach (string name in (list ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!into.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    into.Add(name);
                }
            }
        }
    }

    /// <summary>The one of projects' configurations a build given none takes, as the .NET SDK takes it: Debug and AnyCPU
    /// where offered, else the first offered of each.</summary>
    public static BuildConfiguration? DefaultOf(IReadOnlyList<BuildConfiguration> offered)
    {
        if (offered.Count == 0)
        {
            return null;
        }
        string configuration = offered.FirstOrDefault(o => o.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase))?.Configuration ?? offered[0].Configuration;
        string platform = offered.FirstOrDefault(o => o.Platform.Equals(AnyCpu, StringComparison.OrdinalIgnoreCase))?.Platform ?? offered[0].Platform;
        return offered.FirstOrDefault(o => o.Is(new BuildConfiguration(configuration, platform))) ?? offered[0];
    }

    /// <summary>The one chosen of those offered: the one kept while still offered, else the default.</summary>
    public static BuildConfiguration? Chosen(IReadOnlyList<BuildConfiguration> offered, BuildConfiguration? fallback, string? kept) =>
        BuildConfiguration.Of(kept) is { } named && offered.FirstOrDefault(o => o.Is(named)) is { } still ? still : fallback;
}

/// <summary>The build configuration and platform chosen for a folder where it is not what MSBuild takes when given none: what
/// every build, run, test and evaluation of the folder is given. Under a solution, each of its projects is given what the
/// chosen solution configuration maps it to (<see cref="Mapped"/>), and the solution's build the configuration itself;
/// with none, each project is given the configuration, and the platform where it declares it.</summary>
public sealed record BuildChoice(BuildConfiguration Chosen, bool OfSolution)
{
    /// <summary>What the solution's chosen configuration maps each project to, built or not; none with no solution.</summary>
    public IReadOnlyList<SolutionBuilt> Mapped { get; init; } = [];

    /// <summary>The global properties a project's build and evaluation take: the configuration and platform its solution
    /// maps it to, or the configuration (and the platform, where <paramref name="platforms"/> declares it) with none.</summary>
    public IReadOnlyDictionary<string, string> PropertiesOf(string project, string? platforms)
    {
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        if (Mapped.FirstOrDefault(m => PathNames.Same(m.Project, project)) is { } mapped)
        {
            properties[MSBuildNames.Configuration] = mapped.Configuration;
            properties[MSBuildNames.Platform] = mapped.Platform;
            return properties;
        }
        properties[MSBuildNames.Configuration] = Chosen.Configuration;
        if (!OfSolution && !Chosen.Platform.Equals(BuildConfigurations.AnyCpu, StringComparison.OrdinalIgnoreCase)
            && (platforms ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(Chosen.Platform, StringComparer.OrdinalIgnoreCase))
        {
            properties[MSBuildNames.Platform] = Chosen.Platform;
        }
        return properties;
    }

    /// <summary>What a dotnet build, run or publish of a target is given for it: the solution's own configuration and
    /// platform for the solution, a project's properties (<see cref="PropertiesOf"/>) for a project or a program. dotnet
    /// watch's (<paramref name="watched"/>) gives the platform as <c>--property</c>, its <c>-p</c> being its project's.</summary>
    public IReadOnlyList<string> ArgumentsFor(string target, bool solution, string? platforms, bool watched = false)
    {
        string property = watched ? "--property" : "-p";
        if (solution)
        {
            return ["-c", Chosen.Configuration, $"{property}:{MSBuildNames.Platform}={Chosen.Platform}"];
        }
        IReadOnlyDictionary<string, string> properties = PropertiesOf(target, platforms);
        return properties.TryGetValue(MSBuildNames.Platform, out string? platform)
            ? ["-c", properties[MSBuildNames.Configuration], $"{property}:{MSBuildNames.Platform}={platform}"]
            : ["-c", properties[MSBuildNames.Configuration]];
    }
}

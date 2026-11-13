using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>What a project's row in the Solution view stands for, as MSBuild's evaluation says, which picks its icon
/// (<see cref="FileIcons"/>); <see cref="Other"/> until it is evaluated, while it is unloaded, and for every other row.</summary>
public enum ProjectKind
{
    Other,

    /// <summary>An Aspire AppHost (<see cref="ProjectModel.IsAspireHost"/>).</summary>
    AspireHost,
}

public static class ProjectKinds
{
    /// <summary>The kind a project is, the first that fits in this order: a project of several kinds (an AppHost is an
    /// executable too) shows the one that says most of it.</summary>
    public static ProjectKind Of(ProjectModel project) => project switch
    {
        { IsAspireHost: true } => ProjectKind.AspireHost,
        _ => ProjectKind.Other,
    };
}

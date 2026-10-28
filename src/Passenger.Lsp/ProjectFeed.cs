using Passenger.Roslyn.Extension;

namespace Passenger.Lsp;

/// <summary>Where a server with Passenger's project loader (Passenger.Roslyn.Extension's ProjectLoader) gets its projects:
/// the client's design-time builds of them, in place of the builds the server would run itself. Asked on any
/// thread.</summary>
public interface IProjectFeed
{
    /// <summary>The projects to load: those to load first (the file shown's, and those it references), then the rest, each
    /// with its design-time builds when the client has them standing now, else to be asked for; null when the client can
    /// build none (its project host would not start), and the server builds its projects itself.</summary>
    Task<FedProjects?> ProjectsAsync(CancellationToken cancel);

    /// <summary>One project's design-time builds, which the server asks for as it loads it or finds it changed.</summary>
    Task<FedProject> InfoAsync(string path, CancellationToken cancel);
}

/// <summary>The projects a feed gives a server as it starts, and when the earliest of the builds among them began reading
/// what it read (the project file, its imports, a restore's assets), a build kept from before counting from when its
/// inputs were found as they were; null with none among them.</summary>
public sealed record FedProjects(IReadOnlyList<ProjectToOpen> First, IReadOnlyList<ProjectToOpen> Later, DateTime? Read);

/// <summary>A project's design-time builds as a feed gives them, and the files they read that have changed since, by their
/// canonical paths: a restore that landed as they were made.</summary>
public sealed record FedProject(ProjectInfoResult Info, IReadOnlyList<FileEvent> Changed);

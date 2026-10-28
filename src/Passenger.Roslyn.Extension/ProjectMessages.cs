using System.Text.Json.Serialization;

namespace Passenger.Roslyn.Extension;

// How Passenger gives Roslyn's server its projects, compiled into both: the server loads the projects Passenger names,
// each from what Passenger's project host built of it at design time (Roslyn's own ProjectFileInfo, one a target
// framework), rather than building them itself. A project Passenger names without them, or one the server finds changed
// later, the server asks Passenger for.

public static class ProjectMethods
{
    /// <summary>A notification: <see cref="LoadProjectsParams"/>, the projects to load, after which the server says the
    /// load is complete (workspace/projectInitializationComplete).</summary>
    public const string Load = "passenger/loadProjects";

    /// <summary>A request the server makes of Passenger: <see cref="ProjectInfoParams"/> to <see cref="ProjectInfoResult"/>.</summary>
    public const string Info = "passenger/projectInfo";

    /// <summary>A request answered true by a server that has Passenger's loader, which a server without it refuses.</summary>
    public const string Ready = "passenger/projectLoader";

    /// <summary>A notification of files gone from disk, as workspace/didChangeWatchedFiles gives them, which the server takes
    /// as that once the projects holding them as documents have loaded again without them: so it never reads a document's
    /// file that is gone, each such read waiting a second before it gives up.</summary>
    public const string Deleted = "passenger/filesDeleted";
}

/// <summary>The projects to load (or load again), in order, and the solution they are of.</summary>
public sealed record LoadProjectsParams(
    [property: JsonPropertyName("projects")] IReadOnlyList<ProjectToOpen> Projects,
    [property: JsonPropertyName("solution")] string? Solution);

/// <summary>A project and, when Passenger has them, its design-time builds (a JSON array of Roslyn's ProjectFileInfo) and
/// what they logged.</summary>
public sealed record ProjectToOpen(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("infos")] string? Infos,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ProjectDiagnostic>? Diagnostics = null);

public sealed record ProjectInfoParams([property: JsonPropertyName("path")] string Path);

/// <summary>A project's design-time builds and what they logged, or why there are none.</summary>
public sealed record ProjectInfoResult(
    [property: JsonPropertyName("infos")] string? Infos,
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ProjectDiagnostic>? Diagnostics = null);

/// <summary>An error or warning a project's design-time build logged: an error leaves the project unloaded.</summary>
public sealed record ProjectDiagnostic(
    [property: JsonPropertyName("error")] bool Error,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("project")] string Project);

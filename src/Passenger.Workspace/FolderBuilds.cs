using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>What a folder about to go or move waits for of the project host's design-time builds: each build of a project
/// in it still running, as MSBuild writes its obj/ until it ends; on Windows also the end of the round that built one, as
/// MSBuild leaves the process that built a project (the host, or one of its nodes) working in the project's folder until
/// the round's build session ends, and Windows moves no folder a process works in.</summary>
public static class FolderBuilds
{
    /// <summary>Whether a folder some process works in is kept from moving or going: on Windows.</summary>
    public static bool WorkingFoldersHeld => OperatingSystem.IsWindows();

    /// <summary>Done once <paramref name="going"/> may go: <paramref name="building"/> are the projects asked of the host
    /// and not yet built, each with its landing, and <paramref name="round"/> the host's round running now, if any.</summary>
    public static Task Awaited(string going, IEnumerable<(string Project, Task Landed)> building, DesignTimeRound? round, bool workingFoldersHeld)
    {
        List<Task> awaited = [.. building.Where(b => PathNames.Within(b.Project, going)).Select(b => b.Landed)];
        if (workingFoldersHeld && round is { } running && running.Projects.Any(p => PathNames.Within(p, going)))
        {
            awaited.Add(running.Ended);
        }
        return Task.WhenAll(awaited);
    }
}

/// <summary>A round of design-time builds the project host runs in one build session: the projects asked of it, built or
/// not yet, and its end, which is the session's.</summary>
public sealed record DesignTimeRound(IReadOnlyCollection<string> Projects, Task Ended);

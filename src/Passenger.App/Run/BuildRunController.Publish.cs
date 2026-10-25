using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Run;

/// <summary>Publish: dotnet publish of one project with its profile, as a build of the Build window's (its output there,
/// its problems in Problems, Stop ending it, the Build window's Rerun publishing again), its folder resolved by MSBuild
/// before it starts and said once it has landed there, the status bar's word a way to open it.</summary>
public sealed partial class BuildRunController
{
    /// <summary>The publish under way, from its start to <see cref="FinishBuild"/>.</summary>
    private PublishRequest? publishing;

    /// <summary>The last publish started, which the Build window's Rerun publishes again.</summary>
    private PublishRequest? published;

    /// <summary>Publishes a project as Publish does: its profile read by MSBuild for where it lands, then dotnet publish with
    /// it in the Build window. Says how it ended once it has (published, failed, stopped, refused or unable to start), a
    /// publish that failed never said to have succeeded.</summary>
    public async Task<BuildOutcome> PublishAsync(PublishRequest request, CancellationToken cancel = default)
    {
        UiThread.Check();
        if (session.Folder is not { } folder || session.IsEnded)
        {
            return Refused("Nothing to publish: no folder is open");
        }
        if (IsBuilding)
        {
            return Refused("Another build is running");
        }
        if (session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, request.Project)) is not { } project)
        {
            return Refused($"{request.Name} is not one of the folder's projects");
        }
        if (project is { IsEvaluated: false, Problem: null })
        {
            // An agent's publish as its folder opens: the project is read first.
            await session.Models.Idle;
            project = session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, request.Project)) ?? project;
        }
        if (PublishProfiles.WhyNot(project) is { } why)
        {
            return Refused(why);
        }
        if (parts.Work.Begin(AppActions.Publish, $"Reading how {request.Shown} publishes…") is not { } work)
        {
            return Refused(ActionWork.Busy(parts.Work.Of(AppActions.Publish)!));
        }
        (PublishRequest? resolved, string? unread) = (null, null);
        using (BackgroundWork.Scope())
        {
            try
            {
                (resolved, unread) = await PublishProfiles.ResolveAsync(folder, session.Workspace, project, request, work.Stopping);
            }
            catch (OperationCanceledException) when (work.IsStopped || session.IsEnded)
            {
                // Stopped as it read, or the folder left: said below.
            }
        }
        if (session.IsEnded || work.IsStopped || resolved is null)
        {
            work.Dispose();
            return Said(session.IsEnded ? new BuildOutcome(BuildStatus.Refused, Refusals.SolutionClosed) : work.IsStopped ? new BuildOutcome(BuildStatus.Stopped, "Publish stopped")
                : new BuildOutcome(BuildStatus.NotStarted, unread!));
        }
        TaskCompletionSource<BuildOutcome> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration stopping = default;
        Start(BuildKind.Publish, [request.Project], outcome =>
        {
            stopping.Dispose();
            done.TrySetResult(outcome);
        }, null, BuildCause.Explicit, resolved, work);
        if (!done.Task.IsCompleted)
        {
            // Canceled on the UI thread, by Stop, so the build it stops is this publish, still running.
            stopping = cancel.Register(session.Builder.Stop);
        }
        return await done.Task;

        BuildOutcome Refused(string why) => Said(new BuildOutcome(BuildStatus.Refused, why));
    }

    private BuildOutcome Said(BuildOutcome outcome)
    {
        parts.Say(outcome.Message);
        return outcome;
    }

    /// <summary>A publish that succeeded: the folder it landed in said in the Build window and the status bar (a way to open
    /// it), and its profile what Publish Again publishes the project with.</summary>
    private BuildOutcome Published(PublishRequest publish, string warned)
    {
        string? folder = publish.Folder is { } named && Directory.Exists(named) ? PathNames.Canonical(named) : null;
        if (folder is not null)
        {
            parts.Views.Build.Append($"Published {publish.Shown} to {folder}\n");
        }
        if (publish.Profile is { } profile)
        {
            session.Publishing.Published(publish.Project, profile);
        }
        string where = folder is null ? string.Empty : $" to {PathNames.Shown(folder, session.Folder)}";
        return new BuildOutcome(BuildStatus.Succeeded, $"Published {publish.Shown}{where}{warned}") { Published = folder };
    }

    /// <summary>What a publish is doing, from MSBuild's lines: restoring, building, then copying the app to its folder.</summary>
    private void PublishStage(PublishRequest publish, string line)
    {
        string said = line.Trim();
        string? stage = said.StartsWith("Determining projects to restore", StringComparison.Ordinal) ? $"Restoring {publish.Shown}…"
            : said.StartsWith("Restored ", StringComparison.Ordinal) || said.EndsWith("up-to-date for restore.", StringComparison.Ordinal) ? $"Building {publish.Shown}…"
            : Built().IsMatch(line) ? $"Publishing {publish.Shown}: copying it to its folder…"
            : null;
        if (stage is not null && buildWork is { } work && work.Stage != stage)
        {
            work.Say(stage);
        }
    }

    /// <summary>MSBuild's word that a project's assembly is built ("  Hello -> /…/Hello.dll").</summary>
    [GeneratedRegex(@"^\s+\S.* -> .+\.(dll|exe)$")]
    private static partial Regex Built();
}

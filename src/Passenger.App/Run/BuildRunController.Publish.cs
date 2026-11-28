using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Run;

/// <summary>Publish: dotnet publish of one project with its profile, as a build of the Build window's (its output there,
/// its problems in Problems, Stop ending it, the Build window's Rerun publishing again), its folder resolved by MSBuild
/// before it starts and said once it has landed there, a link in the Build window's line and the status bar's word each
/// a way to open it. A container profile's image goes into the engine on this machine, or to a registry, which a login is
/// looked for where it asks for one; an AppHost publishes through its own publishers, their files landed in the folder chosen.</summary>
public sealed partial class BuildRunController
{
    /// <summary>The publish under way, from its start to <see cref="FinishBuild"/>.</summary>
    private PublishRequest? publishing;

    /// <summary>The last publish started, which the Build window's Rerun publishes again.</summary>
    private PublishRequest? published;

    /// <summary>Whether the SDK said the container publish under way pushed its image, which a publish that only warned
    /// (a base image it could not reach) never does though dotnet ends with 0.</summary>
    private bool pushedImage;

    /// <summary>Publishes a project as Publish does: its profile read by MSBuild for where it lands, then dotnet publish with
    /// it in the Build window. Says how it ended once it has (published, failed, stopped, refused or unable to start), a
    /// publish that failed never said to have succeeded.</summary>
    public async Task<BuildOutcome> PublishAsync(PublishRequest request, CancellationToken cancel = default)
    {
        UiThread.Check();
        if (session.Folder is not { } folder || session.IsEnded)
        {
            return Refused(Refusals.NoFolderTo("publish"));
        }
        if (IsBuilding)
        {
            return Refused(Refusals.Building);
        }
        if (session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, request.Project)) is not { } project)
        {
            return Refused(Refusals.NotOfFolder(request.Name));
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
        if (project.IsAspireHost)
        {
            return await PublishAppHostAsync(request, cancel);
        }
        if (parts.Work.Begin(AppActions.Publish, BuildWords.ReadingPublish(request.Shown)) is not { } work)
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
            return Said(session.IsEnded ? new BuildOutcome(BuildStatus.Refused, Refusals.SolutionClosed) : work.IsStopped ? new BuildOutcome(BuildStatus.Stopped, BuildWords.Stopped("publish"))
                : new BuildOutcome(BuildStatus.NotStarted, unread!));
        }
        if (resolved.Kind == PublishKind.Container)
        {
            (PublishRequest? ready, Words? refused) = await ImageReadyAsync(resolved, work);
            resolved = ready;
            if (resolved is null || session.IsEnded || IsBuilding)
            {
                Words said = refused ?? (session.IsEnded ? Refusals.SolutionClosed : work.IsStopped ? BuildWords.Stopped("publish") : Refusals.Building);
                work.Tell(false, said);
                work.Dispose();
                return Said(new BuildOutcome(work.IsStopped ? BuildStatus.Stopped : BuildStatus.Refused, said));
            }
        }
        return await StartPublishAsync(resolved, work, cancel);

        BuildOutcome Refused(Words why) => Said(new BuildOutcome(BuildStatus.Refused, why));
    }

    /// <summary>What a container publish needs before it starts: for a registry that asks for a login, one the SDK would find
    /// for it (never asked for here, nor given on a command line); for the engine on this machine, an engine that answers. The
    /// engine's CLI is first on the publish's PATH, so the SDK loads the image through it. The request, or why not.</summary>
    private async Task<(PublishRequest? Ready, Words? Refused)> ImageReadyAsync(PublishRequest request, Working work)
    {
        ContainerEngine engine = session.Containers.Engine;
        try
        {
            if (request.Registry is { } registry)
            {
                work.Say(BuildWords.LookingForLogin(registry));
                if (await RegistryLogins.WhyNotAsync(registry, work.Stopping) is { } none)
                {
                    work.Say(BuildWords.AskingForLogin(registry));
                    if (await RegistryLogins.AsksForLoginAsync(registry, work.Stopping))
                    {
                        return (null, BuildWords.NotPushed(request.Shown, none));
                    }
                }
            }
            else
            {
                work.Say(LaunchWords.AskingEngine);
                EngineState state = await engine.StateAsync(cancel: work.Stopping);
                if (state.IsWslc || !state.IsReady)
                {
                    return (null, BuildWords.NotIntoEngine(request.Shown, state.IsWslc ? Refusals.WslcNoImageLoad : state.Problem ?? string.Empty));
                }
                request = request with { LocalRegistry = state.LocalRegistry };
            }
        }
        catch (OperationCanceledException) when (work.IsStopped)
        {
            return (null, BuildWords.Stopped("publish"));
        }
        return engine.Cli is { } cli && Path.GetDirectoryName(cli) is { } bin
            ? (request with { Environment = new Dictionary<string, string> { ["PATH"] = $"{bin}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}" } }, null)
            : (request, null);
    }

    /// <summary>An AppHost published through its own publishers (Docker Compose's, Kubernetes', a cloud's, as its code adds
    /// them), or one of its deploy steps run: as the Aspire CLI's publish and deploy run it, writing to a folder of the
    /// state's, whose files then land in the folder chosen (the one it published to last, else aspire-output beside it)
    /// through the window's writes. A deploy step's logins are looked for again first, never asked for here.</summary>
    private async Task<BuildOutcome> PublishAppHostAsync(PublishRequest request, CancellationToken cancel)
    {
        string output = request.Folder ?? session.Publishing.OutputOf(request.Project) ?? Path.Combine(Path.GetDirectoryName(request.Project)!, "aspire-output");
        Words begun = request.Deploys ? BuildWords.RunningStep(request.Step, request.Name) : BuildWords.PublishingThrough(request.Name);
        if (parts.Work.Begin(AppActions.Publish, begun) is not { } work)
        {
            return Said(new BuildOutcome(BuildStatus.Refused, ActionWork.Busy(parts.Work.Of(AppActions.Publish)!)));
        }
        if (request.Needs != Aspire.DeployNeeds.None)
        {
            work.Say(BuildWords.LookingForNeeds(request.Step));
            string? engineProblem;
            try
            {
                engineProblem = request.Needs.HasFlag(Aspire.DeployNeeds.ContainerEngine) && await session.Containers.Engine.StateAsync(cancel: work.Stopping) is { IsReady: false } state
                    ? state.Problem ?? $"{state.EngineName} does not answer"
                    : null;
            }
            catch (OperationCanceledException) when (work.IsStopped)
            {
                work.Dispose();
                return Said(new BuildOutcome(BuildStatus.Stopped, BuildWords.Stopped("publish")));
            }
            if (Aspire.DeployLogins.Missing(request.Needs, engineProblem) is { } missing)
            {
                Words why = BuildWords.StepNotRun(request.Shown, request.Step, missing);
                work.Tell(false, why);
                work.Dispose();
                return Said(new BuildOutcome(BuildStatus.Refused, why));
            }
            if (session.IsEnded || IsBuilding)
            {
                Words said = session.IsEnded ? Refusals.SolutionClosed : Refusals.Building;
                work.Tell(false, said);
                work.Dispose();
                return Said(new BuildOutcome(BuildStatus.Refused, said));
            }
        }
        string staging = Path.Combine(parts.StateFolder(), "cache", "aspire-publish", ContainerLabels.FolderValue(request.Project));
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(staging);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            work.Tell(false, RefusedException.Of(error));
            work.Dispose();
            return Said(new BuildOutcome(BuildStatus.NotStarted, BuildWords.CannotPublish(request.Name, RefusedException.Of(error))));
        }
        return await StartPublishAsync(request with
        {
            Kind = PublishKind.AppHost, Folder = PathNames.Canonical(output), Staging = PathNames.Canonical(staging),
            Configured = session.Workspace?.BuildArgumentsFor(request.Project) ?? [],
        }, work, cancel);
    }

    /// <summary>Starts a publish worked out, as a build of the Build window's, and says how it ended once it has.</summary>
    private async Task<BuildOutcome> StartPublishAsync(PublishRequest resolved, Working work, CancellationToken cancel)
    {
        pushedImage = false;
        TaskCompletionSource<BuildOutcome> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration stopping = default;
        Start(BuildKind.Publish, [resolved.Project], outcome =>
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
    }

    private BuildOutcome Said(BuildOutcome outcome)
    {
        parts.Say(outcome.Message);
        return outcome;
    }

    /// <summary>A publish that succeeded: the folder it landed in said in the Build window and the status bar (a way to open
    /// it), and its profile what Publish Again publishes the project with.</summary>
    private BuildOutcome Published(PublishRequest publish, int warnings)
    {
        if (publish.Kind == PublishKind.Container && !pushedImage)
        {
            return new BuildOutcome(BuildStatus.Failed, session.Problems.LastOrDefault(p => p.Severity == ProblemSeverity.Warning)?.Message.Split('\n')[0] is { } why
                ? BuildWords.NoImage(publish.Shown, why) : BuildWords.NoImageSaid(publish.Shown));
        }
        if (publish.Kind == PublishKind.Container)
        {
            string image = publish.Registry is { } registry
                ? string.Join(", ", (publish.Image ?? publish.Name).Split(", ").Select(each => $"{RegistryLogins.HostOf(registry)}/{each}"))
                : publish.Image ?? publish.Name;
            int images = image.Split(", ").Length;
            Words said = publish.Registry is null ? BuildWords.PublishedIntoEngine(publish.Shown, images, image) : BuildWords.PublishedPushed(publish.Shown, images, image);
            parts.Views.Build.Append($"{said.English}\n");
            if (publish.Profile is { } kept)
            {
                session.Publishing.Published(publish.Project, kept);
            }
            return new BuildOutcome(BuildStatus.Succeeded, Warned(said, warnings)) { Image = image };
        }
        string? folder = publish.Folder is { } named && Directory.Exists(named) ? PathNames.Canonical(named) : null;
        if (folder is not null)
        {
            string line = $"Published {publish.Shown} to {folder}";
            parts.Views.Build.Append($"{line}\n");
            // The folder is the IDE's own word, not a path a program's output printed: a click shows it in the file manager.
            parts.Views.Build.Links.Claim(line, line.Length - folder.Length, folder.Length, new FolderLink(folder));
        }
        if (publish.Profile is { } profile)
        {
            session.Publishing.Published(publish.Project, profile);
        }
        Words published = folder is null ? BuildWords.Published(publish.Shown) : BuildWords.PublishedTo(publish.Shown, PathNames.Shown(folder, session.Folder));
        return new BuildOutcome(BuildStatus.Succeeded, Warned(published, warnings)) { Published = folder };
    }

    /// <summary>What a build that succeeded came to, with how many warnings it gave where it gave any.</summary>
    private static Words Warned(Words said, int warnings) => warnings > 0 ? BuildWords.WithWarnings(said, warnings) : said;

    /// <summary>An AppHost's publish that ran: the files its publishers wrote landed in its folder through the window's writes,
    /// each said in the Build window as a link to it; then the publish ends, saying so.</summary>
    private async Task LandAsync(PublishRequest publish, int warnings)
    {
        string folder = publish.Folder!;
        string shown = PathNames.Shown(folder, session.Folder);
        buildWork?.Say(BuildWords.WritingFiles(publish.Name, shown));
        (IReadOnlyList<string> Written, string? Problem) landed = parts.LandFiles is { } land
            ? await land(publish.Staging!, folder, $"Publish {publish.Name}")
            : ([], "this window writes no files");
        BuildOutcome ended;
        if (landed.Problem is { } problem)
        {
            ended = new BuildOutcome(BuildStatus.Failed, BuildWords.FilesUnwritten(publish.Name, shown, problem));
        }
        else if (landed.Written.Count == 0 && publish.Deploys)
        {
            session.Publishing.PublishedTo(publish.Project, folder);
            ended = new BuildOutcome(BuildStatus.Succeeded, Warned(BuildWords.RanStep(publish.Step, publish.Name), warnings)) { Published = folder };
        }
        else if (landed.Written.Count == 0)
        {
            ended = new BuildOutcome(BuildStatus.Failed, BuildWords.NothingWritten(publish.Name));
        }
        else
        {
            foreach (string path in landed.Written)
            {
                string line = $"Wrote {path}";
                parts.Views.Build.Append($"{line}\n");
                parts.Views.Build.Links.Claim(line, line.Length - path.Length, path.Length, new FileLink(path, new TextPosition(0, 0)));
            }
            session.Publishing.PublishedTo(publish.Project, folder);
            Words files = Plural.Listed(Words.Names(landed.Written.Select(path => Path.GetRelativePath(folder, path))));
            ended = new BuildOutcome(BuildStatus.Succeeded, Warned(publish.Deploys ? BuildWords.RanStepWrote(publish.Step, publish.Name, shown, files)
                : BuildWords.PublishedFiles(publish.Name, shown, files), warnings)) { Published = folder };
        }
        FinishBuild(ended);
    }

    /// <summary>What a publish is doing, from MSBuild's lines: restoring, building, then copying the app to its folder, making
    /// its image, or running an AppHost's publishers.</summary>
    private void PublishStage(PublishRequest publish, string line)
    {
        string said = line.Trim();
        pushedImage |= said.StartsWith("Pushed image '", StringComparison.Ordinal);
        Words? stage = said.StartsWith("Determining projects to restore", StringComparison.Ordinal) ? BuildWords.Restoring(publish.Shown)
            : said.StartsWith("Restored ", StringComparison.Ordinal) || said.EndsWith("up-to-date for restore.", StringComparison.Ordinal) ? BuildWords.Doing("build", publish.Shown)
            : said.StartsWith("Building image '", StringComparison.Ordinal) ? BuildWords.MakingImage(publish.Shown)
            : said.StartsWith("Pushed image '", StringComparison.Ordinal) ? (publish.Registry is null ? BuildWords.ImageInEngine(publish.Shown) : BuildWords.ImagePushed(publish.Shown))
            : Built().IsMatch(line) ? publish.Kind switch
            {
                PublishKind.AppHost => publish.Deploys ? BuildWords.RunningStep(publish.Step, publish.Name) : BuildWords.PublishersWriting(publish.Name),
                PublishKind.Container => BuildWords.CopyingForImage(publish.Shown),
                _ => BuildWords.Copying(publish.Shown),
            }
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

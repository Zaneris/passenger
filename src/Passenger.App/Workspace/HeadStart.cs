using System.Diagnostics;
using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Host;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>The opening of the folder a windowless Passenger's command line names, begun on workers as the process starts,
/// while its window is still being built: the folder read for its solution and projects, the project host started, the
/// projects evaluated and built at design time there, and a C# language server started and loading them. The window's
/// session takes each part up as it opens that folder, in place of beginning it (an evaluation or build as from a cache,
/// its stamps checked, so what changed since is done again; the server only if it loads what the session would, with the
/// same options, everything it said meanwhile held for the session to hear), and <see cref="EndAsync"/> ends the server
/// it did not take. A part that cannot be done is left to the session, which says why. Not a UI-thread object.</summary>
public sealed class HeadStart
{
    private readonly Lock gate = new();

    /// <summary>Stops the language server's start: the session that took it cancelled, or nothing took it.</summary>
    private readonly CancellationTokenSource stop = new();

    private (Task<RoslynSession?> Started, IReadOnlyDictionary<string, object?> Options)? server;
    private Task<ProjectHost?> hosting = Task.FromResult<ProjectHost?>(null);
    private bool ended;

    private HeadStart(string folder, string? named)
    {
        Folder = folder;
        Named = named;
    }

    /// <summary>The folder opened, canonical.</summary>
    public string Folder { get; }

    /// <summary>The solution or project the command line named over the folder, or null for the folder's own.</summary>
    public string? Named { get; }

    /// <summary>The folder read as a session reads it (<see cref="WorkspaceModel.Read"/>): its solution, and its projects
    /// walked for.</summary>
    public Task<WorkspaceModel> Read { get; private set; } = null!;

    /// <summary>The projects' complete evaluations, and the SDK they were made with; none where the host could not
    /// evaluate them.</summary>
    public Task<ProjectModelCache> Evaluations { get; private set; } = null!;

    /// <summary>The projects' design-time builds, as the language server loads them; none without a language server, or
    /// where the host could not build them.</summary>
    public Task<ProjectBuildCache> Builds { get; private set; } = null!;

    /// <summary>Begins opening the folder or solution the command line names, with a C# language server when
    /// <paramref name="languageServer"/>; null when it names none, or none that is there.</summary>
    public static HeadStart? Begin(LaunchOptions launch, bool languageServer)
    {
        if ((launch.Workspace ?? launch.Folder) is not { } given)
        {
            return null;
        }
        string full = PathNames.Canonical(given);
        bool fileNamed = File.Exists(full) && WorkspaceModel.Opens(full);
        string folder = fileNamed ? Path.GetDirectoryName(full)! : full;
        if (!Directory.Exists(folder))
        {
            return null;
        }
        HeadStart head = new(folder, fileNamed ? full : null);
        IdeErrors.Write($"Opening {folder} as Passenger starts, ahead of its window: its projects read and evaluated"
            + (languageServer ? ", built at design time, and loaded by a C# language server started now" : string.Empty));
        head.Start(languageServer);
        return head;
    }

    private void Start(bool languageServer)
    {
        long began = Stopwatch.GetTimestamp();
        // Not held: nothing drawn waits for these, and the session that takes one up waits for it as its own.
        Read = BackgroundWork.Unheld(() => WorkspaceModel.Read(Folder, Named));
        hosting = BackgroundWork.Unheld(HostAsync);
        Evaluations = BackgroundWork.Unheld(EvaluateAsync);
        Builds = languageServer ? BackgroundWork.Unheld(BuildAsync) : Task.FromResult(new ProjectBuildCache());
        if (languageServer)
        {
            IReadOnlyDictionary<string, object?> options = CSharpService.StartOptions(new Settings.AppSettings());
            server = (BackgroundWork.Unheld(() => StartServerAsync(options, began)), options);
        }
    }

    /// <summary>The folder's project host; null when it will not start, which the session says as it asks for it.</summary>
    private async Task<ProjectHost?> HostAsync()
    {
        try
        {
            return await ProjectHosts.ForAsync(Folder);
        }
        catch (ProjectHostFailed)
        {
            return null;
        }
    }

    /// <summary>The projects the read found that are there, each once.</summary>
    private static List<string> Projects(WorkspaceModel read) =>
        [.. read.Projects.Where(p => p.Exists).Select(p => p.Path).Distinct(StringComparer.Ordinal)];

    private async Task<ProjectModelCache> EvaluateAsync()
    {
        Dictionary<string, EvaluatedProject> complete = new(StringComparer.Ordinal);
        try
        {
            WorkspaceModel read = await Read;
            if (await hosting is not { } host || Projects(read) is not { Count: > 0 } projects)
            {
                return new ProjectModelCache();
            }
            EvaluateDone done = await host.EvaluateAsync(projects, ProjectModels.Properties(read), project =>
            {
                if (project.Complete)
                {
                    lock (complete)
                    {
                        complete[project.Path] = project;
                    }
                }
            });
            IdeErrors.Write($"Project host (process {host.ProcessId}) evaluated {projects.Count} projects of {Folder} ahead of its window in {done.Seconds:0.00} s");
            lock (complete)
            {
                return new ProjectModelCache { Sdk = host.Sdk, Projects = new(complete, StringComparer.Ordinal) };
            }
        }
        catch (Exception unevaluated) when (unevaluated is ProjectHostFailed or IOException or UnauthorizedAccessException)
        {
            // The session evaluates them itself, and says what stops the host.
            return new ProjectModelCache();
        }
    }

    private async Task<ProjectBuildCache> BuildAsync()
    {
        Dictionary<string, KeptBuild> built = new(StringComparer.Ordinal);
        try
        {
            WorkspaceModel read = await Read;
            if (await hosting is not { } host || Projects(read) is not { Count: > 0 } projects)
            {
                return new ProjectBuildCache();
            }
            BuildDone done = await host.BuildAsync(projects, ProjectBuilds.Properties(ProjectModels.Properties(read)), project =>
            {
                if (project.Problem is null)
                {
                    KeptBuild kept = KeptBuild.Of(project);
                    lock (built)
                    {
                        built[project.Path] = kept;
                    }
                }
            });
            IdeErrors.Write($"Project host (process {host.ProcessId}) built {done.Built} projects of {Folder} at design time ahead of its window in {done.Seconds:0.00} s");
            lock (built)
            {
                return new ProjectBuildCache { Sdk = host.Sdk, Solution = read.Solution?.Path, Projects = new(built, StringComparer.Ordinal) };
            }
        }
        catch (Exception unbuilt) when (unbuilt is ProjectHostFailed or IOException or UnauthorizedAccessException)
        {
            // The session builds them itself as the server asks, and says what stops the host.
            return new ProjectBuildCache();
        }
    }

    /// <summary>The language server, started once the read has found a solution or a project for it to load, and loading
    /// them from the builds begun ahead; what it says is held for the session that takes it up. None for a folder with
    /// neither, which the session starts none for.</summary>
    private async Task<RoslynSession?> StartServerAsync(IReadOnlyDictionary<string, object?> options, long began)
    {
        WorkspaceModel read = await Read;
        if (read.Solution is null && read.Projects.Count == 0)
        {
            return null;
        }
        RoslynSession started = await RoslynSession.StartAsync(Folder, read.Solution?.Path, [.. read.Projects.Select(p => p.Path)], CSharpService.LogFolder, stop.Token,
            options, server => server.HoldSaid(), feed: new Feed(this), stateCache: CSharpService.ServerCache);
        IdeErrors.Write($"C# language server (process {started.Server.ProcessId}) started ahead for {Folder} "
            + $"{Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s after the folder was named, loading its projects");
        return started;
    }

    /// <summary>The server started for the folder with these options, for a session to take once: how it waits for that
    /// start, which its cancellation stops, to the server when it loads the solution or projects given, else to none (it
    /// loads others, its start failed or it ended), the session then starting its own. Null when it was taken or ended, or
    /// has another folder or options.</summary>
    internal Func<CancellationToken, Task<RoslynSession?>>? TakeServer(string folder, string? solution, IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, object?> options)
    {
        Task<RoslynSession?> started;
        lock (gate)
        {
            if (ended || server is not { } held || !PathNames.Same(folder, Folder) || !Same(held.Options, options))
            {
                return null;
            }
            started = held.Started;
            server = null;
        }
        return async cancel =>
        {
            RoslynSession? running;
            await using (cancel.Register(stop.Cancel))
            {
                try
                {
                    running = await started;
                }
                catch (Exception unstarted) when (unstarted is LanguageServerFailed or IOException or UnauthorizedAccessException
                    || (unstarted is OperationCanceledException && !cancel.IsCancellationRequested))
                {
                    // Started again by the session, which says why if that start fails too.
                    return null;
                }
            }
            if (running is not null && (running.Server.Ended is not null || cancel.IsCancellationRequested || running.Solution != solution
                || (solution is null && !running.Projects.SequenceEqual(projects, StringComparer.Ordinal))))
            {
                // Ended, given up as it was taken, or loading what the session does not: the session starts another, or none.
                await running.DisposeAsync();
                cancel.ThrowIfCancellationRequested();
                return null;
            }
            return running;
        };
    }

    /// <summary>Ends the server no session took, at once while it starts, else once its start is done; done once that end
    /// has.</summary>
    public async Task EndAsync()
    {
        Task<RoslynSession?>? left;
        lock (gate)
        {
            ended = true;
            left = server?.Started;
            server = null;
        }
        if (left is null)
        {
            return;
        }
        stop.Cancel();
        RoslynSession? started;
        try
        {
            started = await left;
        }
        catch (Exception unstarted) when (unstarted is OperationCanceledException or LanguageServerFailed or IOException or UnauthorizedAccessException)
        {
            // Never started, or stopped as it started, leaving nothing behind.
            return;
        }
        if (started is not null)
        {
            await started.DisposeAsync();
        }
    }

    /// <summary>Whether two sets of options are the same, one set to its default (null) not one never set.</summary>
    private static bool Same(IReadOnlyDictionary<string, object?> set, IReadOnlyDictionary<string, object?> wanted) =>
        set.Count == wanted.Count && set.All(option => wanted.TryGetValue(option.Key, out object? value) && Equals(option.Value, value));

    /// <summary>The design-time builds begun ahead, as the server started ahead asks for them until a session takes it up
    /// and answers in their place: none when the host could not build them, and the server builds its projects itself.</summary>
    private sealed class Feed(HeadStart head) : IProjectFeed
    {
        public async Task<(IReadOnlyList<ProjectToOpen> First, IReadOnlyList<ProjectToOpen> Later)?> ProjectsAsync(CancellationToken cancel)
        {
            ProjectBuildCache builds = await head.Builds;
            WorkspaceModel read = await head.Read;
            if (builds.Sdk is null)
            {
                return null;
            }
            return ([], [.. Projects(read).Select(path => builds.Projects.TryGetValue(path, out KeptBuild? kept)
                ? new ProjectToOpen(path, kept.Read(), ProjectBuilds.Said(kept.Diagnostics)) : new ProjectToOpen(path, null))]);
        }

        public async Task<ProjectInfoResult> InfoAsync(string path, CancellationToken cancel)
        {
            if ((await head.Builds).Projects.TryGetValue(path, out KeptBuild? kept))
            {
                return new ProjectInfoResult(kept.Read(), null, ProjectBuilds.Said(kept.Diagnostics));
            }
            if (await head.hosting is not { } host)
            {
                return new ProjectInfoResult(null, "the project host did not start");
            }
            // One whose build failed ahead, built again for its problem.
            BuiltProject? built = null;
            try
            {
                await host.BuildAsync([path], ProjectBuilds.Properties(ProjectModels.Properties(await head.Read)), project => built = project, cancel);
            }
            catch (ProjectHostFailed failed)
            {
                return new ProjectInfoResult(null, failed.Reason);
            }
            return built switch
            {
                { Problem: { } problem } => new ProjectInfoResult(null, problem),
                { } done => new ProjectInfoResult(done.Infos.ToString(), null, ProjectBuilds.Said(done.Diagnostics)),
                null => new ProjectInfoResult(null, "the project host built nothing of it"),
            };
        }
    }
}

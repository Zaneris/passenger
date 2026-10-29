using System.Collections.Concurrent;
using System.Diagnostics;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>A window's projects as MSBuild evaluates them, in the project host (<see cref="ProjectHosts"/>): the
/// folder's cache of its last evaluations first (<see cref="ProjectModelCache"/>, kept by a window that keeps state,
/// read off the UI thread), then each project whose inputs changed since, or that was never evaluated, evaluated again,
/// the active file's first, each landing on the UI thread as it comes (<see cref="Changed"/>), as background work. One
/// round of checks and evaluations runs at a time; one asked for meanwhile follows it, and finds by the stamps whatever
/// changed while the first ran. A UI-thread object.</summary>
public sealed class ProjectModels
{
    private readonly Dictionary<string, EvaluatedProject> known = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(int Generation, EvaluatedProject Project)> landed = new();
    private StateDocument<ProjectModelCache>? kept;
    private string? folder;
    private string? sdk;

    /// <summary>Bumped as a folder is taken up or let go, so what lands for the one before is dropped.</summary>
    private int generation;

    /// <summary>The generation a round is running for, or -1 with none running.</summary>
    private int running = -1;

    /// <summary>The workspace and active file to follow once the round running ends.</summary>
    private (WorkspaceModel Workspace, string? Active)? wanted;

    /// <summary>The cache's read under way, which the first round waits for, so it evaluates only what changed since.</summary>
    private Task reading = Task.CompletedTask;

    /// <summary>Ends the folder's round in the host as the folder is let go, so the next folder's work need not wait.</summary>
    private CancellationTokenSource ending = new();

    private int posting;

    /// <summary>A project whose next evaluation waits for a task, as a slow MSBuild keeps it (<see cref="HoldEvaluation"/>).</summary>
    private (string Project, Task Until, TaskCompletionSource Reached)? held;

    /// <summary>Projects evaluated (or evaluated again) landed, by path; raised on the UI thread, a batch at a time.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? Changed;

    /// <summary>How many projects the host has evaluated for this window, each counted once it is complete: a folder that
    /// opens on its cache with nothing changed evaluates none.</summary>
    public int Evaluations { get; private set; }

    /// <summary>How many rounds have begun, each reading again what every project's evaluation read (its files and the
    /// folders its wildcards walked) to find those to evaluate.</summary>
    public int Rounds { get; private set; }

    /// <summary>Why the host could not evaluate the folder's projects, the last time it was asked; null when it could.</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the host loaded no .NET SDK for the folder, the last time it was asked; null while it loads one.</summary>
    public MissingSdk? MissingSdk { get; private set; }

    /// <summary>The folder came to have no .NET SDK to load, build and run its projects, or has one again
    /// (<see cref="MissingSdk"/>).</summary>
    public event EventHandler? SdkChanged;

    /// <summary>Whether every project of the workspace last followed has its complete evaluation in.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Whether a workspace was given to follow since the folder was taken up: until then no round has begun, so
    /// <see cref="Idle"/> says nothing of its evaluations.</summary>
    public bool Followed { get; private set; }

    private TaskCompletionSource idle = Done();

    /// <summary>Done while no round runs: what an agent's Open waits for before it says what the projects are.</summary>
    public Task Idle => idle.Task;

    /// <summary>The next round that evaluates a project waits until <paramref name="until"/> has ended before it asks the
    /// host, as a slow evaluation keeps the project unread meanwhile: done once that round is waiting.</summary>
    public Task HoldEvaluation(string project, Task until)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        held = (project, until, reached);
        return reached.Task;
    }

    private static TaskCompletionSource Done()
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>Takes up a folder, with the store's cache of its evaluations when the window keeps one, or the evaluations
    /// begun <paramref name="ahead"/> of the window, either landing as <see cref="Changed"/> once there; the first round
    /// waits for them, so it evaluates only what changed since.</summary>
    public void Open(string opened, StateDocument<ProjectModelCache>? cache, Task<ProjectModelCache>? ahead = null)
    {
        UiThread.Check();
        Close();
        folder = opened;
        kept = cache;
        if ((ahead ?? cache?.ValueAsync()) is { } evaluations)
        {
            idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reading = TakeCacheAsync(generation, evaluations);
        }
    }

    private async Task TakeCacheAsync(int mine, Task<ProjectModelCache> evaluations)
    {
        ProjectModelCache value = await evaluations;
        if (mine != generation)
        {
            return;
        }
        sdk = value.Sdk;
        foreach ((string path, EvaluatedProject project) in value.Projects)
        {
            known[path] = project;
        }
        if (value.Projects.Count > 0)
        {
            Changed?.Invoke(this, [.. value.Projects.Keys]);
        }
        if (running != generation)
        {
            Next();
        }
    }

    /// <summary>Lets the folder go: what is still being evaluated for it is dropped as it lands.</summary>
    public void Close()
    {
        UiThread.Check();
        ending.Cancel();
        ending = new CancellationTokenSource();
        reading = Task.CompletedTask;
        generation++;
        running = -1;
        wanted = null;
        idle.TrySetResult();
        folder = null;
        kept = null;
        sdk = null;
        known.Clear();
        made.Clear();
        Problem = null;
        MissingSdk = null;
        IsComplete = false;
        Followed = false;
    }

    /// <summary>The models made of the evaluations known, each made once (its references are each a path made canonical).</summary>
    private readonly Dictionary<string, ProjectModel> made = new(StringComparer.Ordinal);

    /// <summary>A project as last evaluated, or as given while it never has been.</summary>
    public ProjectModel Apply(ProjectModel project)
    {
        if (!known.TryGetValue(project.Path, out EvaluatedProject? evaluated))
        {
            return project;
        }
        if (!made.TryGetValue(project.Path, out ProjectModel? model) || !ReferenceEquals(model.Evaluated, evaluated))
        {
            made[project.Path] = model = ProjectModel.From(project.Path, evaluated);
        }
        return model;
    }

    public WorkspaceModel Apply(WorkspaceModel workspace) => workspace.WithProjects(Apply);

    /// <summary>Files or folders come: the evaluation that failed of a project at or under one, and the model made of it
    /// (shown missing when its file was), are let go once its file stands there, so the project is taken as it is there
    /// and evaluated again rather than kept as missing.</summary>
    public void Came(IReadOnlyCollection<string> paths)
    {
        UiThread.Check();
        if (paths.Count == 0)
        {
            return;
        }
        foreach (string path in known.Where(k => k.Value.Problem is not null && paths.Any(p => PathNames.Within(k.Key, p)) && File.Exists(k.Key)).Select(k => k.Key).ToList())
        {
            known.Remove(path);
            made.Remove(path);
        }
    }

    /// <summary>Whether an evaluation known read one of these files: a project's own, or one it imports.</summary>
    public bool Read(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }
        HashSet<string> asked = new(paths, PathNames.Comparer);
        return known.Values.Any(e => e.Inputs.Any(i => asked.Contains(i.Path)));
    }

    /// <summary>Whether, while the folder has no SDK, one of these files is a global.json, which may now ask for one installed.</summary>
    public bool SdkMayCome(IEnumerable<string> paths) =>
        MissingSdk is not null && paths.Any(p => Path.GetFileName(p).Equals("global.json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks each project of a workspace against what its evaluation read, off the UI thread, and has those that
    /// changed or were never evaluated evaluated, the one holding <paramref name="active"/> first; the host is asked its SDK
    /// for the folder in any case, and a change of it has every project evaluated again.</summary>
    public void Follow(WorkspaceModel workspace, string? active)
    {
        UiThread.Check();
        if (folder is null)
        {
            return;
        }
        wanted = (workspace, active);
        Followed = true;
        IsComplete = workspace.Projects.Where(p => p.Exists).All(p => known.TryGetValue(p.Path, out EvaluatedProject? e) && e.Complete);
        // Until the cache is read, which follows what is wanted then.
        if (running != generation && reading.IsCompleted)
        {
            Next();
        }
    }

    private void Next()
    {
        if (wanted is not { } next || folder is null)
        {
            running = -1;
            idle.TrySetResult();
            return;
        }
        wanted = null;
        if (idle.Task.IsCompleted)
        {
            idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        running = generation;
        Rounds++;
        List<string> projects = [.. Ordered(next.Workspace, next.Active)];
        Dictionary<string, EvaluatedProject> before = new(StringComparer.Ordinal);
        foreach (string path in projects)
        {
            if (known.TryGetValue(path, out EvaluatedProject? evaluated))
            {
                before[path] = evaluated;
            }
        }
        BackgroundWork.Begin();
        IdeErrors.Observe(RoundAsync(generation, folder, projects, before, Properties(next.Workspace), ending.Token));
    }

    /// <summary>The workspace's projects, the active file's first and then those it references as last evaluated, through
    /// their references: what the language server loads first.</summary>
    private IEnumerable<string> Ordered(WorkspaceModel workspace, string? active)
    {
        List<string> paths = [.. workspace.Projects.Where(p => p.Exists).Select(p => p.Path).Distinct(StringComparer.Ordinal)];
        if (active is null || ProjectModel.Owning(workspace.Projects, active) is not { } owner || !paths.Contains(owner.Path))
        {
            return paths;
        }
        List<string> first = [];
        Queue<string> next = new([owner.Path]);
        while (next.TryDequeue(out string? path))
        {
            if (first.Contains(path) || !paths.Contains(path))
            {
                continue;
            }
            first.Add(path);
            foreach (string reference in known.GetValueOrDefault(path)?.Dependencies.SelectMany(d => d.Projects) ?? [])
            {
                next.Enqueue(PathNames.Canonical(reference));
            }
        }
        return [.. first, .. paths.Except(first, StringComparer.Ordinal)];
    }

    /// <summary>The global properties a build of the solution gives its projects, which a Directory.Build.props may read.</summary>
    public static Dictionary<string, string> Properties(WorkspaceModel workspace)
    {
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        if (workspace.Solution is { } solution)
        {
            properties["SolutionDir"] = Path.GetDirectoryName(solution.Path)! + Path.DirectorySeparatorChar;
            properties["SolutionPath"] = solution.Path;
            properties["SolutionName"] = solution.Name;
            properties["SolutionFileName"] = Path.GetFileName(solution.Path);
            properties["SolutionExt"] = Path.GetExtension(solution.Path);
        }
        return properties;
    }

    /// <summary>The global properties one project is evaluated with for what a build of it makes (a launch's program, a test
    /// run's assemblies, a publish): the solution's, and Windows targeting where the host loaded it with that.</summary>
    public static Dictionary<string, string> Properties(WorkspaceModel? workspace, ProjectModel project)
    {
        Dictionary<string, string> properties = workspace is null ? new(StringComparer.OrdinalIgnoreCase) : Properties(workspace);
        if (project.WindowsTargeting)
        {
            properties["EnableWindowsTargeting"] = "true";
        }
        return properties;
    }

    private async Task RoundAsync(int mine, string following, List<string> projects, Dictionary<string, EvaluatedProject> before, Dictionary<string, string> properties,
        CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        HashSet<string> evaluated = new(StringComparer.Ordinal);
        try
        {
            List<string> stale = await BackgroundWork.Run(() => projects.Where(p => !before.TryGetValue(p, out EvaluatedProject? e) || !Holds(e)).ToList());
            ProjectHost host = await BackgroundWork.Run(() => ProjectHosts.ForAsync(following));
            if (mine != generation)
            {
                return;
            }
            if (sdk is null || !PathNames.Same(sdk, host.Sdk))
            {
                // Evaluated with another SDK's MSBuild (the folder's global.json changed since): none of it stands.
                stale = projects;
                sdk = host.Sdk;
            }
            Problem = null;
            SdkIs(null);
            if (stale.Count == 0)
            {
                return;
            }
            if (held is { } hold && stale.Any(p => PathNames.Same(p, hold.Project)))
            {
                held = null;
                hold.Reached.TrySetResult();
                await hold.Until;
            }
            EvaluateDone done;
            try
            {
                done = await EvaluateAsync(host, stale);
            }
            catch (ProjectHostFailed ended) when (ended.HostEnded && !cancel.IsCancellationRequested)
            {
                // Every window shares the host, which another folder's build may have ended: a host started anew evaluates the rest once.
                List<string> left;
                lock (evaluated)
                {
                    left = [.. stale.Where(p => !evaluated.Contains(p))];
                }
                IdeErrors.Write($"Project host (process {host.ProcessId}) evaluating the projects of {following}: {ended.Reason}. The {left.Count} not yet evaluated are asked of a host started anew");
                host = await BackgroundWork.Run(() => ProjectHosts.ForAsync(following));
                done = await EvaluateAsync(host, left);
            }
            if (mine == generation)
            {
                Keep(projects);
                IdeErrors.Write($"Project host (process {host.ProcessId}) evaluated {stale.Count} of the {projects.Count} projects of {following} in {done.Seconds:0.00} s, {Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s after they were checked");
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The folder was let go: the host was told to end its round.
        }
        catch (Exception error) when (error is ProjectHostFailed or IOException or UnauthorizedAccessException)
        {
            // A folder deleted under the window, a host that would not start or ended: the projects stand as they were.
            if (mine == generation)
            {
                Problem = error is ProjectHostFailed failed ? failed.Reason : error.Message;
                IdeErrors.Write($"The project host could not evaluate the projects of {following}: {Problem}");
                if (error is ProjectHostFailed { MissingSdk: { } missing })
                {
                    SdkIs(missing);
                }
                Changed?.Invoke(this, []);
            }
        }
        finally
        {
            if (mine == generation)
            {
                IsComplete = wanted is null;
                Next();
            }
            BackgroundWork.End();
        }

        Task<EvaluateDone> EvaluateAsync(ProjectHost asked, List<string> projects) => asked.EvaluateAsync(projects, properties, project =>
        {
            if (project.Complete)
            {
                lock (evaluated)
                {
                    evaluated.Add(project.Path);
                }
            }
            landed.Enqueue((mine, project));
            if (Interlocked.Exchange(ref posting, 1) == 0)
            {
                UiThread.Post(Land);
            }
        }, cancel);
    }

    private void SdkIs(MissingSdk? missing)
    {
        if (missing != MissingSdk)
        {
            MissingSdk = missing;
            SdkChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The evaluations landed since the last batch, taken in (those for a folder let go, dropped).</summary>
    private void Land()
    {
        Interlocked.Exchange(ref posting, 0);
        HashSet<string> changed = new(StringComparer.Ordinal);
        while (landed.TryDequeue(out (int Generation, EvaluatedProject Project) one))
        {
            if (one.Generation != generation)
            {
                continue;
            }
            known[one.Project.Path] = one.Project;
            changed.Add(one.Project.Path);
            if (one.Project.Complete)
            {
                Evaluations++;
            }
        }
        if (changed.Count > 0)
        {
            Changed?.Invoke(this, changed);
        }
    }

    /// <summary>The workspace's complete evaluations, kept for the folder's next opening.</summary>
    private void Keep(IReadOnlyList<string> projects)
    {
        if (kept is null || sdk is null)
        {
            return;
        }
        Dictionary<string, EvaluatedProject> complete = new(StringComparer.Ordinal);
        foreach (string path in projects)
        {
            if (known.TryGetValue(path, out EvaluatedProject? evaluated) && evaluated.Complete)
            {
                complete[path] = evaluated;
            }
        }
        kept.Set(new ProjectModelCache { Sdk = sdk, Projects = complete });
    }

    /// <summary>Whether an evaluation stands: complete, and everything it read as it was then.</summary>
    private static bool Holds(EvaluatedProject evaluated) =>
        evaluated.Complete && evaluated.Inputs.All(Stamps.Holds) && evaluated.Folders.All(Stamps.Holds);
}

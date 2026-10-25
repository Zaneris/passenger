using Passenger.App.CSharp;
using Passenger.App.Aspire;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.History;
using Passenger.App.LiveTree;
using Passenger.App.Packages;
using Passenger.App.Navigation;
using Passenger.App.Profiling;
using Passenger.App.Run;
using Passenger.App.State;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Profiler;
using Passenger.Workspace;
using Passenger.Xaml.Projects;

namespace Passenger.App;

/// <summary>What a window lends each session it makes: its editors (whose open files the session's services follow),
/// where its state and Local History are kept, how folders are watched, the warm language server it may take up, the
/// consoles, Debug window and tests tree its runners, debugger and test runner write to, the build a test run starts
/// with, the tab of the Terminal window a program the debugger launches there runs in, the settings as they stand (the
/// Node.js the web language servers run on, whether remote JSON schemas are downloaded), the window's work under way,
/// which Git's operations and test runs show themselves in, and the container engine the folder's containers run
/// on.</summary>
public sealed record SessionSetup(
    EditorArea Editors,
    string StateFolder,
    HistoryKeeping HistoryKeeping,
    FolderWatching Watching,
    Func<bool> SolutionWide,
    WarmServer? WarmServer,
    ToolViews Views,
    Func<CancellationToken, Task<BuildOutcome>> BuildForTests,
    Func<string, Dap.RunInTerminalArguments, Task<TerminalRun>> RunForDebugger,
    Func<Settings.AppSettings> Settings,
    ActionWork Work,
    Func<Containers.ContainerEngine> Engine)
{
    /// <summary>Where the Dockerfile server asks for image tags in Docker Hub's place: the window's.</summary>
    public Uri? DockerHub { get; init; }

    /// <summary>Whether the session starts the C# language server: the window's <see cref="MainWindow.StartsLanguageServer"/>.</summary>
    public bool StartsLanguageServer { get; init; } = true;

    /// <summary>Where the container tools come from that reach a program in a container: the window's.</summary>
    public required Func<Containers.ContainerToolsSource> ContainerTools { get; init; }
}

/// <summary>Projects whose evaluation landed, as the workspace now has them, and whether which projects hold tests, or what
/// packages the projects reference, changed with them.</summary>
public sealed record EvaluatedProjects(IReadOnlyList<ProjectModel> Projects, bool TestsChanged, bool PackagesChanged);

/// <summary>One folder or solution open in a window, or a window's nothing before one opens: what the folder has (its
/// solution read, its files indexed and watched, Local History, run configurations, breakpoints, bookmarks, the last
/// build's problems, recent files and the places Back goes to, what it keeps in the store) and what it runs (the language
/// server, Git, its builds and programs, one in the terminal, the debugger, the profiler's run, its tests), made as the
/// folder opens and ended together as it is left (<see cref="LeaveAsync"/>) or its window closes (<see cref="CloseAsync"/>),
/// so nothing of one folder outlives it into the next. The window builds its controllers over the session it has and binds
/// its views to it. A UI-thread object.</summary>
public sealed class ProjectSession : IAsyncDisposable
{
    /// <summary>How many recently opened files Go to File offers before anything is typed.</summary>
    public const int RecentLimit = 25;

    private readonly SessionSetup setup;
    private readonly List<string> recent = [];

    /// <summary>Bumped by each read of the folder, so a walk for projects that lands after a later read is dropped.</summary>
    private int reads;

    private Task? ending;
    private bool closing;

    private ProjectSession(SessionSetup setup, string? folder)
    {
        this.setup = setup;
        Folder = folder;
        Configurations = new RunConfigurations(folder, () => Workspace);
        Aspire = new AspireController(this, setup.StateFolder);
        AspireFiles = new AspireFiles(this);
        Builder = new ProcessRunner(setup.Views.Build);
        Runner = new ProcessRunner(setup.Views.Run, takesInput: true);
        Tests = new TestController(setup.Views.TestTree, setup.BuildForTests, () => Projects, () => Workspace, line => setup.Views.Build.Append($"{line}\n")) { Work = setup.Work };
        Containers = new Containers.FolderContainers(folder, setup.Engine());
        Vcs.Work = setup.Work;
        // A XAML file's localized strings read from the .resx and XLIFF files as the editor has them.
        Xaml.Snapshots = () => setup.Editors.Texts().Open;
        Xaml.Evaluations = path => evaluations.GetValueOrDefault(path);
        Debug = new DebugController(setup.Editors, setup.Views.Debug)
        {
            RunInTerminal = setup.RunForDebugger,
            Folder = folder,
            Server = () => CSharp?.Session,
            HotReloadOn = () => setup.Settings().HotReloadWhileDebugging,
            HotReloadOnSave = () => setup.Settings().HotReloadOnSave,
        };
        Debug.SelectionChanged += (_, _) =>
        {
            Aspire.SelectDebugger(Debug.Chosen);
            LiveAppChanged?.Invoke(this, EventArgs.Empty);
            InspectedChanged?.Invoke(this, EventArgs.Empty);
        };
        Aspire.Changed += (_, _) =>
        {
            LiveAppChanged?.Invoke(this, EventArgs.Empty);
            InspectedChanged?.Invoke(this, EventArgs.Empty);
        };
        // Kept with the folder from Keep on, while the window keeps state.
        Debug.Breakpoints.Changed += (_, _) => State?.Breakpoints.Set(Debug.Breakpoints.Capture());
        Debug.Breakpoints.ExceptionsChanged += (_, _) => State?.Breakpoints.Set(Debug.Breakpoints.Capture());
        Debug.Breakpoints.BindingErrorsChanged += (_, _) => State?.Breakpoints.Set(Debug.Breakpoints.Capture());
        Debug.TabsChanged += (_, _) => OnDebugTabsChanged();
        Bookmarks.Changed += (_, _) => State?.Bookmarks.Set(Bookmarks.Capture());
        // What a Git operation changes in the files is one change set of the folder's Local History, under its name.
        Vcs.Activity = name => LocalHistory?.During(name);
        Models.Changed += (_, changed) => OnEvaluated(changed);
        Packages = new FolderPackages(() => Projects, () => Solution?.Path is { } solution ? Path.GetDirectoryName(solution) : Folder)
        {
            Work = setup.Work,
            SaveFirst = () => setup.Editors.SaveAll(),
        };
        MSBuild = new MSBuild.MSBuildService(() => Folder, () => Workspace)
        {
            Feed = () => Packages.FeedAsync(null),
            Vulnerabilities = () => Packages.VulnerabilitiesAsync(),
        };
        DesignTimeBuilds = new ProjectBuilds(LoadOrderAsync, () => Workspace is { } open ? ProjectModels.Properties(open) : new Dictionary<string, string>());
    }

    /// <summary>A window's session with no folder open: files may still be open, alone.</summary>
    public static ProjectSession None(SessionSetup setup) => new(setup, null);

    /// <summary>Opens a folder (canonical) with its solution read, or the one named, or none: its Local History, index and
    /// watcher, its configurations, and the language server on its projects; one without a solution is walked for them.
    /// The projects are as the folder's cache in the store has them (<paramref name="kept"/>, when the window keeps state)
    /// until MSBuild has evaluated them again (<see cref="Evaluate"/>).</summary>
    public static ProjectSession Open(SessionSetup setup, string folder, string? named, WorkspaceModel? withSolution, ProjectState? kept = null)
    {
        ProjectSession session = new(setup, folder)
        {
            NamedSolution = named,
            LocalHistory = History.LocalHistory.Open(folder, setup.StateFolder, setup.HistoryKeeping),
        };
        // Where the log's timing of the language server's start (CSharpService) begins.
        IdeErrors.Write($"Opened {folder}{(withSolution is { } read && read.OpenedAs != folder ? $" with {Path.GetFileName(read.OpenedAs)}" : string.Empty)}");
        // A folder without a solution has no projects until the walk for them lands: none to load, run or build meanwhile.
        session.reads++;
        session.Models.Open(folder, kept?.ProjectModels);
        // Its projects' design-time builds as kept, which its language server loads them from.
        session.DesignTimeBuilds.Open(folder, kept?.ProjectBuilds);
        session.openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        session.Workspace = session.Models.Apply(withSolution ?? new WorkspaceModel(folder, null, []));
        session.TimeSolutionView();
        session.Configurations.Configure(session.Projects);
        // The single-file AppHosts are found among the files the index's walk lists, never by a walk of their own.
        session.Index.Walked += (_, _) => session.AspireFiles.Walked(folder, session.Index.Files);
        session.Index.Build(folder);
        session.Watcher = new FolderWatcher(folder, setup.Watching);
        session.Web = new Web.WebLanguages(folder, setup.Settings, setup.Engine) { DockerHub = setup.DockerHub };
        // A compose file's check reads what each .NET service's project reads, once the language server has loaded it.
        session.Web.Checks.Code = () => new Containers.ComposeCode(session.Projects, session.CSharp is { IsReady: true } csharp ? csharp.Session : null);
        session.Web.Checks.GapsChanged += (_, _) => session.CSharp?.ComposeGapsChanged();
        return session;
    }

    /// <summary>The folder open, canonical; null for a window with none.</summary>
    public string? Folder { get; }

    /// <summary>The solution or project opened by name, which a folder with several keeps over the solution its name would
    /// pick.</summary>
    public string? NamedSolution { get; private set; }

    /// <summary>The folder's solution and projects, read when it opens and again when a solution or project changes; null
    /// with no folder.</summary>
    public WorkspaceModel? Workspace
    {
        get => workspace;
        private set
        {
            workspace = value;
            FollowEvaluations();
        }
    }

    private WorkspaceModel? workspace;

    /// <summary>What each evaluated project says of where its restore and build put things, by its path, replaced whole,
    /// so the XAML service reads it from any thread.</summary>
    private volatile IReadOnlyDictionary<string, ProjectEvaluation> evaluations =
        new Dictionary<string, ProjectEvaluation>(PathNames.Comparer);

    /// <summary>The projects' evaluations taken anew, and the XAML of a project whose evaluation moved something read again.</summary>
    private void FollowEvaluations()
    {
        Dictionary<string, ProjectEvaluation> now = new(PathNames.Comparer);
        foreach (ProjectModel project in workspace?.Projects ?? [])
        {
            if (project.Evaluated is { } evaluated)
            {
                now[project.Path] = new ProjectEvaluation(
                    evaluated.Properties.GetValueOrDefault(Passenger.Host.CarriedProperties.ProjectAssetsFile)?.Value,
                    evaluated.Properties.GetValueOrDefault(Passenger.Host.CarriedProperties.NuGetPackageRoot)?.Value,
                    project.Packages.Where(p => p.Version is { Length: > 0 }).DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(p => p.Name, p => p.Version!, StringComparer.OrdinalIgnoreCase),
                    evaluated.Properties.GetValueOrDefault(Passenger.Host.CarriedProperties.TargetPath)?.Value);
            }
        }
        IReadOnlyDictionary<string, ProjectEvaluation> was = evaluations;
        evaluations = now;
        string[] changed = [.. now.Where(p => !was.TryGetValue(p.Key, out ProjectEvaluation? before) || !Same(before, p.Value)).Select(p => p.Key)];
        if (changed.Length > 0)
        {
            Xaml.EvaluationsChanged(changed);
        }

        static bool Same(ProjectEvaluation a, ProjectEvaluation b) =>
            a.AssetsFile == b.AssetsFile && a.PackageRoot == b.PackageRoot && a.TargetPath == b.TargetPath && a.Versions.Count == b.Versions.Count
            && a.Versions.All(v => b.Versions.TryGetValue(v.Key, out string? version) && version == v.Value);
    }

    public SolutionModel? Solution => Workspace?.Solution;

    /// <summary>The projects unloaded in the Solution view, which the folder keeps.</summary>
    public ISet<string> Unloaded { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The workspace's projects, but those unloaded.</summary>
    public IReadOnlyList<ProjectModel> Projects => Workspace is null ? [] : [.. Workspace.Projects.Where(p => !Unloaded.Contains(p.Path))];

    /// <summary>The loaded project a file or folder (a canonical path) belongs to, the nearest one holding it; null outside
    /// them all.</summary>
    public ProjectModel? ProjectOf(string path) => Workspace is null ? null : ProjectModel.Owning(Workspace.Projects.Where(p => !Unloaded.Contains(p.Path)), path);

    /// <summary>The configuration of the project a file belongs to (<see cref="ConfigurationFiles"/>); null outside them all.</summary>
    public ConfigurationProject? ConfigurationOf(string path) => ProjectOf(path) is { } project ? ConfigurationOf(project) : null;

    /// <summary>A project's configuration: its evaluated UserSecretsId, the folders from its own up to the folder open whose
    /// compose files may run it, and the folders of the AppHosts that reference it.</summary>
    public ConfigurationProject ConfigurationOf(ProjectModel project)
    {
        List<string> composing = [];
        for (string? folder = Path.GetDirectoryName(project.Path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            composing.Add(folder);
            if (Folder is null || !PathNames.IsUnder(folder, Folder) || PathNames.Same(folder, Folder))
            {
                break;
            }
        }
        string[] hosts = [.. Projects.Where(p => p.IsAspireHost && p.ProjectReferences.Any(r => PathNames.Same(r, project.Path))).Select(p => Path.GetDirectoryName(p.Path)!)];
        return new ConfigurationProject(project.Path, project.Property(Passenger.Host.CarriedProperties.UserSecretsId) is { Length: > 0 } id ? id : null, composing, hosts);
    }

    /// <summary>The projects whose configuration a settings file gives: an appsettings*.json's, the project whose folder holds
    /// it; a secrets.json under the user secrets' folder, those whose UserSecretsId names its folder.</summary>
    public IReadOnlyList<ConfigurationProject> ConfiguredBy(string path)
    {
        if (ConfigurationFiles.IsSettings(path))
        {
            return ProjectOf(path) is { } project && PathNames.Same(Path.GetDirectoryName(project.Path)!, Path.GetDirectoryName(path)!) ? [ConfigurationOf(project)] : [];
        }
        if (ConfigurationFiles.SecretsIdOf(path) is not { } id)
        {
            return [];
        }
        string secrets = PathNames.Canonical(UserSecrets.FileOf(id)!);
        return [.. Projects.Select(ConfigurationOf).Where(c => c.SecretsIn(EditorArea.ReadText) is { } own && PathNames.Same(own, secrets))];
    }

    /// <summary>The name of the project a file belongs to, as Find and Problems group by it; outside them all,
    /// Miscellaneous Files.</summary>
    public string ProjectNameOf(string path) => ProjectOf(path) is { } owner ? Path.GetFileNameWithoutExtension(owner.Path) : "Miscellaneous Files";

    /// <summary>The read of the folder under way, done once it has landed: a folder without a solution is walked for its
    /// projects on a worker, which an agent's Open waits for before it says what the workspace holds.</summary>
    public Task WorkspaceRead { get; private set; } = Task.CompletedTask;

    /// <summary>The folder's documents in the store, while the window keeps them; null otherwise.</summary>
    public ProjectState? State { get; private set; }

    /// <summary>Every file of the folder, as Go to File and Find in Files search them.</summary>
    public FileIndex Index { get; } = new();

    /// <summary>What another program changes in the folder, seen at once; null with no folder.</summary>
    public FolderWatcher? Watcher { get; private set; }

    /// <summary>The folder's Local History; null with no folder.</summary>
    public LocalHistory? LocalHistory { get; private set; }

    /// <summary>The files opened lately, the latest first, which Go to File and Recent Files list.</summary>
    public IReadOnlyList<string> RecentFiles => recent;

    /// <summary>The places Back and Forward go to, and those edited.</summary>
    public NavigationHistory Navigation { get; } = new();

    /// <summary>Which regions each file opens folded.</summary>
    public FoldMemory Folds { get; } = new();

    /// <summary>Find in Files' scope as its popup left it last.</summary>
    public FindScopeState FindScope { get; set; } = new();

    /// <summary>The rows of the Solution view Find in Files was opened on last, when they are a scope of their own.</summary>
    public (string Name, IReadOnlyList<string> Files, IReadOnlyList<string> Folders)? ChosenScope { get; set; }

    public RunConfigurations Configurations { get; }

    public AspireController Aspire { get; }

    /// <summary>The folder's AppHosts, and what each C# file is to them.</summary>
    public AspireFiles AspireFiles { get; }

    /// <summary>Done once the folder's single-file AppHosts have been read from its first walk.</summary>
    public Task AspireFilesRead => Folder is null ? Task.CompletedTask : AspireFiles.Read;

    /// <summary>The folder's builds, rebuilds and cleans, in the window's Build console.</summary>
    public ProcessRunner Builder { get; }

    /// <summary>The program Run started in the window's Run console, while it runs or since it last did.</summary>
    public ProcessRunner Runner { get; }

    /// <summary>The errors and warnings the last build found, each once.</summary>
    public List<Problem> Problems { get; } = [];

    /// <summary>The program Run last started in a tab of the Terminal window, until a run in the Run console comes after it.</summary>
    public TerminalRun? InTerminal { get; set; }

    /// <summary>The program Run started last, wherever it runs: in a tab of the terminal, else the Run console's (a container
    /// run's log there too).</summary>
    public IRunningProgram RunProgram => InTerminal is { } terminal ? terminal : Runner;

    /// <summary>The plan Run last started a program by, in the Run console or the terminal; null after a container run.</summary>
    public LaunchPlan? RunPlan { get; set; }

    /// <summary>The container configuration Run brought up, its log followed by <see cref="Runner"/>, until Stop takes it
    /// down (or the next run comes); its containers are the folder's (<see cref="Containers"/>) until then.</summary>
    public Containers.ContainerRun? ContainerRun { get; set; }

    /// <summary>The starts of runs of services under way (Run's, Debug's or Profile's of a container configuration or an Aspire
    /// AppHost), each from its first moment until it is up or has given up: a start of what one starts ends it first, and the
    /// folder's end ends them all, as Stop does.</summary>
    public List<Run.StartUnderWay> Starting { get; } = [];

    /// <summary>Stop of a container run (the one under way, or <paramref name="which"/>, one a start made): its log no longer
    /// followed, and its containers taken down (the .NET services alone when backing services are kept, which then go with
    /// the folder); done once the engine has answered. A run another has replaced is only taken down, its own containers
    /// alone, the one under way left as it is.</summary>
    public Task StopContainersAsync(Containers.ContainerRun? which = null)
    {
        UiThread.Check();
        if ((which ?? ContainerRun) is not { } run)
        {
            return Task.CompletedTask;
        }
        if (run != ContainerRun)
        {
            return run.StopAsync(run.IsStopped ? null : run.EndSessionsAsync(Debug));
        }
        Runner.Stop();
        // The services debugged and profiled end first, or the adapter fails and the trace loses its rundown.
        Task stopped = run.StopAsync(run.IsStopped ? null : run.EndSessionsAsync(Debug));
        if (!run.Plan.KeepBacking || run.Plan.Backing.Count == 0)
        {
            Containers.EndedProject(run.Plan.Compose);
        }
        return stopped;
    }

    /// <summary>The run being profiled, or null.</summary>
    public ProfilingSession? Profiling { get; set; }

    /// <summary>The program Profile started, from its hand-over until its end has landed in the Run console, its snapshot
    /// taken or not: Kill, Stop and the folder's end end it.</summary>
    public ProfiledProgram? Profiled
    {
        get => profiled;
        set
        {
            profiled = value;
            ProfiledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ProfiledProgram? profiled;

    /// <summary>Profile's program was handed over or has ended: the toolbar's Stop follows.</summary>
    public event EventHandler? ProfiledChanged;

    /// <summary>Ends the Profile still starting (its program running, not yet at the profiler, so not yet <see cref="Profiling"/>),
    /// with the folder as Kill ends it; null when none is.</summary>
    public CancellationTokenSource? ProfileStarting { get; set; }

    /// <summary>The folder's unit tests: those its test projects declare, their runs and their results.</summary>
    public TestController Tests { get; }

    /// <summary>The folder's debugger, its breakpoints with it.</summary>
    public DebugController Debug { get; }

    /// <summary>The Avalonia or WPF app of the Debug tab chosen, which the Live Visual Tree, binding values and agents' app
    /// tools inspect; null while the chosen tab's program is another, or no tab is. Each tab holds its own app
    /// (<see cref="ToolWindows.DebugTab.App"/>), ended with its debugging, so several debugged at once each keep theirs.</summary>
    public LiveApp? LiveApp => Aspire.SelectedService is not null ? Aspire.SelectedInspection : Debug.Chosen?.App;

    /// <summary>The chosen Debug tab's app as <see cref="LiveAppChanged"/> last told it.</summary>
    private LiveApp? liveAppTold;

    /// <summary>Another app is being inspected, or none.</summary>
    public event EventHandler? LiveAppChanged;

    /// <summary>A plan as Debug launches it, and the app it is: an Avalonia or WPF app's carries the inspector the Live Visual
    /// Tree talks to (its startup hook, and a socket of the app's own), which its Debug tab holds; any other's is the plan
    /// itself, with no app.</summary>
    public (LaunchPlan Plan, LiveApp? App) Inspected(LaunchPlan plan)
    {
        UiThread.Check();
        LiveApp? app = LiveTree.LiveApp.For(plan, Overlays);
        return (app?.Plan ?? plan, app);
    }

    /// <summary>A Debug tab came, went or was chosen: the chosen tab's app may be another, and the apps whose failed bindings
    /// Problems lists others.</summary>
    private void OnDebugTabsChanged()
    {
        if (IsEnded)
        {
            return;
        }
        if (LiveApp != liveAppTold)
        {
            liveAppTold = LiveApp;
            LiveAppChanged?.Invoke(this, EventArgs.Empty);
        }
        InspectedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Avalonia or WPF app Run started last, inspected for its failed bindings and agents' tools (the Live Visual
    /// Tree follows the debugger's); null after any other program, or one Hot Reload runs. It ends with its run, and with the
    /// folder.</summary>
    public LiveApp? RunApp { get; private set; }

    /// <summary>A plan as Run launches it: an Avalonia or WPF app's carries the inspector, as Debug's does, unless Hot Reload
    /// runs it, whose dotnet watch is a .NET program of its own that would load the inspector first.</summary>
    public LaunchPlan InspectedRun(LaunchPlan plan, bool hotReload)
    {
        UiThread.Check();
        RunApp?.Dispose();
        RunApp = hotReload ? null : LiveTree.LiveApp.For(plan, Overlays);
        InspectedChanged?.Invoke(this, EventArgs.Empty);
        return RunApp?.Plan ?? plan;
    }

    /// <summary>The Avalonia app Profile started last, which records its frames for the profiler while it is profiled and
    /// shows the overlays the Run window sets (its output is the Run window's); null after any other program. It ends with
    /// its run, and with the folder.</summary>
    public LiveApp? ProfiledApp { get; private set; }

    /// <summary>A plan as Profile launches it: an Avalonia app's carries the inspector, as Run's does; a WPF app's none, as
    /// what it records (frames, overlays) is Avalonia's.</summary>
    public LaunchPlan InspectedProfile(LaunchPlan plan)
    {
        UiThread.Check();
        ProfiledApp?.Dispose();
        ProfiledApp = LiveTree.LiveApp.For(plan, Overlays, wpf: false);
        InspectedChanged?.Invoke(this, EventArgs.Empty);
        return ProfiledApp?.Plan ?? plan;
    }

    /// <summary>Avalonia's renderer debug overlays (by the names of its flags) an inspected launch shows from its first
    /// window: the launch option the Run and Debug windows' overlay toggles leave, so a launch after shows what the one
    /// before was left showing.</summary>
    public IReadOnlyList<string> Overlays { get; set; } = [];

    /// <summary>Run, Debug or Profile launched another app, inspected or not.</summary>
    public event EventHandler? InspectedChanged;

    /// <summary>The bindings that failed in the inspected apps Problems lists (<see cref="InspectedApps"/>), each once with
    /// its count: those of an app that ended stay until the next Run, or while its tab is there.</summary>
    public IEnumerable<(LiveApp App, BindingFailure Failure)> FailedBindings =>
        InspectedApps.SelectMany(app => app.Bindings.Select(failure => (app, failure)));

    /// <summary>The apps the folder's launches put Passenger's inspector in, an app once: Run's last, Profile's last, each
    /// Debug tab's, and each service an AppHost runs; ended ones too, until the next of their kind. Problems, agents' app
    /// tools and the overlays each take those they act on from it.</summary>
    public IReadOnlyList<InspectedApp> Inspecting => [.. new[]
        {
            RunApp is { } run ? new InspectedApp(run, RunningKind.Run, "run") : null,
            ProfiledApp is { } profiled ? new InspectedApp(profiled, RunningKind.Profile, "profile") : null,
        }.OfType<InspectedApp>()
        .Concat(Debug.Apps.Select(a => new InspectedApp(a.App, RunningKind.Debug, a.Tab.Name) { Tab = a.Tab }))
        .Concat(Aspire.Apps.SelectMany(app => app.Services.Where(service => service.Inspection is not null)
            .Select(service => new InspectedApp(service.Inspection!, RunningKind.Service, app.NameOf(service)))))
        .DistinctBy(a => a.App)];

    /// <summary>The inspected apps whose failed bindings Problems lists: all but Profile's, whose output is the profiler's.</summary>
    public IEnumerable<LiveApp> InspectedApps => Inspecting.Where(a => a.Kind != RunningKind.Profile).Select(a => a.App);

    /// <summary>The runs of services under way: the container run's, and each Aspire AppHost's the window started.</summary>
    public IReadOnlyList<ServiceRun> ServiceRuns => [.. ContainerRun is { IsStopped: false } containers ? new ServiceRun[] { containers } : [],
        .. Aspire.Apps.Where(app => !app.IsTest && !app.IsStopped)];

    /// <summary>What the folder runs now, a program once: Run's (in the Run console or the terminal, while Run's plan is its),
    /// Profile's until its end has landed, each live Debug tab's, and each started .NET service of the runs of services under
    /// way. The one list the Requests window, the routes' addresses and Attach Profiler read.</summary>
    public IReadOnlyList<RunningProgram> Running
    {
        get
        {
            List<RunningProgram> running = [];
            if (RunProgram is { IsRunning: true } run && RunPlan is { } plan)
            {
                running.Add(new RunningProgram(RunningKind.Run, plan.Name, plan.Project) { Process = run.ProcessId, Plan = plan, App = RunApp });
            }
            if (Profiled is { } program && !program.Landed.IsCompleted)
            {
                running.Add(new RunningProgram(RunningKind.Profile, program.Name, null)
                {
                    Process = program.Run.Place is null ? program.Run.ProcessId : null,
                    App = ProfiledApp,
                });
            }
            foreach (DebugTab tab in Debug.Tabs.Where(t => t.IsLive && t.Session is not null))
            {
                Task<int> started = tab.Session!.ProcessStarted;
                bool local = tab.Session.IsLocal;
                running.Add(new RunningProgram(RunningKind.Debug, tab.Name, tab.Plan?.Project)
                {
                    Process = local && started.IsCompletedSuccessfully ? started.Result : null,
                    Starting = local && !started.IsCompleted ? started : null,
                    Plan = tab.Plan,
                    App = tab.App,
                    Tab = tab,
                });
            }
            foreach (ServiceRun services in ServiceRuns)
            {
                running.AddRange(services.Services.Where(s => s.Starts && !services.LeftUnstarted(s)).Select(service =>
                    new RunningProgram(RunningKind.Service, services.NameOf(service), service.Project)
                    {
                        Services = services,
                        Service = service,
                        App = (service as AspireResourceRun)?.Inspection,
                    }));
            }
            return running;
        }
    }

    /// <summary>The folder's Git working tree, once its lookup (<see cref="Start"/>) has found one.</summary>
    public VcsController Vcs { get; } = new();

    public BookmarkStore Bookmarks { get; } = new();

    /// <summary>What the folder had the container engine start, and the labels it goes on with: ended with the folder.</summary>
    public Containers.FolderContainers Containers { get; }

    /// <summary>Where the container tools come from that reach a program in a container, for Attach and a container run alike.</summary>
    public Containers.ContainerToolsSource ContainerTools => setup.ContainerTools();

    /// <summary>What the folder's .http requests may take from the person's computer to a host, as they answered.</summary>
    public Endpoints.EndpointGrants EndpointGrants { get; } = new();

    /// <summary>The routes the folder's web projects declare in their code, as the language server's extension last read them.</summary>
    public Endpoints.RouteCatalog Routes { get; } = new();

    /// <summary>The profile each project last published with, which Publish Again publishes it with.</summary>
    public PublishMemory Publishing { get; } = new();

    /// <summary>The language server on the folder's projects, or null for a folder with none (or no folder).</summary>
    public CSharpService? CSharp { get; private set; }

    /// <summary>The XAML of the folder's projects, read by Passenger's own XAML for each XAML file open.</summary>
    public Xaml.XamlService Xaml { get; } = new();

    /// <summary>The code help in the folder's project, .props and .targets files, from MSBuild's evaluation in the project host.</summary>
    public MSBuild.MSBuildService MSBuild { get; }

    /// <summary>The folder's NuGet: its package sources, feeds and known vulnerabilities, and the one way its projects'
    /// packages change, which the NuGet window, MSBuild files' code help, Migrations and an agent all use.</summary>
    public FolderPackages Packages { get; }

    /// <summary>JavaScript, TypeScript, HTML, CSS and JSON for the folder's files, each kind's language server started as
    /// its first file opens; null with no folder.</summary>
    public Web.WebLanguages? Web { get; private set; }

    /// <summary>A language server started (or none, after one ended) in place of the one before.</summary>
    public event EventHandler? CSharpChanged;

    /// <summary>The folder's solution or projects were read again: what the Explorer, the run configurations and the tests
    /// show follows.</summary>
    public event EventHandler? WorkspaceChanged;

    /// <summary>The folder's projects as MSBuild evaluates them, in the project host.</summary>
    public ProjectModels Models { get; } = new();

    /// <summary>The folder's projects as the project host builds them at design time, which the language server loads.</summary>
    public ProjectBuilds DesignTimeBuilds { get; }

    /// <summary>The projects the language server loads, those it loads first (<see cref="ActiveProjectsAsync"/>) apart.</summary>
    private async Task<(IReadOnlyList<string> First, IReadOnlyList<string> All)> LoadOrderAsync()
    {
        IReadOnlyList<string> first = await ActiveProjectsAsync();
        return (first, [.. Projects.Where(p => p.Exists).Select(p => p.Path).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Projects evaluated (or evaluated again) landed in the workspace: what the Solution view shows of them, and
    /// whether which projects hold tests or what packages they reference changed.</summary>
    public event EventHandler<EvaluatedProjects>? ProjectsEvaluated;

    /// <summary>When the folder opened, which its first complete Solution view is timed from; 0 once it is.</summary>
    private long openedAt;

    /// <summary>Has each project of the workspace evaluated that needs it, the file shown's first.</summary>
    public void Evaluate()
    {
        if (!IsEnded && Workspace is { } following)
        {
            Models.Follow(following, setup.Editors.Active?.Path);
        }
    }

    /// <summary><see cref="ActiveProjects"/> once the evaluation has said what each of them references (the file shown's
    /// project is evaluated first), which the language server waits a few seconds for at most.</summary>
    private async Task<IReadOnlyList<string>> ActiveProjectsAsync()
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!Models.Idle.IsCompleted && DateTime.UtcNow < until && ActiveProjects(evaluated: true) is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        return ActiveProjects() ?? [];
    }

    /// <summary>The project of the file shown and every project it references, through their references, as MSBuild
    /// evaluated them: what the language server opens before the rest. None when that is every project, or nothing is shown;
    /// null, asked for <paramref name="evaluated"/> ones only, while one of them is not evaluated yet.</summary>
    private IReadOnlyList<string>? ActiveProjects(bool evaluated = false)
    {
        if (Workspace is not { } open || setup.Editors.Active?.Path is not { } shown || ProjectModel.Owning(open.Projects, shown) is not { } owner)
        {
            return [];
        }
        Dictionary<string, ProjectModel> byPath = open.Projects.DistinctBy(p => p.Path).ToDictionary(p => p.Path, StringComparer.Ordinal);
        List<string> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<string> next = new([owner.Path]);
        while (next.TryDequeue(out string? path))
        {
            if (!seen.Add(path) || !byPath.TryGetValue(path, out ProjectModel? project))
            {
                continue;
            }
            if (evaluated && project.Evaluated is null)
            {
                return null;
            }
            found.Add(path);
            foreach (string reference in project.ProjectReferences)
            {
                next.Enqueue(reference);
            }
        }
        return found.Count < byPath.Count ? found : [];
    }

    /// <summary>Evaluations landed: the workspace and the run configurations take them in, and the window hears which.</summary>
    private void OnEvaluated(IReadOnlyCollection<string> changed)
    {
        if (IsEnded || Workspace is not { } before)
        {
            return;
        }
        Workspace = Models.Apply(before);
        Configurations.Configure(Projects);
        // A project evaluated again is loaded again by the language server too, which watches only some imports.
        CSharp?.Reload([.. before.Projects.Where(p => changed.Contains(p.Path) && p.Evaluated is { Complete: true }).Select(p => p.Path).Distinct(StringComparer.Ordinal)]);
        bool tests = !before.Projects.Where(p => p.IsTestProject).Select(p => p.Path).SequenceEqual(Workspace.Projects.Where(p => p.IsTestProject).Select(p => p.Path));
        bool packages = before.Projects.Zip(Workspace.Projects).Any(p => changed.Contains(p.First.Path) && !p.First.Packages.SequenceEqual(p.Second.Packages));
        ProjectsEvaluated?.Invoke(this, new EvaluatedProjects([.. Workspace.Projects.Where(p => changed.Contains(p.Path))], tests, packages));
        Web?.Checks.CodeChanged();
        MSBuild.ProjectsEvaluated();
        TimeSolutionView();
    }

    /// <summary>The log's phase of a folder's opening: the Solution view with every project as MSBuild evaluates it.</summary>
    private void TimeSolutionView()
    {
        if (openedAt != 0 && Workspace is { } shown && shown.Projects.Where(p => p.Exists).All(p => p.Evaluated is { Complete: true }))
        {
            IdeErrors.Write($"Solution view shows the {shown.Projects.Count} projects of {shown.Name} as MSBuild evaluates them {System.Diagnostics.Stopwatch.GetElapsedTime(openedAt).TotalSeconds:0.00} s after the folder opened ({Models.Evaluations} evaluated)");
            openedAt = 0;
        }
    }

    /// <summary>Whether the session has been left, or its window closed.</summary>
    public bool IsEnded => closing || ending is not null;

    /// <summary>The folder's documents from now on: what the store kept of the folder's own (its breakpoints, bookmarks,
    /// configurations, Find in Files' scope, unloaded projects, folds and recent files) taken first, so restoring them writes
    /// nothing back.</summary>
    public void Keep(ProjectState kept)
    {
        UiThread.Check();
        Debug.Breakpoints.Restore(kept.Breakpoints.Value);
        Bookmarks.Restore(kept.Bookmarks.Value);
        EndpointGrants.Keep(kept.Endpoints);
        Publishing.Keep(kept.Publish);
        FindScope = kept.Find.Value;
        WorkspaceState workspace = kept.Workspace.Value;
        foreach (string project in workspace.UnloadedProjects)
        {
            Unloaded.Add(project);
        }
        State = kept;
        Configurations.Keep(kept.RunConfigurations, Projects);
        Folds.Restore(workspace.Folds);
        foreach (string file in workspace.RecentFiles.Where(File.Exists).Reverse())
        {
            Opened(file);
        }
    }

    /// <summary>A file the editor opened: the latest of the recent files.</summary>
    public void Opened(string path)
    {
        recent.Remove(path);
        recent.Insert(0, path);
        if (recent.Count > RecentLimit)
        {
            recent.RemoveAt(recent.Count - 1);
        }
    }

    /// <summary>A file the editor opened: the language server, the debugger, Git, the bookmarks and the places follow it,
    /// its gutter showing their marks.</summary>
    public void Attach(OpenDocument document)
    {
        CSharp?.Attach(document);
        Web?.Attach(document);
        Xaml.Attach(document);
        MSBuild.Attach(document);
        Debug.Attach(document);
        Vcs.Attach(document);
        Bookmarks.Attach(document.Path, document.Text);
        Navigation.Attach(document.Path, document.Text);
    }

    /// <summary>A file the editor closed, which they follow no more.</summary>
    public void Detach(OpenDocument document)
    {
        CSharp?.Detach(document);
        Web?.Detach(document);
        Xaml.Detach(document);
        MSBuild.Detach(document);
        Debug.Detach(document);
        Vcs.Detach(document);
        Bookmarks.Detach(document.Path);
        Navigation.Detach(document.Path);
    }

    /// <summary>A file or folder moved: its bookmarks and breakpoints, Git, the recent files, the places, Local History and
    /// the solution or project opened by name follow it, and the language server knows an open file at its new path.</summary>
    public void Moved(FileMove move)
    {
        if (NamedSolution is { } named)
        {
            NamedSolution = PathNames.Moved(named, move.From, move.To);
        }
        Vcs.Moved(move.From, move.To);
        LocalHistory?.Moved(move.From, move.To);
        Followed(move);
    }

    /// <summary>A folder another program moved, which open files followed: its bookmarks and breakpoints, the recent
    /// files and the places follow it too, and the language servers know the open files at their new paths. Git and
    /// Local History read that move from the disk themselves.</summary>
    public void Followed(FileMove move)
    {
        Bookmarks.Move(move.From, move.To);
        Debug.Breakpoints.Move(move.From, move.To);
        Navigation.Move(move.From, move.To);
        for (int i = 0; i < recent.Count; i++)
        {
            recent[i] = PathNames.Moved(recent[i], move.From, move.To);
        }
        foreach (OpenDocument document in move.Documents)
        {
            CSharp?.Detach(document);
            CSharp?.Attach(document);
            Web?.Detach(document);
            Web?.Attach(document);
            Xaml.Detach(document);
            Xaml.Attach(document);
            MSBuild.Detach(document);
            MSBuild.Attach(document);
        }
    }

    /// <summary>Reads the folder's solution again (after a package or project change, here or on disk), keeping the run
    /// configuration chosen when it is still there.</summary>
    public void ReadAgain()
    {
        if (Folder is not null)
        {
            Read(WorkspaceModel.ReadSolution(Folder, NamedSolution));
        }
    }

    /// <summary>The same folder opened again, with the solution or project named this time, or none: the folder is read
    /// again with it, and the language server starts again only when what it loads changed.</summary>
    public void Reopen(string? named, WorkspaceModel? withSolution)
    {
        NamedSolution = named;
        Read(withSolution);
    }

    /// <summary>Takes the folder's solution, read now; or for a folder without one the projects it had, read again now,
    /// and those added or gone since once a worker has walked its folders for them.</summary>
    private void Read(WorkspaceModel? withSolution)
    {
        reads++;
        Reload(withSolution ?? Workspace!.Reread());
        if (withSolution is null)
        {
            WalkForProjects();
        }
    }

    /// <summary>Starts what a folder runs once the window has bound its views to the session: Git's lookup, the language
    /// server, and for a folder without a solution the walk for its projects.</summary>
    public void Start(bool walk)
    {
        IdeErrors.Observe(Vcs.OpenAsync(Folder!));
        StartCSharp();
        if (walk)
        {
            WalkForProjects();
        }
        // Once the files it opens with are open, so the one shown's project is evaluated first.
        UiThread.Post(Evaluate);
    }

    /// <summary>Walks the folder for its projects off the window's thread, as background work, and takes what it finds when
    /// it differs from what the session has, unless another read came first or the session was left.</summary>
    private void WalkForProjects()
    {
        string walking = Folder!;
        string? named = NamedSolution;
        int walk = ++reads;
        TaskCompletionSource landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkspaceRead = landed.Task;
        // Held through the walk, then by the crossing, which lets go of what it can never deliver.
        IDisposable held = BackgroundWork.Scope();
        IdeErrors.Observe(Task.Run(() => WorkspaceModel.Read(walking, named)).ContinueWith(walked =>
        {
            // Seen here, so a walk that failed after another read began is no unobserved task's error later.
            _ = walked.Exception;
            UiThread.Post(() =>
            {
                try
                {
                    if (walk == reads && !IsEnded && walked.GetAwaiter().GetResult() is { } found && !Workspace!.LoadsAs(found))
                    {
                        Reload(found);
                    }
                }
                finally
                {
                    landed.TrySetResult();
                }
            });
            held.Dispose();
        }, TaskScheduler.Default));
    }

    /// <summary>Takes a new read of the folder. The language server starts again only when what it loaded changed: another
    /// solution, or other projects.</summary>
    private void Reload(WorkspaceModel read)
    {
        bool loaded = Workspace is not null && Workspace.LoadsAs(read);
        Workspace = Models.Apply(read);
        Configurations.Configure(Projects);
        if (!loaded)
        {
            StartCSharp();
        }
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        // Each project whose evaluation no longer stands (its file or an import changed) is evaluated again.
        Evaluate();
    }

    /// <summary>The language server on the workspace, in place of the one before, the files open its too; none for a
    /// folder with neither a solution nor a project, nor in a window that starts none.</summary>
    private void StartCSharp()
    {
        IdeErrors.Observe(CSharp?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        CSharp = null;
        if (!IsEnded && setup.StartsLanguageServer && Workspace is { } opening && (opening.Solution is not null || opening.Projects.Count > 0))
        {
            CSharp = new CSharpService(setup.WarmServer)
            {
                SolutionWide = setup.SolutionWide(),
                FirstProjects = ActiveProjectsAsync,
                Feed = DesignTimeBuilds.Feed,
                // Razor's HTML is the HTML server's to answer.
                HtmlServer = Web is { } web ? web.HtmlAsync : null,
                AspireRoleOf = AspireFiles.RoleOf,
                AspireAppHostOf = path => AspireFiles.AppHostOf(path) is { } host
                    ? new AspireAppHostSettings(host, Projects.FirstOrDefault(p => PathNames.Same(p.Path, host))?.Property(Passenger.Host.CarriedProperties.UserSecretsId))
                    : null,
                ComposeGaps = () => Web?.Checks.Gaps ?? [],
                ConfigurationOf = ConfigurationOf,
                Texts = setup.Editors.Texts,
            };
            CSharpChanged?.Invoke(this, EventArgs.Empty);
            CSharpService started = CSharp;
            bool loaded = false;
            started.StateChanged += (_, _) =>
            {
                // Loaded, the projects' code can be read for what their compose files' services need.
                if (started.IsReady != loaded && (loaded = started.IsReady) && CSharp == started)
                {
                    Web?.Checks.CodeChanged();
                }
            };
            CSharp.Start(opening);
            foreach (OpenDocument open in setup.Editors.Documents)
            {
                CSharp.Attach(open);
            }
            return;
        }
        CSharpChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Leaves the folder while its window stays: everything it runs ends (a program the debugger attached to is
    /// let go, running on, as the window's close lets it go), and the task finishes once each end has landed (a stopped
    /// build's outcome, a killed program's exit, said in the window's views before another folder takes them) and the
    /// session has let go of the window's views, or <see cref="MainWindow.EndLimit"/> after, each late one named in the
    /// log. What else ends with it (the folder's terminals, the window's) is waited for alike.</summary>
    public Task LeaveAsync(params (string What, Task Ending)[] with)
    {
        UiThread.Check();
        if (ending is not null)
        {
            return ending;
        }
        Task debuggers = Debug.LeaveAsync();
        List<(string What, Task Ending)> parts = [.. with, ("the debuggers", debuggers)];
        // Their ends are posted to the window's thread, which carries on while it waits.
        if (Builder.IsRunning)
        {
            parts.Add(("the build", Builder.Ended));
        }
        if (RunProgram.IsRunning)
        {
            parts.Add((RunProgram == InTerminal ? "the program in the terminal" : "the program", RunProgram.Ended));
        }
        if (Profiled is { } program)
        {
            parts.Add(("the profiled program", program.Landed));
        }
        Task ended = Within(MainWindow.EndLimit, Folder, End(parts, debuggers));
        ending = LetGoOnceEnded();
        return ending;

        async Task LetGoOnceEnded()
        {
            await ended;
            LetGo();
        }
    }

    /// <summary>Ends what the folder runs as its window closes: programs and builds killed, the rest asked to end, and the
    /// task finishes once those have, or <see cref="MainWindow.EndLimit"/> after. It needs no UI thread once begun, so the
    /// process can wait for it after the window's loop has stopped.</summary>
    public Task CloseAsync(params (string What, Task Ending)[] with)
    {
        UiThread.Check();
        if (ending is not null)
        {
            return ending;
        }
        Task debuggers = Debug.CloseAsync();
        ending = Within(MainWindow.EndLimit, Folder, End([.. with, ("the debuggers", debuggers)], debuggers));
        LetGo();
        return ending;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>What ends at once (the store's documents, the watcher, Local History, the bookmarks', places' and breakpoints'
    /// hold on the files left open, programs, builds, the profiled run and its port, a test run), and what is asked to end:
    /// the test runner and the language server, with the parts given.</summary>
    private List<(string What, Task Ending)> End(List<(string What, Task Ending)> parts, Task debuggers)
    {
        closing = true;
        State = null;
        Models.Close();
        DesignTimeBuilds.Close();
        Watcher?.Dispose();
        LocalHistory?.Release();
        // Files left open are the next session's to follow; what each store kept of them stays where it was carried.
        Bookmarks.DetachAll();
        Navigation.DetachAll();
        Debug.Breakpoints.DetachAll();
        // A build stopped now says so with nothing of this folder's in Problems, which another folder's take next.
        Problems.Clear();
        parts.Add(("the Aspire applications", Aspire.DisposeAsync().AsTask()));
        RunProgram.Stop();
        Builder.Stop();
        Packages.Stop();
        ProfileStarting?.Cancel();
        foreach (Run.StartUnderWay start in Starting.ToList())
        {
            start.Work.Cancel();
        }
        // Profile's program ends with the others, and each run, read by no one now, leaves no port listening.
        Profiled?.Run.Kill();
        foreach (ProfilingSession run in new[] { Profiling, Profiled?.Run }.OfType<ProfilingSession>().Distinct())
        {
            // Not held, and off the window's thread, whose loop may stop first: nothing it does is drawn.
            parts.Add(("the profiler", Task.Run(() => run.LetGoAsync().AsTask())));
        }
        Profiling = null;
        foreach ((_, LiveApp app) in Debug.Apps)
        {
            app.Dispose();
        }
        RunApp?.Dispose();
        ProfiledApp?.Dispose();
        Tests.Stop();
        parts.Add(("the test runner", Tests.DisposeAsync().AsTask()));
        parts.Add(("the language server", CSharp?.DisposeAsync().AsTask() ?? Task.CompletedTask));
        CSharp = null;
        parts.Add(("the web language servers", Web?.DisposeAsync().AsTask() ?? Task.CompletedTask));
        Web = null;
        // No child holds a container: the engine ends what the folder started, once Compose has stopped making them.
        Task abandoned = ContainerRun?.Abandon() ?? Task.CompletedTask;
        parts.Add(("the folder's containers", Containers.EndAsync(Task.WhenAll(debuggers, abandoned))));
        Xaml.Dispose();
        MSBuild.Dispose();
        return parts;
    }

    /// <summary>The session lets go of the window's consoles, Debug window and open files, which the next session's runners,
    /// debugger and Git take.</summary>
    private void LetGo()
    {
        Builder.Dispose();
        Runner.Dispose();
        Debug.Dispose();
        Vcs.Dispose();
    }

    /// <summary>Waits for what is ending, for at most the limit, naming in the log what had not ended by then.</summary>
    private static Task Within(TimeSpan limit, string? folder, IReadOnlyList<(string What, Task Ending)> parts)
    {
        if (parts.All(p => p.Ending.IsCompleted))
        {
            return Task.CompletedTask;
        }
        // Not held: nothing it does is drawn, and the process waits for it once the window's loop has stopped.
        return Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(parts.Select(p => p.Ending)).WaitAsync(limit);
            }
            catch (TimeoutException)
            {
                string late = string.Join(" and ", parts.Where(p => !p.Ending.IsCompleted).Select(p => p.What));
                IdeErrors.Write($"Closing{(folder is null ? string.Empty : $" {folder}")}: {late} had not ended after {limit.TotalSeconds:0} s");
            }
            catch (Exception error)
            {
                IdeErrors.Write($"Closing{(folder is null ? string.Empty : $" {folder}")}: ending what it started failed: {error}");
            }
        });
    }
}

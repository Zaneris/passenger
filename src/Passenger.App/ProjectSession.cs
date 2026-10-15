using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.History;
using Passenger.App.LiveTree;
using Passenger.App.Navigation;
using Passenger.App.Run;
using Passenger.App.State;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Profiler;

namespace Passenger.App;

/// <summary>What a window lends each session it makes: its editors (whose open files the session's services follow), where
/// its state and Local History are kept, how folders are watched, the warm language server a test's windows share, the
/// consoles, Debug window and tests tree its runners, debugger and test runner write to, the build a test run starts with,
/// the tab of the Terminal window a program the debugger launches there runs in, and the settings as they stand (the
/// Node.js the web language servers run on, whether remote JSON schemas are downloaded).</summary>
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
    Func<Settings.AppSettings> Settings);

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

    private ProjectSession(SessionSetup setup, string? folder)
    {
        this.setup = setup;
        Folder = folder;
        Configurations = new RunConfigurations(folder);
        Builder = new ProcessRunner(setup.Views.Build);
        Runner = new ProcessRunner(setup.Views.Run, takesInput: true);
        Tests = new TestController(setup.Views.TestTree, setup.BuildForTests, () => Projects, line => setup.Views.Build.Append($"{line}\n"));
        Debug = new DebugController(setup.Editors, setup.Views.Debug)
        {
            RunInTerminal = setup.RunForDebugger,
            Folder = folder,
            Server = () => CSharp?.Session,
            Inspector = () => LiveApp,
            HotReloadOn = () => setup.Settings().HotReloadWhileDebugging,
            HotReloadOnSave = () => setup.Settings().HotReloadOnSave,
        };
        // Kept with the folder from Keep on, while the window keeps state.
        Debug.Breakpoints.Changed += (_, _) => State?.Breakpoints.Set(Debug.Breakpoints.Capture());
        Debug.Breakpoints.ExceptionsChanged += (_, _) => State?.Breakpoints.Set(Debug.Breakpoints.Capture());
        Debug.Breakpoints.BindingErrorsChanged += (_, _) => State?.Breakpoints.Set(Debug.Breakpoints.Capture());
        Bookmarks.Changed += (_, _) => State?.Bookmarks.Set(Bookmarks.Capture());
        // What a Git operation changes in the files is one change set of the folder's Local History, under its name.
        Vcs.Activity = name => LocalHistory?.During(name);
        Models.Changed += (_, changed) => OnEvaluated(changed);
        MSBuild = new MSBuild.MSBuildService(() => Folder, () => Workspace);
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
        session.Index.Build(folder);
        session.Watcher = new FolderWatcher(folder, setup.Watching);
        session.Web = new Web.WebLanguages(folder, setup.Settings);
        return session;
    }

    /// <summary>The folder open, canonical; null for a window with none.</summary>
    public string? Folder { get; }

    /// <summary>The solution or project opened by name, which a folder with several keeps over the solution its name would
    /// pick.</summary>
    public string? NamedSolution { get; private set; }

    /// <summary>The folder's solution and projects, read when it opens and again when a solution or project changes; null
    /// with no folder.</summary>
    public WorkspaceModel? Workspace { get; private set; }

    public SolutionModel? Solution => Workspace?.Solution;

    /// <summary>The projects unloaded in the Solution view, which the folder keeps.</summary>
    public ISet<string> Unloaded { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The workspace's projects, but those unloaded.</summary>
    public IReadOnlyList<ProjectModel> Projects => Workspace is null ? [] : [.. Workspace.Projects.Where(p => !Unloaded.Contains(p.Path))];

    /// <summary>The loaded project a file or folder (a canonical path) belongs to, the nearest one holding it; null outside
    /// them all.</summary>
    public ProjectModel? ProjectOf(string path) => ProjectModel.Owning(Projects, path);

    /// <summary>The name of the project a file belongs to, as Find and Problems group by it; outside them all, Rider's
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

    /// <summary>The folder's builds, rebuilds and cleans, in the window's Build console.</summary>
    public ProcessRunner Builder { get; }

    /// <summary>The program Run started in the window's Run console, while it runs or since it last did.</summary>
    public ProcessRunner Runner { get; }

    /// <summary>The errors and warnings the last build found, each once.</summary>
    public List<Problem> Problems { get; } = [];

    /// <summary>The program Run last started in a tab of the Terminal window, until a run in the Run console comes after it.</summary>
    public TerminalRun? InTerminal { get; set; }

    /// <summary>The run being profiled, or null.</summary>
    public ProfilingSession? Profiling { get; set; }

    /// <summary>The folder's unit tests: those its test projects declare, their runs and their results.</summary>
    public TestController Tests { get; }

    /// <summary>The folder's debugger, its breakpoints with it.</summary>
    public DebugController Debug { get; }

    /// <summary>The Avalonia or WPF app the debugger launched last, which the Live Visual Tree inspects; null after any other
    /// program. It ends with its debugging, and with the folder.</summary>
    public LiveApp? LiveApp { get; private set; }

    /// <summary>Another app is being inspected, or none.</summary>
    public event EventHandler? LiveAppChanged;

    /// <summary>A plan as Debug launches it: an Avalonia or WPF app's carries the inspector the Live Visual Tree talks to (its
    /// startup hook, and a socket of the app's own), any other's is the plan itself.</summary>
    public LaunchPlan Inspected(LaunchPlan plan)
    {
        UiThread.Check();
        LiveApp?.Dispose();
        LiveApp = LiveTree.LiveApp.For(plan, Overlays);
        LiveAppChanged?.Invoke(this, EventArgs.Empty);
        InspectedChanged?.Invoke(this, EventArgs.Empty);
        return LiveApp?.Plan ?? plan;
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

    /// <summary>The bindings that failed in the apps Run and Debug launched last, each once with its count: those of an app
    /// that ended stay until the next of its kind launches.</summary>
    public IEnumerable<(LiveApp App, BindingFailure Failure)> FailedBindings =>
        new[] { RunApp, LiveApp }.OfType<LiveApp>().SelectMany(app => app.Bindings.Select(failure => (app, failure)));

    /// <summary>The folder's Git working tree, once its lookup (<see cref="Start"/>) has found one.</summary>
    public VcsController Vcs { get; } = new();

    public BookmarkStore Bookmarks { get; } = new();

    /// <summary>The language server on the folder's projects, or null for a folder with none (or no folder).</summary>
    public CSharpService? CSharp { get; private set; }

    /// <summary>The XAML of the folder's projects, read by Passenger's own XAML for each XAML file open.</summary>
    public Xaml.XamlService Xaml { get; } = new();

    /// <summary>The code help in the folder's project, .props and .targets files, from MSBuild's evaluation in the project host.</summary>
    public MSBuild.MSBuildService MSBuild { get; }

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
        // A project evaluated again, its file or what it imports changed: the language server loads it again too, as it
        // watches only some of what a project imports.
        CSharp?.Reload([.. before.Projects.Where(p => changed.Contains(p.Path) && p.Evaluated is { Complete: true }).Select(p => p.Path).Distinct(StringComparer.Ordinal)]);
        bool tests = !before.Projects.Where(p => p.IsTestProject).Select(p => p.Path).SequenceEqual(Workspace.Projects.Where(p => p.IsTestProject).Select(p => p.Path));
        bool packages = before.Projects.Zip(Workspace.Projects).Any(p => changed.Contains(p.First.Path) && !p.First.Packages.SequenceEqual(p.Second.Packages));
        ProjectsEvaluated?.Invoke(this, new EvaluatedProjects([.. Workspace.Projects.Where(p => changed.Contains(p.Path))], tests, packages));
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
    public bool IsEnded => ending is not null;

    /// <summary>The folder's documents from now on: what the store kept of the folder's own (its breakpoints, bookmarks,
    /// configurations, Find in Files' scope, unloaded projects, folds and recent files) taken first, so restoring them writes
    /// nothing back.</summary>
    public void Keep(ProjectState kept)
    {
        UiThread.Check();
        Debug.Breakpoints.Restore(kept.Breakpoints.Value);
        Bookmarks.Restore(kept.Bookmarks.Value);
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

    /// <summary>A file the editor opened: the language server, the debugger and Git follow it, its gutter showing their
    /// marks.</summary>
    public void Attach(OpenDocument document)
    {
        CSharp?.Attach(document);
        Web?.Attach(document);
        Xaml.Attach(document);
        MSBuild.Attach(document);
        Debug.Attach(document);
        Vcs.Attach(document);
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
    }

    /// <summary>A file or folder moved: its bookmarks and breakpoints, Git, the recent files, the places and Local History
    /// follow it, and the language server knows an open file at its new path.</summary>
    public void Moved(FileMove move)
    {
        Bookmarks.Move(move.From, move.To);
        Debug.Breakpoints.Move(move.From, move.To);
        Vcs.Moved(move.From, move.To);
        Navigation.Move(move.From, move.To);
        LocalHistory?.Moved(move.From, move.To);
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
        _ = Vcs.OpenAsync(Folder!);
        StartCSharp();
        if (walk)
        {
            WalkForProjects();
        }
        // Once the files it opens with are open (those kept, those the command line names), so the one shown's project
        // is evaluated first.
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
        Task.Run(() => WorkspaceModel.Read(walking, named)).ContinueWith(walked =>
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
        }, TaskScheduler.Default);
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
    /// folder with neither a solution nor a project.</summary>
    private void StartCSharp()
    {
        _ = CSharp?.DisposeAsync();
        CSharp = null;
        if (!IsEnded && Workspace is { } opening && (opening.Solution is not null || opening.Projects.Count > 0))
        {
            CSharp = new CSharpService(setup.WarmServer)
            {
                SolutionWide = setup.SolutionWide(),
                FirstProjects = ActiveProjectsAsync,
                Feed = DesignTimeBuilds.Feed,
                // Razor's HTML is the HTML server's to answer.
                HtmlServer = Web is { } web ? web.HtmlAsync : null,
            };
            CSharpChanged?.Invoke(this, EventArgs.Empty);
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
        List<(string What, Task Ending)> parts = [.. with, ("the debugger", Debug.LeaveAsync())];
        // Their ends are posted to the window's thread, which carries on while it waits.
        if (Builder.IsRunning)
        {
            parts.Add(("the build", Builder.Ended));
        }
        if (Runner.IsRunning)
        {
            parts.Add(("the program", Runner.Ended));
        }
        if (InTerminal is { IsRunning: true } inTerminal)
        {
            parts.Add(("the program in the terminal", inTerminal.Ended));
        }
        Task ended = Within(MainWindow.EndLimit, Folder, End(parts));
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
        ending = Within(MainWindow.EndLimit, Folder, End([.. with, ("the debugger", Debug.CloseAsync())]));
        LetGo();
        return ending;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>What ends at once (the store's documents, the watcher, Local History, programs, builds, the profiled run, a
    /// test run), and what is asked to end: the test runner and the language server, with the parts given.</summary>
    private List<(string What, Task Ending)> End(List<(string What, Task Ending)> parts)
    {
        State = null;
        Models.Close();
        DesignTimeBuilds.Close();
        Watcher?.Dispose();
        LocalHistory?.Release();
        // A build stopped now says so with nothing of this folder's in Problems, which another folder's take next.
        Problems.Clear();
        Runner.Stop();
        Builder.Stop();
        InTerminal?.Stop();
        Profiling?.Kill();
        LiveApp?.Dispose();
        RunApp?.Dispose();
        ProfiledApp?.Dispose();
        Tests.Stop();
        parts.Add(("the test runner", Tests.DisposeAsync().AsTask()));
        parts.Add(("the language server", CSharp?.DisposeAsync().AsTask() ?? Task.CompletedTask));
        CSharp = null;
        parts.Add(("the web language servers", Web?.DisposeAsync().AsTask() ?? Task.CompletedTask));
        Web = null;
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

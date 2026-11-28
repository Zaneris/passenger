using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Passenger.App.Aspire;
using Passenger.App.Containers;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.App.Settings;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>What a window lends the builds and runs of its sessions: the Build, Run, Problems and Debug windows, its
/// editors (saved before a build), its tool windows' frame, its settings, the status bar, a tab of the Terminal window
/// for a program that uses one, the browser opened after launch, and its state folder, under which a Passenger it
/// launches keeps its own (<see cref="LaunchPlan.SandboxedState"/>).</summary>
public sealed record BuildRunParts(
    ToolViews Views,
    EditorArea Editors,
    DockHost Dock,
    Func<AppSettings> Settings,
    StatusSay Say,
    Func<string, ProcessStartInfo, Task<TerminalRun>> InTerminal,
    Action<LaunchPlan, Task> OpenBrowser,
    Func<string> StateFolder,
    ActionWork Work,
    Func<RunConfiguration, CancellationToken, Task<Words?>>? PrepareAspire = null)
{
    /// <summary>Shows a Compose project in the Containers window, chosen there, as a container run comes up.</summary>
    public Action<string>? Reveal { get; init; }

    /// <summary>A project run's dashboard went: the Containers window's Telemetry tab follows.</summary>
    public Action? TelemetryChanged { get; init; }

    /// <summary>The dotnet <c>dev-certs</c> runs through for a run that serves HTTPS (containers' or Aspire's): the machine's,
    /// or a test's stand-in.</summary>
    public DotnetWords? DevCertificates { get; init; }

    /// <summary>Asks the person whether to make and trust the development certificate (true) or run over HTTP (false), given
    /// the configuration, the services that serve HTTPS and whether there is none at all; the question closed unanswered
    /// (false) once the start is stopped.</summary>
    public Func<string, string, bool, CancellationToken, Task<bool>>? AskCertificate { get; init; }

    /// <summary>Opens a folder in the file manager: where a publish put the app, from the status bar's word that it did.</summary>
    public Action<string>? ShowFolder { get; init; }

    /// <summary>A run of a project (its path and name) whose Mac Catalyst build trims .NET's own assemblies: the window offers
    /// over the Run window to keep them whole in its Debug build.</summary>
    public Action<string, string>? Trimmed { get; init; }

    /// <summary>A program run with the Run window's console, or a Debug tab's, said it needs a terminal: the window offers over
    /// that console to start it in one.</summary>
    public Action<TerminalNeed, OutputView>? NeedsTerminal { get; init; }

    /// <summary>A build that needs rebuilding, saying why: the window offers a rebuild over the Build window's output.</summary>
    public Action<string>? OfferRebuild { get; init; }

    /// <summary>Lands the files a folder holds in another, as the window writes files (one command of Undo named by the
    /// title, for those in the open folder): the files written, or why not.</summary>
    public Func<string, string, string, Task<(IReadOnlyList<string> Written, string? Problem)>>? LandFiles { get; init; }

    /// <summary>Where the folder's global.json chose an SDK that does not build one of its projects, as the window says it;
    /// null where it did not.</summary>
    public Func<SdkChosen?>? SdkChosen { get; init; }

    /// <summary>Opens a file at a place in the editor: global.json, from the status bar's word that it chose the SDK.</summary>
    public Action<string, TextPosition>? Navigate { get; init; }

    /// <summary>Asks the person whether a container run may replace containers of its Compose project Passenger did not make
    /// for its folder (true), given the configuration, the project and those containers' names; closed unanswered (false)
    /// once the start is stopped.</summary>
    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<bool>>? AskReplace { get; init; }

    /// <summary>Plays a configuration whose game Godot's editor open on the project plays (<see cref="RunConfiguration.GodotInEditor"/>),
    /// under the debugger where asked: null once it plays, else why not.</summary>
    public Func<RunConfiguration, bool, CancellationToken, Task<Words?>>? InGodotEditor { get; init; }

    /// <summary>Stop's end of the games Godot's editor plays because Passenger asked it to.</summary>
    public Func<Task>? StopInGodotEditor { get; init; }

    /// <summary>Whether Godot's editor plays a game because Passenger asked it to.</summary>
    public Func<bool>? PlaysInGodotEditor { get; init; }

    /// <summary>Whether nobody is at the window to ask (an agent's own IDE, <c>--mcp</c>): what would ask the person takes the
    /// way that asks nothing, saying so.</summary>
    public bool AsksNoOne { get; init; }

    /// <summary>Asks how a start of a configuration that runs goes on (its name, and the start's verb: run, debug or
    /// profile): what runs of it stopped and it started again, only stopped, or left running, as it is once the start is
    /// stopped meanwhile.</summary>
    public Func<string, string, CancellationToken, Task<RunAgain>>? AskRunAgain { get; init; }

    /// <summary>Whether nobody sees the window (<see cref="MainWindow.Unseen"/>): every Godot game and editor it runs runs
    /// headless.</summary>
    public bool Unseen { get; init; }

    /// <summary>Has the window's profiler record a service's program of a run of services where its kind runs it (in its
    /// container, or on this machine), held at its start (a run's Profile, which the recording lets go) or as it runs (its
    /// last argument): null once it records, the run's Stop then reading its snapshot first, else why not.</summary>
    public Func<ServiceRun, IRunService, ServiceProgram, bool, CancellationToken, Task<Words?>>? ProfileService { get; init; }
}

/// <summary>A session's Build, Run and Debug as the window shows them: builds, rebuilds and cleans of the solution or of
/// projects one after another, or side by side for a start (the output in the Build window, what they find in Problems,
/// the window brought forward as Settings says), and the chosen run configuration resolved into a <see cref="LaunchPlan"/>,
/// built as it says and started in the Run window, a tab of the Terminal window or under the debugger. The window makes
/// one for each session it binds (<see cref="ProjectSession"/> owns the runners it drives), so a build or run never
/// outlives its folder. A UI-thread object.</summary>
public sealed partial class BuildRunController
{
    /// <summary>How many of a build's last lines its outcome carries.</summary>
    private const int BuiltLinesKept = 20;

    private readonly ProjectSession session;
    private readonly BuildRunParts parts;
    private readonly Queue<string> buildQueue = new();

    /// <summary>The build's last lines, which its outcome carries.</summary>
    private readonly Queue<string> builtLines = new();

    /// <summary>The problem the build's last line began, kept from Problems until a line that doesn't go on with its
    /// message, so a message of several lines is one problem, listed once however often MSBuild repeats it.</summary>
    private Problem? reading;

    /// <summary>The files the build's problems name, each as the disk spells it, found once a build.</summary>
    private readonly Dictionary<string, string> builtPaths = new(StringComparer.Ordinal);

    /// <summary>The texts of the files the build's problems are in, each read once a build: a compiler's line in a file
    /// Roslyn or a XAML compiler compiles is read there as the editor counts it (<see cref="ServerPlaces"/>).</summary>
    private Func<string, TextSnapshot?>? builtTexts;

    private BuildKind buildKind;
    private IReadOnlyList<string>? lastProjects;

    /// <summary>The projects the build under way builds side by side, and the last build's, which Rerun builds so again.</summary>
    private TogetherBuild? together;
    private TogetherBuild? lastTogether;

    /// <summary>Whether the projects queued for the build under way are put side by side once they are read as written
    /// (<see cref="JoinAsync"/>): a test run's.</summary>
    private bool joining;
    private string? buildFramework;
    private string? buildOutput;

    /// <summary>The global properties the build under way is given beyond its configuration's (<see cref="BuildStep.Properties"/>).</summary>
    private IReadOnlyList<string>? buildProperties;

    /// <summary>The workspace whose build configuration the build under way is given, a run's own (<see cref="ProjectSession.WorkspaceOfAsync"/>);
    /// null for the folder's, which a build of its own is given.</summary>
    private WorkspaceModel? buildConfigured;
    private Action<BuildOutcome>? afterBuild;

    /// <summary>What a person's build under way checked before each of its dotnet steps (<see cref="UpToDate"/>), kept once
    /// it succeeds as each project's last build here; null for a build that keeps nothing (a start's own, a clean, a publish).</summary>
    private List<Checked>? checking;

    /// <summary>The assemblies the build under way said it made ("Lib -> /src/Lib/bin/Debug/net10.0/Lib.dll"), by project.</summary>
    private readonly List<(string Project, string Path)> made = [];

    private TaskCompletionSource? building;

    /// <summary>The build under way as the window's actions show it, from its start to <see cref="FinishBuild"/>.</summary>
    private Working? buildWork;

    /// <summary>The run configuration whose program (or container run) the Run console, or a tab of the terminal, has now;
    /// forgotten once it has ended.</summary>
    private string ran = string.Empty;

    /// <summary>Where each failed binding of an app goes in Problems, found once, when it first fails.</summary>
    private readonly Dictionary<(LiveApp App, int Id), Problem> placed = [];

    /// <summary>The XAML texts the failures heard in one turn of the window's thread are placed in, each file read once
    /// a turn: an app's window tells up to a thousand failures of one file as it opens, each of which read it from the disk
    /// again. Let go at the next turn, so a failure told later reads the file as it is then.</summary>
    private Dictionary<string, TextSnapshot?>? failureTexts;

    public BuildRunController(ProjectSession session, BuildRunParts parts)
    {
        this.session = session;
        this.parts = parts;
        // A Debug tab that comes or goes brings its app's failed bindings to Problems, or takes them away.
        session.InspectedChanged += (_, _) => ShowBindings();
        session.GameStarted += (_, game) => FollowGame(game);
        session.Debug.ExceptionRead += (_, stopped) => HearThrown(stopped);
        session.Builder.Line += (_, line) =>
        {
            (Problem? next, bool continued) = BuildOutput.Next(reading, line, builtPaths);
            if (!continued)
            {
                ReadProblem();
            }
            reading = next;
            if (BuildOutput.Made(line) is { } assembly)
            {
                made.Add(assembly);
            }
            builtLines.Enqueue(line);
            if (builtLines.Count > BuiltLinesKept)
            {
                builtLines.Dequeue();
            }
            if (publishing is { } publish)
            {
                PublishStage(publish, line);
            }
        };
        session.Builder.Exited += (_, code) => OnBuilt(code);
        // An Aspire service held at its start is attached to as DCP starts it, through the run of services' one attach.
        session.Aspire.AttachStart = async (app, service, purpose, stop) =>
        {
            List<string> notes = [];
            Words? refused = await AttachStartAsync(app, service, purpose, notes, stop);
            foreach (string note in notes)
            {
                app.Console.Append($"{note}\n");
            }
            // What the start says of itself (a service its runtime's diagnostics keep from the debugger, which runs on) is said.
            if (notes.Count > 0 && !session.IsEnded)
            {
                parts.Say(string.Join("; ", notes));
            }
            return refused;
        };
        session.Aspire.Said += (_, said) =>
        {
            if (!session.IsEnded)
            {
                parts.Say(said);
            }
        };
        session.Aspire.Chosen = (configuration, service) =>
            chosenDebugged.TryGetValue(configuration, out Dictionary<string, bool>? choices) && choices.TryGetValue(service, out bool debugged) ? debugged : null;
        session.Aspire.Changed += (_, _) =>
        {
            if (!session.IsEnded)
            {
                ShowBindings();
                RunningChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        session.Aspire.BindingsChanged += (_, _) =>
        {
            if (!session.IsEnded)
            {
                ShowBindings();
            }
        };
        session.Runner.Exited += (_, code) =>
        {
            // A container run's log that ended by itself, its run not stopped, is followed again while its containers run.
            if (session.ContainerRun is { IsStopped: false } containers && !session.Runner.WasStopped && !session.IsEnded && session.Runner.Unstarted is null)
            {
                followingAgain = containers;
                BackgroundWork.Track(FollowContainersAgainAsync(containers, code));
                return;
            }
            RunLogEnded(code);
        };
    }

    /// <summary>The Run window's program ended (a container run's log, which Stop or its services' end ended): said, and Stop
    /// greyed.</summary>
    private void RunLogEnded(int code)
    {
        UnfollowWatched();
        parts.Views.Run.SetRunning(false);
        // The folder's last container run is said only while its log is what ran here, not a program run after it.
        string said = session.ContainerRun is { } containers && containers.Name == ran ? ContainersEnded(containers) : RunEnded(ran, code);
        // What ran has ended: no configuration of it runs, whatever else does (an AppHost).
        ran = string.Empty;
        RunningChanged?.Invoke(this, EventArgs.Empty);
        parts.Say(said, StatusPlace.Window(parts.Dock, ToolWindowIds.Run));
    }

    /// <summary>A program started or ended, or a launch was refused: the toolbar's Stop follows.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>A build ended, how it did said; after its caller has heard.</summary>
    public event EventHandler<BuildOutcome>? BuildFinished;

    /// <summary>What lets go of the projects' build output before a build's first step writes it, waited for once the
    /// design-time builds it meets have ended (the XAML previews, whose previewers hold it on Windows); its end is
    /// <see cref="BuildFinished"/>.</summary>
    public Func<Task>? LettingGo { get; set; }

    /// <summary>How a run that ended is named, in the status bar and to an agent.</summary>
    public static string RunEnded(string name, int code) => $"{name} ended with {ExitCodes.Said(code)}";

    /// <summary>Whether a build, rebuild or clean is under way, from its start until its callers have heard how it ended.</summary>
    public bool IsBuilding => building is not null;

    /// <summary>Whether a program Run started runs, in the Run window or the terminal, or an AppHost it started.</summary>
    public bool IsRunning => session.Aspire.IsRunning || RunsInConsole || copyRuns.Any(c => c.Run.IsRunning) || partRuns.Any(p => p.IsRunning);

    /// <summary>The copies of a game whose configuration runs in the terminal (<see cref="GameCopies"/>) and the projects a run
    /// of several parts runs there, each in a tab of the Terminal window, by its configuration's name and its place there:
    /// Stop ends them together.</summary>
    private readonly List<(string Configuration, int Copy, TerminalRun Run)> copyRuns = [];

    /// <summary>The starts of configurations by Run, Debug and Profile, each by its configuration's name, from the moment it
    /// asks <see cref="EndRunningAsync"/> to its hand-over (those ended passed over).</summary>
    private readonly List<(string Configuration, Working Work)> starts = [];

    /// <summary>Whether the Run console has a program Run started (a plain run's, or a container run's log), or a tab of the
    /// terminal has: one at a time, which Run and Rerun of a configuration that runs there stop first. An AppHost's is its own.</summary>
    private bool RunsInConsole => session.RunProgram.IsRunning || followingAgain is not null;

    /// <summary>Whether the program Run started runs under dotnet watch (Settings' Hot Reload when running), which applies
    /// the edits of each file saved.</summary>
    public bool WatchesHotReload => watching && IsRunning;

    private bool watching;

    /// <summary>The end of the program Run last started, in the Run window or the terminal, with its exit code.</summary>
    public Task<int> RunEnd => aspireEnd ?? session.RunProgram.Ended;

    private Task<int>? aspireEnd;

    /// <summary>The project folder the Run window's program came from and the Debug window's, where a relative path in
    /// their output is looked for first.</summary>
    public string? RunProject { get; private set; }

    public string? DebugProject { get; private set; }

    /// <summary>Builds as Build does, and says how it ended once it has: built, failed, stopped, refused or unable to start.
    /// Canceled, the build is stopped as the Build window's Stop stops it.</summary>
    public Task<BuildOutcome> BuildAsync(CancellationToken cancel) => BuildAsync(null, BuildCause.Explicit, cancel);

    /// <summary>A test run's build of the projects its tests are in and those they reference, side by side in one build
    /// where they can be (<see cref="JoinAsync"/>), each given the configuration and platform chosen as a build of it alone
    /// is; said as <see cref="BuildAsync(CancellationToken)"/> says it.</summary>
    public Task<BuildOutcome> BuildTestsAsync(IReadOnlyList<string> projects, CancellationToken cancel) => BuildAsync(projects, BuildCause.Tests, cancel);

    /// <summary>A build of these projects (the whole for none), begun now, its end heard as it ends; canceled, stopped.</summary>
    private Task<BuildOutcome> BuildAsync(IReadOnlyList<string>? projects, BuildCause cause, CancellationToken cancel)
    {
        TaskCompletionSource<BuildOutcome> built = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration stopping = default;
        Start(BuildKind.Build, projects, outcome =>
        {
            stopping.Dispose();
            built.TrySetResult(outcome);
        }, null, cause, null, null, joined: cause == BuildCause.Tests);
        if (!built.Task.IsCompleted)
        {
            // Canceled on the UI thread, by Stop, so the build it stops is the one begun here, still running.
            stopping = cancel.Register(session.Builder.Stop);
        }
        return built.Task;
    }

    /// <summary>The Build window's Rerun: the last build, rebuild, clean or publish again, of what it built.</summary>
    public void Rebuild()
    {
        if (buildKind == BuildKind.Publish && published is { } again)
        {
            IdeErrors.Observe(PublishAsync(again with { Folder = null }));
            return;
        }
        Start(buildKind, lastProjects, null, buildFramework, BuildCause.Explicit, null, null, buildOutput, lastTogether, buildConfigured, buildProperties);
    }

    /// <summary>Builds, rebuilds or cleans the solution, or the projects given one after another (stopping at the first
    /// that fails): the output in the Build window, which opens as it starts as Settings says for its <paramref name="cause"/>,
    /// the problems of all of them in Problems. <paramref name="then"/> always hears how it ended, a build refused or never
    /// started included. A framework given builds the projects for it alone, and an output folder given builds them there
    /// (a single-file AppHost's). A build or rebuild that succeeds is each project's last build here, for a launch's check.</summary>
    public void Build(BuildKind kind, IReadOnlyList<string>? projects = null, Action<BuildOutcome>? then = null, string? framework = null,
        BuildCause cause = BuildCause.Explicit, string? output = null) => Start(kind, projects, then, framework, cause, null, null, output);

    /// <summary>What a dotnet build of a target is given for the build configuration chosen (none for the one MSBuild takes):
    /// the run's own where the build under way is a run's, else the folder's.</summary>
    private IReadOnlyList<string> Configured(string target) => (buildConfigured ?? session.Workspace)?.BuildArgumentsFor(target) ?? [];

    /// <summary>Starts a build, or the publish given (its work begun already, which the build holds until it ends), or the
    /// build of projects side by side given (its project the one in <paramref name="projects"/>), under a run's own build
    /// configuration where <paramref name="configured"/> is given; projects given <paramref name="joined"/> are put side by
    /// side once read as written.</summary>
    private void Start(BuildKind kind, IReadOnlyList<string>? projects, Action<BuildOutcome>? then, string? framework, BuildCause cause, PublishRequest? publish,
        Working? work, string? output = null, TogetherBuild? side = null, WorkspaceModel? configured = null, IReadOnlyList<string>? properties = null,
        bool joined = false)
    {
        UiThread.Check();
        if (session.Folder is null || IsBuilding || session.IsEnded)
        {
            BuildOutcome refused = new(BuildStatus.Refused, (session.Folder is null || session.IsEnded ? Refusals.NoFolderTo("build") : Refusals.Building));
            work?.Dispose();
            parts.Say(refused.Message);
            then?.Invoke(refused);
            return;
        }
        publishing = publish;
        if (publish is not null)
        {
            published = publish;
        }
        buildKind = kind;
        lastProjects = projects;
        together = side;
        lastTogether = side;
        buildFramework = projects is { Count: > 0 } ? framework : null;
        buildOutput = projects is { Count: > 0 } ? output : null;
        buildConfigured = projects is { Count: > 0 } ? configured : null;
        buildProperties = projects is { Count: > 0 } ? properties : null;
        IReadOnlyList<string> whole = session.Workspace!.BuildTargets(session.Projects);
        buildQueue.Clear();
        // A folder with no solution or project of its own is built only where MSBuild would find one there.
        foreach (string target in projects is { Count: > 0 } ? projects : whole.Where(t => !PathNames.Same(t, session.Folder) || HoldsBuildable(t)))
        {
            buildQueue.Enqueue(target);
        }
        if (buildQueue.Count == 0)
        {
            BuildOutcome nothing = new(BuildStatus.Refused, (whole.Count == 1 && PathNames.Same(whole[0], session.Folder) ? Refusals.NothingToBuild : "Nothing to build: every project is unloaded"));
            publishing = null;
            together = null;
            work?.Tell(false, nothing.Message);
            work?.Dispose();
            parts.Say(nothing.Message);
            then?.Invoke(nothing);
            return;
        }
        joining = joined && side is null && projects is { Count: > 1 };
        afterBuild = then;
        // A start's own build checks and keeps its projects itself (BuildForAsync).
        checking = kind is BuildKind.Build or BuildKind.Rebuild && publish is null && side is null && cause != BuildCause.BeforeLaunch ? [] : null;
        made.Clear();
        string? unsaved = parts.Editors.SaveAllSaying();
        session.Problems.Clear();
        reading = null;
        builtTexts = null;
        builtPaths.Clear();
        builtLines.Clear();
        parts.Views.Build.Clear();
        parts.Views.Build.SetRunning(true);
        if (OpensBuildWindow(cause))
        {
            parts.Dock.Show(ToolWindowIds.Build);
        }
        building = new TaskCompletionSource();
        BackgroundWork.Track(building.Task);
        // A build is a start Stop ends, as the Build window's Stop does; held until it ends, whoever began it.
        buildWork = work ?? parts.Work.Begin(AppActions.Build, $"Building {together?.Shown ?? Path.GetFileName(buildQueue.Peek())}…");
        if (unsaved is not null)
        {
            // It would compile the text on disk, not what the editor shows.
            FinishBuild(new BuildOutcome(BuildStatus.NotStarted, unsaved));
        }
        else
        {
            IdeErrors.Observe(BuildAsSavedAsync());
        }
    }

    /// <summary>Starts the build once the saves still landing have (<see cref="EditorArea.LandedAsync"/>), so it compiles what
    /// was saved, and the projects stand as saved (<see cref="ProjectSession.ProjectsAsWrittenAsync"/>), so what it is given
    /// and checks by them (Windows targeting, a project's frameworks) is as written: one that never lands, or a stop
    /// meanwhile, ends the build unstarted, saying why.</summary>
    private async Task BuildAsSavedAsync()
    {
        Working? work = buildWork;
        CancellationToken stopping = work?.Stopping ?? CancellationToken.None;
        string stage = (work?.Stage ?? string.Empty).Shown;
        if (FileText.Landings().Count > 0 && await EditorArea.LandedAsync(work, stopping) is { } unsaved)
        {
            FinishBuild(work is { IsStopped: true } ? new BuildOutcome(BuildStatus.Stopped, "Build stopped") : new BuildOutcome(BuildStatus.NotStarted, unsaved));
            return;
        }
        try
        {
            await session.ProjectsAsWrittenAsync(said => work?.Say(said ?? stage), stopping);
            if (joining)
            {
                await JoinAsync();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Said below, as a stop while the projects' files were looked at is.
        }
        if (work is { IsStopped: true })
        {
            FinishBuild(new BuildOutcome(BuildStatus.Stopped, "Build stopped"));
        }
        else if (session.IsEnded)
        {
            FinishBuild(new BuildOutcome(BuildStatus.NotStarted, Refusals.NoFolderTo("build")));
        }
        else
        {
            BuildNext(first: true);
        }
    }

    /// <summary>Puts the projects queued for the build under way side by side in one build (<see cref="TogetherBuild"/>),
    /// each given what a build of it alone is and told of what any of them must be, so a project two of them reference is
    /// built once; where they cannot be, they stay queued one after another.</summary>
    private async Task JoinAsync()
    {
        joining = false;
        TaskCompletionSource? mine = building;
        List<string> projects = [.. buildQueue];
        string state = parts.StateFolder();
        string folder = session.Folder!;
        Func<string, IReadOnlyDictionary<string, string>> properties = (session.Workspace ?? new WorkspaceModel(folder, null, [])).BuiltTogetherWith(projects);
        TogetherBuild? side = await BackgroundWork.Run(() => TogetherBuild.Write(state, folder, projects, properties));
        if (side is not null && mine is not null && building == mine)
        {
            buildQueue.Clear();
            buildQueue.Enqueue(side.Project);
            together = side;
            lastTogether = side;
            lastProjects = [side.Project];
            // As for any build side by side, no project's last build is kept from it.
            checking = null;
        }
    }

    /// <summary>Whether a build opens the Build window as it starts, as Settings says: a test run's never; the status bar
    /// says what builds either way.</summary>
    private bool OpensBuildWindow(BuildCause cause)
    {
        AppSettings settings = parts.Settings();
        return settings.OpenBuildWindowOnBuild && cause switch
        {
            BuildCause.Explicit => true,
            BuildCause.BeforeLaunch => settings.OpenBuildWindowOnImplicitBuild,
            _ => false,
        };
    }

    /// <summary>Whether a build's end brings the Build window forward, as Settings says: one that could not start counts as
    /// failed, and one stopped as neither failed nor warned.</summary>
    private bool ShowsBuildResults(BuildOutcome ended) => parts.Settings().ShowBuildResults switch
    {
        BuildResultsMode.Always => true,
        BuildResultsMode.ForWarningsOrErrors => ended.Status is BuildStatus.Failed or BuildStatus.NotStarted
            || ended.Problems.Any(p => p.Severity == ProblemSeverity.Warning),
        BuildResultsMode.ForErrors => ended.Status is BuildStatus.Failed or BuildStatus.NotStarted,
        _ => false,
    };

    /// <summary>Starts the next project's build, the first once the design-time builds it would meet have ended
    /// (<see cref="HoldDesignTimeBuildsAsync"/>), and a person's once what it reads is checked (<see cref="CheckBeforeAsync"/>).</summary>
    private void BuildNext(bool first = false)
    {
        string target = buildQueue.Dequeue();
        string verb = buildKind switch { BuildKind.Rebuild => "Rebuilding", BuildKind.Clean => "Cleaning", BuildKind.Publish => "Publishing", _ => "Building" };
        string what = publishing is { } shown ? shown.Shown : together?.Shown ?? Path.GetFileName(target);
        parts.Say($"{verb} {what}…");
        buildWork?.Say($"{verb} {what}…");
        bool windows = WindowsTargeting(together?.Projects ?? [target]);
        IReadOnlyList<string> arguments = BuildOutput.TakenBy(publishing is { } publish ? publish.Arguments(windows)
            : BuildOutput.Arguments(target, buildKind, buildFramework, windows, buildOutput, [.. Configured(target), .. buildProperties ?? []]), session.Models.SdkVersion);
        Func<CancellationToken, Task>? after = first ? BeforeFirstStepAsync : null;
        if (checking is { } checks)
        {
            Func<CancellationToken, Task>? held = after;
            after = async stop =>
            {
                if (held is not null)
                {
                    await held(stop);
                }
                await CheckBeforeAsync(target, windows, checks, stop);
            };
        }
        if (session.Builder.StartDotnet(arguments, session.Folder!, publishing?.Environment, after: after) is { } problem)
        {
            FinishBuild(new BuildOutcome(BuildStatus.NotStarted, problem));
        }
    }

    /// <summary>A check of what a person's build reads, made before its dotnet step, and whether that step must show it built
    /// the project where a launch's build of it would (<see cref="Shows"/>): one of a solution's projects, or one framework
    /// of a project the step built for all its frameworks.</summary>
    private sealed record Checked(UpToDate.Check Check, bool MustShow);

    /// <summary>Checks what a person's build of a target reads, as a launch's build would be checked (<see cref="UpToDate"/>):
    /// a project or program built as a launch builds it, or each of the solution's projects a launch would build with the
    /// global properties the solution's build gives it; a runnable project of several frameworks built for none is checked
    /// as a launch builds each of them. Any other target, or a project its host cannot say, is not checked.</summary>
    private async Task CheckBeforeAsync(string target, bool windows, List<Checked> checks, CancellationToken stop)
    {
        List<(BuildStep Step, bool MustShow)> steps = session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, target)) is { } project
            ? buildProperties is not null ? []
            : buildFramework is null && buildOutput is null && Launched(project) is { } frameworks
                ? [.. frameworks.Select(framework => (new BuildStep(target, framework), true))]
                : [(new BuildStep(target, buildFramework, buildOutput), false)]
            : session.Workspace?.Solution is { } solution && PathNames.Same(solution.Path, target)
                ? [.. session.Projects.Where(p => !p.IsProgram && WindowsTargeting([p.Path]) == windows)
                    .SelectMany(p => Launched(p) is { } frameworks ? frameworks.Select(framework => new BuildStep(p.Path, framework)) : [new BuildStep(p.Path, null)])
                    .Select(step => (step, true))]
                : [];
        if (steps.Count == 0 || session.Folder is not { } folder || session.IsEnded || session.Workspace is not { } configured)
        {
            return;
        }
        // The host is asked of one framework at a time, each framework's projects side by side.
        List<List<(BuildStep Step, bool MustShow)>> asked = [.. steps.GroupBy(step => step.Step.Framework).Select(group => group.ToList())];
        IReadOnlyList<UpToDate.Check?>[] found = await BackgroundWork.Run(() => Task.WhenAll(asked.Select(group => UpToDate.CheckAsync([.. group.Select(step => step.Step)],
            [.. group.Select(step => BuildOutput.Arguments(step.Step.Project, BuildKind.Build, step.Step.Framework, windows, step.Step.Output, configured.BuildArgumentsFor(step.Step.Project)))],
            windows, folder, stop, configured.BuiltWith))));
        checks.AddRange(found.Zip(asked).SelectMany(each => each.First.Zip(each.Second)).Where(each => each.First is not null)
            .Select(each => new Checked(each.First!, each.Second.MustShow)));
    }

    /// <summary>The frameworks a launch builds a project of several for, one at a time; null for a project of one, or one
    /// nothing launches.</summary>
    private static IReadOnlyList<string>? Launched(ProjectModel project) => project.IsRunnable && project.TargetFrameworks.Count > 1 ? project.TargetFrameworks : null;

    /// <summary>Keeps what a person's build that succeeded checked (<see cref="UpToDate.BuiltAsync"/>), each of the solution's
    /// projects only where the build showed it built it as a launch's build would, the rest let go; then ends the build, so
    /// a launch begun meanwhile waits for what is kept.</summary>
    private async Task KeepAsync(List<Checked> checks, BuildOutcome ended)
    {
        checking = null;
        try
        {
            List<(string Project, string Path)> said = [.. made];
            CancellationToken stopping = buildWork?.Stopping ?? CancellationToken.None;
            await BackgroundWork.Run(() =>
            {
                List<(string Project, string Path)> assemblies = [.. said.Select(each => (each.Project, PathNames.Canonical(each.Path)))];
                List<UpToDate.Check> kept = [];
                foreach (Checked each in checks)
                {
                    if (!each.MustShow || Shows(each.Check, assemblies))
                    {
                        kept.Add(each.Check);
                    }
                    else
                    {
                        UpToDate.Forget(each.Check);
                    }
                }
                return Task.WhenAll(kept.Select(check => UpToDate.BuiltAsync(check, stopping)));
            });
        }
        finally
        {
            FinishBuild(ended);
        }
    }

    /// <summary>Whether a build said it made a project's assembly in the folder a launch's build of it puts it, and the
    /// assembly of each project it references where that build reads it: a solution gives each project the configuration
    /// and platform it maps it to, which may build it elsewhere, or not at all.</summary>
    private static bool Shows(UpToDate.Check check, List<(string Project, string Path)> assemblies)
    {
        string name = Path.GetFileNameWithoutExtension(check.Project);
        return check.Inputs.Output is { Length: > 0 } output && PathNames.Canonical(Path.TrimEndingDirectorySeparator(output)) is var folder
            && assemblies.Any(each => each.Project.Equals(name, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(each.Path) is { } at && PathNames.Same(at, folder))
            && check.Inputs.Referenced.All(reference => PathNames.Canonical(reference) is var path && assemblies.Any(each => PathNames.Same(each.Path, path)));
    }

    /// <summary>What a build's first step waits for: the design-time builds it would meet (<see cref="HoldDesignTimeBuildsAsync"/>),
    /// then what holds the build's output (<see cref="LettingGo"/>).</summary>
    private async Task BeforeFirstStepAsync(CancellationToken stop)
    {
        await HoldDesignTimeBuildsAsync(stop);
        if (LettingGo is { } letGo)
        {
            await letGo().WaitAsync(stop);
        }
    }

    /// <summary>Waits for the project host's design-time builds of WPF and Avalonia projects under way, the Build window
    /// saying which, and holds the rest off until the build ends (<see cref="FinishBuild"/>): both write a project's
    /// obj/…/App.g.cs or obj/…/Avalonia/resources, and on Windows the second writer fails (MC1000, MSB4018).</summary>
    private Task HoldDesignTimeBuildsAsync(CancellationToken stop) => session.DesignTimeBuilds.HoldAsync(running =>
    {
        if (session.Builder.IsRunning)
        {
            string which = running.Count == 1 ? $"build of {Path.GetFileNameWithoutExtension(running[0])}" : $"builds of {Numbers.Said(running.Count)} projects";
            parts.Views.Build.Append($"Waiting for the design-time {which} to end\n");
            buildWork?.Say($"Waiting for the design-time {which} to end…");
        }
    }, stop);

    /// <summary>Puts the problem being read in Problems at its line as the editor counts it, unless it is there already.</summary>
    private void ReadProblem()
    {
        if (reading is { } read && Placed(Chosen(read)) is var problem && !session.Problems.Contains(problem))
        {
            session.Problems.Add(problem);
        }
        reading = null;
    }

    /// <summary>A build's problem with why global.json's SDK does not build its project, where that is what it says.</summary>
    private Problem Chosen(Problem problem) =>
        problem.Code == Run.SdkChosen.TooOld && parts.SdkChosen?.Invoke() is { } chosen ? chosen.Placed(problem) : problem;

    /// <summary>A problem's place as the editor counts its file's lines (<see cref="Placed(Problem, Func{string, TextSnapshot?})"/>),
    /// its files' texts read once a build.</summary>
    private Problem Placed(Problem problem) => Placed(problem, builtTexts ??= ServerPlaces.Texts(parts.Editors));

    /// <summary>A problem's place as the editor counts its file's lines, from the lines its compiler counted
    /// (<see cref="ServerPlaces.Compiled"/>); the place a XAML compiler's message repeats at its end is said at the editor's
    /// line too.</summary>
    private static Problem Placed(Problem problem, Func<string, TextSnapshot?> texts)
    {
        if (problem.Path is not { } path || !ServerPlaces.CountedApart(path))
        {
            return problem;
        }
        TextPosition at = ServerPlaces.PositionOfCompiled(path, texts(path), new Lsp.LspPosition(problem.Position.Line, problem.Position.Column));
        return at == problem.Position ? problem : problem with
        {
            Position = at,
            Message = ServerPlaces.CountedAsXml(path) ? BuildOutput.Moved(problem.Message, problem.Position, at) : problem.Message,
        };
    }

    private void OnBuilt(int code)
    {
        ReadProblem();
        if (session.Builder.Unstarted is { } unstarted)
        {
            FinishBuild(new BuildOutcome(BuildStatus.NotStarted, unstarted));
            return;
        }
        if (code == 0 && buildQueue.Count > 0 && !session.Builder.WasStopped)
        {
            parts.Views.Build.Append("\n");
            BuildNext();
            return;
        }
        List<Problem> problems = session.Problems;
        int errors = problems.Count(p => p.Severity == ProblemSeverity.Error);
        int warnings = problems.Count - errors;
        string noun = buildKind switch { BuildKind.Rebuild => "Rebuild", BuildKind.Clean => "Clean", BuildKind.Publish => "Publish", _ => "Build" };
        string warned = warnings > 0 ? $" with {Plural.Of(warnings, "warning")}" : "";
        if (code == 0 && !session.Builder.WasStopped && publishing is { Kind: PublishKind.AppHost } appHost)
        {
            IdeErrors.Observe(LandAsync(appHost, warned));
            return;
        }
        BuildOutcome ended = session.Builder.WasStopped ? new BuildOutcome(BuildStatus.Stopped, $"{noun} stopped")
            : code == 0 && publishing is { } publish ? Published(publish, warned)
            : code == 0 ? new BuildOutcome(BuildStatus.Succeeded, $"{noun} succeeded{warned}")
            : problems.FirstOrDefault(Run.SdkChosen.IsPlaced) is { } chose ? new BuildOutcome(BuildStatus.Failed, $"{noun} failed: {chose.Message.Split('\n')[0]}")
            : new BuildOutcome(BuildStatus.Failed, $"{noun} failed{FailedIn(problems)}: {Plural.Of(errors, "error")}, {Plural.Of(warnings, "warning")}; "
                + (errors > 0 ? "Problems lists them" : $"dotnet ended with {ExitCodes.Said(code)}, the Build window says why"));
        if (ended.Succeeded && buildKind != BuildKind.Clean && CompilingXaml() is { Count: > 0 } xaml)
        {
            IdeErrors.Observe(CheckXamlAsync(xaml, ended));
            return;
        }
        Keep(ended);
    }

    /// <summary>Ends the build, a person's that succeeded once what it checked is kept (<see cref="KeepAsync"/>).</summary>
    private void Keep(BuildOutcome ended)
    {
        if (ended.Succeeded && checking is { Count: > 0 } checks)
        {
            IdeErrors.Observe(KeepAsync(checks, ended));
            return;
        }
        FinishBuild(ended);
    }

    /// <summary>The assemblies the build said it made of projects whose Avalonia XAML their builds compile, by project.</summary>
    private List<(string Project, string Path)> CompilingXaml() =>
        [.. made.Where(each => session.Projects.Any(p => p.CompilesAvaloniaXaml && Path.GetFileNameWithoutExtension(p.Path).Equals(each.Project, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>A build that succeeded, its assemblies of Avalonia projects looked at off the window's thread: one holding XAML
    /// never compiled (<see cref="Passenger.Xaml.Metadata.UncompiledXaml"/>), as an earlier build's C# compiler wrote it before
    /// Avalonia's XAML compiler failed, which this build copied out as it was, compiling nothing, ends it failed, a rebuild
    /// offered.</summary>
    private async Task CheckXamlAsync(List<(string Project, string Path)> assemblies, BuildOutcome ended)
    {
        List<string> uncompiled = await BackgroundWork.Run(() => assemblies.Where(each => Passenger.Xaml.Metadata.UncompiledXaml.In(each.Path)).Select(each => each.Project)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList());
        if (uncompiled.Count > 0 && !session.IsEnded)
        {
            ended = new BuildOutcome(BuildStatus.Failed, Refusals.XamlUncompiled(uncompiled));
            parts.OfferRebuild?.Invoke(ended.Message.Shown);
        }
        Keep(ended);
    }

    /// <summary>Where a build of projects side by side failed: " in" and the projects its errors name, so each service whose
    /// build failed is said, though their output came together; nothing for any other build.</summary>
    private string FailedIn(IEnumerable<Problem> problems) =>
        together is not null && problems.Where(p => p.Severity == ProblemSeverity.Error && p.Project is not null).Select(p => p.Project!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } named
            ? $" in {Plural.Listed(named)}"
            : string.Empty;

    /// <summary>Whether a build of these projects needs EnableWindowsTargeting, one of them or a project it references needing it.</summary>
    private bool WindowsTargeting(IReadOnlyList<string> projects) => projects.Any(p => BuildOutput.WindowsTargeting(p, session.Projects));

    /// <summary>The one way a build that began ends: its window, Problems and the status bar say how, its caller hears it
    /// with what it found, and the background work it held is let go.</summary>
    private void FinishBuild(BuildOutcome ended)
    {
        // A build that did not succeed leaves no last build standing for what it checked.
        checking?.ForEach(each => UpToDate.Forget(each.Check));
        checking = null;
        List<Problem> problems = session.Problems;
        BuildOutcome outcome = ended with { Problems = [.. problems], Output = [.. builtLines] };
        buildQueue.Clear();
        Action<BuildOutcome>? then = afterBuild;
        afterBuild = null;
        TaskCompletionSource? held = building;
        building = null;
        publishing = null;
        together = null;
        buildWork?.Tell(outcome.Succeeded, outcome.Message);
        buildWork?.Dispose();
        buildWork = null;
        builtTexts = null;
        session.DesignTimeBuilds.Release();
        parts.Views.Build.SetRunning(false);
        ShowProblems();
        session.Xaml.Built(session.Problems);
        session.CSharp?.Built();
        parts.Say(outcome.Message, Place(outcome));
        if (ShowsBuildResults(outcome))
        {
            parts.Dock.Show(ToolWindowIds.Build);
        }
        try
        {
            then?.Invoke(outcome);
            BuildFinished?.Invoke(this, outcome);
        }
        finally
        {
            held?.TrySetResult();
        }
    }

    /// <summary>Where a build's end has more to see: its problems in Problems' Build, the first chosen, or the Build window's
    /// output when it failed with none read; nowhere for a build that succeeded cleanly, was stopped or never started.</summary>
    public StatusPlace? Place(BuildOutcome ended) =>
        ended is { Succeeded: true, Published: { } folder } && parts.ShowFolder is { } show
            ? new StatusPlace($"open {Path.GetFileName(folder)} in {FileManager.Name}", () => show(folder))
        : ended.Status == BuildStatus.Failed && ended.Problems.FirstOrDefault(Run.SdkChosen.IsPlaced) is { Path: { } chose } placed && parts.Navigate is { } navigate
            ? new StatusPlace($"open {Path.GetFileName(chose)}", () => navigate(chose, placed.Position))
        : ended.Problems.Count > 0 ? StatusPlace.Problems(parts.Dock, parts.Views.Problems, ProblemsTab.Build, _ => true)
        : ended.Status == BuildStatus.Failed ? StatusPlace.Window(parts.Dock, ToolWindowIds.Build)
        : null;

    /// <summary>Whether a folder holds a file MSBuild builds when given the folder: a solution, a solution filter or a project.</summary>
    private static bool HoldsBuildable(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any(f => Path.GetExtension(f) is { } extension
                && (extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || extension is ".sln" or ".slnx" or ".slnf"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The last build's problems in the Problems window, its title counting them.</summary>
    public void ShowProblems()
    {
        List<Problem> problems = session.Problems;
        parts.Views.Problems.Show(problems);
        if (parts.Dock[ToolWindowIds.Problems] is { } problemsWindow)
        {
            problemsWindow.Title = problems.Count == 0 ? "Problems" : $"Problems {Numbers.Said(problems.Count)}";
        }
    }

    /// <summary>A configuration's plan, with these arguments in place of its own if given, worked out in the workspace its run
    /// reads (<see cref="ProjectSession.WorkspaceOfAsync"/>), its projects as the start's save wrote them
    /// (<see cref="ProjectSession.ProjectsAsWrittenAsync"/>), or why it has none.</summary>
    public async Task<(LaunchPlan? Plan, Words? Problem)> ResolvePlanAsync(RunConfiguration chosen, string? arguments, CancellationToken stop = default)
    {
        try
        {
            await session.ProjectsAsWrittenAsync(null, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return (null, StoppedStarting(chosen.Name));
        }
        (WorkspaceModel? configured, Words? unconfigured) = await session.WorkspaceOfAsync(chosen);
        if (unconfigured is not null)
        {
            return (null, unconfigured);
        }
        try
        {
            return (await LaunchPlan.ResolveAsync(arguments is null ? chosen : chosen with { Arguments = arguments }, session.Projects, configured, parts.StateFolder(), stop,
                parts.Settings().GodotExecutable, parts.Editors.Active?.Path, session.GodotEnvironment, parts.Unseen, session.Targets.InForce), null);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return (null, StoppedStarting(chosen.Name));
        }
        catch (Exception error) when (error is LaunchException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, RefusedException.Of(error));
        }
    }

    /// <summary>Why Run Current Scene cannot run now: the file open is in no Godot project of the folder's.</summary>
    public Words? WhyNotRunScene() => parts.Editors.Active?.Path is { } path && session.RunProjects.Any(p => p.GodotFolder is { } folder && PathNames.IsUnder(path, folder))
        ? null : Refusals.NoGodotScene;

    /// <summary>Run Current Scene: the Godot game of the project the open scene (or script) is in, run from that scene (or the
    /// one the script is attached in, <see cref="Passenger.Workspace.GodotProject.SceneOf"/>), its configuration chosen (the
    /// one chosen when it runs that game, else its generated one). Null once it runs, else why not.</summary>
    public async Task<Words?> RunCurrentSceneAsync()
    {
        UiThread.Check();
        string? path = parts.Editors.Active?.Path;
        ProjectModel? project = path is null ? null : session.RunProjects.FirstOrDefault(p => p.GodotFolder is { } folder && PathNames.IsUnder(path, folder));
        if (project is null)
        {
            return Said(Refusals.NoGodotScene);
        }
        RunConfiguration? configuration = session.Configurations.Chosen is { GodotEditor: false, IsContainer: false } chosen && PathNames.Same(chosen.Project, project.Path)
            && chosen.NamesNoOtherProgram(session.Projects) ? chosen
            : session.Configurations.All.FirstOrDefault(c => c.Generated && !c.GodotEditor && PathNames.Same(c.Project, project.Path));
        if (configuration is null || !session.Configurations.Select(configuration.Name))
        {
            return Said(LaunchWords.NoGameConfiguration(project.Name));
        }
        return await RunAsync(scene: RunConfiguration.CurrentScene);
    }

    /// <summary>What a start Stop ended says, every kind's: a program's, a container run's, an AppHost's.</summary>
    public static Words StoppedStarting(string name) => LaunchWords.StoppedStarting(name);

    /// <summary>What a restart of a run's services Stop ended says (Rerun's, a window's Restart, Hot Reload's), every kind's.</summary>
    public static Words StoppedRestarting(string name) => LaunchWords.StoppedRestarting(name);

    /// <summary>What Rerun of a run of services says once done, every kind's: the services restarted with their new build
    /// (with <paramref name="more"/>, what else came of it), or that nothing changed.</summary>
    public static string Reran(string name, IReadOnlyList<string> restarted, string? more = null) => restarted.Count == 0
        ? $"{name}: nothing changed, nothing restarted" : $"{name}: restarted {string.Join(", ", restarted)} with the new build{more}";

    /// <summary>A configuration's plan with its build before launch done: the plan to start, or why there is none, the folder
    /// left or Stop meanwhile among the reasons; <c>Unresolved</c> when the problem is the plan's own (what the configuration
    /// runs could not be worked out), which a start shows where it shows its own state. Where the start <paramref name="collects"/> (Run's, Debug's, Profile's) and its configuration
    /// collects its logs and traces, its dashboard comes up as its project builds (<see cref="RunDashboard.CollectAsync"/>), ended with the program.</summary>
    public async Task<(LaunchPlan? Plan, Words? Problem, bool Unresolved)> PrepareAsync(RunConfiguration chosen, string? arguments, Working work, bool build = true,
        Action? reading = null, bool collects = false)
    {
        (LaunchPlan? plan, Words? problem) = await ResolvePlanAsync(chosen, arguments, work.Stopping);
        if (Checkpoint(work, chosen.Name) is { } stopped)
        {
            return (null, stopped, false);
        }
        if (plan is null)
        {
            return (null, problem, true);
        }
        using CancellationTokenSource collecting = CancellationTokenSource.CreateLinkedTokenSource(work.Stopping);
        Task<LaunchPlan>? told = collects && Collects(chosen, plan) ? CollectAsync(plan, collecting.Token) : null;
        if (build && plan.Build is { } step && await BuildForAsync(step, work, reading, plan.Workspace) is { Succeeded: false } unbuilt)
        {
            await collecting.CancelAsync();
            await AbandonAsync(told);
            return (null, Checkpoint(work, chosen.Name) ?? unbuilt.Message, false);
        }
        if (build && plan.Device is { App: null } && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, plan.Project!)) is { } device)
        {
            ProjectSession listing = session;
            TargetPlatform platform = plan.Device.Target.Platform;
            (LaunchPlan? installed, string? uninstalled) = await DeviceLaunches.ReadyAsync(plan, Workspace.ProjectModels.Properties(plan.Workspace, device), parts.Unseen,
                said => work.Say(said), targets => UiThread.Post(() =>
                {
                    if (!listing.IsEnded)
                    {
                        listing.Targets.List(device, platform, targets);
                    }
                }), work.Stopping, listing.Booted);
            if (installed is null)
            {
                await collecting.CancelAsync();
                await AbandonAsync(told);
                return (null, Checkpoint(work, chosen.Name) ?? uninstalled, false);
            }
            plan = installed;
        }
        if (build && plan.MacCatalyst is { App: null } && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, plan.Project!)) is { } catalyst)
        {
            (LaunchPlan? opened, string? unopened) = await Devices.MacCatalystApps.ReadyAsync(plan, Workspace.ProjectModels.Properties(plan.Workspace, catalyst), work.Stopping);
            if (opened is null)
            {
                await collecting.CancelAsync();
                await AbandonAsync(told);
                return (null, Checkpoint(work, chosen.Name) ?? unopened, false);
            }
            plan = opened;
        }
        if (told is not null)
        {
            if (!told.IsCompleted)
            {
                work.Say($"Starting Aspire's dashboard for {chosen.Name}'s logs and traces…");
            }
            try
            {
                plan = await told;
            }
            catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested)
            {
                return (null, Checkpoint(work, chosen.Name) ?? StoppedStarting(chosen.Name), false);
            }
        }
        if (Checkpoint(work, chosen.Name) is { } left)
        {
            plan.Dashboard?.Dispose();
            return (null, left, false);
        }
        return (plan, null, false);
    }

    /// <summary>Whether a start of a configuration collects its program's logs and traces: a project run's whose configuration
    /// says Collect logs and traces, but an AppHost's, which has a dashboard of its own, and a Godot game's.</summary>
    private static bool Collects(RunConfiguration chosen, LaunchPlan plan) =>
        chosen is { Telemetry: true, Kind: RunKind.Project, Aspire: false } && plan is { Godot: null, Project: not null };

    /// <summary>A plan its dashboard tells, the dashboard the session's while it runs (the Containers window's Telemetry tab
    /// and an agent's <c>telemetry</c> read it), until it is taken down.</summary>
    private async Task<LaunchPlan> CollectAsync(LaunchPlan plan, CancellationToken cancel)
    {
        ProjectSession collecting = session;
        // The run's next start ends what its last left, kept or not.
        foreach (RunDashboard last in collecting.Dashboards.Where(d => d.Name == plan.Name).ToList())
        {
            last.Dispose();
        }
        LaunchPlan told = await RunDashboard.CollectAsync(plan, collecting.Containers.Engine, collecting.Containers, collecting.Folder, parts.StateFolder(), cancel);
        if (told.Dashboard is { } dashboard)
        {
            collecting.Dashboards.Add(dashboard);
            dashboard.Stopped += (_, _) =>
            {
                collecting.Dashboards.Remove(dashboard);
                TelemetryFollows();
            };
            // Chosen in the Containers window, as a container run's project is, its Telemetry tab a click away.
            parts.Reveal?.Invoke(dashboard.ProjectName);
        }
        return told;
    }

    /// <summary>A run's dashboard handed to its program's end: taken down where the IDE ended the program (<paramref name="stopped"/>,
    /// the folder's end), else kept, its records read on, the run saying so through <paramref name="say"/>.</summary>
    public void Keep(RunDashboard? dashboard, Task ended, Func<bool> stopped, Action<string> say)
    {
        if (dashboard is null)
        {
            return;
        }
        ProjectSession running = session;
        IdeErrors.Observe(KeepAsync());

        async Task KeepAsync()
        {
            await ended.Ended();
            if (dashboard.ProgramEnded(stopped() || running.IsEnded))
            {
                say(dashboard.KeptSaid);
                TelemetryFollows();
            }
        }
    }

    /// <summary>The Run window's Telemetry follows the dashboard of the run its console shows (none: the console alone); one
    /// its last run kept goes as another run takes the console.</summary>
    public void ShowInRun(RunDashboard? shown)
    {
        // The console comes back in place of a run of several parts' tabs, those of parts still running kept for Stop.
        EndPartRuns(ended: true);
        parts.Views.RunWindow.ShowParts([]);
        if (parts.Views.RunWindow.Dashboard is { Kept: true } kept && kept != shown)
        {
            kept.Dispose();
        }
        parts.Views.RunWindow.Dashboard = shown;
    }

    /// <summary>A dashboard came up, was kept or went: the Run, Debug and Containers windows' Telemetry follow.</summary>
    private void TelemetryFollows()
    {
        parts.Views.RunWindow.Follow();
        parts.Views.Debug.TelemetryChanged();
        parts.TelemetryChanged?.Invoke();
    }

    /// <summary>A dashboard's start that its run will not use, ended: what came of it taken down.</summary>
    private static async Task AbandonAsync(Task<LaunchPlan>? told)
    {
        if (told is null)
        {
            return;
        }
        try
        {
            (await told).Dashboard?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Canceled as it came up, it took down what it made.
        }
    }

    /// <summary>Why a start goes no further now, or null to go on: its folder has ended, or Stop has ended the start.</summary>
    private Words? Checkpoint(Working work, string name) => session.IsEnded ? Refusals.SolutionClosed : work.IsStopped ? StoppedStarting(name) : null;

    /// <summary>What a start ended by a cancel says: its folder's end, else Stop's.</summary>
    private Words Canceled(Working work, string name) => Canceled(name);

    private Words Canceled(string name) => session.IsEnded ? Refusals.SolutionClosed : StoppedStarting(name);

    /// <summary>A start's build of what it starts, in the Build window, which comes forward only as Settings says for a build
    /// Run, Debug or Profile begins: stopped as the start is (<paramref name="stopping"/>), as Stop stops a build. What is
    /// open is saved first, <paramref name="reading"/> told just before; a project whose last build here stands
    /// (<see cref="UpToDate"/>) is not built again, the Build window and the status bar saying so. Built as
    /// <paramref name="configured"/>'s build configuration where given (a run's own), else as the folder's.</summary>
    public Task<BuildOutcome> BuildForAsync(BuildStep step, CancellationToken stopping, Action? reading = null, WorkspaceModel? configured = null) =>
        BuildForAsync([step.Project], stopping, null, step.Framework, step.Output, reading, configured, step.Properties);

    /// <summary>A start's build of what it starts, as <see cref="BuildForAsync(BuildStep, CancellationToken)"/>, saying in its
    /// work when it waits for a build under way; <paramref name="reading"/> is told as it saves the files its build reads.</summary>
    public Task<BuildOutcome> BuildForAsync(BuildStep step, Working work, Action? reading = null, WorkspaceModel? configured = null) =>
        BuildForAsync([step.Project], work.Stopping, work, step.Framework, step.Output, reading, configured, step.Properties);

    /// <summary>What a start says of a project it did not build: nothing its build reads changed since its last.</summary>
    public static string UpToDateSaid(string project) => $"{project} is up to date: nothing its build reads has changed since it was built, so it was not built again";

    /// <summary>What a start says of projects it did not build, one or several.</summary>
    public static string UpToDateSaid(IReadOnlyList<string> projects) => projects.Count == 1 ? UpToDateSaid(projects[0])
        : $"{Plural.Listed(projects)} are up to date: nothing their builds read has changed since they were built, so they were not built again";

    /// <summary>A start's build of its projects (<see cref="BuildForAsync(BuildStep, CancellationToken)"/>), several side by
    /// side in one dotnet build (<see cref="TogetherBuild"/>), after any build under way: none is built when the last build
    /// here of each stands, and all are when one does not, what each build left then kept for its next start's check; one
    /// given <paramref name="properties"/> of its own is always built. <paramref name="reading"/> is told just before each
    /// save of the files, which the build then reads as saved.</summary>
    private async Task<BuildOutcome> BuildForAsync(IReadOnlyList<string> projects, CancellationToken stopping, Working? work, string? framework = null, string? output = null,
        Action? reading = null, WorkspaceModel? configured = null, IReadOnlyList<string>? properties = null)
    {
        UiThread.Check();
        List<UpToDate.Check> checks = [];
        TogetherBuild? side = null;
        while (session.Folder is { } folder && !session.IsEnded && projects.Count > 0)
        {
            if (building is { } under)
            {
                // Begun before this start or while it checked, a build may change what the check reads.
                string? stage = (work?.Stage)?.Shown;
                work?.Say("Waiting for the build under way to end…");
                try
                {
                    await under.Task.WaitAsync(stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return new BuildOutcome(BuildStatus.Stopped, "Build stopped");
                }
                if (stage is not null && work is { IsStopped: false })
                {
                    work.Say(stage);
                }
                continue;
            }
            // In the save's own turn of the window's thread: an edit made after it is not the build's.
            reading?.Invoke();
            if (await parts.Editors.SaveAllLandedAsync(work, stopping) is { } unsaved)
            {
                return stopping.IsCancellationRequested ? new BuildOutcome(BuildStatus.Stopped, "Build stopped") : new BuildOutcome(BuildStatus.NotStarted, unsaved);
            }
            bool windows = WindowsTargeting(projects);
            string state = parts.StateFolder();
            WorkspaceModel within = configured ?? session.Workspace ?? new WorkspaceModel(folder, null, []);
            if (properties is not null)
            {
                if (IsBuilding)
                {
                    continue;
                }
                break;
            }
            (IReadOnlyList<UpToDate.Check?> found, side) = await BackgroundWork.Run(async () => (
                await UpToDate.CheckAsync([.. projects.Select(project => new BuildStep(project, framework, output))],
                    [.. projects.Select(project => BuildOutput.Arguments(project, BuildKind.Build, framework, windows, output, within.BuildArgumentsFor(project)))], windows, folder,
                    stopping, within.BuiltWith),
                projects.Count > 1 ? TogetherBuild.Write(state, folder, projects, within.BuiltTogetherWith(projects)) : null));
            if (stopping.IsCancellationRequested)
            {
                return new BuildOutcome(BuildStatus.Stopped, "Build stopped");
            }
            if (IsBuilding)
            {
                continue;
            }
            if (found.All(check => check is { Holds: true }) && !session.IsEnded)
            {
                string said = UpToDateSaid([.. projects.Select(project => Path.GetFileNameWithoutExtension(project))]);
                parts.Views.Build.Clear();
                parts.Views.Build.Append($"{said}\n");
                parts.Say(said);
                return new BuildOutcome(BuildStatus.Succeeded, said);
            }
            checks = [.. found.OfType<UpToDate.Check>()];
            break;
        }
        work?.Say($"Building {string.Join(", ", projects.Select(Path.GetFileNameWithoutExtension))}…");
        TaskCompletionSource<BuildOutcome> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Where they cannot be built together (TogetherBuild.Write's null), they are built one after another.
        Start(BuildKind.Build, side is null ? projects : [side.Project], outcome => ended.TrySetResult(outcome), framework, BuildCause.BeforeLaunch, null, null, output, side,
            configured, properties);
        BuildOutcome built;
        // On the window's thread, and only while the build under way is this one; the folder's end stops it itself.
        using (stopping.Register(() => UiThread.Post(() =>
        {
            if (!ended.Task.IsCompleted && !session.IsEnded)
            {
                session.Builder.Stop();
            }
        })))
        {
            built = await ended.Task;
        }
        if (built.Succeeded)
        {
            await BackgroundWork.Run(() => Task.WhenAll(checks.Select(check => UpToDate.BuiltAsync(check, stopping))));
        }
        else
        {
            checks.ForEach(UpToDate.Forget);
        }
        return built;
    }

    /// <summary>Runs the chosen configuration, with these arguments in place of its own if given: its plan's program started
    /// after its build, its output and input in the Run window, or in a tab of the Terminal window when the configuration
    /// uses the terminal; under dotnet watch as Settings' Hot Reload says, or as <paramref name="hotReload"/> does (an agent's
    /// run, whose headless IDE keeps the default settings). Null once it runs (<see cref="RunEnd"/> is its end), else why it
    /// could not (a program Profile started has the Run console, say), which the status bar says too.</summary>
    public async Task<Words?> RunAsync(string? arguments = null, bool? hotReload = null, CancellationToken cancellation = default, string? scene = null)
    {
        if (session.Configurations.Chosen is not { } chosen)
        {
            return Said(session.NothingTo("run"));
        }
        if (chosen.IsMultiple)
        {
            return await StartProjectsAsync(chosen, debug: false, hotReload, cancellation);
        }
        // Run Current Scene's, or a restart's or Rerun's of a run that began in a scene of its own: this start of a Godot game begins there.
        bool sceneOwn = scene is not null || chosen.GodotScene == RunConfiguration.CurrentScene;
        if (scene is not null)
        {
            chosen = chosen with { GodotScene = scene };
        }
        if (session.Aspire.IsAspire(chosen))
        {
            RunStart mode = (hotReload ?? parts.Settings().HotReload) ? RunStart.HotReload : RunStart.Run;
            return await RunAspireAsync(chosen, arguments, mode, cancellation);
        }
        if (chosen.GodotInEditor && parts.InGodotEditor is { } inEditor)
        {
            return await inEditor(chosen, false, cancellation) is { } refused ? Said(refused) : null;
        }
        // The Run console's program is one at a time; an AppHost of another configuration runs beside it.
        if (WhyNotConsole(chosen) is { } taken)
        {
            return Said(taken);
        }
        aspireEnd = null;
        // The Run console is one program's: Profile's writes there too, from its start to its end.
        if (parts.Work.Of(AppActions.Profile) is { } profiling)
        {
            return Said(ActionWork.Busy(profiling));
        }
        if (session.Profiled is { Run.HasExited: false, Terminal: null } profiled)
        {
            return Said(LaunchWords.ProfiledHasConsole(profiled.Name));
        }
        // Begun before what runs of it ends, so an agent's Run hears this start through to its hand-over.
        if (parts.Work.Begin(AppActions.RunProgram, $"Working out how to run {chosen.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(AppActions.RunProgram)!));
        }
        using (work)
        {
            return await RunAsync(chosen, arguments, hotReload, sceneOwn, work);
        }
    }

    private async Task<Words?> RunAsync(RunConfiguration chosen, string? arguments, bool? hotReload, bool sceneOwn, Working work)
    {
        if (await EndRunningAsync(chosen, work, "run") is { } left)
        {
            return Said(left);
        }
        // What the Run window's last program said it needs is its own: this start is heard afresh where it uses the console.
        HearNeeds(null, chosen.Name, debugged: false);
        if (chosen.IsScript)
        {
            return await RunScriptAsync(chosen, arguments, work);
        }
        if (chosen.IsContainer)
        {
            // With Hot Reload through the debugger in each service: dotnet watch would need the SDK in its image.
            return await RunContainersAsync(chosen, work, hotReload ?? parts.Settings().HotReload ? RunStart.HotReload : RunStart.Run);
        }
        using IDisposable held = BackgroundWork.Scope();
        // The edits made since its build saved the files, followed for a Godot game's or a XAML app's Hot Reload.
        BuiltEdits? edits = null;
        // A XAML app's update handlers' hook, its debugger's once attached.
        UpdateHandlerHook? handlers = null;
        // Its logs and traces' dashboard, ended with its program once handed over, else with the start.
        RunDashboard? dashboard = null;
        try
        {
            if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
            {
                return Said(unsaved);
            }
            parts.Views.Run.Clear();
            ShowInRun(null);
            UnfollowWatched();
            // dotnet watch builds the project itself, and runs it again after an edit it cannot apply.
            bool functions = chosen.RunsFunctions(session.Projects);
            bool wanted = (hotReload ?? parts.Settings().HotReload) && chosen.NamesNoOtherProgram(session.Projects) && !functions;
            bool godot = chosen.RunsGodot(session.Projects);
            // An app on a device, or a Mac Catalyst app, takes its edits through Passenger's relay in it, which holds its start until the debugger attaches.
            bool elsewhere = RunsOnDevice(chosen) || RunsOnMacCatalyst(chosen);
            // A Windows app package, which Windows starts: its edits through the debugger, attached quietly as it starts held.
            bool packaged = chosen.RunsPackage(session.Projects);
            // A game's errors, which Godot writes every frame they happen, folded in its console.
            parts.Views.Run.Fold = godot && !chosen.GodotEditor ? new Godot.GodotConsoleFold() : null;
            // An Avalonia, WPF or Windows Forms app's inspector (its XAML's edits, its live tree) is in no app dotnet watch starts.
            bool inspectedApp = wanted && !godot && await RunsInspectedAppAsync(chosen);
            bool watched = wanted && !godot && !inspectedApp && !elsewhere && !packaged;
            // A Godot game and a XAML app take their edits through the debugger, attached quietly as each starts held; Godot's editor loads its builds itself.
            bool reloaded = wanted && (godot && !chosen.GodotEditor || inspectedApp || elsewhere || packaged);
            watching = watched;
            (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(chosen, arguments, work, build: !watched, reading: reloaded ? () =>
            {
                edits?.Dispose();
                edits = session.Debug.Building(hotReload: true);
            } : null, collects: true);
            if (plan is null)
            {
                return Said(problem!);
            }
            startedIn = sceneOwn && plan.GodotScene is { } begun ? (chosen.Name, begun) : null;
            dashboard = plan.Dashboard;
            if (plan.WhyNot("run", built: !watched) is { } why)
            {
                return Said(why);
            }
            // Hot Reload reaches a 32-bit program by the 32-bit debugger, had before it starts held: without, it runs as it is.
            Words? unreloaded = null;
            try
            {
                (_, unreloaded) = await session.Debug.X86HostAsync(reloaded && plan.RunsAsX86, work, work.Stopping);
            }
            catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested)
            {
                return Said(Checkpoint(work, chosen.Name) ?? StoppedStarting(chosen.Name));
            }
            if (unreloaded is not null)
            {
                reloaded = false;
                edits?.Dispose();
                edits = null;
            }
            if (watched && MakeWorkingFolder(plan) is { } unmade)
            {
                return Said(unmade);
            }
            // A Windows app package is registered from its build first, as a deploy registers it.
            WindowsPackage? package = null;
            if (plan.Package is { } launch && OperatingSystem.IsWindows())
            {
                List<string> registered = [];
                (package, string? unregistered) = await WindowsPackages.RegisterAsync(chosen.Name, launch, plan.Unseen, registered.Add, work.Stopping);
                if (package is null)
                {
                    return Said(unregistered!);
                }
                plan = plan with { Notes = [.. plan.Notes, .. registered] };
            }
            // Copies of a game each run in a tab of their own, the Run window's or, where its configuration says, the Terminal window's.
            if (GameCopies.Count(chosen, plan) is > 1 and int count)
            {
                return await RunCopiesAsync(chosen, plan, count, reloaded, work, Take(ref edits));
            }
            // A game of GDScript alone takes its saved scripts over its own connection: no debugger holds its start for C#'s edits.
            bool heldStart = reloaded && !plan.ScriptsAlone && plan.Device is null && plan.MacCatalyst is null;
            work.Say($"Starting {chosen.Name}…");
            ran = chosen.Name;
            RunProject = Path.GetDirectoryName(plan.Project);
            // A Godot game's, with Passenger as its remote debugger.
            (plan, Godot.GodotGame? game) = session.Listened(plan);
            game?.ReloadsScripts = reloaded;
            game?.Console = parts.Views.Run.Fold;
            session.RunPlan = plan;
            string running = watched || reloaded ? $"Running {chosen.Name} with Hot Reload: a save applies its changes"
                : unreloaded is not null ? $"Running {chosen.Name} without Hot Reload: {unreloaded}"
                : wanted && godot && !plan.ScriptsAlone ? $"Running {chosen.Name} without Hot Reload: {GodotLaunch.EditorReloads}"
                : functions && (hotReload ?? parts.Settings().HotReload) ? $"Running {chosen.Name} without Hot Reload: {Refusals.FunctionsUnreloaded}"
                : $"Running {chosen.Name}";
            // An Avalonia or WPF app is launched with the inspector in it, which tells of its failed bindings while it runs.
            LaunchPlan launched = session.InspectedRun(plan, watched);
            LiveApp? inspected = session.RunApp;
            // Held from its first step on a device until what it puts in there is out again, which Stop and the folder's end wait for.
            using Devices.DeviceStart? onDevice = (plan.Device?.App ?? (Devices.IDeviceApp?)plan.MacCatalyst?.App) is { } app ? session.StartsOnDevice(chosen.Name, app) : null;
            (Devices.RelaySession? relay, UpdateHandlerHook? deviceHandlers, string? unrelayed) = reloaded && onDevice is not null
                ? await RelayOnDeviceAsync(plan, onDevice, inspected, work) : (null, null, null);
            handlers ??= deviceHandlers;
            if (unrelayed is not null)
            {
                running = $"Running {chosen.Name} without Hot Reload: {unrelayed}";
            }
            if (relay is null && onDevice is not null)
            {
                await InspectOnDeviceAsync(onDevice, inspected, work);
            }
            if (work.Stopping.IsCancellationRequested)
            {
                // Stopped before its program starts (as what it puts on a device went in, say): it never starts, and what went in comes out first.
                inspected?.Dispose();
                if (onDevice is not null)
                {
                    await onDevice.TakeOutAsync();
                }
                return Said(Canceled(work, chosen.Name));
            }
            ShowBindings();
            // dotnet watch opens no browser of its own: the plan's opens through the window once the program listens.
            Dictionary<string, string> variables = watched
                ? new Dictionary<string, string>(launched.Environment)
                {
                    ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1", ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true", ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1",
                }
                : new Dictionary<string, string>(launched.Environment);
            if (heldStart)
            {
                // Held at its runtime's start until the debugger attaches, so each of its modules takes Edit and Continue as it loads.
                foreach ((string name, string value) in HeldStart.Variables(changeable: true))
                {
                    variables[name] = value;
                }
                // An inspected app's update handlers run as each update lands, through Passenger's hook, as a debugged one's do.
                if (inspectedApp && UpdateHandlerHook.For(launched with { Environment = variables }) is ({ } hook, { } hooked))
                {
                    (handlers, variables) = (hook, new Dictionary<string, string>(hooked.Environment));
                }
            }
            if (plan.Device?.App is Devices.SimulatorApp simulator)
            {
                // A simulator's app takes its variables from simctl's launch: the inspector's, the relay's and the hook's with the plan's.
                variables = Devices.SimulatorApp.Given(simulator.Variables(variables, inspected?.Device, relay, handlers));
            }
            else if (plan.MacCatalyst?.App is { } carried)
            {
                // A Mac Catalyst app takes the opener's environment, which the system hands it: the inspector's, the relay's and the hook's with the plan's.
                variables = carried.Variables(variables, inspected?.Device, relay, handlers);
            }
            if (plan.Console == ConsoleKind.Terminal)
            {
                // The terminal's text is the program's own: its failed bindings are in Problems alone.
                WatchBindings(inspected, null);
                string program = GodotConsole.Program(plan, plan.Program);
                // What the run says of itself goes with its status, the terminal's tab being the program's own.
                Words? refused = await RunInTerminalAsync(chosen.Name, watched ? DotnetStart(Watch(plan), plan.WorkingDirectory) : ChildProcesses.Command(program, plan.Arguments, plan.WorkingDirectory), variables,
                    string.Join("; ", [running, .. plan.Notes]), work);
                if (refused is null && session.InTerminal is { } inTerminal)
                {
                    parts.OpenBrowser(plan, inTerminal.Ended);
                    if (heldStart)
                    {
                        try
                        {
                            await GodotConsole.FollowAsync(inTerminal, plan.Program, program);
                        }
                        catch (LaunchException unfollowed)
                        {
                            return Said(unfollowed.Why);
                        }
                        await AttachForHotReloadAsync(chosen.Name, inTerminal.Debuggee, null, work, Take(ref edits), running, Take(ref handlers), inspected, ProgramBuild.Of(plan));
                    }
                }
                if (refused is null && session.InTerminal is { } apart)
                {
                    TakeOn(plan, apart.Ended);
                }
                if (onDevice is not null)
                {
                    onDevice.EndsWith(EndOnDeviceAsync(refused is null && session.InTerminal is { } shell ? shell.Ended : Task.FromResult(0), onDevice));
                    if (refused is null && relay is not null)
                    {
                        await AttachOnDeviceForHotReloadAsync(chosen.Name, onDevice, relay, null, work, Take(ref edits), running, inspected, ProgramBuild.Of(plan),
                            Take(ref handlers));
                    }
                }
                EndWith(inspected, refused is null && session.InTerminal is { } run ? run.Ended : Task.CompletedTask);
                EndWith(game, refused is null && session.InTerminal is { } played ? played.Ended : Task.CompletedTask);
                if (refused is null && session.InTerminal is { } collected)
                {
                    Keep(Take(ref dashboard), collected.Ended, () => collected.WasStopped, said => parts.Say(said));
                }
                return refused;
            }
            session.InTerminal = null;
            parts.Views.Run.SetRunning(true);
            parts.Dock.Show(ToolWindowIds.Run);
            foreach (string note in plan.Notes)
            {
                parts.Views.Run.Append($"{note}\n");
            }
            if (plan is { MacCatalyst.App.Whole: false, Project: { } trimmed })
            {
                parts.Trimmed?.Invoke(trimmed, chosen.Name);
            }
            ShowInRun(dashboard);
            Running(work, running);
            WatchBindings(inspected, parts.Views.Run);
            HearNeeds(parts.Views.Run, chosen.Name, debugged: false);
            if (watched)
            {
                FollowWatched(chosen.Name);
            }
            // A package's app is told its id by its starter, which Windows' start lasts as long as.
            string? startedFile = package is not null && heldStart && OperatingSystem.IsWindows() ? WindowsPackages.StartedFile() : null;
            // Its fork off the window's thread (#441): the program runs from now as Stop and IsRunning see it, its id known once started.
            Words? notStarted = Started(watched ? await session.Runner.StartDotnetAsync(Watch(plan), plan.WorkingDirectory, variables, plan.Placed)
                : package is not null && OperatingSystem.IsWindows()
                    ? await session.Runner.StartAsync(WindowsPackages.Starter(package, plan.Arguments, variables, plan.WorkingDirectory, startedFile), new Dictionary<string, string>())
                : await session.Runner.StartAsync(plan.Start(), variables));
            if (notStarted is null)
            {
                TakeOn(plan, session.Runner.Ended);
                parts.OpenBrowser(plan, session.Runner.Ended);
            }
            EndWith(inspected, notStarted is null ? session.Runner.Ended : Task.CompletedTask);
            EndWith(game, notStarted is null ? session.Runner.Ended : Task.CompletedTask);
            if (notStarted is null)
            {
                Keep(Take(ref dashboard), session.Runner.Ended, () => session.Runner.WasStopped, said => parts.Views.Run.Append($"{said}\n"));
            }
            if (onDevice is not null)
            {
                // Its shell on the device ends with the app; Stop ends the shell here, so the app is ended there too.
                onDevice.EndsWith(EndOnDeviceAsync(notStarted is null ? session.Runner.Ended : Task.FromResult(0), onDevice));
            }
            if (notStarted is null && relay is not null && onDevice is not null)
            {
                await AttachOnDeviceForHotReloadAsync(chosen.Name, onDevice, relay, parts.Views.Run, work, Take(ref edits), running, inspected, ProgramBuild.Of(plan),
                    Take(ref handlers));
            }
            if (notStarted is null && heldStart && (startedFile is null || !OperatingSystem.IsWindows() ? session.Runner.ProcessId : await WindowsPackages.StartedAsync(startedFile, () => session.Runner.Ended.IsCompleted)) is int id)
            {
                await AttachForHotReloadAsync(chosen.Name, id, parts.Views.Run, work, Take(ref edits), running, Take(ref handlers), inspected, ProgramBuild.Of(plan));
            }
            return notStarted;
        }
        finally
        {
            edits?.Dispose();
            handlers?.Dispose();
            dashboard?.Dispose();
        }
    }

    /// <summary>An app the system starts apart from what its run started (a Mac Catalyst app's opener, simctl's launch on a
    /// simulator) found once it runs by its start's own token, so it ends with Passenger, and a Mac Catalyst app with its run
    /// as it <paramref name="ended"/>; a simulator's run ends its app through simctl.</summary>
    private static void TakeOn(LaunchPlan plan, Task ended)
    {
        if (plan.MacCatalyst?.App is { } opened)
        {
            IdeErrors.Observe(Devices.MacCatalystApps.EndWithAsync(opened, ended));
        }
        else if (plan.Device?.App is Devices.SimulatorApp simulator)
        {
            IdeErrors.Observe(simulator.TakeOnAsync(ended));
        }
    }

    /// <summary>Runs copies of a game from one build, each in a tab named for its place (<see cref="GameCopies"/>), the Run
    /// window's, its console folding Godot's errors, or the Terminal window's where its configuration says, followed in the
    /// Live Visual Tree; with Hot Reload on, each held at its start and attached to quietly with its own copy of the edits
    /// since the build (<paramref name="edits"/>, the first's). An earlier run's copies end first. Null once every one runs,
    /// else why the first that could not start did not, those before it running on.</summary>
    private async Task<Words?> RunCopiesAsync(RunConfiguration chosen, LaunchPlan plan, int count, bool reloaded, Working work, BuiltEdits? edits)
    {
        List<BuiltEdits?> each = [edits, .. Enumerable.Range(2, count - 1).Select(_ => edits is null ? null : session.Debug.Copied(edits))];
        try
        {
            await EndCopyRunsAsync(chosen.Name);
            bool inTerminal = plan.Console == ConsoleKind.Terminal;
            EndPartRuns(ended: true);
            PartRun[] tabs = inTerminal ? [] : [.. Enumerable.Range(1, count).Select(copy => CopyTab(chosen, copy))];
            foreach (PartRun tab in tabs)
            {
                partRuns.Add(tab);
                Follow(tab);
            }
            if (!inTerminal)
            {
                parts.Views.RunWindow.ShowParts(partRuns);
                parts.Dock.Show(ToolWindowIds.Run);
            }
            List<string> started = [];
            for (int copy = 1; copy <= count; copy++)
            {
                work.Say($"Starting {CopyName(chosen, copy)}, {copy} of {Numbers.Said(count)}…");
                BuiltEdits? its = each[copy - 1];
                each[copy - 1] = null;
                if (await (inTerminal ? RunCopyInTerminalAsync(chosen, plan, copy, reloaded, work, its) : RunCopyAsync(chosen, plan, tabs[copy - 1], reloaded, work, its)) is { } refused)
                {
                    foreach (PartRun waiting in tabs.Skip(copy))
                    {
                        waiting.Set(PartState.Ended);
                    }
                    return Said(copy == 1 ? refused : LaunchWords.CopyDidNotStart(CopyName(chosen, copy), refused, Plural.Listed(started)));
                }
                started.Add(CopyName(chosen, copy));
            }
            string window = inTerminal ? ToolWindowIds.Terminal : ToolWindowIds.Run;
            parts.Say($"Running {Numbers.Said(count)} copies of {chosen.Name}{(reloaded && !plan.ScriptsAlone ? " with Hot Reload: a save applies its changes in each" : string.Empty)}, each in a tab of the {(inTerminal ? "Terminal" : "Run")} window",
                StatusPlace.Window(parts.Dock, window));
            return null;
        }
        finally
        {
            foreach (BuiltEdits? left in each)
            {
                left?.Dispose();
            }
        }
    }

    /// <summary>A copy's tab in the Run window (from one), waiting for its game.</summary>
    private PartRun CopyTab(RunConfiguration chosen, int copy) =>
        new(CopyName(chosen, copy), chosen.Name, copy, CopyPart(chosen)) { Project = chosen, Copy = new GameCopy(chosen.Name, copy) };

    /// <summary>What a copy runs, as a part of its configuration's: its project.</summary>
    private static RunPart CopyPart(RunConfiguration chosen) => new(chosen.Project, ProjectLaunchAction.Start, string.Empty) { Kind = RunPartKind.Project };

    /// <summary>Runs a copy of a game (its tab's, <see cref="PartRun.Copy"/>) in its tab of the Run window, its console folding
    /// Godot's errors and its game ending with its program; the first copy over the Run window's own console, there as the
    /// console's program (<see cref="PartRun.OfConsole"/>), with the configuration's own plan. With Hot Reload on and C# in
    /// the game, held at its runtime's start and attached to quietly (<see cref="AttachForHotReloadAsync"/>) with
    /// <paramref name="edits"/>, which it takes, as Run's one game is. Null once it runs, else why not.</summary>
    private async Task<Words?> RunCopyAsync(RunConfiguration chosen, LaunchPlan plan, PartRun tab, bool reloaded, Working work, BuiltEdits? edits)
    {
        bool inConsole = tab.Console == parts.Views.Run;
        bool heldStart = reloaded && !plan.ScriptsAlone;
        (LaunchPlan each, Godot.GodotGame? game) = session.Listened(inConsole ? plan : GameCopies.Plan(chosen, plan, tab.Place, ConfigurationNames));
        game?.ReloadsScripts = reloaded;
        // A game's errors, which Godot writes every frame they happen, folded in its console.
        tab.Console.Fold = new Godot.GodotConsoleFold();
        game?.Console = tab.Console.Fold;
        if (inConsole)
        {
            ran = chosen.Name;
            session.RunPlan = each;
            session.InTerminal = null;
        }
        ProcessStartInfo start = ChildProcesses.Command(each.Program, each.Arguments, each.WorkingDirectory, each.Environment);
        if (heldStart)
        {
            // Held at its runtime's start until the debugger attaches, so each of its modules takes Edit and Continue as it loads.
            ChildProcesses.WithVariables(start, HeldStart.Variables(changeable: true));
        }
        tab.StartedBy = Mcp.AgentCall.Current?.Connection;
        if (await tab.StartAsync(start, null) is { } refused)
        {
            game?.Dispose();
            edits?.Dispose();
            return refused;
        }
        tab.Set(PartState.Running);
        EndWith(game, tab.Ended);
        RunningChanged?.Invoke(this, EventArgs.Empty);
        if (!heldStart || tab.ProcessId is not { } process)
        {
            edits?.Dispose();
            return null;
        }
        await AttachForHotReloadAsync(each.Name, process, tab.Console, work, edits, $"Running {each.Name} with Hot Reload: a save applies its changes",
            builds: ProgramBuild.Of(each));
        return null;
    }

    /// <summary>Runs one copy of a game (from one) whose configuration runs in the terminal in a tab of the Terminal window of
    /// its own, through Godot's console wrapper on Windows (<see cref="GodotConsole"/>); with Hot Reload on and C# in the
    /// game, held at its runtime's start and attached to quietly (<see cref="AttachForHotReloadAsync"/>) with
    /// <paramref name="edits"/>, which it takes, as Run's one game is. Null once it runs, else why not.</summary>
    private async Task<Words?> RunCopyInTerminalAsync(RunConfiguration chosen, LaunchPlan plan, int copy, bool reloaded, Working work, BuiltEdits? edits)
    {
        bool heldStart = reloaded && !plan.ScriptsAlone;
        (LaunchPlan each, Godot.GodotGame? game) = session.Listened(GameCopies.Plan(chosen, plan, copy, ConfigurationNames));
        game?.ReloadsScripts = reloaded;
        string program = GodotConsole.Program(each, each.Program);
        ProcessStartInfo start = ChildProcesses.Command(program, each.Arguments, each.WorkingDirectory, each.Environment);
        if (heldStart)
        {
            // Held at its runtime's start until the debugger attaches, so each of its modules takes Edit and Continue as it loads.
            ChildProcesses.WithVariables(start, HeldStart.Variables(changeable: true));
        }
        TerminalRun run;
        try
        {
            run = await parts.InTerminal(each.Name, start);
        }
        catch (LaunchException failed)
        {
            game?.Dispose();
            edits?.Dispose();
            return failed.Why;
        }
        run.StartedBy = Mcp.AgentCall.Current?.Connection;
        copyRuns.Add((chosen.Name, copy, run));
        EndWith(game, run.Ended);
        IdeErrors.Observe(CopyEnded());
        RunningChanged?.Invoke(this, EventArgs.Empty);
        if (!heldStart)
        {
            edits?.Dispose();
            return null;
        }
        try
        {
            await GodotConsole.FollowAsync(run, each.Program, program);
        }
        catch (LaunchException unfollowed)
        {
            edits?.Dispose();
            return unfollowed.Why;
        }
        await AttachForHotReloadAsync(each.Name, run.Debuggee, null, work, edits, $"Running {each.Name} with Hot Reload: a save applies its changes",
            builds: ProgramBuild.Of(each));
        return null;

        async Task CopyEnded()
        {
            await run.Ended;
            copyRuns.RemoveAll(c => c.Run == run);
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A copy of a game restarted alone in its tab, the other copies running on: built again and started in its place
    /// as Run starts it, under Hot Reload as Settings says or as it ran (<paramref name="hotReloaded"/>); the first copy over
    /// the Run console started there again. Null once it runs, else why not.</summary>
    private async Task<Words?> RestartCopyAsync(PartRun copy, RunConfiguration chosen, bool hotReloaded, Working work)
    {
        bool reloaded = (parts.Settings().HotReload || hotReloaded) && chosen.NamesNoOtherProgram(session.Projects);
        BuiltEdits? edits = null;
        (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(chosen, null, work, reading: reloaded ? () =>
        {
            edits?.Dispose();
            edits = session.Debug.Building(hotReload: true);
        } : null);
        if (plan is null)
        {
            edits?.Dispose();
            return problem;
        }
        return await RunCopyAsync(chosen, plan, copy, reloaded, work, Take(ref edits));
    }

    /// <summary>Run Another Copy: one more copy of the chosen configuration's game run beside the copies Run runs now (or
    /// beside its one game in the Run console, the second copy, that game then the first copy's tab over the console), in a
    /// tab of the Run window of its own, or of the Terminal window where its configuration runs in the terminal, on the build
    /// they run; null once it runs, else why not.</summary>
    public async Task<Words?> RunAnotherCopyAsync(CancellationToken cancellation = default)
    {
        if (session.Configurations.Chosen is not { } chosen)
        {
            return Said(session.NothingTo("run"));
        }
        if (WhyNotRunAnotherCopy(chosen) is { } why)
        {
            return Said(why);
        }
        if (parts.Work.Begin(AppActions.RunProgram, $"Working out how to run another copy of {chosen.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(AppActions.RunProgram)!));
        }
        using (work)
        using (cancellation.Register(work.Cancel))
        using (BackgroundWork.Scope())
        {
            int next = RunningCopies(chosen.Name).Max() + 1;
            (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(chosen, null, work, build: false);
            if (plan is null)
            {
                return Said(problem!);
            }
            if (WhyNotRunAnotherCopy(chosen) is { } gone)
            {
                return Said(gone);
            }
            work.Say($"Starting {CopyName(chosen, next)}…");
            bool reloaded = parts.Settings().HotReload && chosen.NamesNoOtherProgram(session.Projects);
            // The edits since the build the copies run, as the first of them attached for Hot Reload follows them.
            string? first = RunsOwnProgram(chosen.Name) ? chosen.Name
                : CopyTabs(chosen.Name).Where(c => c.IsRunning).Select(c => (c.Place, c.Name)).Concat(copyRuns.Where(c => c.Configuration == chosen.Name).Select(c => (Place: c.Copy, c.Run.Name)))
                    .OrderBy(c => c.Place).Select(c => c.Name).FirstOrDefault();
            BuiltEdits? edits = reloaded && !plan.ScriptsAlone && first is not null ? session.Debug.Carried(first, hotReload: true) : null;
            if (plan.Console == ConsoleKind.Terminal)
            {
                if (await RunCopyInTerminalAsync(chosen, plan, next, reloaded, work, edits) is { } unstarted)
                {
                    return Said(unstarted);
                }
                parts.Say($"Running {CopyName(chosen, next)} beside {chosen.Name}, in a tab of the Terminal window", StatusPlace.Window(parts.Dock, ToolWindowIds.Terminal));
                return null;
            }
            // The ended copies of it give their tabs up; the game in the Run console is the first copy's tab, over the console.
            EndPartRuns(ended: true, run: chosen.Name);
            if (RunsOwnProgram(chosen.Name) && !CopyTabs(chosen.Name).Any(c => c.Console == parts.Views.Run))
            {
                PartRun console = PartRun.OfConsole(chosen.Name, CopyPart(chosen), parts.Views.Run, session.Runner, () => RunsOwnProgram(chosen.Name),
                    SceneOf(chosen) is { } scene ? chosen with { GodotScene = scene } : chosen, new GameCopy(chosen.Name, 1));
                console.Set(PartState.Running);
                partRuns.Add(console);
                Follow(console);
            }
            PartRun tab = CopyTab(chosen, next);
            partRuns.Add(tab);
            Follow(tab);
            parts.Views.RunWindow.ShowParts(partRuns);
            parts.Views.RunWindow.ShowPart(tab);
            parts.Dock.Show(ToolWindowIds.Run);
            if (await RunCopyAsync(chosen, plan, tab, reloaded, work, edits) is { } refused)
            {
                tab.Set(PartState.Ended);
                return Said(refused);
            }
            parts.Say($"Running {CopyName(chosen, next)} beside {chosen.Name}, in a tab of the Run window", StatusPlace.Window(parts.Dock, ToolWindowIds.Run));
            return null;
        }
    }

    /// <summary>What each configuration goes by, which a copy's name stays clear of.</summary>
    private IEnumerable<string> ConfigurationNames => session.Configurations.All.Select(c => c.Name);

    /// <summary>A copy's name among this folder's configurations.</summary>
    private string CopyName(RunConfiguration chosen, int copy) => GameCopies.Name(chosen.Name, copy, ConfigurationNames);

    /// <summary>The tabs of the copies of a configuration's game in the Run window, running or not.</summary>
    private IEnumerable<PartRun> CopyTabs(string configuration) => partRuns.Where(p => p.Copy?.Configuration == configuration);

    /// <summary>The copies of a configuration's game its last Run started, each in its tab of the Run or Terminal window, in
    /// their order.</summary>
    public IReadOnlyList<INamedRun> CopiesOf(string configuration) =>
        [.. CopyTabs(configuration).Select(c => (c.Place, Run: (INamedRun)c)).Concat(copyRuns.Where(c => c.Configuration == configuration && !profileRuns.ContainsKey(c.Run)).Select(c => (Place: c.Copy, Run: (INamedRun)c.Run)))
            .OrderBy(c => c.Place).Select(c => c.Run)];

    /// <summary>The places of the copies of a configuration's game Run runs now, in tabs of the Run or Terminal window, the
    /// game in the Run console (or its terminal's tab) the first.</summary>
    private IReadOnlySet<int> RunningCopies(string configuration) =>
        new HashSet<int>([.. CopyTabs(configuration).Where(c => c.IsRunning).Select(c => c.Place), .. copyRuns.Where(c => c.Configuration == configuration).Select(c => c.Copy),
            .. RunsOwnProgram(configuration) ? [1] : Array.Empty<int>()]);

    /// <summary>Why Run Another Copy cannot run now: the chosen configuration starts no Godot game, or Run runs none of its
    /// copies, or as many as there may be.</summary>
    public Words? WhyNotRunAnotherCopy(RunConfiguration chosen)
    {
        if (chosen.IsContainer || session.Aspire.IsAspire(chosen) || !chosen.RunsGodot(session.Projects) || chosen.GodotEditor)
        {
            return LaunchWords.NoGameToRunAgain(chosen.Name);
        }
        int running = RunningCopies(chosen.Name).Count;
        return running == 0 ? LaunchWords.RunsNoCopy(chosen.Name)
            : running >= GameCopies.Most ? LaunchWords.MostCopies(name: chosen.Name, most: GameCopies.Most)
            : null;
    }

    /// <summary>Ends the projects a run of several parts runs in the Terminal window (of one run, or every one), done once each
    /// has ended.</summary>
    private Task EndCopyRunsAsync(string? configuration = null)
    {
        List<TerminalRun> ending = [.. copyRuns.Where(c => configuration is null || c.Configuration == configuration).Select(c => c.Run)];
        foreach (TerminalRun run in ending)
        {
            run.Stop();
        }
        return Task.WhenAll(ending.Select(r => r.Ended));
    }

    /// <summary>Whether Run runs the Godot game of configuration <paramref name="name"/>, or the copy of one so named, or a
    /// multi-project configuration's project of that name (in a tab of the Run or Terminal window), with Hot Reload through
    /// the debugger.</summary>
    public bool RunsGameWithHotReload(string name) =>
        (RunsOwnProgram(name) && session.RunPlan is { Godot: not null, GodotEditor: false } || copyRuns.Any(c => c.Run.Name == name) || partRuns.Any(p => p.Name == name && p.Project is not null))
        && session.Debug.Running.Any(r => r.Name == name);

    /// <summary>Restarts the Godot game Run runs with Hot Reload so the edits Hot Reload could not apply run: built and started
    /// again, held and attached to again, while its configuration is still the one chosen; a copy of it, or a multi-project
    /// configuration's project, alone, the others running on. True once it runs again.</summary>
    public async Task<bool> RestartGameForEditAsync(string name)
    {
        if (!RunsGameWithHotReload(name))
        {
            return false;
        }
        if (partRuns.FirstOrDefault(p => p.Name == name && p.Project is not null) is { } part)
        {
            parts.Say($"Hot Reload: restarting {name} to apply the changes");
            return await RestartPartAsync(part);
        }
        if (copyRuns.FirstOrDefault(c => c.Run.Name == name) is { Run: { } copy } run)
        {
            if (profileRuns.ContainsKey(copy))
            {
                return await RestartProfileRunAsync(copy);
            }
            return session.Configurations.Chosen is { } chosen && chosen.Name == run.Configuration
                && await RestartTerminalCopyAsync(SceneOf(chosen) is { } scene ? chosen with { GodotScene = scene } : chosen, copy, run.Copy);
        }
        if (session.Configurations.Chosen is not { } running || running.Name != name)
        {
            return false;
        }
        parts.Say($"Hot Reload: restarting {name} to apply the changes");
        return await RunAsync(hotReload: true, scene: SceneOf(running)) is null;
    }

    /// <summary>The scene the last start of a configuration began in, given it or resolved from the scene open (Run Current
    /// Scene's), by its name, which its restarts and Rerun begin in again whatever is open then; null where none was.</summary>
    private (string Configuration, string Scene)? startedIn;

    /// <summary>The scene a configuration's restart or Rerun begins in (<see cref="startedIn"/>), or null for its own.</summary>
    private string? SceneOf(RunConfiguration chosen) => startedIn is { } begun && begun.Configuration == chosen.Name ? begun.Scene : null;

    /// <summary>One copy of a game Run runs in a tab of the Terminal window, with Hot Reload, restarted alone so the edits Hot
    /// Reload could not apply run: ended, built again, and started in its tab's place, held and attached to again with the
    /// edits from this build, the other copies running on. True once it runs again.</summary>
    private async Task<bool> RestartTerminalCopyAsync(RunConfiguration chosen, TerminalRun copy, int place)
    {
        if (parts.Work.Begin(AppActions.RunProgram, $"Restarting {copy.Name} to apply the changes…") is not { } work)
        {
            return false;
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            parts.Say($"Hot Reload: restarting {copy.Name} to apply the changes");
            copy.Stop();
            await copy.Ended;
            BuiltEdits? edits = null;
            (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(chosen, null, work, reading: () =>
            {
                edits?.Dispose();
                edits = session.Debug.Building(hotReload: true);
            });
            if (plan is null)
            {
                edits?.Dispose();
                parts.Say(LaunchWords.CouldNotRestart(copy.Name, problem!));
                return false;
            }
            return await RunCopyInTerminalAsync(chosen, plan, place, reloaded: true, work, Take(ref edits)) is null;
        }
    }

    /// <summary>What a start holds (the edits followed for a session, its dashboard), handed over once: what takes it lets it go.</summary>
    private static T? Take<T>(ref T? held) where T : class
    {
        T? taken = held;
        held = null;
        return taken;
    }

    /// <summary>Whether a configuration runs an Avalonia, WPF or Windows Forms app of its project, whose Live Visual Tree and
    /// XAML edits reach it through Passenger's inspector, which the app dotnet watch starts never carries: Run's Hot Reload takes
    /// it through the debugger. However it references Avalonia: its own packages, those of the projects it references, or what
    /// its restore resolved, read off the window's thread only where they say nothing.</summary>
    private async Task<bool> RunsInspectedAppAsync(RunConfiguration chosen)
    {
        if (chosen.IsContainer || chosen.Program.Length > 0 || session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is not { } project)
        {
            return false;
        }
        return DesktopPrograms.TakesXaml(project, session.Projects) || DesktopPrograms.TakesForms(project, session.Projects)
            || await BackgroundWork.Run(() => DesktopPrograms.RestoredXaml(Packages.FolderPackages.Restored(project)));
    }

    /// <summary>A Godot game or a XAML app (its inspector in <paramref name="app"/>, its update handlers' hook <paramref name="handlers"/>)
    /// Run started held for Hot Reload, attached to quietly (<see cref="DebugController.AttachForHotReloadAsync"/>), which
    /// lets it go: what its applies come to is said in <paramref name="console"/> (null for the terminal's tab, whose text is
    /// the program's own) and the status bar, which says <paramref name="running"/> once attached. One the debugger cannot
    /// reach is let go and runs without Hot Reload, saying why.</summary>
    private async Task AttachForHotReloadAsync(string name, int processId, OutputView? console, Working work, BuiltEdits? edits, string running,
        UpdateHandlerHook? handlers = null, LiveApp? app = null, ProgramBuild? builds = null)
    {
        work.Say($"Attaching to {name} for Hot Reload…");
        Words? refused = await session.Debug.AttachForHotReloadAsync(processId, name, (line, error) =>
        {
            console?.Append($"{line}\n", error);
            parts.Say(line);
        }, cancellation: work.Stopping, edits: edits, handlers: handlers, app: app, builds: builds);
        if (refused is null)
        {
            Running(work, running);
            return;
        }
        // Held at its start, it would wait for good for a debugger that is not coming.
        await HeldStart.LetGoAsync(processId);
        Running(work, $"Running {name} without Hot Reload: {refused}");
    }

    /// <summary>Why a start of a configuration cannot have the Run console (or the terminal's tab): another configuration's
    /// program has it, named. Null where it is free, where the configuration's own program has it (which <see
    /// cref="EndRunningAsync"/> ends), or where a container run has it that a container start ends itself (one container run
    /// at a time).</summary>
    private Words? WhyNotConsole(RunConfiguration chosen) =>
        !RunsInConsole || chosen.IsContainer && session.ContainerRun is { IsStopped: false } || RunsOwnProgram(chosen.Name) ? null : AlreadyRunning();

    /// <summary>Why a start that needs the Run console cannot have it: what has it, by its configuration's name.</summary>
    private Words AlreadyRunning() => ran.Length > 0 ? LaunchWords.AlreadyRunning(ran) : LaunchWords.AlreadyRunningProgram;

    /// <summary>Whether the program Run started for this configuration has the Run console (or the terminal's tab), which a
    /// start of it ends first (<see cref="EndRunningAsync"/>).</summary>
    public bool RunsOwnProgram(string name) => RunsInConsole && ran == name && session.ContainerRun is not { IsStopped: false };

    /// <summary>The one rule for a start of a configuration that runs, by Run, Debug or Profile (<paramref name="doing"/>),
    /// whichever of them started it: what runs of it ends first, before its build (Windows' cannot write over a program
    /// running): another start of it under way, stopped, then the copies of it Run runs, Run's program of it and the
    /// programs Debug launched of it, done once their ends have landed. Null to go on, else why not (the folder left
    /// meanwhile, or a debugged program the debugger cannot let go).</summary>
    public async Task<Words?> EndRunningAsync(RunConfiguration chosen, Working work, string doing)
    {
        starts.RemoveAll(s => s.Work.HasEnded);
        Working[] under = [.. starts.Where(s => s.Configuration == chosen.Name && s.Work != work).Select(s => s.Work)];
        starts.Add((chosen.Name, work));
        bool copies = copyRuns.Any(c => c.Configuration == chosen.Name) || CopyTabs(chosen.Name).Any(c => c.IsRunning);
        bool runs = under.Length > 0 || copies || RunsOwnProgram(chosen.Name)
            || session.Debug.CopiesOf(chosen.Name).Any(c => debugged.ContainsKey(c.Tab.Name));
        RunAgain answer = runs && parts.AskRunAgain is { } ask ? await ask(chosen.Name, doing, work.Stopping) : RunAgain.StopAndRerun;
        if (answer == RunAgain.Cancel || session.IsEnded)
        {
            return session.IsEnded ? Refusals.SolutionClosed : LaunchWords.RunsOn(chosen.Name);
        }
        if (under.Length > 0)
        {
            work.Say($"Stopping the start of {chosen.Name} under way to {doing} it…");
            foreach (Working other in under)
            {
                other.Cancel();
            }
            await Task.WhenAll(under.Select(other => other.Ended));
            if (session.IsEnded)
            {
                return Refusals.SolutionClosed;
            }
        }
        if (copies)
        {
            work.Say($"Ending the copies of {chosen.Name} Run runs to {doing} it…");
            await Task.WhenAll(EndCopyRunsAsync(chosen.Name), EndPartRunsAsync(chosen.Name));
            if (session.IsEnded)
            {
                return Refusals.SolutionClosed;
            }
        }
        if (RunsOwnProgram(chosen.Name))
        {
            work.Say($"Ending {chosen.Name}'s run to {doing} it…");
            if (parts.Work.Of(AppActions.RunProgram) is { } starting && starting != work)
            {
                starting.Cancel();
            }
            Task<int> ended = session.RunProgram.Ended;
            session.RunProgram.Stop();
            await ended;
            if (session.IsEnded)
            {
                return Refusals.SolutionClosed;
            }
        }
        List<DebugTab> debugging = [.. session.Debug.CopiesOf(chosen.Name).Select(c => c.Tab).Where(t => debugged.ContainsKey(t.Name))];
        if (debugging.Count > 0)
        {
            work.Say($"Ending the debugging of {chosen.Name} to {doing} it…");
            if (await session.Debug.EndAsync(debugging) is { } kept)
            {
                return kept;
            }
            if (session.IsEnded)
            {
                return Refusals.SolutionClosed;
            }
        }
        return answer == RunAgain.Stop ? LaunchWords.StoppedNotStarted(chosen.Name) : null;
    }

    /// <summary>Ends what the Run console (or the terminal's tab) has, done once its end has landed.</summary>
    private async Task EndInConsoleAsync()
    {
        Task<int> ended = session.RunProgram.Ended;
        StopRun();
        await ended;
    }

    /// <summary>dotnet watch starts in the program's working folder, which its first build makes for a project never built:
    /// made first, as that build would. Null once it is there, else why it could not be.</summary>
    private static Words? MakeWorkingFolder(LaunchPlan plan)
    {
        try
        {
            Directory.CreateDirectory(plan.WorkingDirectory);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return LaunchWords.WorkingFolderUnmade(plan.ProgramName, plan.WorkingDirectory, RefusedException.Of(error));
        }
    }

    /// <summary>The .NET command line as the IDE runs it, or null where there is none (which the run then says).</summary>
    private static ProcessStartInfo? DotnetStart(IReadOnlyList<string> arguments, string folder)
    {
        try
        {
            return ChildProcesses.DotnetCommand(arguments, folder);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Runs a plan's program in a tab of the Terminal window named for its configuration, the keys going to it; Stop
    /// ends it, and its end shows in the status bar. Null once it runs, else why it could not.</summary>
    private async Task<Words?> RunInTerminalAsync(string name, ProcessStartInfo? start, IReadOnlyDictionary<string, string> variables, Words running, Working work)
    {
        if (start is null)
        {
            return Said(Dotnet.NotFound);
        }
        ChildProcesses.WithVariables(start, variables);
        TerminalRun run;
        try
        {
            run = await parts.InTerminal(name, start);
        }
        catch (LaunchException failed)
        {
            return Said(failed.Why);
        }
        run.StartedBy = Mcp.AgentCall.Current?.Connection;
        session.InTerminal = run;
        RunningChanged?.Invoke(this, EventArgs.Empty);
        Running(work, running);
        IdeErrors.Observe(Ended());
        return null;

        async Task Ended()
        {
            int code = await run.Ended;
            if (session.InTerminal == run)
            {
                ran = string.Empty;
            }
            RunningChanged?.Invoke(this, EventArgs.Empty);
            if (session.InTerminal == run)
            {
                parts.Say(RunEnded(name, code), StatusPlace.Window(parts.Dock, ToolWindowIds.Terminal));
            }
        }
    }

    /// <summary>dotnet watch's command line for a plan: the project (a file-based program's C# file) for its framework, no
    /// launch profile (the plan has its values already), the program's own arguments after <c>--</c>, but none that are the
    /// project's RunArguments (its RunCommand's program's too), which dotnet watch gives it itself. It runs the program in
    /// its own working folder, which is the plan's; the build configuration and platform the plan's workspace gives the
    /// project (<see cref="LaunchPlan.Workspace"/>).</summary>
    private static List<string> Watch(LaunchPlan plan)
    {
        List<string> watch = ["watch", "--non-interactive", "run", Passenger.Host.ProgramFiles.IsProgram(plan.Project!) ? "--file" : "--project", plan.Project!, "--no-launch-profile"];
        if (plan.Framework is { } framework)
        {
            watch.AddRange(["--framework", framework]);
        }
        watch.AddRange(plan.Workspace?.BuildArgumentsFor(plan.Project!, watched: true) ?? []);
        watch.Add("--");
        if (!plan.ArgumentsOfProject)
        {
            watch.AddRange(plan.OwnArguments);
        }
        return watch;
    }

    /// <summary>dotnet watch's words once the program it ran has ended and it waits for a file to change before starting it
    /// again, once its build of the program has failed and it waits for a file to change to build it again, and once it
    /// watches the program it has started.</summary>
    private const string WatchWaits = "Waiting for a file to change before restarting";

    private const string WatchBuildFails = "Fix the error to continue";

    private const string WatchWatches = "Waiting for changes";

    /// <summary>dotnet watch said the program it ran in the Run window ended, or that its build failed, and it waits for a
    /// change (<see cref="HotReloadResult.Failed"/>), or that a change started it again (<see cref="HotReloadResult.Applied"/>),
    /// with the status bar's words of it.</summary>
    public event EventHandler<HotReloadOutcome>? WatchSaid;

    /// <summary>Ends following dotnet watch's words of the program it runs in the Run window, taking away what the window
    /// says of them; null while none is followed.</summary>
    private Action? unfollowWatched;

    /// <summary>Follows what dotnet watch says of the program it runs in the Run window, a line at a time, until the run's end
    /// or the next start: once the program has ended, or dotnet watch's build of it has failed (its errors in Problems as a
    /// build's, the first named), and dotnet watch waits for a change, the status bar and the Run window say so rather than
    /// what the last apply came to; once dotnet watch has started it and watches it, the status bar says that.</summary>
    private void FollowWatched(string name)
    {
        UnfollowWatched();
        ProcessRunner runner = session.Runner;
        string? ended = null;
        bool waits = false;
        bool started = false;
        // While the program runs, the lines that are not dotnet watch's are its own, never its build's.
        bool runs = false;
        List<Problem> errors = [];
        List<Problem> listed = [];
        EventHandler<string> heard = (_, line) =>
        {
            if (!line.StartsWith("dotnet watch", StringComparison.Ordinal))
            {
                if (!runs && BuildOutput.Parse(line) is { Severity: ProblemSeverity.Error } error
                    && Placed(error, ServerPlaces.Texts(parts.Editors)) is var placed && !errors.Contains(placed))
                {
                    errors.Add(placed);
                }
                return;
            }
            if (line.Contains(WatchWaits, StringComparison.Ordinal))
            {
                string end = ended ?? $"{name} ended";
                SayWaiting($"Hot Reload: {end}; dotnet watch starts it again when a change is saved, or Rerun starts it now",
                    $"{end}: dotnet watch starts it again when a change is saved", rerun: true, StatusPlace.Window(parts.Dock, ToolWindowIds.Run));
            }
            else if (line.Contains(WatchBuildFails, StringComparison.Ordinal))
            {
                List<Problem> failed = errors;
                ListErrors(failed);
                const string later = "dotnet watch builds it again when a change is saved";
                if (failed.Count == 0)
                {
                    SayWaiting($"Hot Reload: {name}'s build failed; {later}", $"{name}'s build failed: {later}", rerun: false, StatusPlace.Window(parts.Dock, ToolWindowIds.Run));
                    return;
                }
                Problem first = failed[0];
                string more = failed.Count > 1 ? $", and {Plural.Of(failed.Count - 1, "more error")}" : string.Empty;
                SayWaiting($"Hot Reload: {name}'s build failed: {first.Message}{(first.Where is { Length: > 0 } where ? $" ({where})" : string.Empty)}{more}; {later}",
                    $"{name}'s build failed with {Plural.Of(failed.Count, "error")}: {later}", rerun: false,
                    StatusPlace.Problems(parts.Dock, parts.Views.Problems, ProblemsTab.Build, p => p == first));
            }
            else if (WatchedEnd(line, name) is { } end)
            {
                ended = end;
                runs = false;
                errors = [];
            }
            else if (line.Contains(WatchWatches, StringComparison.Ordinal))
            {
                ended = null;
                runs = true;
                errors = [];
                if (waits)
                {
                    waits = false;
                    ListErrors([]);
                    string again = started ? $"Running {name} again with Hot Reload: a save applies its changes" : $"Running {name} with Hot Reload: a save applies its changes";
                    parts.Views.Run.ShowWaiting(null);
                    parts.Say(again);
                    WatchSaid?.Invoke(this, new HotReloadOutcome(HotReloadResult.Applied, again));
                }
                started = true;
            }
        };
        runner.Line += heard;
        unfollowWatched = () =>
        {
            runner.Line -= heard;
            parts.Views.Run.ShowWaiting(null);
        };

        // dotnet watch waits for a change: the status bar says why, a click going where it has more, and the Run window too.
        void SayWaiting(string said, string shown, bool rerun, StatusPlace place)
        {
            waits = true;
            runs = false;
            errors = [];
            parts.Views.Run.ShowWaiting(shown, rerun);
            parts.Say(said, place);
            WatchSaid?.Invoke(this, new HotReloadOutcome(HotReloadResult.Failed, said));
        }

        // Problems lists the errors of dotnet watch's last build in place of those of the one before, as it lists a build's.
        void ListErrors(List<Problem> now)
        {
            int gone = session.Problems.RemoveAll(listed.Contains);
            listed = [.. now.Where(p => !session.Problems.Contains(p))];
            session.Problems.AddRange(listed);
            if (gone + listed.Count > 0)
            {
                ShowProblems();
            }
        }
    }

    private void UnfollowWatched()
    {
        Action? unfollow = unfollowWatched;
        unfollowWatched = null;
        unfollow?.Invoke();
    }

    /// <summary>How a line of dotnet watch's says the program it ran ended ("dotnet watch : [Hello (net10.0)] Exited", "…
    /// Exited with error code 134"), the program named <paramref name="name"/>; null for any other line.</summary>
    private static string? WatchedEnd(string line, string name)
    {
        const string exited = "] Exited";
        const string coded = " with error code ";
        int at = line.IndexOf(exited, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        ReadOnlySpan<char> rest = line.AsSpan(at + exited.Length).TrimEnd();
        return rest.IsEmpty ? RunEnded(name, 0)
            : rest.StartsWith(coded, StringComparison.Ordinal)
                && int.TryParse(rest[coded.Length..].TrimEnd('.'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) ? RunEnded(name, code)
            : $"{name} ended";
    }

    /// <summary>A run that could not start leaves the Run window idle, Stop as the debugger has it, and says why.</summary>
    private Words? Started(Words? problem)
    {
        if (problem is not null)
        {
            UnfollowWatched();
            parts.Views.Run.SetRunning(false);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            ActionWork.Tell(false, problem);
            parts.Say(problem);
        }
        else
        {
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        return problem;
    }

    /// <summary>Says why a launch did not happen, unless its folder has ended (the window says the next one's then), tells it
    /// to the action heard in its flow, and gives it back.</summary>
    private Words Said(Words problem)
    {
        ActionWork.Tell(false, problem);
        if (!session.IsEnded)
        {
            parts.Say(problem);
        }
        return problem;
    }

    /// <summary>Says what a start of Run's came to as its program runs, with or without Hot Reload and why, and tells it as
    /// its work's end (<see cref="Working.Tell"/>), which an agent's Run answers.</summary>
    private void Running(Working work, Words said)
    {
        parts.Say(said);
        work.Tell(true, said);
    }

    public async Task<Words?> RunAspireAsync(RunConfiguration chosen, string? arguments, RunStart mode, CancellationToken cancellation = default)
    {
        // Each start under its own action's work: Debug's, Profile's, else Run's.
        string action = mode switch { RunStart.Debug => AppActions.Debug, RunStart.Profile => AppActions.Profile, _ => AppActions.RunProgram };
        if (parts.Work.Begin(action, $"Starting {chosen.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(action)!));
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            using CancellationTokenRegistration requestedStop = cancellation.Register(work.Cancel);
            try
            {
                if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
                {
                    return Said(unsaved);
                }
                RunConfiguration configuration = arguments is null ? chosen : chosen with { Arguments = arguments };
                // A start of the same AppHosts under way (Run's, as Debug begins) ends first.
                using StartTaken? taken = await TakeStartAsync([configuration.Project, .. configuration.LinkedAppHosts], work);
                if (taken is null)
                {
                    return Said(Canceled(work, chosen.Name));
                }
                if (Checkpoint(work, chosen.Name) is { } stopped)
                {
                    return Said(stopped);
                }
                if (parts.PrepareAspire is { } prepare && await prepare(configuration, work.Stopping) is { } notReady)
                {
                    return Said(notReady);
                }
                if (Checkpoint(work, chosen.Name) is { } prepared)
                {
                    return Said(prepared);
                }
                // The same configuration's AppHosts running end first; an AppHost a test run holds is the test run's.
                string[] hosts = [configuration.Project, .. configuration.LinkedAppHosts];
                List<string> running = [.. session.Aspire.Apps.Where(app => !app.IsTest && !app.Stopping && hosts.Any(host => PathNames.Same(host, app.Configuration.Project)))
                    .Select(app => app.Group).Distinct(StringComparer.Ordinal)];
                if (running.Count > 0)
                {
                    work.Say($"Ending {chosen.Name}'s run to start it again…");
                    foreach (string group in running)
                    {
                        await session.Aspire.StopGroupAsync(group);
                    }
                    if (Checkpoint(work, chosen.Name) is { } ended)
                    {
                        return Said(ended);
                    }
                }
                // Once, before any AppHost starts.
                HttpsChoice https = await CertificateAsync(chosen.Name, await session.Aspire.ServingHttpsAsync(configuration), Path.GetDirectoryName(configuration.Project)!,
                    ContainerPlans.StateOf(parts.StateFolder(), session.Folder ?? Path.GetDirectoryName(configuration.Project)!), export: false, work);
                if (Checkpoint(work, chosen.Name) is { } asked)
                {
                    return Said(asked);
                }
                parts.Dock.Show(ToolWindowIds.Aspire);
                Words? problem = await session.Aspire.StartAsync(configuration, mode, (step, reading, configured) => BuildForAsync(step, work, reading, configured), work.Stopping,
                    https);
                if (problem is not null)
                {
                    return Said(problem);
                }
                // This start's AppHosts alone, one group: another configuration's running on is no part of its end.
                string? own = session.Aspire.Apps.LastOrDefault(app => !app.IsTest && PathNames.Same(app.Configuration.Project, configuration.Project))?.Group;
                AspireAppRun[] started = [.. session.Aspire.Apps.Where(app => !app.IsTest && app.Group == own)];
                aspireEnd = Ended(started);
                parts.Say(AspireStarted(configuration, mode, started) + (https.OverHttp is { } overHttp ? $" ({overHttp})" : string.Empty),
                    StatusPlace.Window(parts.Dock, ToolWindowIds.Aspire));
                RunningChanged?.Invoke(this, EventArgs.Empty);
                return null;
            }
            catch (OperationCanceledException) when (work.IsStopped)
            {
                return Said(Canceled(work, chosen.Name));
            }
            catch (Exception error) when (error is LaunchException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                return Said(RefusedException.Of(error));
            }
        }

        static async Task<int> Ended(IReadOnlyList<AspireAppRun> running)
        {
            int[] codes = await Task.WhenAll(running.Select(app => app.Runner.Ended));
            return codes.FirstOrDefault(code => code != 0);
        }
    }

    /// <summary>What an AppHost's start says once up (<see cref="ServicesStarted"/>): the services it debugs are those marked
    /// Debug, else each it starts; the service profiled is the one recorded.</summary>
    private static string AspireStarted(RunConfiguration configuration, RunStart mode, IReadOnlyList<AspireAppRun> started)
    {
        string[] marked = [.. configuration.ServiceActions.Where(pair => pair.Value == ServiceStart.Debug).Select(pair => pair.Key).Order(StringComparer.Ordinal)];
        return ServicesStarted(configuration.Name, mode, mode switch
        {
            RunStart.Debug => marked.Length > 0 ? Plural.Listed(marked) : "each .NET service it starts",
            RunStart.Profile => started.Select(app => app.Profiled?.Name).OfType<string>().FirstOrDefault() ?? "its service",
            _ => "its .NET services",
        });
    }

    /// <summary>What a run of services' start says once up, every kind's: debugged, the <paramref name="services"/> under the
    /// debugger; run with Hot Reload, a save applying to them; profiled, the one recorded; or run.</summary>
    private static string ServicesStarted(string name, RunStart how, string services) => how switch
    {
        RunStart.Debug => $"Debugging {name}: {services} under the debugger",
        RunStart.HotReload => $"Running {name} with Hot Reload: a save applies its changes to {services}",
        RunStart.Profile => $"Profiling {services} of {name}: Get Snapshot when it has done what is to be measured",
        _ => $"Running {name}",
    };

    public Task<Words?> ProfileAspireAsync(RunConfiguration configuration, string? arguments = null, CancellationToken cancellation = default) =>
        RunAspireAsync(configuration, arguments, RunStart.Profile, cancellation);

    /// <summary>Rerun, which follows the configuration chosen: an AppHost's that runs, what changed in it restarted
    /// (<see cref="RerunAspireAsync"/>); else the container run of it under way in fast mode, the services whose build changed
    /// restarted (<see cref="RerunContainersAsync"/>); else what the Run console (or a terminal's tab) has stopped, its end
    /// waited for, and the chosen run, as Run runs it. What else runs (another configuration's AppHost, a debugged program,
    /// tests, a build) runs on. Null once done, else why not, said.</summary>
    public async Task<Words?> RerunAsync()
    {
        UiThread.Check();
        if (session.Configurations.Chosen is not { } chosen)
        {
            return Said(session.NothingTo("run"));
        }
        if (chosen.IsMultiple)
        {
            // Its programs are its own, never the Run console's: Run ends its last start itself.
            return await RunAsync();
        }
        switch (RestartedInPlace(chosen))
        {
            case AspireAppRun app:
                return await RerunAspireAsync(chosen, app);
            case ContainerRun containers:
                return await RerunContainersAsync(containers);
        }
        // A container run of it run again whole keeps what it started later (StartContainersAsync).
        rerun = Containers is { } going && going.Name == chosen.Name ? going : null;
        try
        {
            if (RunsInConsole || session.ContainerRun is { IsStopped: false })
            {
                await EndInConsoleAsync();
                if (session.IsEnded)
                {
                    return Refusals.SolutionClosed;
                }
            }
            return await RunAsync(scene: SceneOf(chosen));
        }
        finally
        {
            rerun = null;
        }
    }

    /// <summary>Rerun's one choice: the run of services of the chosen configuration that Rerun restarts in place, what changed
    /// in it alone (an AppHost's that runs, or its container run in fast mode, its log followed), or null when Rerun runs the
    /// configuration again whole.</summary>
    private ServiceRun? RestartedInPlace(RunConfiguration chosen) =>
        session.Aspire.IsAspire(chosen) && session.Aspire.FindApp(chosen.Project) is { IsTest: false, Stopping: false } app && app.Runner.IsRunning ? app
        : Containers is { Plan.Mode: ContainerMode.Fast } containers && containers.Plan.Name == chosen.Name && session.Runner.IsRunning ? containers
        : null;

    private async Task<Words?> RerunAspireAsync(RunConfiguration chosen, AspireAppRun main)
    {
        if (parts.Work.Begin(AppActions.RunProgram, $"Looking at what changed in {chosen.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(AppActions.RunProgram)!));
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            try
            {
                if (await parts.Editors.SaveAllLandedAsync(work, work.Stopping) is { } unsaved)
                {
                    return Said(unsaved);
                }
                AspireAppRun[] apps = session.Aspire.Apps.Where(app => app.Group == main.Group && !app.Stopping).ToArray();
                // Each service with when the build it runs began: its own restart's, else its AppHost's.
                (AspireAppRun App, (AspireResourceRun Service, BuiltInputs.Listing Since)[] Running)[] runs = [.. apps.Select(app => (app,
                    app.Services.Where(s => !s.Stopping.IsCancellationRequested).Select(s => (s, app.Rebuilt.GetValueOrDefault(s.Project, app.BuildBegan))).ToArray()))];
                (string Project, BuiltInputs.Listing Since)[] hosts = [.. apps.Select(app => (app.Configuration.Project, app.BuildBegan))];
                IReadOnlyList<ProjectModel> projects = session.Projects;
                // Read before anything is built, so only what changed stops and builds, never over a program's running files.
                (bool hostChanged, (AspireAppRun App, List<AspireResourceRun> Changed)[] services) = await BackgroundWork.Run(() =>
                    (hosts.Any(host => BuiltInputs.ChangedSince(host.Project, host.Since, projects, references: false)),
                    runs.Select(run => (run.App, run.Running.Where(s => s.Service.ChangedSinceStart(projects, s.Since)).Select(s => s.Service).ToList())).ToArray()));
                if (session.IsEnded || work.IsStopped)
                {
                    return Said(StoppedRestarting(chosen.Name));
                }
                if (!hostChanged)
                {
                    List<string> restarted = [];
                    foreach ((AspireAppRun app, List<AspireResourceRun> changes) in services.Where(s => s.Changed.Count > 0))
                    {
                        // Sessions first, held again, each stopped before its build.
                        if (await RestartServicesAsync(app, changes, work, "its code changed", build: true) is { } failed)
                        {
                            return Said(failed);
                        }
                        // Replicas share their service's name, said once with their count.
                        restarted.AddRange(changes.GroupBy(s => ((IRunService)s).Name).Select(named => named.Count() == 1 ? named.Key
                            : $"{named.Key} ({Plural.Of(named.Count(), "replica")})"));
                    }
                    parts.Say(Reran(chosen.Name, restarted));
                    return null;
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
            {
                return Said(error is OperationCanceledException ? StoppedRestarting(chosen.Name) : RefusedException.Of(error));
            }
        }
        // An AppHost's own code changed: it and its services stop, then it is built and run again as it was run.
        RunConfiguration again = main.Configuration;
        RunStart mode = main.Mode;
        parts.Say($"{chosen.Name}'s AppHost changed: stopping it to build and run it again…");
        await session.Aspire.StopGroupAsync(main.Group);
        return session.IsEnded ? Refusals.SolutionClosed : await RunAspireAsync(again, null, mode);
    }

    /// <summary>Whether Hot Reload reaches the program a configuration runs: one program, the folder's own (not a script's,
    /// nor Godot's editor, which loads its builds itself, nor a Functions app's, which func runs).</summary>
    public bool ReachesHotReload(RunConfiguration chosen) =>
        !chosen.IsScript && !chosen.GodotEditor && chosen.NamesNoOtherProgram(session.Projects) && !chosen.RunsFunctions(session.Projects);

    /// <summary>Resolves and builds the chosen configuration, then launches its plan under the debugger, with these arguments
    /// in place of its own if given; the Debug window opens on its Console until the program stops somewhere. Null once its
    /// session is open, or what stopped it (a failed build, no configuration, the adapter's refusal).</summary>
    public Task<Words?> DebugAsync(string? arguments = null, CancellationToken cancellation = default) =>
        session.Configurations.Chosen is { } chosen ? DebugAsync(chosen, arguments, cancellation) : Task.FromResult<Words?>(session.NothingTo("debug"));

    /// <summary>Each configuration Debug launched, by its name, and the arguments it was given, which a restart for Hot
    /// Reload launches again.</summary>
    private readonly Dictionary<string, (RunConfiguration Configuration, string? Arguments)> debugged = [];

    /// <summary>Hot Reload's restart for an edit it cannot apply: the program Debug launched for a configuration (the
    /// debugger's chosen session's) is stopped (on Windows its files are locked while it runs), built again and launched with
    /// the same configuration and arguments, the breakpoints the store's. Null once the new session is open, else why it
    /// could not.</summary>
    public async Task<Words?> RestartDebugAsync(string name)
    {
        if (profileTabs.TryGetValue(name, out (RunConfiguration Profile, RunConfiguration Project, int Place) member)
            && session.Debug.CopiesOf(member.Profile.Name).Any(c => c.Tab.Name == name))
        {
            return await RestartProfileTabAsync(name, member.Profile, member.Project, member.Place);
        }
        if (!debugged.TryGetValue(name, out (RunConfiguration Configuration, string? Arguments) last))
        {
            return LaunchWords.NothingToRestart;
        }
        (RunConfiguration configuration, string? arguments) = last;
        // A copy among several restarts alone, the others debugged on.
        IReadOnlyList<(ToolWindows.DebugTab Tab, int Copy)> copies = session.Debug.CopiesOf(configuration.Name);
        if (copies.Count > 1 && copies.FirstOrDefault(c => c.Tab.Name == name) is { Tab: { } tab } own)
        {
            return await RestartDebuggedCopyAsync(configuration, arguments, tab, own.Copy);
        }
        await session.Debug.StopAsync(configuration.Name);
        return await DebugAsync(configuration, arguments);
    }

    /// <summary>One copy of a game debugged beside others restarted alone: its session stopped, the game built again, and the
    /// copy launched in its place with its own arguments and the edits from this build; null once its session is open, else
    /// why not.</summary>
    private async Task<Words?> RestartDebuggedCopyAsync(RunConfiguration configuration, string? arguments, ToolWindows.DebugTab tab, int copy)
    {
        if (parts.Work.Begin(AppActions.Debug, $"Restarting {tab.Name}…") is not { } work)
        {
            return ActionWork.Busy(parts.Work.Of(AppActions.Debug)!);
        }
        using (work)
        using (BackgroundWork.Scope())
        {
            await session.Debug.StopAsync(tab);
            BuiltEdits? edits = null;
            try
            {
                (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(configuration, arguments, work, reading: () =>
                {
                    edits?.Dispose();
                    edits = session.Debug.Building(session.Debug.HotReloadOn());
                });
                if (plan is null)
                {
                    return problem;
                }
                session.Debug.JoinCopies(configuration.Name);
                return await LaunchCopiesAsync(configuration, plan, [copy], arguments, work, edits);
            }
            finally
            {
                edits?.Dispose();
            }
        }
    }

    private async Task<Words?> DebugAsync(RunConfiguration chosen, string? arguments, CancellationToken cancellation = default)
    {
        if (chosen.IsScript)
        {
            return Said(Refusals.ScriptOnlyRuns(chosen.Name));
        }
        if (chosen.IsMultiple)
        {
            return await StartProjectsAsync(chosen, debug: true, null, cancellation);
        }
        if (session.Aspire.IsAspire(chosen))
        {
            return await RunAspireAsync(chosen, arguments, RunStart.Debug, cancellation);
        }
        if (chosen.GodotInEditor && parts.InGodotEditor is { } inEditor)
        {
            return await inEditor(chosen, true, cancellation);
        }
        if (parts.Work.Begin(AppActions.Debug, $"Working out how to debug {chosen.Name}…") is not { } work)
        {
            return ActionWork.Busy(parts.Work.Of(AppActions.Debug)!);
        }
        using (work)
        {
            Words? refused = await DebugAsync(chosen, arguments, work);
            // What it came to, which an agent's Debug answers: the window says it itself.
            ActionWork.Tell(refused is null, refused ?? LaunchWords.Debugging(chosen.Name));
            return refused;
        }
    }

    private async Task<Words?> DebugAsync(RunConfiguration chosen, string? arguments, Working work)
    {
        if (chosen.IsContainer)
        {
            // A session per service, each attached in its container as Compose brings the run up.
            return await RunContainersAsync(chosen, work, RunStart.Debug);
        }
        debugged[chosen.Name] = (chosen, arguments);
        profileTabs.Remove(chosen.Name);
        HearNeeds(null, chosen.Name, debugged: true);
        using IDisposable held = BackgroundWork.Scope();
        if (await EndRunningAsync(chosen, work, "debug") is { } left)
        {
            return left;
        }
        // The edits made since its build saved the files, followed for its session (Hot Reload's, its built lines).
        BuiltEdits? edits = null;
        RunDashboard? dashboard = null;
        try
        {
            (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(chosen, arguments, work, reading: () =>
            {
                edits?.Dispose();
                edits = session.Debug.Building(session.Debug.HotReloadOn());
            }, collects: true);
            if (plan is null)
            {
                return problem;
            }
            dashboard = plan.Dashboard;
            int count = GameCopies.Count(chosen, plan);
            Words? refused = count == 1 ? await LaunchDebuggedAsync(chosen, plan, work, edits) : await LaunchCopiesAsync(chosen, plan, Enumerable.Range(1, count), arguments, work, edits);
            if (refused is null && session.Debug.Tabs.FirstOrDefault(t => t.Name == plan.Name && t.Plan?.Dashboard == dashboard) is { } tab)
            {
                // Debug's own Stop of the session takes it down (DebugController.StopAsync).
                Keep(Take(ref dashboard), DebugEnded(session.Debug.SessionNamed(plan.Name)), () => false, said => tab.Console.Append($"{said}\n"));
            }
            return refused;
        }
        finally
        {
            edits?.Dispose();
            dashboard?.Dispose();
        }
    }

    /// <summary>Copies of a game launched under the debugger one after another from one build, each a session of its own named
    /// for its place (<see cref="GameCopies"/>), each following its own copy of the edits made since the build; null once
    /// every one is open, else why the first that could not start did not, the copies before it running on.</summary>
    private async Task<Words?> LaunchCopiesAsync(RunConfiguration chosen, LaunchPlan plan, IEnumerable<int> copies, string? arguments, Working work, BuiltEdits? edits)
    {
        List<int> numbers = [.. copies];
        List<BuiltEdits?> each = [edits, .. numbers.Skip(1).Select(_ => edits is null ? null : session.Debug.Copied(edits))];
        try
        {
            for (int i = 0; i < numbers.Count; i++)
            {
                LaunchPlan copy = GameCopies.Plan(chosen, plan, numbers[i], ConfigurationNames);
                debugged[copy.Name] = (chosen, arguments);
                if (await LaunchDebuggedAsync(chosen, copy, work, each[i], copies: chosen.Name) is { } refused)
                {
                    return i == 0 ? refused : LaunchWords.CopyDidNotStart(copy.Name, refused, Plural.Listed([.. numbers.Take(i).Select(n => CopyName(chosen, n))]));
                }
            }
            return null;
        }
        finally
        {
            foreach (BuiltEdits? left in each.Skip(1))
            {
                left?.Dispose();
            }
        }
    }

    /// <summary>Debug Another Copy: one more copy of the chosen configuration's game debugged beside its copies debugged now,
    /// on the build they run, the next in their count with that copy's own arguments; null once its session is open, else
    /// why not.</summary>
    public async Task<Words?> DebugAnotherCopyAsync(CancellationToken cancellation = default)
    {
        if (session.Configurations.Chosen is not { } chosen)
        {
            return session.NothingTo("debug");
        }
        if (WhyNotAnotherCopy(chosen) is { } why)
        {
            return why;
        }
        if (parts.Work.Begin(AppActions.Debug, $"Working out how to debug another copy of {chosen.Name}…") is not { } work)
        {
            return ActionWork.Busy(parts.Work.Of(AppActions.Debug)!);
        }
        using (work)
        using (cancellation.Register(work.Cancel))
        using (BackgroundWork.Scope())
        {
            IReadOnlyList<(ToolWindows.DebugTab Tab, int Copy)> running = session.Debug.CopiesOf(chosen.Name);
            int next = running.Max(c => c.Copy) + 1;
            (LaunchPlan? plan, Words? problem, _) = await PrepareAsync(chosen, null, work, build: false);
            if (plan is null)
            {
                return problem;
            }
            if (WhyNotAnotherCopy(chosen) is { } gone)
            {
                return gone;
            }
            session.Debug.JoinCopies(chosen.Name);
            // The edits since the build the copies run, as the one debugged first follows them.
            using BuiltEdits? edits = session.Debug.Carried(running.MinBy(c => c.Copy).Tab.Name, session.Debug.HotReloadOn());
            return await LaunchCopiesAsync(chosen, plan, [next], null, work, edits);
        }
    }

    /// <summary>Why Debug Another Copy cannot run now: the chosen configuration starts no Godot game, or none of its copies is
    /// debugged, or it starts as many as there may be.</summary>
    public Words? WhyNotAnotherCopy(RunConfiguration chosen)
    {
        if (chosen.IsContainer || session.Aspire.IsAspire(chosen) || !chosen.RunsGodot(session.Projects) || chosen.GodotEditor)
        {
            return LaunchWords.NoGameToDebugAgain(chosen.Name);
        }
        IReadOnlyList<(ToolWindows.DebugTab Tab, int Copy)> running = session.Debug.CopiesOf(chosen.Name);
        return running.Count == 0 ? LaunchWords.DebugsNoCopy(chosen.Name)
            : running.Count >= GameCopies.Most ? LaunchWords.MostCopies(name: chosen.Name, most: GameCopies.Most)
            : null;
    }

    /// <summary>The next Debug's launch held by a test once its build has ended, or once its session is open
    /// (<see cref="HoldNextLaunch"/>): what it waits for, and done once it waits.</summary>
    private (Task Until, TaskCompletionSource Holding, bool Opened)? launchHeld;

    /// <summary>Holds the next Debug's launch, once its build has ended (with <paramref name="opened"/>, once its session is
    /// open and its program runs, before the launch returns), until <paramref name="until"/> ends, for a test of what goes on
    /// between them. Done once it is held.</summary>
    public Task HoldNextLaunch(Task until, bool opened = false)
    {
        TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        launchHeld = (until, holding, opened);
        return holding.Task;
    }

    /// <summary>Launches a built plan under the debugger, its session taking the <paramref name="edits"/> followed since its
    /// build; null once its session is open, else why not.</summary>
    private async Task<Words?> LaunchDebuggedAsync(RunConfiguration chosen, LaunchPlan plan, Working work, BuiltEdits? edits, string? copies = null)
    {
        if (plan.WhyNot("debug") is { } why)
        {
            return why;
        }
        if (launchHeld is { Opened: false } hold)
        {
            launchHeld = null;
            hold.Holding.TrySetResult();
            try
            {
                await hold.Until.WaitAsync(work.Stopping);
            }
            catch (OperationCanceledException)
            {
                return Canceled(work, chosen.Name);
            }
        }
        if (plan.Device?.App is { } device)
        {
            return await DebugOnDeviceAsync(plan, device, work, edits);
        }
        if (plan.MacCatalyst is { } catalyst)
        {
            // Its runtime's debugger libraries load only into a Mac Catalyst process: it is debugged through Passenger's relay in it.
            return catalyst.App is { } opened ? await DebugOnDeviceAsync(plan, opened, work, edits) : LaunchWords.CatalystAppUnbuilt(plan.Name);
        }
        if (plan.Functions)
        {
            return await DebugFunctionsAsync(plan, work, edits);
        }
        if (plan.Package is { } package && OperatingSystem.IsWindows())
        {
            return await DebugPackageAsync(plan, package, work, edits);
        }
        work.Say($"Starting {chosen.Name} under the debugger…");
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        // Before the session clears the Debug window's console, which forgets the links the last one's output had.
        DebugProject = Path.GetDirectoryName(plan.Project);
        DebugController debug = session.Debug;
        // A Godot game's GDScript is debugged from its first line, beside its C#.
        (LaunchPlan listened, Godot.GodotGame? game) = session.Listened(plan, plan.Godot is { } folder ? debug.ScriptStart(folder) : null);
        if (plan.ScriptsAlone && game is not null)
        {
            return await LaunchScriptsAsync(listened, game, copies);
        }
        // A Blazor app's code in the browser is debugged in a Chromium browser, where one is found.
        IReadOnlyList<ProjectModel> projects = session.Projects;
        PageBrowser? named = PageBrowsers.Of(listened.BrowserLine, listened.Name, parts.Settings().Browser);
        (Debugging.BrowserDebugging? inBrowser, string? notInBrowser) = await BackgroundWork.Run(() => Debugging.BrowserDebugging.For(listened, projects, named));
        listened = listened with { Browser = inBrowser, BrowserWhyNot = notInBrowser };
        if (inBrowser is not null && PathNames.Same(listened.InBrowser[0], listened.Project!))
        {
            // A standalone app's browser session runs its development server itself, no engine beside it.
            return await UntrustedPageAsync(inBrowser) is { } untrusted ? LaunchWords.BrowserNotDebugged(listened.Name, untrusted)
                : await debug.LaunchBrowserAsync(listened, inBrowser);
        }
        // An Avalonia or WPF app is launched with the Live Visual Tree's inspector, held by its tab, ending with this debugging.
        (LaunchPlan launched, LiveApp? inspected) = session.Inspected(listened);
        Task<Words?> starting = debug.StartAsync(launched, app: inspected, edits: edits, copies: copies, work: work);
        // Its tab, chosen as the start began, has its console where the program writes there.
        HearNeeds(copies is null && launched.Console == ConsoleKind.Internal ? debug.Chosen?.Console : null, chosen.Name, debugged: true);
        if (game is not null && debug.Chosen?.Console is { } gameConsole)
        {
            gameConsole.Fold = new Godot.GodotConsoleFold();
            game.Console = gameConsole.Fold;
        }
        // Its tab, chosen as the start began, has the console its failed bindings are written to.
        WatchBindings(inspected, debug.Chosen?.Console);
        Words? refused = await starting;
        if (refused is null && launchHeld is { Opened: true } runs)
        {
            launchHeld = null;
            runs.Holding.TrySetResult();
            await runs.Until.Ended();
        }
        if (game is not null && refused is null)
        {
            game.Debugged = debug.SessionNamed(launched.Name);
            game.ReloadsScripts = debug.HotReloadOn();
            EndsWithGame(game);
            IdeErrors.Observe(DebugScriptsAsync(game, launched.Name));
        }
        else
        {
            game?.LetScriptsGo();
        }
        Task ended = refused is null ? DebugEnded(debug.SessionNamed(launched.Name)) : Task.CompletedTask;
        EndWith(inspected, ended);
        EndWith(game, ended);
        if (refused is null && inBrowser is not null)
        {
            IdeErrors.Observe(DebugBrowserAsync(launched, inBrowser, ended));
        }
        else if (refused is null)
        {
            parts.OpenBrowser(plan, ended);
        }
        return refused;
    }

    /// <summary>Debugs a Functions app as func runs it (<see cref="Aspire.FunctionsHost"/>): func started with the app's worker
    /// held for the debugger, which attaches to the worker once func has started it, func's output the session's console,
    /// and func ended with the session. Null once the session is open, else why not.</summary>
    private async Task<Words?> DebugFunctionsAsync(LaunchPlan plan, Working work, BuiltEdits? edits)
    {
        work.Say($"Starting {plan.Name} by func, its worker held for the debugger…");
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        DebugProject = Path.GetDirectoryName(plan.Project);
        Words? refused = await session.Debug.StartThroughAsync(Aspire.FunctionsHost.Debugged(plan), Aspire.FunctionsHost.StartedWorkerAsync, Aspire.FunctionsHost.NoWorker, edits,
            work);
        if (refused is null)
        {
            parts.OpenBrowser(plan, DebugEnded(session.Debug.SessionNamed(plan.Name)));
        }
        return refused;
    }

    /// <summary>A Windows app package debugged: registered from its build, then started by Windows through Passenger's starter,
    /// held at its runtime's start (and open to Hot Reload) until the debugger attaches to it, its inspector in it as a desktop
    /// app's is, its starter's words in its console; Stop ends it with its starter. Null once its session is open, else why not.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<Words?> DebugPackageAsync(LaunchPlan plan, WindowsPackageLaunch launch, Working work, BuiltEdits? edits)
    {
        (WindowsPackage? package, string? unregistered) = await WindowsPackages.RegisterAsync(plan.Name, launch, plan.Unseen, said => work.Say(said), work.Stopping);
        if (package is null)
        {
            return unregistered;
        }
        work.Say($"Starting {plan.Name} under the debugger, held at its start until the debugger has it…");
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        DebugProject = Path.GetDirectoryName(plan.Project);
        (LaunchPlan launched, LiveApp? inspected) = session.Inspected(plan);
        Dictionary<string, string> variables = new(launched.Environment);
        foreach ((string name, string value) in HeldStart.Variables(changeable: true))
        {
            variables[name] = value;
        }
        string started = WindowsPackages.StartedFile();
        LaunchPlan starter = launched with
        {
            Program = Product.Program,
            Arguments = [],
            Leading = 0,
            Environment = WindowsPackages.StarterVariables(package, plan.Arguments, variables, started),
        };
        Task<Words?> starting = session.Debug.StartThroughAsync(starter, (by, stop) => WindowsPackages.StartedAsync(started, () => stop.IsCancellationRequested || !DotnetProcesses.Runs(by)),
            LaunchWords.NoPackageAppStarted(package.FamilyName), edits, work, hotReload: true, app: inspected, by: "Windows");
        WatchBindings(inspected, session.Debug.Chosen?.Console);
        return await starting;
    }

    /// <summary>A Blazor Web App's WebAssembly half debugged beside its server, in a tab of its own
    /// (<see cref="DebugController.DebugBrowserAsync"/>), ended as the server's session ends; one whose browser could not
    /// start says why, the server debugged on.</summary>
    private async Task DebugBrowserAsync(LaunchPlan plan, Debugging.BrowserDebugging browser, Task serverEnded)
    {
        DebugController debug = session.Debug;
        if ((await UntrustedPageAsync(browser) ?? await debug.DebugBrowserAsync(plan, browser)) is { } refused)
        {
            parts.Say(LaunchWords.BrowserNotDebugged(plan.Name, refused));
            return;
        }
        await serverEnded;
        if (debug.SessionNamed(DebugController.BrowserTab(plan.Name)) is { State: not DebugState.Ended })
        {
            await debug.StopAsync(DebugController.BrowserTab(plan.Name));
        }
    }

    /// <summary>Why a page served over HTTPS cannot be debugged in the browser: the development certificate it is served with
    /// is missing or not trusted here, which the browser refuses, with the person's fix. The certificate is only read, as
    /// dotnet dev-certs checks it, never made or trusted here; null for a page over HTTP, or one the check could not say.</summary>
    private async Task<Words?> UntrustedPageAsync(Debugging.BrowserDebugging browser)
    {
        if (browser.Page.Scheme != Uri.UriSchemeHttps || session.Folder is not { } folder)
        {
            return null;
        }
        DotnetWords dotnet = parts.DevCertificates ?? DevelopmentCertificate.Machine;
        (DevelopmentCertificate.Standing standing, _, _) = await BackgroundWork.Run(() => DevelopmentCertificate.CheckAsync(dotnet, folder, CancellationToken.None));
        return standing switch
        {
            DevelopmentCertificate.Standing.NotTrusted => LaunchWords.PageCertificateUntrusted(browser.Page.Authority),
            DevelopmentCertificate.Standing.Missing => LaunchWords.PageCertificateMissing(browser.Page.Authority),
            _ => null,
        };
    }

    /// <summary>Debug of a Godot game of GDScript alone: its GDScript session launches it, no C# engine beside it, Hot Reload
    /// of its saved scripts as Settings' Hot Reload while debugging says. Null once its session is open, else why not.</summary>
    private async Task<Words?> LaunchScriptsAsync(LaunchPlan plan, Godot.GodotGame game, string? copies)
    {
        DebugController debug = session.Debug;
        Words? refused = await debug.LaunchScriptsAsync(plan, game, copies);
        if (refused is null)
        {
            game.Debugged = debug.SessionNamed(plan.Name);
            game.ReloadsScripts = debug.HotReloadOn();
        }
        EndWith(game, refused is null ? DebugEnded(debug.SessionNamed(plan.Name)) : Task.CompletedTask);
        return refused;
    }

    /// <summary>What Godot's reload of a saved script keeps and what it does not, said once a game in its console.</summary>
    public const string ScriptReloadKeeps = "Godot reloads a saved GDScript file in place: each node running it keeps its members' values and runs "
        + "the new code from its next call; _init and _ready do not run again, and a member the edit adds starts as null, its initializer unrun";

    /// <summary>Has each Godot game that runs with Hot Reload (Run's with it on, Debug's while Hot Reload while debugging is)
    /// reload a GDScript file of its project just saved, through its debugger's <c>reload_scripts</c>: the GDScript sessions'
    /// breakpoints follow the file's lines as saved. Said in the status bar, and once a game in its console what the reload keeps.</summary>
    public async Task ReloadScriptAsync(string path)
    {
        UiThread.Check();
        if (!Godot.GodotScriptAdapter.Takes(path))
        {
            return;
        }
        List<string> reloaded = [];
        foreach (Godot.GodotGame game in session.Games.Where(g => g is { State: LiveAppState.Connected, ReloadsScripts: true }).ToList())
        {
            if (GodotResPaths.Of(game.Folder, path) is not { } resource)
            {
                continue;
            }
            try
            {
                await game.SendToAsync(Godot.GodotWords.ReloadScripts, Godot.GodotMessage.MainThread, resource);
            }
            catch (LiveAppException lost)
            {
                parts.Say($"Hot Reload: {Path.GetFileName(path)} did not reach {game.Name}: {lost.Message}");
                continue;
            }
            reloaded.Add(game.Name);
            if (!game.ToldReload)
            {
                game.ToldReload = true;
                OutputView console = game.Debugged is { } debugged && session.Debug.TabOf(debugged) is { } tab ? tab.Console : parts.Views.Run;
                console.Append($"Hot Reload: {ScriptReloadKeeps}\n");
            }
        }
        if (reloaded.Count > 0)
        {
            await session.Debug.ScriptReloadedAsync(path);
            parts.Say($"Hot Reload: {Path.GetFileName(path)} reloaded in {Plural.Listed(reloaded)}");
        }
    }

    /// <summary>A Godot game's GDScript debugged beside its C#, in a tab of its own (<see cref="DebugController.DebugScriptsAsync"/>);
    /// one whose debugger could not start lets the game go on past its script stops, as a game Run runs does.</summary>
    private async Task DebugScriptsAsync(Godot.GodotGame game, string name)
    {
        if (await session.Debug.DebugScriptsAsync(game, name) is not null)
        {
            game.LetScriptsGo();
        }
    }

    /// <summary>The errors and warnings the last Godot game launched raised, in Problems' Godot tab as they come, each at the
    /// first place of its stack in the project (a script's frame, else where the engine says it was raised), the newest
    /// game's alone.</summary>
    private void FollowGame(Godot.GodotGame game)
    {
        parts.Views.Problems.BeginGodot();
        game.Raised += (_, error) =>
        {
            if (session.Game != game || session.IsEnded)
            {
                return;
            }
            parts.Views.Problems.RaiseGodot(Godot.GodotProblems.Of(error, game));
        };
    }

    /// <summary>An app's inspector (or a Godot game's debugger) let go once its run or debugging ends: one that never
    /// connected (startup hooks turned off in the app, a game that failed first) waits no longer. Its failed bindings stay in
    /// Problems until the next launch of its kind.</summary>
    /// <summary>A debugged Godot game's session is shown ended as the game ends, its connection and then its process gone,
    /// whatever its debugger is still doing.</summary>
    internal static void EndsWithGame(Godot.GodotGame game)
    {
        if (game.Debugged is not { } debugged)
        {
            return;
        }
        void Changed(object? sender, EventArgs e)
        {
            if (game.State != LiveTree.LiveAppState.Ended)
            {
                return;
            }
            game.StateChanged -= Changed;
            // A session over already (Stop, Detach) let the game go: nothing is to be waited for.
            if (debugged.State != DebugState.Ended)
            {
                IdeErrors.Observe(GoneAsync());
            }
        }
        async Task GoneAsync()
        {
            if (game.ProcessId is int id && await BackgroundWork.Run(() => Exits(id, TimeSpan.FromSeconds(5))))
            {
                debugged.ProgramGone();
            }
        }
        game.StateChanged += Changed;

        static bool Exits(int id, TimeSpan limit)
        {
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
                process.WaitForExit(limit);
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already, or not to be waited on: whether it runs says which.
            }
            return !DotnetProcesses.Runs(id);
        }
    }

    private static void EndWith(IDisposable? app, Task ended)
    {
        if (app is not null)
        {
            IdeErrors.Observe(EndWithAsync(app, ended));
        }

        static async Task EndWithAsync(IDisposable app, Task ended)
        {
            await ended;
            app.Dispose();
        }
    }

    /// <summary>An app's failed bindings as they come: each a line of its console linked to its XAML (none for a program in
    /// the terminal, whose text is its own), and Problems' Bindings, with what the app could not tell. Those told at once
    /// (thousands, as a window opens) are written and listed once, after the last of them.</summary>
    private void WatchBindings(LiveApp? app, OutputView? console)
    {
        if (app is null)
        {
            return;
        }
        StringBuilder said = new();
        bool due = false;
        bool untoldSaid = false;
        app.BindingFailed += (_, failed) =>
        {
            said.Append(BindingErrors.Line(Placed(app, failed.Failure), session.Folder));
            Show();
        };
        app.UntoldChanged += (_, _) =>
        {
            if (app.Untold.Bindings > 0 && !untoldSaid)
            {
                untoldSaid = true;
                said.Append(BindingErrors.UntoldLine(app));
            }
            Show();
        };

        void Show()
        {
            if (due)
            {
                return;
            }
            due = true;
            UiThread.Post(() =>
            {
                due = false;
                if (session.IsEnded)
                {
                    return;
                }
                if (said.Length > 0)
                {
                    console?.Append(said.ToString(), error: true);
                    said.Clear();
                }
                ShowBindings();
            });
        }
    }

    /// <summary>The failed bindings of the apps Run and Debug launched last, in Problems' Bindings.</summary>
    public void ShowBindings()
    {
        List<(LiveApp App, BindingFailure Failure)> bindings = [.. session.FailedBindings];
        HashSet<LiveApp> showing = [.. bindings.Select(binding => binding.App)];
        foreach ((LiveApp App, int Id) key in placed.Keys.Where(key => !showing.Contains(key.App)).ToArray())
        {
            placed.Remove(key);
        }
        parts.Views.Problems.ShowBindings([.. bindings.Select(binding => Placed(binding.App, binding.Failure))],
            [.. session.InspectedApps.SelectMany(BindingErrors.Untold)]);
    }

    /// <summary>A failure with its problem, which is placed once, when it first fails: at its misspelt member where its XAML
    /// has it, else at its control's element.</summary>
    public BindingProblem Placed(LiveApp app, BindingFailure failure)
    {
        if (!placed.TryGetValue((app, failure.Id), out Problem? problem))
        {
            problem = placed[(app, failure.Id)] = BindingErrors.Problem(failure, failure.Place is { } at ? FailureText(at.File) : null).Problem;
        }
        return new BindingProblem(failure, problem);
    }

    private TextSnapshot? FailureText(string file)
    {
        if (failureTexts is null)
        {
            failureTexts = new(StringComparer.Ordinal);
            Dispatcher.UIThread.Post(() => failureTexts = null);
        }
        if (!failureTexts.TryGetValue(file, out TextSnapshot? text))
        {
            failureTexts[file] = text = BindingErrors.TextOf(file, parts.Editors.Documents);
        }
        return text;
    }

    /// <summary>The end of a session; done already when there is none.</summary>
    private static Task DebugEnded(DebugSession? session)
    {
        if (session is not { State: not DebugState.Ended } running)
        {
            return Task.CompletedTask;
        }
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        running.StateChanged += (_, _) =>
        {
            if (running.State == DebugState.Ended)
            {
                ended.TrySetResult();
            }
        };
        return ended.Task;
    }

    /// <summary>Stop: any start under way (Run's, Debug's or Profile's, ended at whatever stage it is), the debugger's chosen
    /// session (every one when none is chosen), the program Run started (in the Run window or the terminal), the one Profile
    /// started (its snapshot taken or not) and the build.</summary>
    public void Stop()
    {
        parts.Work.Stop();
        IdeErrors.Observe(session.Debug.StopAsync());
        IdeErrors.Observe(StopRest());
    }

    /// <summary>Stop All, the window's and an agent's stop: as Stop, every program debugged ended rather than the chosen one;
    /// done once each has ended, Run's program has let go of the Run console (its end landed there, so a start after the
    /// answer finds it free), a container run's containers are taken down (not left running as its log's end would leave
    /// them), an app on a device has ended there, and a start on one it stopped has taken out what it had put in.</summary>
    public Task StopAllAsync()
    {
        parts.Work.Stop();
        Task debugged = session.Debug.StopAllAsync();
        return Task.WhenAll(debugged, StopRest(), session.EndedOnDevicesAsync());
    }

    /// <summary>What every Stop ends besides the debugger's: a container run (done once its containers are down), Run's
    /// program in the Run window or the terminal (done once its end has landed), Profile's, and the build.</summary>
    private Task StopRest()
    {
        // The Run window's run ends now (Run's or Profile's), and what runs that ended by themselves kept.
        foreach (RunDashboard collected in session.Dashboards.Where(d => d.Kept || d == parts.Views.RunWindow.Dashboard).ToList())
        {
            collected.Dispose();
        }
        Task containers = session.StopContainersAsync();
        Task aspire = session.Aspire.StopAllAsync();
        Task ran = session.RunProgram.IsRunning ? session.RunProgram.Ended : Task.CompletedTask;
        session.RunProgram.Stop();
        Task copies = EndCopyRunsAsync();
        Task partsRun = EndPartRunsAsync();
        session.Profiled?.Kill();
        session.Builder.Stop();
        Task played = parts.StopInGodotEditor?.Invoke() ?? Task.CompletedTask;
        return Task.WhenAll(containers, aspire, ran, copies, partsRun, played);
    }

    /// <summary>Rerun's stop: Run's own start under way and the program it started (in the Run window or the terminal, or its
    /// container run), never another configuration's AppHost, the debugger's session, a test run or a build, which Rerun
    /// does not run again.</summary>
    public void StopRun()
    {
        parts.Work.Of(AppActions.RunProgram)?.Cancel();
        IdeErrors.Observe(session.StopContainersAsync());
        session.RunProgram.Stop();
        IdeErrors.Observe(EndCopyRunsAsync());
        IdeErrors.Observe(EndPartRunsAsync());
    }

    /// <summary>Whether a run of a configuration is up: Run's program (in the Run window or the terminal) or container run,
    /// while the Run console (or the terminal's tab) has it, a session Debug launched for it, or the program Profile started.
    /// An AppHost's run is said by its own (<see cref="MainWindow.Shown"/>).</summary>
    public bool IsRunningConfiguration(string name) =>
        RunsInConsole && (Containers?.Plan.Name ?? ran) == name
        || copyRuns.Any(c => c.Configuration == name && c.Run.IsRunning)
        || partRuns.Any(p => p.Run == name && p.IsRunning)
        || session.Debug.CopiesOf(name).Any(c => debugged.ContainsKey(c.Tab.Name) || profileTabs.ContainsKey(c.Tab.Name))
        || session.Profiled is { Run.HasExited: false } profiled && profiled.Name == name;

    /// <summary>Why Stop cannot run now: nothing it stops is running.</summary>
    public string? WhyNotStop() => session.Debug.IsDebugging || IsRunning || session.Profiled is not null || session.Builder.IsRunning || parts.Work.CanStop
        || parts.PlaysInGodotEditor?.Invoke() == true || session.Dashboards.Any(d => d.Kept) ? null : "Nothing is running";
}

/// <summary>How a start of a configuration that runs goes on, as the person answered (<see cref="BuildRunParts.AskRunAgain"/>).</summary>
public enum RunAgain
{
    StopAndRerun,
    Stop,
    Cancel,
}

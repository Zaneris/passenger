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
    Func<RunConfiguration, CancellationToken, Task<string?>>? PrepareAspire = null)
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

    /// <summary>Lands the files a folder holds in another, as the window writes files (one command of Undo named by the
    /// title, for those in the open folder): the files written, or why not.</summary>
    public Func<string, string, string, Task<(IReadOnlyList<string> Written, string? Problem)>>? LandFiles { get; init; }

    /// <summary>Where the folder's global.json chose an SDK that does not build one of its projects, as the window says it;
    /// null where it did not.</summary>
    public Func<SdkChosen?>? SdkChosen { get; init; }

    /// <summary>Opens a file at a place in the editor: global.json, from the status bar's word that it chose the SDK.</summary>
    public Action<string, TextPosition>? Navigate { get; init; }

    /// <summary>Asks the person whether a container run may replace containers of its Compose project Passenger did not make
    /// (true), given the configuration, the project and those containers' names; closed unanswered (false) once the start is
    /// stopped.</summary>
    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<bool>>? AskReplace { get; init; }

    /// <summary>Plays a configuration whose game Godot's editor open on the project plays (<see cref="RunConfiguration.GodotInEditor"/>),
    /// under the debugger where asked: null once it plays, else why not.</summary>
    public Func<RunConfiguration, bool, CancellationToken, Task<string?>>? InGodotEditor { get; init; }

    /// <summary>Stop's end of the games Godot's editor plays because Passenger asked it to.</summary>
    public Func<Task>? StopInGodotEditor { get; init; }

    /// <summary>Whether Godot's editor plays a game because Passenger asked it to.</summary>
    public Func<bool>? PlaysInGodotEditor { get; init; }

    /// <summary>Whether nobody is at the window to ask (an agent's own IDE, <c>--mcp</c>): what would ask the person takes the
    /// way that asks nothing, saying so.</summary>
    public bool AsksNoOne { get; init; }

    /// <summary>Whether nobody sees the window (<see cref="MainWindow.Unseen"/>): every Godot game and editor it runs runs
    /// headless.</summary>
    public bool Unseen { get; init; }

    /// <summary>Has the window's profiler record a service's program of a run of services where its kind runs it (in its
    /// container, or on this machine), held at its start (a run's Profile, which the recording lets go) or as it runs (its
    /// last argument): null once it records, the run's Stop then reading its snapshot first, else why not.</summary>
    public Func<ServiceRun, IRunService, ServiceProgram, bool, CancellationToken, Task<string?>>? ProfileService { get; init; }
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
    private string? buildFramework;
    private string? buildOutput;
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
        session.Builder.Line += (_, line) =>
        {
            (Problem? next, bool continued) = BuildOutput.Next(reading, line, builtPaths);
            if (!continued)
            {
                ReadProblem();
            }
            reading = next;
            if (checking is not null && BuildOutput.Made(line) is { } assembly)
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
            string? refused = await AttachStartAsync(app, service, purpose, notes, stop);
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
        session.Aspire.RunsUndebugged += (_, said) =>
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
        string said = session.ContainerRun is { } containers ? ContainersEnded(containers) : RunEnded(ran, code);
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
    public static string RunEnded(string name, int code) => $"{name} exited with code {code}";

    /// <summary>Whether a build, rebuild or clean is under way, from its start until its callers have heard how it ended.</summary>
    public bool IsBuilding => building is not null;

    /// <summary>Whether a program Run started runs, in the Run window or the terminal, or an AppHost it started.</summary>
    public bool IsRunning => session.Aspire.IsRunning || RunsInConsole || copyRuns.Any(c => c.Run.IsRunning);

    /// <summary>The copies of a Godot game Run runs side by side, each in a tab of the Terminal window, by its configuration's
    /// name (<see cref="GameCopies"/>): Stop ends them together.</summary>
    private readonly List<(string Configuration, TerminalRun Run)> copyRuns = [];

    /// <summary>The copies of a game Run runs now, each in its tab of the Terminal window.</summary>
    public IReadOnlyList<TerminalRun> CopyRuns => [.. copyRuns.Select(c => c.Run)];

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
    public Task<BuildOutcome> BuildAsync(BuildCause cause, CancellationToken cancel)
    {
        TaskCompletionSource<BuildOutcome> built = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration stopping = default;
        Build(BuildKind.Build, null, outcome =>
        {
            stopping.Dispose();
            built.TrySetResult(outcome);
        }, cause: cause);
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
        Start(buildKind, lastProjects, null, buildFramework, BuildCause.Explicit, null, null, buildOutput, lastTogether);
    }

    /// <summary>Builds, rebuilds or cleans the solution, or the projects given one after another (stopping at the first
    /// that fails): the output in the Build window, which opens as it starts as Settings says for its <paramref name="cause"/>,
    /// the problems of all of them in Problems. <paramref name="then"/> always hears how it ended, a build refused or never
    /// started included. A framework given builds the projects for it alone, and an output folder given builds them there
    /// (a single-file AppHost's). A build or rebuild that succeeds is each project's last build here, for a launch's check.</summary>
    public void Build(BuildKind kind, IReadOnlyList<string>? projects = null, Action<BuildOutcome>? then = null, string? framework = null,
        BuildCause cause = BuildCause.Explicit, string? output = null) => Start(kind, projects, then, framework, cause, null, null, output);

    /// <summary>Starts a build, or the publish given (its work begun already, which the build holds until it ends), or the
    /// build of projects side by side given (its project the one in <paramref name="projects"/>).</summary>
    private void Start(BuildKind kind, IReadOnlyList<string>? projects, Action<BuildOutcome>? then, string? framework, BuildCause cause, PublishRequest? publish,
        Working? work, string? output = null, TogetherBuild? side = null)
    {
        UiThread.Check();
        if (session.Folder is null || IsBuilding || session.IsEnded)
        {
            BuildOutcome refused = new(BuildStatus.Refused, session.Folder is null || session.IsEnded ? Refusals.NoFolderTo("build") : Refusals.Building);
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
        IReadOnlyList<string> whole = session.Workspace!.BuildTargets(session.Projects);
        buildQueue.Clear();
        foreach (string target in projects is { Count: > 0 } ? projects : whole)
        {
            buildQueue.Enqueue(target);
        }
        if (buildQueue.Count == 0)
        {
            BuildOutcome nothing = new(BuildStatus.Refused, "Nothing to build: every project is unloaded");
            publishing = null;
            together = null;
            work?.Tell(false, nothing.Message);
            work?.Dispose();
            parts.Say(nothing.Message);
            then?.Invoke(nothing);
            return;
        }
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
        else if (FileText.Landings().Count == 0)
        {
            BuildNext(first: true);
        }
        else
        {
            IdeErrors.Observe(BuildLandedAsync());
        }
    }

    /// <summary>Starts the build once the saves still landing have (<see cref="EditorArea.LandedAsync"/>), so it compiles what
    /// was saved: one that never lands, or a stop meanwhile, ends the build unstarted, saying why.</summary>
    private async Task BuildLandedAsync()
    {
        Working? work = buildWork;
        if (await EditorArea.LandedAsync(work, work?.Stopping ?? CancellationToken.None) is { } unsaved)
        {
            FinishBuild(work is { IsStopped: true } ? new BuildOutcome(BuildStatus.Stopped, "Build stopped") : new BuildOutcome(BuildStatus.NotStarted, unsaved));
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
        IReadOnlyList<string> arguments = publishing is { } publish ? publish.Arguments(windows) : BuildOutput.Arguments(target, buildKind, buildFramework, windows, buildOutput);
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
            ? buildFramework is null && buildOutput is null && Launched(project) is { } frameworks
                ? [.. frameworks.Select(framework => (new BuildStep(target, framework), true))]
                : [(new BuildStep(target, buildFramework, buildOutput), false)]
            : session.Workspace?.Solution is { } solution && PathNames.Same(solution.Path, target)
                ? [.. session.Projects.Where(p => !p.IsProgram && WindowsTargeting([p.Path]) == windows)
                    .SelectMany(p => Launched(p) is { } frameworks ? frameworks.Select(framework => new BuildStep(p.Path, framework)) : [new BuildStep(p.Path, null)])
                    .Select(step => (step, true))]
                : [];
        if (steps.Count == 0 || session.Folder is not { } folder || session.IsEnded)
        {
            return;
        }
        // The host is asked of one framework at a time, each framework's projects side by side.
        List<List<(BuildStep Step, bool MustShow)>> asked = [.. steps.GroupBy(step => step.Step.Framework).Select(group => group.ToList())];
        IReadOnlyList<UpToDate.Check?>[] found = await BackgroundWork.Run(() => Task.WhenAll(asked.Select(group => UpToDate.CheckAsync([.. group.Select(step => step.Step)],
            [.. group.Select(step => BuildOutput.Arguments(step.Step.Project, BuildKind.Build, step.Step.Framework, windows, step.Step.Output))], windows, folder, stop))));
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
            string which = running.Count == 1 ? $"build of {Path.GetFileNameWithoutExtension(running[0])}" : $"builds of {running.Count} projects";
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
                + (errors > 0 ? "Problems lists them" : $"dotnet exited with code {code}, the Build window says why"));
        if (ended.Succeeded && checking is { Count: > 0 } checks)
        {
            IdeErrors.Observe(KeepAsync(checks, ended));
            return;
        }
        FinishBuild(ended);
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

    /// <summary>The last build's problems in the Problems window, its title counting them.</summary>
    public void ShowProblems()
    {
        List<Problem> problems = session.Problems;
        parts.Views.Problems.Show(problems);
        if (parts.Dock[ToolWindowIds.Problems] is { } problemsWindow)
        {
            problemsWindow.Title = problems.Count == 0 ? "Problems" : $"Problems {problems.Count}";
        }
    }

    /// <summary>A configuration's plan, with these arguments in place of its own if given, or why it has none.</summary>
    public async Task<(LaunchPlan? Plan, string? Problem)> ResolvePlanAsync(RunConfiguration chosen, string? arguments, CancellationToken stop = default)
    {
        try
        {
            return (await LaunchPlan.ResolveAsync(arguments is null ? chosen : chosen with { Arguments = arguments }, session.Projects, session.Workspace, parts.StateFolder(), stop,
                parts.Settings().GodotExecutable, parts.Editors.Active?.Path, session.GodotEnvironment, parts.Unseen), null);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return (null, StoppedStarting(chosen.Name));
        }
        catch (Exception error) when (error is LaunchException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, error.Message);
        }
    }

    /// <summary>Why Run Current Scene cannot run now: the file open is in no Godot project of the folder's.</summary>
    public string? WhyNotRunScene() => parts.Editors.Active?.Path is { } path && session.RunProjects.Any(p => p.GodotFolder is { } folder && PathNames.IsUnder(path, folder))
        ? null : Refusals.NoGodotScene;

    /// <summary>Run Current Scene: the Godot game of the project the open scene (or script) is in, run from that scene (or the
    /// one the script is attached in, <see cref="Passenger.Workspace.GodotProject.SceneOf"/>), its configuration chosen (the
    /// one chosen when it runs that game, else its generated one). Null once it runs, else why not.</summary>
    public async Task<string?> RunCurrentSceneAsync()
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
            return Said($"{project.Name} has no configuration that runs its game");
        }
        return await RunAsync(scene: RunConfiguration.CurrentScene);
    }

    /// <summary>What a start Stop ended says, every kind's: a program's, a container run's, an AppHost's.</summary>
    public static string StoppedStarting(string name) => $"{name} was stopped as it started";

    /// <summary>What a restart of a run's services Stop ended says (Rerun's, a window's Restart, Hot Reload's), every kind's.</summary>
    public static string StoppedRestarting(string name) => $"{name}'s restart was stopped";

    /// <summary>What Rerun of a run of services says once done, every kind's: the services restarted with their new build
    /// (with <paramref name="more"/>, what else came of it), or that nothing changed.</summary>
    public static string Reran(string name, IReadOnlyList<string> restarted, string? more = null) => restarted.Count == 0
        ? $"{name}: nothing changed, nothing restarted" : $"{name}: restarted {string.Join(", ", restarted)} with the new build{more}";

    /// <summary>A configuration's plan with its build before launch done: the plan to start, or why there is none, the folder
    /// left or Stop meanwhile among the reasons; <c>Unresolved</c> when the problem is the plan's own (what the configuration
    /// runs could not be worked out), which a start shows where it shows its own state. Where the start <paramref name="collects"/>
    /// (Run's, Debug's, Profile's) and its configuration collects its logs and traces, its dashboard comes up as its project
    /// builds, the plan's program told of it (<see cref="RunDashboard.CollectAsync"/>), and the start ends it with the program.</summary>
    public async Task<(LaunchPlan? Plan, string? Problem, bool Unresolved)> PrepareAsync(RunConfiguration chosen, string? arguments, Working work, bool build = true,
        Action? reading = null, bool collects = false)
    {
        (LaunchPlan? plan, string? problem) = await ResolvePlanAsync(chosen, arguments, work.Stopping);
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
        if (build && plan.Build is { } step && await BuildForAsync(step, work, reading) is { Succeeded: false } unbuilt)
        {
            await collecting.CancelAsync();
            await AbandonAsync(told);
            return (null, Checkpoint(work, chosen.Name) ?? unbuilt.Message, false);
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
    private string? Checkpoint(Working work, string name) => session.IsEnded ? Refusals.SolutionClosed : work.IsStopped ? StoppedStarting(name) : null;

    /// <summary>What a start ended by a cancel says: its folder's end, else Stop's.</summary>
    private string Canceled(Working work, string name) => session.IsEnded ? Refusals.SolutionClosed : StoppedStarting(name);

    /// <summary>A start's build of what it starts, in the Build window, which comes forward only as Settings says for a build
    /// Run, Debug or Profile begins: stopped as the start is (<paramref name="stopping"/>), as Stop stops a build. What is
    /// open is saved first, <paramref name="reading"/> told just before; a project whose last build here stands
    /// (<see cref="UpToDate"/>) is not built again, the Build window and the status bar saying so.</summary>
    public Task<BuildOutcome> BuildForAsync(BuildStep step, CancellationToken stopping, Action? reading = null) =>
        BuildForAsync([step.Project], stopping, null, step.Framework, step.Output, reading);

    /// <summary>A start's build of what it starts, as <see cref="BuildForAsync(BuildStep, CancellationToken)"/>, saying in its
    /// work when it waits for a build under way; <paramref name="reading"/> is told as it saves the files its build reads.</summary>
    public Task<BuildOutcome> BuildForAsync(BuildStep step, Working work, Action? reading = null) =>
        BuildForAsync([step.Project], work.Stopping, work, step.Framework, step.Output, reading);

    /// <summary>What a start says of a project it did not build: nothing its build reads changed since its last.</summary>
    public static string UpToDateSaid(string project) => $"{project} is up to date: nothing its build reads has changed since it was built, so it was not built again";

    /// <summary>What a start says of projects it did not build, one or several.</summary>
    public static string UpToDateSaid(IReadOnlyList<string> projects) => projects.Count == 1 ? UpToDateSaid(projects[0])
        : $"{Plural.Listed(projects)} are up to date: nothing their builds read has changed since they were built, so they were not built again";

    /// <summary>A start's build of its projects (<see cref="BuildForAsync(BuildStep, CancellationToken)"/>), several side by
    /// side in one dotnet build (<see cref="TogetherBuild"/>), after any build under way: none is built when the last build
    /// here of each stands, and all are when one does not, what each build left then kept for its next start's check.
    /// <paramref name="reading"/> is told just before each save of the files, which the build then reads as saved.</summary>
    private async Task<BuildOutcome> BuildForAsync(IReadOnlyList<string> projects, CancellationToken stopping, Working? work, string? framework = null, string? output = null,
        Action? reading = null)
    {
        UiThread.Check();
        List<UpToDate.Check> checks = [];
        TogetherBuild? side = null;
        while (session.Folder is { } folder && !session.IsEnded && projects.Count > 0)
        {
            if (building is { } under)
            {
                // Begun before this start or while it checked, a build may change what the check reads.
                string? stage = work?.Stage;
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
            (IReadOnlyList<UpToDate.Check?> found, side) = await BackgroundWork.Run(async () => (
                await UpToDate.CheckAsync([.. projects.Select(project => new BuildStep(project, framework, output))],
                    [.. projects.Select(project => BuildOutput.Arguments(project, BuildKind.Build, framework, windows, output))], windows, folder, stopping),
                projects.Count > 1 ? TogetherBuild.Write(state, folder, projects) : null));
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
        Start(BuildKind.Build, side is null ? projects : [side.Project], outcome => ended.TrySetResult(outcome), framework, BuildCause.BeforeLaunch, null, null, output, side);
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
    public async Task<string?> RunAsync(string? arguments = null, bool? hotReload = null, CancellationToken cancellation = default, string? scene = null)
    {
        if (session.Configurations.Chosen is not { } chosen)
        {
            return Said(session.NothingTo("run"));
        }
        // Run Current Scene's: this start of a Godot game begins in the scene open.
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
        if (await TakeConsoleAsync(chosen) is { } taken)
        {
            return Said(taken);
        }
        // Copies of it run beside in the Terminal window end too, as its launch again ends the last.
        await EndCopyRunsAsync(chosen.Name);
        aspireEnd = null;
        // The Run console is one program's: Profile's writes there too, from its start to its end.
        if (parts.Work.Of(AppActions.Profile) is { } profiling)
        {
            return Said(ActionWork.Busy(profiling));
        }
        if (session.Profiled is { Run.HasExited: false } profiled)
        {
            return Said($"{profiled.Name}, which Profile started, uses the Run console: Kill it first");
        }
        if (parts.Work.Begin(AppActions.RunProgram, $"Working out how to run {chosen.Name}…") is not { } work)
        {
            return Said(ActionWork.Busy(parts.Work.Of(AppActions.RunProgram)!));
        }
        if (chosen.IsContainer)
        {
            using (work)
            {
                // With Hot Reload through the debugger in each service: dotnet watch would need the SDK in its image.
                return await RunContainersAsync(chosen, work, hotReload ?? parts.Settings().HotReload ? RunStart.HotReload : RunStart.Run);
            }
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
            bool wanted = (hotReload ?? parts.Settings().HotReload) && chosen.NamesNoOtherProgram(session.Projects);
            bool godot = chosen.RunsGodot(session.Projects);
            // A game's errors, which Godot writes every frame they happen, folded in its console.
            parts.Views.Run.Fold = godot && !chosen.GodotEditor ? new Godot.GodotConsoleFold() : null;
            // An Avalonia or WPF app's XAML edits reach it through the inspector, which the app dotnet watch starts never carries.
            bool markup = wanted && !godot && RunsXamlApp(chosen);
            bool watched = wanted && !godot && !markup;
            // A Godot game and a XAML app take their edits through the debugger, attached quietly as each starts held; Godot's editor loads its builds itself.
            bool reloaded = wanted && (godot && !chosen.GodotEditor || markup);
            watching = watched;
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, arguments, work, build: !watched, reading: reloaded ? () =>
            {
                edits?.Dispose();
                edits = session.Debug.Building(hotReload: true);
            } : null, collects: true);
            if (plan is null)
            {
                return Said(problem!);
            }
            dashboard = plan.Dashboard;
            if (plan.WhyNot("run", built: !watched) is { } why)
            {
                return Said(why);
            }
            if (watched && MakeWorkingFolder(plan) is { } unmade)
            {
                return Said(unmade);
            }
            // Copies of a game each run in a tab of the Terminal window of their own: the Run console is one program's.
            if (GameCopies.Count(chosen, plan) is > 1 and int count)
            {
                return await RunCopiesAsync(chosen, plan, count, reloaded, work, Take(ref edits));
            }
            // A game of GDScript alone takes its saved scripts over its own connection: no debugger holds its start for C#'s edits.
            bool heldStart = reloaded && !plan.ScriptsAlone;
            work.Say($"Starting {chosen.Name}…");
            ran = chosen.Name;
            RunProject = Path.GetDirectoryName(plan.Project);
            // A Godot game's, with Passenger as its remote debugger.
            (plan, Godot.GodotGame? game) = session.Listened(plan);
            game?.ReloadsScripts = reloaded;
            game?.Console = parts.Views.Run.Fold;
            session.RunPlan = plan;
            string running = watched || reloaded ? $"Running {chosen.Name} with Hot Reload: a save applies its changes"
                : wanted && godot && !plan.ScriptsAlone ? $"Running {chosen.Name} without Hot Reload: {GodotLaunch.EditorReloads}"
                : $"Running {chosen.Name}";
            // An Avalonia or WPF app is launched with the inspector in it, which tells of its failed bindings while it runs.
            LaunchPlan launched = session.InspectedRun(plan, watched);
            LiveApp? inspected = session.RunApp;
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
                // A XAML app's update handlers run as each update lands, through Passenger's hook, as a debugged one's do.
                if (markup && UpdateHandlerHook.For(launched with { Environment = variables }) is ({ } hook, { } hooked))
                {
                    (handlers, variables) = (hook, new Dictionary<string, string>(hooked.Environment));
                }
            }
            if (plan.Console == ConsoleKind.Terminal)
            {
                // The terminal's text is the program's own: its failed bindings are in Problems alone.
                WatchBindings(inspected, null);
                string program = GodotConsole.Program(plan, plan.Program);
                // What the run says of itself goes with its status, the terminal's tab being the program's own.
                string? refused = await RunInTerminalAsync(chosen.Name, watched ? DotnetStart(Watch(plan), plan.WorkingDirectory) : ChildProcesses.Command(program, plan.Arguments, plan.WorkingDirectory), variables,
                    string.Join("; ", [running, .. plan.Notes]));
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
                            return Said(unfollowed.Message);
                        }
                        await AttachForHotReloadAsync(chosen.Name, inTerminal.Debuggee, null, work, Take(ref edits), running, Take(ref handlers), inspected);
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
            ShowInRun(dashboard);
            parts.Say(running);
            WatchBindings(inspected, parts.Views.Run);
            if (watched)
            {
                FollowWatched(chosen.Name);
            }
            // Its fork off the window's thread (#441): the program runs from now as Stop and IsRunning see it, its id known once started.
            string? notStarted = Started(watched
                ? await session.Runner.StartDotnetAsync(Watch(plan), plan.WorkingDirectory, variables, plan.Placed)
                : await session.Runner.StartAsync(plan.Start(), variables));
            if (notStarted is null)
            {
                parts.OpenBrowser(plan, session.Runner.Ended);
            }
            EndWith(inspected, notStarted is null ? session.Runner.Ended : Task.CompletedTask);
            EndWith(game, notStarted is null ? session.Runner.Ended : Task.CompletedTask);
            if (notStarted is null)
            {
                Keep(Take(ref dashboard), session.Runner.Ended, () => session.Runner.WasStopped, said => parts.Views.Run.Append($"{said}\n"));
            }
            if (notStarted is null && heldStart && session.Runner.ProcessId is int id)
            {
                await AttachForHotReloadAsync(chosen.Name, id, parts.Views.Run.Sink, work, Take(ref edits), running, Take(ref handlers), inspected);
            }
            return notStarted;
        }
        finally
        {
            edits?.Dispose();
            handlers?.Dispose();
            dashboard?.Dispose();
            work.Dispose();
        }
    }

    /// <summary>Runs copies of a game side by side from one build, each in a tab of the Terminal window named for its place
    /// (<see cref="GameCopies"/>), its own console, followed in the Live Visual Tree; where Hot Reload is on, a saved GDScript
    /// file reloads in each, and each is held at its start and attached to quietly for C#'s, following its own copy of the
    /// edits since the build (<paramref name="edits"/>, the first's). An earlier run's copies of it end first. Null once every
    /// one runs, else why the first that could not start did not, the copies before it running on.</summary>
    private async Task<string?> RunCopiesAsync(RunConfiguration chosen, LaunchPlan plan, int count, bool reloaded, Working work, BuiltEdits? edits)
    {
        List<BuiltEdits?> each = [edits, .. Enumerable.Range(2, count - 1).Select(_ => edits is null ? null : session.Debug.Copied(edits))];
        try
        {
            await EndCopyRunsAsync(chosen.Name);
            List<string> started = [];
            for (int copy = 1; copy <= count; copy++)
            {
                work.Say($"Starting {GameCopies.Name(chosen.Name, copy)}, {copy} of {count}…");
                BuiltEdits? its = each[copy - 1];
                each[copy - 1] = null;
                if (await RunCopyAsync(chosen, plan, copy, reloaded, work, its) is { } refused)
                {
                    return Said(copy == 1 ? refused : $"{GameCopies.Name(chosen.Name, copy)} did not start ({refused}); {Plural.Listed(started)} run on");
                }
                started.Add(GameCopies.Name(chosen.Name, copy));
            }
            parts.Say($"Running {count} copies of {chosen.Name}{(reloaded && !plan.ScriptsAlone ? " with Hot Reload: a save applies its changes in each" : string.Empty)}, each in a tab of the Terminal window",
                StatusPlace.Window(parts.Dock, ToolWindowIds.Terminal));
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

    /// <summary>Runs one copy of a game (from one) in a tab of the Terminal window of its own, as Run's copies run; with Hot
    /// Reload on and C# in the game, held at its runtime's start and attached to quietly (<see cref="AttachForHotReloadAsync"/>)
    /// with <paramref name="edits"/>, which it takes, as Run's one game is. Null once it runs, else why not.</summary>
    private async Task<string?> RunCopyAsync(RunConfiguration chosen, LaunchPlan plan, int copy, bool reloaded, Working work, BuiltEdits? edits)
    {
        bool heldStart = reloaded && !plan.ScriptsAlone;
        (LaunchPlan each, Godot.GodotGame? game) = session.Listened(GameCopies.Plan(chosen, plan, copy));
        game?.ReloadsScripts = reloaded;
        string program = GodotConsole.Program(each, each.Program);
        ProcessStartInfo start = ChildProcesses.Command(program, each.Arguments, each.WorkingDirectory);
        foreach ((string variable, string value) in each.Environment)
        {
            start.Environment[variable] = value;
        }
        if (heldStart)
        {
            // Held at its runtime's start until the debugger attaches, so each of its modules takes Edit and Continue as it loads.
            foreach ((string name, string value) in HeldStart.Variables(changeable: true))
            {
                start.Environment[name] = value;
            }
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
            return failed.Message;
        }
        run.StartedBy = Mcp.AgentCall.Current?.Connection;
        copyRuns.Add((chosen.Name, run));
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
            return unfollowed.Message;
        }
        await AttachForHotReloadAsync(each.Name, run.Debuggee, null, work, edits, $"Running {each.Name} with Hot Reload: a save applies its changes");
        return null;

        async Task CopyEnded()
        {
            await run.Ended;
            copyRuns.RemoveAll(c => c.Run == run);
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Run Another Copy: one more copy of the chosen configuration's game run beside the copies Run runs now (or
    /// beside its one game in the Run console, the second copy), in a tab of the Terminal window of its own, on the build they
    /// run; null once it runs, else why not.</summary>
    public async Task<string?> RunAnotherCopyAsync(CancellationToken cancellation = default)
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
            int next = copyRuns.Where(c => c.Configuration == chosen.Name).Select(c => GameCopies.Of(c.Run.Name).Copy)
                .Append(RunsOwnProgram(chosen.Name) ? 1 : 0).Max() + 1;
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, null, work, build: false);
            if (plan is null)
            {
                return Said(problem!);
            }
            if (WhyNotRunAnotherCopy(chosen) is { } gone)
            {
                return Said(gone);
            }
            work.Say($"Starting {GameCopies.Name(chosen.Name, next)}…");
            bool reloaded = parts.Settings().HotReload && chosen.NamesNoOtherProgram(session.Projects);
            // The edits since the build the copies run, as the first of them attached for Hot Reload follows them.
            string? first = RunsOwnProgram(chosen.Name) ? chosen.Name : copyRuns.Where(c => c.Configuration == chosen.Name).Select(c => c.Run.Name).MinBy(n => GameCopies.Of(n).Copy);
            BuiltEdits? edits = reloaded && !plan.ScriptsAlone && first is not null ? session.Debug.Carried(first, hotReload: true) : null;
            if (await RunCopyAsync(chosen, plan, next, reloaded, work, edits) is { } refused)
            {
                return Said(refused);
            }
            parts.Say($"Running {GameCopies.Name(chosen.Name, next)} beside {chosen.Name}, in a tab of the Terminal window", StatusPlace.Window(parts.Dock, ToolWindowIds.Terminal));
            return null;
        }
    }

    /// <summary>How many copies of a configuration's game Run runs in the Terminal window.</summary>
    private int CopiesRun(string configuration) => copyRuns.Count(c => c.Configuration == configuration);

    /// <summary>Why Run Another Copy cannot run now: the chosen configuration starts no Godot game, or Run runs none of its
    /// copies, or as many as there may be.</summary>
    public string? WhyNotRunAnotherCopy(RunConfiguration chosen)
    {
        if (chosen.IsContainer || session.Aspire.IsAspire(chosen) || !chosen.RunsGodot(session.Projects) || chosen.GodotEditor)
        {
            return $"{chosen.Name} starts no Godot game, of which Run Another Copy runs one more beside those running";
        }
        int running = CopiesRun(chosen.Name) + (RunsOwnProgram(chosen.Name) ? 1 : 0);
        return running == 0 ? $"Run runs no copy of {chosen.Name}: Run starts it, then Run Another Copy one more beside it"
            : running >= GameCopies.Most ? $"{chosen.Name} runs {GameCopies.Most} copies, the most one configuration runs"
            : null;
    }

    /// <summary>Ends the copies Run runs (of one configuration, or every one), done once each has ended.</summary>
    private Task EndCopyRunsAsync(string? configuration = null)
    {
        List<TerminalRun> ending = [.. copyRuns.Where(c => configuration is null || c.Configuration == configuration).Select(c => c.Run)];
        foreach (TerminalRun run in ending)
        {
            run.Stop();
        }
        return Task.WhenAll(ending.Select(r => r.Ended));
    }

    /// <summary>Whether Run runs the Godot game of configuration <paramref name="name"/>, or the copy of one so named, with Hot
    /// Reload through the debugger.</summary>
    public bool RunsGameWithHotReload(string name) =>
        (RunsOwnProgram(name) && session.RunPlan is { Godot: not null, GodotEditor: false } || copyRuns.Any(c => c.Run.Name == name))
        && session.Debug.Running.Any(r => r.Name == name);

    /// <summary>Restarts the Godot game Run runs with Hot Reload so the edits Hot Reload could not apply run: built and started
    /// again, held and attached to again, while its configuration is still the one chosen; a copy of it alone, the others
    /// running on. True once it runs again.</summary>
    public async Task<bool> RestartGameForEditAsync(string name)
    {
        if (!RunsGameWithHotReload(name))
        {
            return false;
        }
        if (copyRuns.FirstOrDefault(c => c.Run.Name == name) is { Run: { } copy } run)
        {
            return session.Configurations.Chosen is { } chosen && chosen.Name == run.Configuration && await RestartRunCopyAsync(chosen, copy);
        }
        if (session.Configurations.Chosen?.Name != name)
        {
            return false;
        }
        parts.Say($"Hot Reload: restarting {name} to apply the changes");
        return await RunAsync(hotReload: true) is null;
    }

    /// <summary>One copy of a game Run runs with Hot Reload restarted alone so the edits Hot Reload could not apply run: ended,
    /// built again, and started in its tab's place, held and attached to again with the edits from this build, the other
    /// copies running on. True once it runs again.</summary>
    private async Task<bool> RestartRunCopyAsync(RunConfiguration chosen, TerminalRun copy)
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
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, null, work, reading: () =>
            {
                edits?.Dispose();
                edits = session.Debug.Building(hotReload: true);
            });
            if (plan is null)
            {
                edits?.Dispose();
                parts.Say($"Hot Reload could not restart {copy.Name}: {problem}");
                return false;
            }
            return await RunCopyAsync(chosen, plan, GameCopies.Of(copy.Name).Copy, reloaded: true, work, Take(ref edits)) is null;
        }
    }

    /// <summary>What a start holds (the edits followed for a session, its dashboard), handed over once: what takes it lets it go.</summary>
    private static T? Take<T>(ref T? held) where T : class
    {
        T? taken = held;
        held = null;
        return taken;
    }

    /// <summary>Whether a configuration runs an Avalonia or WPF app of its project, whose XAML edits reach it through
    /// Passenger's inspector, which the app dotnet watch starts never carries: Run's Hot Reload takes it through the debugger.</summary>
    private bool RunsXamlApp(RunConfiguration chosen) =>
        !chosen.IsContainer && chosen.Program.Length == 0 && session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, chosen.Project)) is { } project
        && (project.Packages.Any(p => p.Name.Equals("Avalonia", StringComparison.OrdinalIgnoreCase))
            || project.Evaluated?.Dependencies.Any(d => FrameworkNames.UsesWpf(d.FrameworkReferences)) == true);

    /// <summary>A Godot game or a XAML app (its inspector in <paramref name="app"/>, its update handlers' hook <paramref name="handlers"/>)
    /// Run started held for Hot Reload, attached to quietly (<see cref="DebugController.AttachForHotReloadAsync"/>), which
    /// lets it go: what its applies come to is said in <paramref name="console"/> (null for the terminal's tab, whose text is
    /// the program's own) and the status bar, which says <paramref name="running"/> once attached. One the debugger cannot
    /// reach is let go and runs without Hot Reload, saying why.</summary>
    private async Task AttachForHotReloadAsync(string name, int processId, ConsoleSink? console, Working work, BuiltEdits? edits, string running,
        UpdateHandlerHook? handlers = null, LiveApp? app = null)
    {
        work.Say($"Attaching to {name} for Hot Reload…");
        string? refused = await session.Debug.AttachForHotReloadAsync(processId, name, (line, error) =>
        {
            console?.Write($"{line}\n", error ? ConsoleStream.Error : ConsoleStream.Output);
            parts.Say(line);
        }, cancellation: work.Stopping, edits: edits, handlers: handlers, app: app);
        if (refused is null)
        {
            parts.Say(running);
            return;
        }
        // Held at its start, it would wait for good for a debugger that is not coming.
        await HeldStart.LetGoAsync(processId);
        parts.Say($"Running {name} without Hot Reload: {refused}");
    }

    /// <summary>The Run console (or the terminal's tab) for a start of a configuration: free; the same configuration's program
    /// there ended first and its end waited for, as a start of a configuration that runs ends it and starts again; or a
    /// container run there, which a container start ends itself (one container run at a time). Null once it is the start's,
    /// else why not: another configuration's program has it, named.</summary>
    private async Task<string?> TakeConsoleAsync(RunConfiguration chosen)
    {
        if (!RunsInConsole || chosen.IsContainer && session.ContainerRun is { IsStopped: false })
        {
            return null;
        }
        if (session.ContainerRun is { IsStopped: false } || ran != chosen.Name)
        {
            return AlreadyRunning();
        }
        await EndInConsoleAsync();
        return session.IsEnded ? Refusals.SolutionClosed : null;
    }

    /// <summary>Why a start that needs the Run console cannot have it: what has it, by its configuration's name.</summary>
    private string AlreadyRunning() => $"Already running: {(ran.Length > 0 ? ran : "a program")} has the Run console; stop it first";

    /// <summary>Whether the program Run started for this configuration has the Run console (or the terminal's tab), which a
    /// start of it under the debugger or the profiler ends first.</summary>
    public bool RunsOwnProgram(string name) => RunsInConsole && ran == name && session.ContainerRun is not { IsStopped: false };

    /// <summary>Ends the program Run started for a configuration that Debug or Profile (<paramref name="doing"/>) is to start
    /// in its place, and the copies of it Run runs: done once their ends have landed. Null to go on, else why not (the
    /// folder left meanwhile).</summary>
    public async Task<string?> EndOwnRunAsync(RunConfiguration chosen, Working work, string doing)
    {
        if (copyRuns.Any(c => c.Configuration == chosen.Name))
        {
            work.Say($"Ending the copies of {chosen.Name} Run runs to {doing} it…");
            await EndCopyRunsAsync(chosen.Name);
            if (session.IsEnded)
            {
                return Refusals.SolutionClosed;
            }
        }
        if (!RunsOwnProgram(chosen.Name))
        {
            return null;
        }
        work.Say($"Ending {chosen.Name}'s run to {doing} it…");
        await EndInConsoleAsync();
        return session.IsEnded ? Refusals.SolutionClosed : null;
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
    private static string? MakeWorkingFolder(LaunchPlan plan)
    {
        try
        {
            Directory.CreateDirectory(plan.WorkingDirectory);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"Could not run {plan.ProgramName}: its working folder {plan.WorkingDirectory} could not be made: {error.Message}";
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
    private async Task<string?> RunInTerminalAsync(string name, ProcessStartInfo? start, IReadOnlyDictionary<string, string> variables, string running)
    {
        if (start is null)
        {
            return Said("No .NET command line found: install the .NET SDK, or set DOTNET_ROOT to where it is.");
        }
        foreach ((string variable, string value) in variables)
        {
            start.Environment[variable] = value;
        }
        TerminalRun run;
        try
        {
            run = await parts.InTerminal(name, start);
        }
        catch (LaunchException failed)
        {
            return Said(failed.Message);
        }
        run.StartedBy = Mcp.AgentCall.Current?.Connection;
        session.InTerminal = run;
        RunningChanged?.Invoke(this, EventArgs.Empty);
        parts.Say(running);
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
    /// its own working folder, which is the plan's.</summary>
    private static List<string> Watch(LaunchPlan plan)
    {
        List<string> watch = ["watch", "--non-interactive", "run", Passenger.Host.ProgramFiles.IsProgram(plan.Project!) ? "--file" : "--project", plan.Project!, "--no-launch-profile"];
        if (plan.Framework is { } framework)
        {
            watch.AddRange(["--framework", framework]);
        }
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
    private string? Started(string? problem)
    {
        if (problem is not null)
        {
            UnfollowWatched();
            parts.Views.Run.SetRunning(false);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            parts.Say(problem);
        }
        else
        {
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        return problem;
    }

    /// <summary>Says why a launch did not happen, unless its folder has ended (the window says the next one's then), and gives
    /// it back.</summary>
    private string Said(string problem)
    {
        if (!session.IsEnded)
        {
            parts.Say(problem);
        }
        return problem;
    }

    public async Task<string?> RunAspireAsync(RunConfiguration chosen, string? arguments, RunStart mode, CancellationToken cancellation = default)
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
                string? problem = await session.Aspire.StartAsync(configuration, mode, (step, reading) => BuildForAsync(step, work, reading), work.Stopping, https);
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
                return Said(error.Message);
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

    public Task<string?> ProfileAspireAsync(RunConfiguration configuration, string? arguments = null, CancellationToken cancellation = default) =>
        RunAspireAsync(configuration, arguments, RunStart.Profile, cancellation);

    /// <summary>Rerun, which follows the configuration chosen: an AppHost's that runs, what changed in it restarted
    /// (<see cref="RerunAspireAsync"/>); else the container run of it under way in fast mode, the services whose build changed
    /// restarted (<see cref="RerunContainersAsync"/>); else what the Run console (or a terminal's tab) has stopped, its end
    /// waited for, and the chosen run, as Run runs it. What else runs (another configuration's AppHost, a debugged program,
    /// tests, a build) runs on. Null once done, else why not, said.</summary>
    public async Task<string?> RerunAsync()
    {
        UiThread.Check();
        if (session.Configurations.Chosen is not { } chosen)
        {
            return Said(session.NothingTo("run"));
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
            return await RunAsync();
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

    private async Task<string?> RerunAspireAsync(RunConfiguration chosen, AspireAppRun main)
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
                return Said(error is OperationCanceledException ? StoppedRestarting(chosen.Name) : error.Message);
            }
        }
        // An AppHost's own code changed: it and its services stop, then it is built and run again as it was run.
        RunConfiguration again = main.Configuration;
        RunStart mode = main.Mode;
        parts.Say($"{chosen.Name}'s AppHost changed: stopping it to build and run it again…");
        await session.Aspire.StopGroupAsync(main.Group);
        return session.IsEnded ? Refusals.SolutionClosed : await RunAspireAsync(again, null, mode);
    }

    /// <summary>Resolves and builds the chosen configuration, then launches its plan under the debugger, with these arguments
    /// in place of its own if given; the Debug window opens on its Console until the program stops somewhere. Null once its
    /// session is open, or what stopped it (a failed build, no configuration, the adapter's refusal).</summary>
    public Task<string?> DebugAsync(string? arguments = null, CancellationToken cancellation = default) =>
        session.Configurations.Chosen is { } chosen ? DebugAsync(chosen, arguments, cancellation) : Task.FromResult<string?>(session.NothingTo("debug"));

    /// <summary>Each configuration Debug launched, by its name, and the arguments it was given, which a restart for Hot
    /// Reload launches again.</summary>
    private readonly Dictionary<string, (RunConfiguration Configuration, string? Arguments)> debugged = [];

    /// <summary>Hot Reload's restart for an edit it cannot apply: the program Debug launched for a configuration (the
    /// debugger's chosen session's) is stopped (on Windows its files are locked while it runs), built again and launched with
    /// the same configuration and arguments, the breakpoints the store's. Null once the new session is open, else why it
    /// could not.</summary>
    public async Task<string?> RestartDebugAsync(string name)
    {
        if (!debugged.TryGetValue(name, out (RunConfiguration Configuration, string? Arguments) last))
        {
            return "Nothing was launched under the debugger to restart";
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
    private async Task<string?> RestartDebuggedCopyAsync(RunConfiguration configuration, string? arguments, ToolWindows.DebugTab tab, int copy)
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
                (LaunchPlan? plan, string? problem, _) = await PrepareAsync(configuration, arguments, work, reading: () =>
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

    private async Task<string?> DebugAsync(RunConfiguration chosen, string? arguments, CancellationToken cancellation = default)
    {
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
            return await DebugAsync(chosen, arguments, work);
        }
    }

    private async Task<string?> DebugAsync(RunConfiguration chosen, string? arguments, Working work)
    {
        if (chosen.IsContainer)
        {
            // A session per service, each attached in its container as Compose brings the run up.
            return await RunContainersAsync(chosen, work, RunStart.Debug);
        }
        debugged[chosen.Name] = (chosen, arguments);
        using IDisposable held = BackgroundWork.Scope();
        if (await EndOwnRunAsync(chosen, work, "debug") is { } left)
        {
            return left;
        }
        // The edits made since its build saved the files, followed for its session (Hot Reload's, its built lines).
        BuiltEdits? edits = null;
        RunDashboard? dashboard = null;
        try
        {
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, arguments, work, reading: () =>
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
            // An earlier launch's copies past this one's count end with it, as the rest end in their tabs' turn.
            await Task.WhenAll([.. session.Debug.CopiesOf(chosen.Name).Where(c => c.Copy > count).Select(c => session.Debug.StopAsync(c.Tab))]);
            string? refused = count == 1 ? await LaunchDebuggedAsync(chosen, plan, work, edits) : await LaunchCopiesAsync(chosen, plan, Enumerable.Range(1, count), arguments, work, edits);
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
    private async Task<string?> LaunchCopiesAsync(RunConfiguration chosen, LaunchPlan plan, IEnumerable<int> copies, string? arguments, Working work, BuiltEdits? edits)
    {
        List<int> numbers = [.. copies];
        List<BuiltEdits?> each = [edits, .. numbers.Skip(1).Select(_ => edits is null ? null : session.Debug.Copied(edits))];
        try
        {
            for (int i = 0; i < numbers.Count; i++)
            {
                LaunchPlan copy = GameCopies.Plan(chosen, plan, numbers[i]);
                debugged[copy.Name] = (chosen, arguments);
                if (await LaunchDebuggedAsync(chosen, copy, work, each[i], copies: chosen.Name) is { } refused)
                {
                    return i == 0 ? refused : $"{copy.Name} did not start ({refused}); {Plural.Listed([.. numbers.Take(i).Select(n => GameCopies.Name(chosen.Name, n))])} run on";
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
    public async Task<string?> DebugAnotherCopyAsync(CancellationToken cancellation = default)
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
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, null, work, build: false);
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
    public string? WhyNotAnotherCopy(RunConfiguration chosen)
    {
        if (chosen.IsContainer || session.Aspire.IsAspire(chosen) || !chosen.RunsGodot(session.Projects) || chosen.GodotEditor)
        {
            return $"{chosen.Name} starts no Godot game, of which Debug Another Copy debugs one more beside those debugged";
        }
        IReadOnlyList<(ToolWindows.DebugTab Tab, int Copy)> running = session.Debug.CopiesOf(chosen.Name);
        return running.Count == 0 ? $"No copy of {chosen.Name} is debugged: Debug starts it, then Debug Another Copy one more beside it"
            : running.Count >= GameCopies.Most ? $"{chosen.Name} runs {GameCopies.Most} copies, the most one configuration runs"
            : null;
    }

    /// <summary>The next Debug's launch held by a test once its build has ended (<see cref="HoldNextLaunch"/>): what it waits
    /// for, and done once it waits.</summary>
    private (Task Until, TaskCompletionSource Holding)? launchHeld;

    /// <summary>Holds the next Debug's launch, once its build has ended, until <paramref name="until"/> ends, for a test of
    /// what goes on between them. Done once it is held.</summary>
    public Task HoldNextLaunch(Task until)
    {
        TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        launchHeld = (until, holding);
        return holding.Task;
    }

    /// <summary>Launches a built plan under the debugger, its session taking the <paramref name="edits"/> followed since its
    /// build; null once its session is open, else why not.</summary>
    private async Task<string?> LaunchDebuggedAsync(RunConfiguration chosen, LaunchPlan plan, Working work, BuiltEdits? edits, string? copies = null)
    {
        if (plan.WhyNot("debug") is { } why)
        {
            return why;
        }
        if (launchHeld is { } hold)
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
        string? browserSetting = parts.Settings().DebugBrowser;
        (Debugging.BrowserDebugging? inBrowser, string? notInBrowser) = await BackgroundWork.Run(() => Debugging.BrowserDebugging.For(listened, projects, browserSetting));
        listened = listened with { Browser = inBrowser, BrowserWhyNot = notInBrowser };
        if (inBrowser is not null && PathNames.Same(listened.InBrowser[0], listened.Project!))
        {
            // A standalone app's browser session runs its development server itself, no engine beside it.
            return await UntrustedPageAsync(inBrowser) is { } untrusted ? $"{listened.Name}'s components in the browser are not debugged: {untrusted}"
                : await debug.LaunchBrowserAsync(listened, inBrowser);
        }
        // An Avalonia or WPF app is launched with the Live Visual Tree's inspector, held by its tab, ending with this debugging.
        (LaunchPlan launched, LiveApp? inspected) = session.Inspected(listened);
        Task<string?> starting = debug.StartAsync(launched, app: inspected, edits: edits, copies: copies);
        if (game is not null && debug.Chosen?.Console is { } gameConsole)
        {
            gameConsole.Fold = new Godot.GodotConsoleFold();
            game.Console = gameConsole.Fold;
        }
        // Its tab, chosen as the start began, has the console its failed bindings are written to.
        WatchBindings(inspected, debug.Chosen?.Console);
        string? refused = await starting;
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

    /// <summary>A Blazor Web App's WebAssembly half debugged beside its server, in a tab of its own
    /// (<see cref="DebugController.DebugBrowserAsync"/>), ended as the server's session ends; one whose browser could not
    /// start says why, the server debugged on.</summary>
    private async Task DebugBrowserAsync(LaunchPlan plan, Debugging.BrowserDebugging browser, Task serverEnded)
    {
        DebugController debug = session.Debug;
        if ((await UntrustedPageAsync(browser) ?? await debug.DebugBrowserAsync(plan, browser)) is { } refused)
        {
            parts.Say($"{plan.Name}'s components in the browser are not debugged: {refused}");
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
    private async Task<string?> UntrustedPageAsync(Debugging.BrowserDebugging browser)
    {
        if (browser.Page.Scheme != Uri.UriSchemeHttps || session.Folder is not { } folder)
        {
            return null;
        }
        DotnetWords dotnet = parts.DevCertificates ?? DevelopmentCertificate.Machine;
        (DevelopmentCertificate.Standing standing, _, _) = await BackgroundWork.Run(() => DevelopmentCertificate.CheckAsync(dotnet, folder, CancellationToken.None));
        return standing switch
        {
            DevelopmentCertificate.Standing.NotTrusted => $"the development certificate its page {browser.Page.Authority} is served with is not trusted on this machine, "
                + "so the browser refuses the page: run dotnet dev-certs https --trust in a terminal, then debug again",
            DevelopmentCertificate.Standing.Missing => $"its page {browser.Page.Authority} is served over HTTPS, and this machine has no development certificate: "
                + "run dotnet dev-certs https --trust in a terminal, then debug again",
            _ => null,
        };
    }

    /// <summary>Debug of a Godot game of GDScript alone: its GDScript session launches it, no C# engine beside it, Hot Reload
    /// of its saved scripts as Settings' Hot Reload while debugging says. Null once its session is open, else why not.</summary>
    private async Task<string?> LaunchScriptsAsync(LaunchPlan plan, Godot.GodotGame game, string? copies)
    {
        DebugController debug = session.Debug;
        string? refused = await debug.LaunchScriptsAsync(plan, game, copies);
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
            if (Passenger.Workspace.GodotScenes.ResPathOf(game.Folder, path) is not { } resource)
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
    /// answer finds it free) and a container run's containers are taken down (not left running as its log's end would
    /// leave them).</summary>
    public Task StopAllAsync()
    {
        parts.Work.Stop();
        Task debugged = session.Debug.StopAllAsync();
        return Task.WhenAll(debugged, StopRest());
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
        session.Profiled?.Run.Kill();
        session.Builder.Stop();
        Task played = parts.StopInGodotEditor?.Invoke() ?? Task.CompletedTask;
        return Task.WhenAll(containers, aspire, ran, copies, played);
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
    }

    /// <summary>Whether a run of a configuration is up: Run's program (in the Run window or the terminal) or container run,
    /// while the Run console (or the terminal's tab) has it, a session Debug launched for it, or the program Profile started.
    /// An AppHost's run is said by its own (<see cref="MainWindow.Shown"/>).</summary>
    public bool IsRunningConfiguration(string name) =>
        RunsInConsole && (Containers?.Plan.Name ?? ran) == name
        || copyRuns.Any(c => c.Configuration == name && c.Run.IsRunning)
        || session.Debug.Tabs.Any(t => t.IsLive && GameCopies.Of(t.Name).Configuration == name && debugged.ContainsKey(t.Name))
        || session.Profiled is { Run.HasExited: false } profiled && profiled.Name == name;

    /// <summary>Why Stop cannot run now: nothing it stops is running.</summary>
    public string? WhyNotStop() => session.Debug.IsDebugging || IsRunning || session.Profiled is not null || session.Builder.IsRunning || parts.Work.CanStop
        || parts.PlaysInGodotEditor?.Invoke() == true || session.Dashboards.Any(d => d.Kept) ? null : "Nothing is running";
}

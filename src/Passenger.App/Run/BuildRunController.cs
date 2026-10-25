using System.Diagnostics;
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

    /// <summary>The dotnet <c>dev-certs</c> runs through for a run that serves HTTPS (containers' or Aspire's): the machine's,
    /// or a test's stand-in.</summary>
    public DotnetWords? DevCertificates { get; init; }

    /// <summary>Asks the person whether to make and trust the development certificate (true) or run over HTTP (false), given
    /// the configuration, the services that serve HTTPS and whether there is none at all; the question closed unanswered
    /// (false) once the start is stopped.</summary>
    public Func<string, string, bool, CancellationToken, Task<bool>>? AskCertificate { get; init; }

    /// <summary>Opens a folder in the file manager: where a publish put the app, from the status bar's word that it did.</summary>
    public Action<string>? ShowFolder { get; init; }

    /// <summary>Asks the person whether a container run may replace containers of its Compose project Passenger did not make
    /// (true), given the configuration, the project and those containers' names; closed unanswered (false) once the start is
    /// stopped.</summary>
    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<bool>>? AskReplace { get; init; }

    /// <summary>Whether nobody is at the window to ask (an agent's own IDE, <c>--mcp</c>): what would ask the person takes the
    /// way that asks nothing, saying so.</summary>
    public bool AsksNoOne { get; init; }

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
        session.Builder.Line += (_, line) =>
        {
            if (reading is not null && BuildOutput.Continuing(reading, line) is { } longer)
            {
                reading = longer;
            }
            else
            {
                ReadProblem();
                reading = BuildOutput.Parse(line);
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

    /// <summary>How a run that ended is named, in the status bar and to an agent.</summary>
    public static string RunEnded(string name, int code) => $"{name} exited with code {code}";

    /// <summary>Whether a build, rebuild or clean is under way, from its start until its callers have heard how it ended.</summary>
    public bool IsBuilding => building is not null;

    /// <summary>Whether a program Run started runs, in the Run window or the terminal, or an AppHost it started.</summary>
    public bool IsRunning => session.Aspire.IsRunning || RunsInConsole;

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
    /// (a single-file AppHost's).</summary>
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
            BuildOutcome refused = new(BuildStatus.Refused, session.Folder is null || session.IsEnded ? "Nothing to build: no folder is open" : "Another build is running");
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
            work?.Dispose();
            parts.Say(nothing.Message);
            then?.Invoke(nothing);
            return;
        }
        afterBuild = then;
        parts.Editors.SaveAll();
        session.Problems.Clear();
        reading = null;
        builtTexts = null;
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
        BuildNext(first: true);
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
    /// (<see cref="HoldDesignTimeBuildsAsync"/>).</summary>
    private void BuildNext(bool first = false)
    {
        string target = buildQueue.Dequeue();
        string verb = buildKind switch { BuildKind.Rebuild => "Rebuilding", BuildKind.Clean => "Cleaning", BuildKind.Publish => "Publishing", _ => "Building" };
        string what = publishing is { } shown ? shown.Shown : together?.Shown ?? Path.GetFileName(target);
        parts.Say($"{verb} {what}…");
        buildWork?.Say($"{verb} {what}…");
        bool windows = WindowsTargeting(together?.Projects ?? [target]);
        IReadOnlyList<string> arguments = publishing is { } publish ? publish.Arguments(windows) : BuildOutput.Arguments(target, buildKind, buildFramework, windows, buildOutput);
        if (session.Builder.StartDotnet(arguments, session.Folder!, after: first ? HoldDesignTimeBuildsAsync : null) is { } problem)
        {
            FinishBuild(new BuildOutcome(BuildStatus.NotStarted, problem));
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
        if (reading is { } read && Placed(read) is var problem && !session.Problems.Contains(problem))
        {
            session.Problems.Add(problem);
        }
        reading = null;
    }

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
        FinishBuild(session.Builder.WasStopped ? new BuildOutcome(BuildStatus.Stopped, $"{noun} stopped")
            : code == 0 && publishing is { } publish ? Published(publish, warned)
            : code == 0 ? new BuildOutcome(BuildStatus.Succeeded, $"{noun} succeeded{warned}")
            : new BuildOutcome(BuildStatus.Failed, $"{noun} failed{FailedIn(problems)}: {Plural.Of(errors, "error")}, {Plural.Of(warnings, "warning")}; "
                + (errors > 0 ? "Problems lists them" : $"dotnet exited with code {code}, the Build window says why")));
    }

    /// <summary>Where a build of projects side by side failed: " in" and the projects its errors name, so each service whose
    /// build failed is said, though their output came together; nothing for any other build.</summary>
    private string FailedIn(IEnumerable<Problem> problems) =>
        together is not null && problems.Where(p => p.Severity == ProblemSeverity.Error && p.Project is not null).Select(p => p.Project!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } named
            ? $" in {Listed(named)}"
            : string.Empty;

    /// <summary>Names as a sentence lists them: "Web", "Api and Web", "Api, Web and Worker".</summary>
    private static string Listed(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>Whether a build of these projects needs EnableWindowsTargeting, one of them or a project it references needing it.</summary>
    private bool WindowsTargeting(IReadOnlyList<string> projects) => projects.Any(p => BuildOutput.WindowsTargeting(p, session.Projects));

    /// <summary>The one way a build that began ends: its window, Problems and the status bar say how, its caller hears it
    /// with what it found, and the background work it held is let go.</summary>
    private void FinishBuild(BuildOutcome ended)
    {
        List<Problem> problems = session.Problems;
        BuildOutcome outcome = ended with { Problems = [.. problems], Output = [.. builtLines] };
        buildQueue.Clear();
        Action<BuildOutcome>? then = afterBuild;
        afterBuild = null;
        TaskCompletionSource? held = building;
        building = null;
        publishing = null;
        together = null;
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
    private StatusPlace? Place(BuildOutcome ended) =>
        ended is { Succeeded: true, Published: { } folder } && parts.ShowFolder is { } show
            ? new StatusPlace($"open {Path.GetFileName(folder)} in {FileManager.Name}", () => show(folder))
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
            return (await LaunchPlan.ResolveAsync(arguments is null ? chosen : chosen with { Arguments = arguments }, session.Projects, session.Workspace, parts.StateFolder(), stop), null);
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
    /// runs could not be worked out), which a start shows where it shows its own state.</summary>
    public async Task<(LaunchPlan? Plan, string? Problem, bool Unresolved)> PrepareAsync(RunConfiguration chosen, string? arguments, Working work, bool build = true)
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
        if (build && plan.Build is { } step && await BuildForAsync(step, work.Stopping) is { Succeeded: false } unbuilt)
        {
            return (null, Checkpoint(work, chosen.Name) ?? unbuilt.Message, false);
        }
        return Checkpoint(work, chosen.Name) is { } left ? (null, left, false) : (plan, null, false);
    }

    /// <summary>Why a start goes no further now, or null to go on: its folder has ended, or Stop has ended the start.</summary>
    private string? Checkpoint(Working work, string name) => session.IsEnded ? Refusals.SolutionClosed : work.IsStopped ? StoppedStarting(name) : null;

    /// <summary>What a start ended by a cancel says: its folder's end, else Stop's.</summary>
    private string Canceled(Working work, string name) => session.IsEnded ? Refusals.SolutionClosed : StoppedStarting(name);

    /// <summary>A start's build of what it starts, in the Build window, which comes forward only as Settings says for a build
    /// Run, Debug or Profile begins: stopped as the start is (<paramref name="stopping"/>), as Stop stops a build. What is
    /// open is saved first; a project whose last build here stands (<see cref="UpToDate"/>) is not built again, the Build
    /// window and the status bar saying so.</summary>
    public Task<BuildOutcome> BuildForAsync(BuildStep step, CancellationToken stopping) => BuildForAsync([step.Project], stopping, step.Framework, step.Output);

    /// <summary>What a start says of a project it did not build: nothing its build reads changed since its last.</summary>
    public static string UpToDateSaid(string project) => $"{project} is up to date: nothing its build reads has changed since it was built, so it was not built again";

    /// <summary>What a start says of projects it did not build, one or several.</summary>
    public static string UpToDateSaid(IReadOnlyList<string> projects) => projects.Count == 1 ? UpToDateSaid(projects[0])
        : $"{Listed(projects)} are up to date: nothing their builds read has changed since they were built, so they were not built again";

    /// <summary>A start's build of its projects (<see cref="BuildForAsync(BuildStep, CancellationToken)"/>), several side by
    /// side in one dotnet build (<see cref="TogetherBuild"/>): none is built when the last build here of each stands, and all
    /// are when one does not, what each build left then kept for its next start's check.</summary>
    private async Task<BuildOutcome> BuildForAsync(IReadOnlyList<string> projects, CancellationToken stopping, string? framework = null, string? output = null)
    {
        UiThread.Check();
        List<UpToDate.Check> checks = [];
        TogetherBuild? side = null;
        if (session.Folder is { } folder && !IsBuilding && !session.IsEnded && projects.Count > 0)
        {
            parts.Editors.SaveAll();
            bool windows = WindowsTargeting(projects);
            string state = parts.StateFolder();
            (UpToDate.Check?[] found, side) = await BackgroundWork.Run(async () => (
                await Task.WhenAll(projects.Select(project => UpToDate.CheckAsync(new BuildStep(project, framework, output),
                    BuildOutput.Arguments(project, BuildKind.Build, framework, windows, output), windows, folder, stopping))),
                projects.Count > 1 ? TogetherBuild.Write(state, folder, projects) : null));
            if (stopping.IsCancellationRequested)
            {
                return new BuildOutcome(BuildStatus.Stopped, "Build stopped");
            }
            if (found.All(check => check is { Holds: true }) && !IsBuilding && !session.IsEnded)
            {
                string said = UpToDateSaid([.. projects.Select(project => Path.GetFileNameWithoutExtension(project))]);
                parts.Views.Build.Clear();
                parts.Views.Build.Append($"{said}\n");
                parts.Say(said);
                return new BuildOutcome(BuildStatus.Succeeded, said);
            }
            checks = [.. found.OfType<UpToDate.Check>()];
        }
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
    public async Task<string?> RunAsync(string? arguments = null, bool? hotReload = null, CancellationToken cancellation = default)
    {
        if (session.Configurations.Chosen is not { } chosen)
        {
            return Said("Nothing to run: no executable project");
        }
        if (session.Aspire.IsAspire(chosen))
        {
            RunStart mode = (hotReload ?? parts.Settings().HotReload) ? RunStart.HotReload : RunStart.Run;
            return await RunAspireAsync(chosen, arguments, mode, cancellation);
        }
        // The Run console's program is one at a time; an AppHost of another configuration runs beside it.
        if (await TakeConsoleAsync(chosen) is { } taken)
        {
            return Said(taken);
        }
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
        try
        {
            parts.Editors.SaveAll();
            parts.Views.Run.Clear();
            // dotnet watch builds the project itself, and runs it again after an edit it cannot apply.
            bool watched = (hotReload ?? parts.Settings().HotReload) && chosen.Program.Length == 0;
            watching = watched;
            (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, arguments, work, build: !watched);
            if (plan is null)
            {
                return Said(problem!);
            }
            if (plan.WhyNot("run", built: !watched) is { } why)
            {
                return Said(why);
            }
            if (watched && MakeWorkingFolder(plan) is { } unmade)
            {
                return Said(unmade);
            }
            work.Say($"Starting {chosen.Name}…");
            ran = chosen.Name;
            RunProject = Path.GetDirectoryName(plan.Project);
            session.RunPlan = plan;
            string running = watched ? $"Running {chosen.Name} with Hot Reload: a save applies its changes" : $"Running {chosen.Name}";
            // An Avalonia or WPF app is launched with the inspector in it, which tells of its failed bindings while it runs.
            LaunchPlan launched = session.InspectedRun(plan, watched);
            LiveApp? inspected = session.RunApp;
            ShowBindings();
            Dictionary<string, string> variables = watched
                ? new Dictionary<string, string>(launched.Environment) { ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1", ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true" }
                : new Dictionary<string, string>(launched.Environment);
            if (plan.Console == ConsoleKind.Terminal)
            {
                // The terminal's text is the program's own: its failed bindings are in Problems alone.
                WatchBindings(inspected, null);
                string? refused = await RunInTerminalAsync(chosen.Name, watched ? DotnetStart(Watch(plan), plan.WorkingDirectory) : ChildProcesses.Command(plan.Program, plan.Arguments, plan.WorkingDirectory), variables, running);
                if (refused is null && session.InTerminal is { } inTerminal)
                {
                    parts.OpenBrowser(plan, inTerminal.Ended);
                }
                EndWith(inspected, refused is null && session.InTerminal is { } run ? run.Ended : Task.CompletedTask);
                return refused;
            }
            session.InTerminal = null;
            parts.Views.Run.SetRunning(true);
            parts.Dock.Show(ToolWindowIds.Run);
            parts.Say(running);
            WatchBindings(inspected, parts.Views.Run);
            // Its fork off the window's thread (#441): the program runs from now as Stop and IsRunning see it, its id known once started.
            string? notStarted = Started(watched
                ? await session.Runner.StartDotnetAsync(Watch(plan), plan.WorkingDirectory, variables)
                : await session.Runner.StartAsync(plan.Program, plan.Arguments, plan.WorkingDirectory, variables));
            if (notStarted is null)
            {
                parts.OpenBrowser(plan, session.Runner.Ended);
            }
            EndWith(inspected, notStarted is null ? session.Runner.Ended : Task.CompletedTask);
            return notStarted;
        }
        finally
        {
            work.Dispose();
        }
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
    /// in its place: done once its end has landed. Null to go on, else why not (the folder left meanwhile).</summary>
    public async Task<string?> EndOwnRunAsync(RunConfiguration chosen, Working work, string doing)
    {
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

    /// <summary>dotnet watch's command line for a plan: the project for its framework, no launch profile (the plan has its
    /// values already), the program's own arguments after <c>--</c>. It runs the program in its own working folder, which is
    /// the plan's.</summary>
    private static List<string> Watch(LaunchPlan plan)
    {
        List<string> watch = ["watch", "--non-interactive", "run", "--project", plan.Project!, "--no-launch-profile"];
        if (plan.Framework is { } framework)
        {
            watch.AddRange(["--framework", framework]);
        }
        watch.Add("--");
        watch.AddRange(plan.Built is { } assembly && plan.Arguments.Count > 0 && plan.Arguments[0] == assembly ? plan.Arguments.Skip(1) : plan.Arguments);
        return watch;
    }

    /// <summary>A run that could not start leaves the Run window idle, Stop as the debugger has it, and says why.</summary>
    private string? Started(string? problem)
    {
        if (problem is not null)
        {
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
                parts.Editors.SaveAll();
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
                string? problem = await session.Aspire.StartAsync(configuration, mode, step => BuildForAsync(step, work.Stopping), work.Stopping, https);
                if (problem is not null)
                {
                    return Said(problem);
                }
                AspireAppRun[] started = session.Aspire.Apps.Where(app => !app.IsTest).ToArray();
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
            RunStart.Debug => marked.Length > 0 ? string.Join(" and ", marked) : "each .NET service it starts",
            RunStart.Profile => started.Select(app => app.Profiled).OfType<string>().FirstOrDefault() ?? "its service",
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
            return Said("Nothing to run: no executable project");
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
                parts.Editors.SaveAll();
                AspireAppRun[] apps = session.Aspire.Apps.Where(app => app.Group == main.Group && !app.Stopping).ToArray();
                // Each service with when the build it runs began: its own restart's, else its AppHost's.
                (AspireAppRun App, (AspireResourceRun Service, DateTime Since)[] Running)[] runs = [.. apps.Select(app => (app,
                    app.Services.Where(s => !s.Stopping.IsCancellationRequested).Select(s => (s, app.Rebuilt.GetValueOrDefault(s.Project, app.BuildBegan))).ToArray()))];
                (string Project, DateTime Since)[] hosts = [.. apps.Select(app => (app.Configuration.Project, app.BuildBegan))];
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
                        restarted.AddRange(changes.Select(s => ((IRunService)s).Name));
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
        session.Configurations.Chosen is { } chosen ? DebugAsync(chosen, arguments, cancellation) : Task.FromResult<string?>("Nothing to debug: no executable project");

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
        await session.Debug.StopAsync(configuration.Name);
        return await DebugAsync(configuration, arguments);
    }

    private async Task<string?> DebugAsync(RunConfiguration chosen, string? arguments, CancellationToken cancellation = default)
    {
        if (session.Aspire.IsAspire(chosen))
        {
            return await RunAspireAsync(chosen, arguments, RunStart.Debug, cancellation);
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
        (LaunchPlan? plan, string? problem, _) = await PrepareAsync(chosen, arguments, work);
        if (plan is null)
        {
            return problem;
        }
        if (plan.WhyNot("debug") is { } why)
        {
            return why;
        }
        work.Say($"Starting {chosen.Name} under the debugger…");
        parts.Dock.Show(ToolWindowIds.Debug);
        parts.Views.Debug.ShowTab(console: true);
        // Before the session clears the Debug window's console, which forgets the links the last one's output had.
        DebugProject = Path.GetDirectoryName(plan.Project);
        DebugController debug = session.Debug;
        // An Avalonia or WPF app is launched with the Live Visual Tree's inspector, held by its tab, ending with this debugging.
        (LaunchPlan launched, LiveApp? inspected) = session.Inspected(plan);
        Task<string?> starting = debug.StartAsync(launched, app: inspected);
        // Its tab, chosen as the start began, has the console its failed bindings are written to.
        WatchBindings(inspected, debug.Chosen?.Console);
        string? refused = await starting;
        Task ended = refused is null ? DebugEnded(debug.SessionNamed(launched.Name)) : Task.CompletedTask;
        EndWith(inspected, ended);
        if (refused is null)
        {
            parts.OpenBrowser(plan, ended);
        }
        return refused;
    }

    /// <summary>An app's inspector let go once its run or debugging ends: one that never connected (startup hooks turned off
    /// in the app) waits no longer. Its failed bindings stay in Problems until the next launch of its kind.</summary>
    private static void EndWith(LiveApp? app, Task ended)
    {
        if (app is not null)
        {
            IdeErrors.Observe(EndWithAsync(app, ended));
        }

        static async Task EndWithAsync(LiveApp app, Task ended)
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
        Task containers = session.StopContainersAsync();
        Task aspire = session.Aspire.StopAllAsync();
        Task ran = session.RunProgram.IsRunning ? session.RunProgram.Ended : Task.CompletedTask;
        session.RunProgram.Stop();
        session.Profiled?.Run.Kill();
        session.Builder.Stop();
        return Task.WhenAll(containers, aspire, ran);
    }

    /// <summary>Rerun's stop: Run's own start under way and the program it started (in the Run window or the terminal, or its
    /// container run), never another configuration's AppHost, the debugger's session, a test run or a build, which Rerun
    /// does not run again.</summary>
    public void StopRun()
    {
        parts.Work.Of(AppActions.RunProgram)?.Cancel();
        IdeErrors.Observe(session.StopContainersAsync());
        session.RunProgram.Stop();
    }

    /// <summary>Whether a run of a configuration is up: Run's program (in the Run window or the terminal) or container run,
    /// while the Run console (or the terminal's tab) has it, a session Debug launched for it, or the program Profile started.
    /// An AppHost's run is said by its own (<see cref="MainWindow.Shown"/>).</summary>
    public bool IsRunningConfiguration(string name) =>
        RunsInConsole && (Containers?.Plan.Name ?? ran) == name
        || session.Debug.Tabs.Any(t => t.IsLive && t.Name == name && debugged.ContainsKey(name))
        || session.Profiled is { Run.HasExited: false } profiled && profiled.Name == name;

    /// <summary>Why Stop cannot run now: nothing it stops is running.</summary>
    public string? WhyNotStop() => session.Debug.IsDebugging || IsRunning || session.Profiled is not null || session.Builder.IsRunning || parts.Work.CanStop ? null : "Nothing is running";
}

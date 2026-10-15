using System.Diagnostics;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.LiveTree;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.Run;

/// <summary>What a window lends the builds and runs of its sessions: the Build, Run, Problems and Debug windows, its
/// editors (saved before a build), its tool windows' frame, its settings, the status bar, a tab of the
/// Terminal window for a program that uses one, Rider's Open browser after launch, and its state folder, under which a
/// Passenger it launches keeps its own (<see cref="LaunchPlan.SandboxedState"/>).</summary>
public sealed record BuildRunParts(
    ToolViews Views,
    EditorArea Editors,
    DockHost Dock,
    Func<AppSettings> Settings,
    Action<string> Say,
    Func<string, ProcessStartInfo, Task<TerminalRun>> InTerminal,
    Action<LaunchPlan, Task> OpenBrowser,
    Func<string> StateFolder);

/// <summary>A session's Build, Run and Debug as the window shows them: builds, rebuilds and cleans of the solution or of
/// projects one after another (the output in the Build window, what they find in Problems, the window brought forward as
/// Settings says), and the chosen run configuration resolved into a <see cref="LaunchPlan"/>, built as it says and started
/// in the Run window, a tab of the Terminal window or under the debugger. The window makes one for each session it binds
/// (<see cref="ProjectSession"/> owns the runners it drives), so a build or run never outlives its folder. A UI-thread
/// object.</summary>
public sealed class BuildRunController
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

    private BuildKind buildKind;
    private IReadOnlyList<string>? lastProjects;
    private string? buildFramework;
    private Action<BuildOutcome>? afterBuild;
    private TaskCompletionSource? building;

    /// <summary>A Run between its press and its program's start (the plan resolved, the project built), which a second
    /// press may not start again.</summary>
    private bool launching;

    /// <summary>The run configuration the Run window last ran.</summary>
    private string ran = string.Empty;

    /// <summary>Where each failed binding of an app goes in Problems, found once, when it first fails.</summary>
    private readonly Dictionary<(LiveApp App, int Id), Problem> placed = [];

    public BuildRunController(ProjectSession session, BuildRunParts parts)
    {
        this.session = session;
        this.parts = parts;
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
        };
        session.Builder.Exited += (_, code) => OnBuilt(code);
        session.Runner.Exited += (_, code) =>
        {
            parts.Views.Run.SetRunning(false);
            RunningChanged?.Invoke(this, EventArgs.Empty);
            parts.Say(RunEnded(ran, code));
        };
    }

    /// <summary>A program started or ended, or a launch was refused: the toolbar's Stop follows.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>A build ended, how it did said; after its caller has heard.</summary>
    public event EventHandler<BuildOutcome>? BuildFinished;

    /// <summary>How a run that ended is named, in the status bar and to an agent.</summary>
    public static string RunEnded(string name, int code) => $"{name} exited with code {code}";

    /// <summary>Whether a build, rebuild or clean is under way, from its start until its callers have heard how it ended.</summary>
    public bool IsBuilding => building is not null;

    /// <summary>Whether a program Run started runs, in the Run window or the terminal.</summary>
    public bool IsRunning => session.Runner.IsRunning || session.InTerminal is { IsRunning: true };

    /// <summary>Whether the program Run started runs under dotnet watch (Settings' Hot Reload when running), which applies
    /// the edits of each file saved.</summary>
    public bool WatchesHotReload => watching && IsRunning;

    private bool watching;

    /// <summary>The end of the program Run last started, in the Run window or the terminal, with its exit code.</summary>
    public Task<int> RunEnd => session.InTerminal?.Ended ?? session.Runner.Ended;

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

    /// <summary>The Build window's Rerun: the last build, rebuild or clean again, of what it built.</summary>
    public void Rebuild() => Build(buildKind, lastProjects, framework: buildFramework);

    /// <summary>Builds, rebuilds or cleans the solution, or the projects given one after another (stopping at the first
    /// that fails), as Rider's Build menu and the Solution view's do: the output in the Build window, which opens as it
    /// starts as Settings says for its <paramref name="cause"/>, the problems of all of them in Problems.
    /// <paramref name="then"/> always hears how it ended, a build refused or never started included. A framework given
    /// builds the projects for it alone, as a run configuration's build before launch does.</summary>
    public void Build(BuildKind kind, IReadOnlyList<string>? projects = null, Action<BuildOutcome>? then = null, string? framework = null,
        BuildCause cause = BuildCause.Explicit)
    {
        UiThread.Check();
        if (session.Folder is null || IsBuilding || session.IsEnded)
        {
            BuildOutcome refused = new(BuildStatus.Refused, session.Folder is null || session.IsEnded ? "Nothing to build: no folder is open" : "Another build is running");
            parts.Say(refused.Message);
            then?.Invoke(refused);
            return;
        }
        buildKind = kind;
        lastProjects = projects;
        buildFramework = projects is { Count: > 0 } ? framework : null;
        IReadOnlyList<string> whole = session.Workspace!.BuildTargets(session.Projects);
        buildQueue.Clear();
        foreach (string target in projects is { Count: > 0 } ? projects : whole)
        {
            buildQueue.Enqueue(target);
        }
        if (buildQueue.Count == 0)
        {
            BuildOutcome nothing = new(BuildStatus.Refused, "Nothing to build: every project is unloaded");
            parts.Say(nothing.Message);
            then?.Invoke(nothing);
            return;
        }
        afterBuild = then;
        parts.Editors.SaveAll();
        session.Problems.Clear();
        reading = null;
        builtLines.Clear();
        parts.Views.Build.Clear();
        parts.Views.Build.SetRunning(true);
        if (OpensBuildWindow(cause))
        {
            parts.Dock.Show("build");
        }
        building = new TaskCompletionSource();
        BackgroundWork.Track(building.Task);
        BuildNext();
    }

    /// <summary>Whether a build opens the Build window as it starts, as Rider's Presentation settings have it: a test run's
    /// never, its unit testing asking for no window; the status bar says what builds either way.</summary>
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

    /// <summary>Whether a build's end brings the Build window forward, as Rider's "Showing build results after build
    /// completed" says: one that could not start counts as failed, and one stopped as neither failed nor warned.</summary>
    private bool ShowsBuildResults(BuildOutcome ended) => parts.Settings().ShowBuildResults switch
    {
        BuildResultsMode.Always => true,
        BuildResultsMode.ForWarningsOrErrors => ended.Status is BuildStatus.Failed or BuildStatus.NotStarted
            || ended.Problems.Any(p => p.Severity == ProblemSeverity.Warning),
        BuildResultsMode.ForErrors => ended.Status is BuildStatus.Failed or BuildStatus.NotStarted,
        _ => false,
    };

    private void BuildNext()
    {
        string target = buildQueue.Dequeue();
        string verb = buildKind switch { BuildKind.Rebuild => "Rebuilding", BuildKind.Clean => "Cleaning", _ => "Building" };
        parts.Say($"{verb} {Path.GetFileName(target)}…");
        if (session.Builder.StartDotnet(BuildOutput.Arguments(target, buildKind, buildFramework, BuildOutput.WindowsTargeting(target, session.Projects)), session.Folder!) is { } problem)
        {
            FinishBuild(new BuildOutcome(BuildStatus.NotStarted, problem));
        }
    }

    /// <summary>Puts the problem being read in Problems, unless it is there already.</summary>
    private void ReadProblem()
    {
        if (reading is { } problem && !session.Problems.Contains(problem))
        {
            session.Problems.Add(problem);
        }
        reading = null;
    }

    private void OnBuilt(int code)
    {
        ReadProblem();
        if (code == 0 && buildQueue.Count > 0 && !session.Builder.WasStopped)
        {
            parts.Views.Build.Append("\n");
            BuildNext();
            return;
        }
        List<Problem> problems = session.Problems;
        int errors = problems.Count(p => p.Severity == ProblemSeverity.Error);
        int warnings = problems.Count - errors;
        string noun = buildKind switch { BuildKind.Rebuild => "Rebuild", BuildKind.Clean => "Clean", _ => "Build" };
        FinishBuild(session.Builder.WasStopped ? new BuildOutcome(BuildStatus.Stopped, $"{noun} stopped")
            : code == 0 ? new BuildOutcome(BuildStatus.Succeeded, $"{noun} succeeded{(warnings > 0 ? $" with {warnings} warning{(warnings == 1 ? "" : "s")}" : "")}")
            : new BuildOutcome(BuildStatus.Failed, $"{noun} failed: {errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}; Problems lists them"));
    }

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
        parts.Views.Build.SetRunning(false);
        ShowProblems();
        session.Xaml.Built(session.Problems);
        parts.Say(outcome.Message);
        if (ShowsBuildResults(outcome))
        {
            parts.Dock.Show("build");
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

    /// <summary>The last build's problems in the Problems window, its title counting them.</summary>
    public void ShowProblems()
    {
        List<Problem> problems = session.Problems;
        parts.Views.Problems.Show(problems);
        if (parts.Dock["problems"] is { } problemsWindow)
        {
            problemsWindow.Title = problems.Count == 0 ? "Problems" : $"Problems {problems.Count}";
        }
    }

    /// <summary>A configuration's plan, with these arguments in place of its own if given, or why it has none.</summary>
    public async Task<(LaunchPlan? Plan, string? Problem)> ResolvePlanAsync(RunConfiguration chosen, string? arguments)
    {
        try
        {
            return (await LaunchPlan.ResolveAsync(arguments is null ? chosen : chosen with { Arguments = arguments }, session.Projects, parts.StateFolder()), null);
        }
        catch (Exception error) when (error is LaunchException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, error.Message);
        }
    }

    /// <summary>A configuration's plan with its build before launch done: the plan to start, or why there is none (the
    /// folder left meanwhile among the reasons).</summary>
    private async Task<(LaunchPlan? Plan, string? Problem)> PrepareAsync(RunConfiguration chosen, string? arguments, bool build = true)
    {
        (LaunchPlan? plan, string? problem) = await ResolvePlanAsync(chosen, arguments);
        if (build && plan?.Build is { } step && await BuildBeforeLaunchAsync(step) is { Succeeded: false } unbuilt)
        {
            return (null, unbuilt.Message);
        }
        return session.IsEnded ? (null, Left) : (plan, problem);
    }

    private const string Left = "The solution was closed";

    /// <summary>A plan's build before launch, in the Build window as Build Selected Projects writes it, which comes forward
    /// only as Settings says for a build Run, Debug or Profile starts.</summary>
    public Task<BuildOutcome> BuildBeforeLaunchAsync(BuildStep step)
    {
        TaskCompletionSource<BuildOutcome> built = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Build(BuildKind.Build, [step.Project], outcome => built.TrySetResult(outcome), step.Framework, BuildCause.BeforeLaunch);
        return built.Task;
    }

    /// <summary>Runs the chosen configuration, with these arguments in place of its own if given: its plan's program started
    /// after its build, its output and input in the Run window, or in a tab of the Terminal window when the configuration
    /// uses the terminal. Null once it runs (<see cref="RunEnd"/> is its end), else why it could not, which the status bar
    /// says too.</summary>
    public Task<string?> RunAsync(string? arguments = null) => session.Configurations.Chosen is { } chosen
        ? RunAsync(chosen, arguments) : Task.FromResult<string?>(Said("Nothing to run: no executable project"));

    private (RunConfiguration Configuration, string? Arguments)? watched;

    /// <summary>Restarts the watched configuration, preserving its arguments, so XAML edits requiring a build take effect.</summary>
    public async Task<string?> RestartRunAsync()
    {
        if (!WatchesHotReload || watched is not var (configuration, arguments))
        {
            return "No watched program is running to restart";
        }
        Task<int> ended = RunEnd;
        session.Runner.Stop();
        session.InTerminal?.Stop();
        try
        {
            await ended.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            return "The watched program did not finish stopping; retry once it has ended";
        }
        return session.IsEnded ? Left : await RunAsync(configuration, arguments);
    }

    private async Task<string?> RunAsync(RunConfiguration chosen, string? arguments)
    {
        if (IsRunning || launching)
        {
            return Said("Already running");
        }
        launching = true;
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            parts.Editors.SaveAll();
            parts.Views.Run.Clear();
            // dotnet watch builds the project itself, and runs it again after an edit it cannot apply.
            bool hotReload = parts.Settings().HotReload && chosen.Program.Length == 0;
            watching = hotReload;
            watched = hotReload ? (chosen, arguments) : null;
            (LaunchPlan? plan, string? problem) = await PrepareAsync(chosen, arguments, build: !hotReload);
            if (plan is null)
            {
                return Said(problem!);
            }
            if (plan.WhyNot("run", built: !hotReload) is { } why)
            {
                return Said(why);
            }
            ran = chosen.Name;
            RunProject = Path.GetDirectoryName(plan.Project);
            string running = hotReload ? $"Running {chosen.Name} with Hot Reload: a save applies its changes" : $"Running {chosen.Name}";
            // An Avalonia or WPF app is launched with the inspector in it, which tells of its failed bindings while it runs.
            LaunchPlan launched = session.InspectedRun(plan, hotReload);
            LiveApp? inspected = session.RunApp;
            await session.RunXaml.StartAsync(hotReload ? inspected : null,
                session.Projects.SelectMany(p => p.Evaluated?.Items.Select(i => i.Path) ?? []).Concat(parts.Editors.Documents.Select(d => d.Path)));
            ShowBindings();
            Dictionary<string, string> variables = hotReload
                ? new Dictionary<string, string>(launched.Environment) { ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1", ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "true" }
                : new Dictionary<string, string>(launched.Environment);
            string? watchHooks = null;
            if (hotReload && inspected is not null)
            {
                try
                {
                    watchHooks = await WatchHooksAsync(launched);
                }
                catch (Exception error) when (error is ProjectHostFailed or IOException or UnauthorizedAccessException or LaunchException)
                {
                    inspected.Dispose();
                    return Said($"Could not start Hot Reload with the XAML inspector: {error.Message}");
                }
                // watch and dotnet run are .NET programs too. Only the target gets the inspector, alongside watch's agent.
                variables[Wire.StartupHooks] = string.Empty;
            }
            if (plan.Console == ConsoleKind.Terminal)
            {
                // The terminal's text is the program's own: its failed bindings are in Problems alone.
                WatchBindings(inspected, null);
                string? refused = await RunInTerminalAsync(chosen.Name, hotReload ? DotnetStart(Watch(plan, watchHooks), plan.WorkingDirectory) : ChildProcesses.Command(plan.Program, plan.Arguments, plan.WorkingDirectory), variables, running);
                if (refused is null && session.InTerminal is { } inTerminal)
                {
                    parts.OpenBrowser(plan, inTerminal.Ended);
                }
                EndWith(inspected, refused is null && session.InTerminal is { } run ? run.Ended : Task.CompletedTask);
                return refused;
            }
            session.InTerminal = null;
            parts.Views.Run.SetRunning(true);
            parts.Dock.Show("run");
            parts.Say(running);
            WatchBindings(inspected, parts.Views.Run);
            string? notStarted = Started(hotReload
                ? session.Runner.StartDotnet(Watch(plan, watchHooks), plan.WorkingDirectory, variables)
                : session.Runner.Start(plan.Program, plan.Arguments, plan.WorkingDirectory, variables));
            if (notStarted is null)
            {
                parts.OpenBrowser(plan, session.Runner.Ended);
            }
            EndWith(inspected, notStarted is null ? session.Runner.Ended : Task.CompletedTask);
            return notStarted;
        }
        finally
        {
            launching = false;
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

    /// <summary>Runs a plan's program in a tab of the Terminal window named for its configuration, the keys going to it, as
    /// Rider's Use external console runs one in a console of its own; Stop ends it, and its end shows in the status bar as
    /// a run's in the Run window does. Null once it runs, else why it could not.</summary>
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
        _ = Ended();
        return null;

        async Task Ended()
        {
            int code = await run.Ended;
            RunningChanged?.Invoke(this, EventArgs.Empty);
            if (session.InTerminal == run)
            {
                parts.Say(RunEnded(name, code));
            }
        }
    }

    /// <summary>dotnet watch's command line for a plan, as Rider's Hot Reload runs it: the project for its framework, no
    /// launch profile (the plan has its values already), the program's own arguments after <c>--</c>. It runs the program in
    /// its own working folder, which is the plan's.</summary>
    private static List<string> Watch(LaunchPlan plan, string? hooks)
    {
        List<string> watch = ["watch", "--non-interactive", "run", "--project", plan.Project!, "--no-launch-profile"];
        if (plan.Framework is { } framework)
        {
            watch.AddRange(["--framework", framework]);
        }
        if (hooks is not null)
        {
            watch.AddRange(["--environment", $"{Wire.StartupHooks}={hooks}"]);
        }
        watch.Add("--");
        watch.AddRange(plan.Built is { } assembly && plan.Arguments.Count > 0 && plan.Arguments[0] == assembly ? plan.Arguments.Skip(1) : plan.Arguments);
        return watch;
    }

    /// <summary>dotnet watch supplies its agent through run's -e option, replacing an inherited startup-hook list.
    /// Its target-only override must retain that agent as well as Passenger's inspector and the user's hooks.</summary>
    private async Task<string> WatchHooksAsync(LaunchPlan plan)
    {
        ProjectHost host = await ProjectHosts.ForAsync(Path.GetDirectoryName(plan.Project)!);
        string tools = Path.Combine(host.Sdk, "DotnetTools", "dotnet-watch");
        string framework = plan.Framework ?? session.Projects.FirstOrDefault(p => PathNames.Same(p.Path, plan.Project!))?.TargetFrameworks.FirstOrDefault() ?? "net8.0";
        int target = FrameworkMajor(framework);
        string? agent = Directory.Exists(tools) ? Directory.EnumerateFiles(tools, "Microsoft.Extensions.DotNetDeltaApplier.dll", SearchOption.AllDirectories)
            .Where(p => FrameworkMajor(Path.GetFileName(Path.GetDirectoryName(p))) <= target)
            .OrderByDescending(p => FrameworkMajor(Path.GetFileName(Path.GetDirectoryName(p)))).FirstOrDefault() : null;
        if (agent is null)
        {
            throw new LaunchException($"SDK {host.Version} has no compatible dotnet watch startup agent for {framework}");
        }
        string hooks = plan.Environment[Wire.StartupHooks];
        return string.Join(Path.PathSeparator, new[] { agent }.Concat(hooks.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
    }

    private static int FrameworkMajor(string? framework) => framework is not null && framework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(framework[3..].Split('.', '-')[0], out int major) ? major : 0;

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

    /// <summary>Says why a launch did not happen, and gives it back.</summary>
    private string Said(string problem)
    {
        parts.Say(problem);
        return problem;
    }

    /// <summary>Resolves and builds the chosen configuration, then launches its plan under the debugger, as Rider's Debug
    /// does, with these arguments in place of its own if given; the Debug window opens on its Console until the program
    /// stops somewhere. Null once its session is open, or what stopped it (a failed build, no configuration, the adapter's
    /// refusal).</summary>
    public Task<string?> DebugAsync(string? arguments = null) =>
        session.Configurations.Chosen is { } chosen ? DebugAsync(chosen, arguments) : Task.FromResult<string?>("Nothing to debug: no executable project");

    /// <summary>The configuration Debug last launched, and the arguments it was given, which a restart for Hot Reload
    /// launches again.</summary>
    private (RunConfiguration Configuration, string? Arguments)? debugged;

    /// <summary>Hot Reload's restart for an edit it cannot apply: the program Debug last launched is stopped (on Windows its
    /// files are locked while it runs), built again and launched with the same configuration and arguments, the
    /// breakpoints the store's. Null once the new session is open, else why it could not.</summary>
    public async Task<string?> RestartDebugAsync()
    {
        if (debugged is not var (configuration, arguments))
        {
            return "Nothing was launched under the debugger to restart";
        }
        await session.Debug.StopAsync();
        return await DebugAsync(configuration, arguments);
    }

    private async Task<string?> DebugAsync(RunConfiguration chosen, string? arguments)
    {
        debugged = (chosen, arguments);
        using IDisposable held = BackgroundWork.Scope();
        (LaunchPlan? plan, string? problem) = await PrepareAsync(chosen, arguments);
        if (plan is null)
        {
            return problem;
        }
        if (plan.WhyNot("debug") is { } why)
        {
            return why;
        }
        parts.Dock.Show("debug");
        parts.Views.Debug.ShowTab(console: true);
        // Before the session clears the Debug window's console, which forgets the links the last one's output had.
        DebugProject = Path.GetDirectoryName(plan.Project);
        DebugController debug = session.Debug;
        // An Avalonia or WPF app is launched with the Live Visual Tree's inspector in it, which ends with this debugging.
        LaunchPlan launched = session.Inspected(plan);
        LiveApp? inspected = session.LiveApp;
        ShowBindings();
        WatchBindings(inspected, parts.Views.Debug.Console);
        string? refused = await debug.StartAsync(launched);
        Task ended = refused is null ? DebugEnded(debug) : Task.CompletedTask;
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
            _ = EndWithAsync(app, ended);
        }

        static async Task EndWithAsync(LiveApp app, Task ended)
        {
            await ended;
            app.Dispose();
        }
    }

    /// <summary>An app's failed bindings as they come: each a line of its console linked to its XAML (none for a program in
    /// the terminal, whose text is its own), and Problems' Bindings.</summary>
    private void WatchBindings(LiveApp? app, OutputView? console)
    {
        if (app is null)
        {
            return;
        }
        app.StateChanged += (_, _) =>
        {
            if (!session.IsEnded && app.State == LiveAppState.Waiting)
            {
                foreach ((LiveApp App, int Id) key in placed.Keys.Where(k => k.App == app).ToList())
                {
                    placed.Remove(key);
                }
                ShowBindings();
            }
        };
        app.BindingFailed += (_, failed) =>
        {
            if (session.IsEnded)
            {
                return;
            }
            console?.Append(BindingErrors.Line(Placed(app, failed.Failure), session.Folder), error: true);
            ShowBindings();
        };
    }

    /// <summary>The failed bindings of the apps Run and Debug launched last, in Problems' Bindings.</summary>
    public void ShowBindings() => parts.Views.Problems.ShowBindings([.. session.FailedBindings.Select(b => Placed(b.App, b.Failure))]);

    /// <summary>A failure with its problem, which is placed once, when it first fails: at its misspelt member where its XAML
    /// has it, else at its control's element.</summary>
    public BindingProblem Placed(LiveApp app, BindingFailure failure)
    {
        if (!placed.TryGetValue((app, failure.Id), out Problem? problem))
        {
            problem = placed[(app, failure.Id)] = BindingErrors.Problem(failure, failure.Place is { } at ? BindingErrors.TextOf(at.File, parts.Editors.Documents) : null).Problem;
        }
        return new BindingProblem(failure, problem);
    }

    /// <summary>The end of the session being debugged; done already when there is none.</summary>
    private static Task DebugEnded(DebugController debug)
    {
        if (debug.Session is not { State: not DebugState.Ended } running)
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

    /// <summary>Stop: the debugger's session, the program Run started (in the Run window or the terminal) and the build.</summary>
    public void Stop()
    {
        _ = session.Debug.StopAsync();
        session.Runner.Stop();
        session.InTerminal?.Stop();
        session.Builder.Stop();
    }

    /// <summary>Why Stop cannot run now: nothing it stops is running.</summary>
    public string? WhyNotStop() => session.Debug.IsDebugging || IsRunning || session.Builder.IsRunning ? null : "Nothing is running";
}

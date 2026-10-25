using Passenger.App.Aspire;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>Unit testing in the IDE: the solution's tests shown as their code declares them from the moment
/// it opens, the test projects built and their tests found by a runner, runs of all of them, a chosen few or the ones
/// that failed, results as they come, and a test debugged under Passenger's own debugger. It works in the IDE's own
/// tests and outcomes, through <see cref="ITestRunner"/> (VSTest's and Microsoft.Testing.Platform's, <see cref="TestRunners"/>).
/// A UI-thread object: everything a run says crosses to the UI thread before it changes the tree.</summary>
public sealed class TestController : IAsyncDisposable
{
    /// <summary>What the window says when Stop ends a run before its tests ran.</summary>
    public const string StoppedMessage = "Tests stopped";

    private static readonly IReadOnlyList<TestSource> NoSources = [];

    private readonly Func<CancellationToken, Task<Run.BuildOutcome>> build;
    private readonly Func<IReadOnlyList<ProjectModel>> projects;

    /// <summary>The folder's workspace as last read, whose solution's properties MSBuild is given where the tests build.</summary>
    private readonly Func<WorkspaceModel?> workspaceModel;
    private readonly Action<string> log;
    private readonly Func<string, CancellationToken, Task<ITestRunner>> startRunner;
    private ITestRunner? runner;
    private string? folder;
    private IReadOnlyList<TestNode> lastRun = [];

    /// <summary>The build, discovery or run under way, which Stop cancels whichever it is.</summary>
    private CancellationTokenSource? stopping;

    /// <summary>The test projects last asked where they build, and the answer, kept until the folder's projects are read
    /// again (a project or props file changed), which makes them other objects.</summary>
    private (IReadOnlyList<ProjectModel> Projects, IReadOnlyList<TestAssembly> Assemblies)? resolved;

    /// <summary>What discovery found in each assembly, by its path, with the time the assembly was written then: an
    /// assembly not built again since is not asked again.</summary>
    private readonly Dictionary<string, (DateTime Written, IReadOnlyList<TestItem> Tests)> discovered = new(StringComparer.Ordinal);

    /// <summary>Each project's assemblies' tests as last discovered, by project path; a project's list is the same object
    /// until what was found in it changes, so the tree keeps the project as it was.</summary>
    private Dictionary<string, IReadOnlyList<TestSource>> sources = new(StringComparer.Ordinal);

    /// <summary>The tests each test project's code declares, by project path.</summary>
    private IReadOnlyDictionary<string, CodeTests> code = new Dictionary<string, CodeTests>();

    public TestController(TestTree tree, Func<CancellationToken, Task<Run.BuildOutcome>> build, Func<IReadOnlyList<ProjectModel>> projects,
        Func<WorkspaceModel?> workspaceModel, Action<string> log, Func<string, CancellationToken, Task<ITestRunner>>? startRunner = null)
    {
        Tree = tree;
        this.build = build;
        this.projects = projects;
        this.workspaceModel = workspaceModel;
        this.log = log;
        this.startRunner = startRunner ?? ((workspace, _) => Task.FromResult<ITestRunner>(new TestRunners(workspace)));
        Finder.Changed += (_, found) =>
        {
            code = found;
            Load();
        };
        Finder.Moved += (_, moved) => Tree.Move(moved.Path, moved.Moved);
        Load();
    }

    public TestTree Tree { get; }

    /// <summary>The window's work under way, which a run shows itself in from its first moment to its end, Stop ending it;
    /// null with no window to show it in.</summary>
    public ActionWork? Work { get; init; }

    /// <summary>The run under way as the window shows it.</summary>
    private Working? working;

    /// <summary>The tests the code declares, which the tree shows before any build.</summary>
    public TestFinder Finder { get; } = new();

    /// <summary>Finding or running tests, the build before included: what Stop ends.</summary>
    public bool IsBusy
    {
        get => busy;
        private set
        {
            if (busy != value)
            {
                busy = value;
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool busy;

    /// <summary>Raised when <see cref="IsBusy"/> changes, for the Unit Tests window's Stop.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>The last run's tally, for the window's status line.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Tests were found or results came in: the gutter's marks follow.</summary>
    public event EventHandler? Changed;

    /// <summary>Something for the status bar.</summary>
    public event EventHandler<string>? Message;

    /// <summary>A run of tests ended, whether every test passed.</summary>
    public event EventHandler<bool>? Ran;

    /// <summary>Asks the container engine before a run of a project that references Testcontainers: why it cannot serve
    /// (not running, no CLI), or the <c>DOCKER_HOST</c> of its current context, for a test host that has none set.</summary>
    public Func<CancellationToken, Task<(string? Problem, string? DockerHost)>>? ContainerEngine { get; set; }

    /// <summary>A test to debug: the debugger launches the test host the runner gives and says what process it is.</summary>
    public Func<TestHostStart, Task<int>>? LaunchUnderDebugger { get; set; }

    /// <summary>A test to debug in a test host the runner started itself: the debugger attaches to it, true once it has.</summary>
    public Func<int, Task<bool>>? AttachDebugger { get; set; }

    /// <summary>Starts a project-scoped Aspire endpoint before its test host is launched.</summary>
    public Func<ProjectModel, bool, CancellationToken, Task<AspireTestRun?>>? BeginAspireTest { get; set; }

    /// <summary>Ends only the test hosts debugged by this run.</summary>
    public Func<Task>? EndDebugging { get; set; }

    internal CancellationToken RunCancellation => stopping?.Token ?? CancellationToken.None;

    /// <summary>Opens a folder's tests, or the same folder's again after its projects were read again: its test projects'
    /// code is read for the tests it declares, and what was discovered in projects no longer there goes. Until a folder
    /// without a solution has been walked for its projects there are none to read, and the window says it is looking.</summary>
    public void Open(string? workspace, bool projectsRead = true)
    {
        UiThread.Check();
        if (workspace != folder && !IsBusy)
        {
            Forget(workspace);
        }
        if (projectsRead)
        {
            Finder.Scan(workspace, workspace is null ? [] : projects());
        }
        else if (workspace != Finder.Folder)
        {
            Finder.Scan(workspace, []);
        }
        awaitingProjects = !projectsRead;
        Load();
    }

    private bool awaitingProjects;

    /// <summary>Whether the tests are still being found from code, as the window's "Looking for tests…" says: the folder's
    /// projects not yet walked, or its test projects' files still being read.</summary>
    public bool IsLooking => awaitingProjects || Finder.IsLooking;

    /// <summary>Builds, then finds the tests of every test project. Null once found, or what stopped it.</summary>
    public async Task<string?> DiscoverAsync(string workspace)
    {
        UiThread.Check();
        if (IsBusy)
        {
            return "Tests are already running";
        }
        IsBusy = true;
        BackgroundWork.Begin();
        using CancellationTokenSource stop = Begin();
        try
        {
            return await DiscoverCore(workspace, build: true, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Stopped();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return $"The tests could not be found: {error.Message}";
        }
        finally
        {
            stopping = null;
            IsBusy = false;
            BackgroundWork.End();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Run Until Failure: these rows' tests run again and again, built once, until one fails or Stop is
    /// pressed, each run counted as it goes; the failing run's results stay to read.</summary>
    public async Task<string?> RunUntilFailureAsync(string workspace, IEnumerable<TestNode> rows)
    {
        List<TestNode> chosen = [.. rows];
        stopRequested = false;
        for (int run = 1; ; run++)
        {
            if (await RunAsync(workspace, chosen, debug: false, build: run == 1, prefix: $"Run {run}: ") is { } problem)
            {
                return problem;
            }
            Runs = run;
            if (FirstFailed(chosen) is { } failed)
            {
                Message?.Invoke(this, $"{failed.Name} failed on run {run}");
                return null;
            }
            if (stopRequested)
            {
                Message?.Invoke(this, $"Stopped after {Plural.Of(run, "run")}; none failed");
                return null;
            }
        }
    }

    /// <summary>How many runs the last Run Until Failure made.</summary>
    public int Runs { get; private set; }

    /// <summary>The first of these rows' tests that failed in the last run, if one did.</summary>
    public TestNode? FirstFailed(IEnumerable<TestNode> rows) =>
        rows.SelectMany(r => r.Kind == TestNodeKind.Row ? [r] : r.Methods).FirstOrDefault(m => m.State == TestState.Failed);

    private bool stopRequested;

    /// <summary>Runs tests: these rows' (all when none), after a build and a look for the tests built since, so a test
    /// the code declares runs once it has been built.</summary>
    public async Task<string?> RunAsync(string workspace, IEnumerable<TestNode>? rows = null, bool debug = false, bool build = true, string prefix = "")
    {
        UiThread.Check();
        if (IsBusy)
        {
            return "Tests are already running";
        }
        IsBusy = true;
        BackgroundWork.Begin();
        holdingWork = true;
        List<TestNode>? chosen = rows?.ToList();
        using CancellationTokenSource stop = Begin();
        working = Work?.Begin(AppActions.RunTests, build ? "Building the test projects…" : "Finding the tests…");
        using CancellationTokenRegistration stopped = working?.Stopping.Register(stop.Cancel) ?? default;
        try
        {
            if ((build || runner is null) && await DiscoverCore(workspace, build, stop.Token) is { } problem)
            {
                return problem;
            }
            stop.Token.ThrowIfCancellationRequested();
            List<TestItem> tests = Chosen(chosen ?? Tree.Methods);
            if (tests.Count == 0)
            {
                return chosen is null ? "There are no tests to run" : "None of these tests was found in the test projects as built";
            }
            List<TestNode> methods = [.. tests.Select(Tree.Node).OfType<TestNode>().Select(n => n.Kind == TestNodeKind.Row ? n.Parent! : n).Distinct()];
            HashSet<string> containerized = [.. projects().Where(p => p.Packages.Any(n => n.Name.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Path).Where(p => tests.Any(t => t.Project == p))];
            string? dockerHost = null;
            if (containerized.Count > 0 && ContainerEngine is { } engine)
            {
                // Testcontainers' own words for a missing engine are a page per test; the engine's state says it once.
                working?.Say("Checking the container engine for Testcontainers…");
                (string? engineProblem, dockerHost) = await engine(stop.Token);
                if (engineProblem is not null)
                {
                    string said = $"{prefix}The tests were not run: they use Testcontainers, and {engineProblem}";
                    Summary = said;
                    Message?.Invoke(this, said);
                    return said;
                }
            }
            lastRun = chosen ?? [];
            working?.Say($"{prefix}Running {Plural.Of(tests.Count, "test")}…");
            Tree.Begin(tests);
            RunSummary? summary = null;
            List<AspireTestRun> aspireRuns = [];
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
                Message?.Invoke(this, $"{prefix}Running {Plural.Of(tests.Count, "test")}");
                HashSet<TestOutcome> applied = new(ReferenceEqualityComparer.Instance);
                void Apply(IReadOnlyList<TestOutcome> outcomes) => Tree.Apply(outcomes.Where(applied.Add));
                // Each test project's tests run with its launch profile's environment, so projects whose differ run apart.
                Dictionary<string, IReadOnlyDictionary<string, string>> environments = tests.Select(t => t.Project).Distinct(StringComparer.Ordinal)
                    .ToDictionary(p => p, p => containerized.Contains(p) ? WithDockerHost(EnvironmentOf(p), dockerHost) : EnvironmentOf(p), StringComparer.Ordinal);
                if (BeginAspireTest is { } beginAspire)
                {
                    foreach (string path in environments.Keys.ToArray())
                    {
                        ProjectModel? project = resolved?.Assemblies.FirstOrDefault(assembly => assembly.Project.Path == path)?.Project;
                        if (project is not null && await beginAspire(project, debug, stop.Token) is { } aspire)
                        {
                            aspireRuns.Add(aspire);
                            Dictionary<string, string> environment = new(environments[path], StringComparer.Ordinal);
                            environment.Remove("DEBUG_SESSION_SERVER_CERTIFICATE");
                            foreach ((string name, string value) in aspire.Environment)
                            {
                                environment[name] = value;
                            }
                            environments[path] = environment;
                        }
                        stop.Token.ThrowIfCancellationRequested();
                    }
                }
                foreach (IGrouping<string, (TestItem Test, IReadOnlyDictionary<string, string> Environment)> group in tests
                    .Select(t => (Test: t, Environment: environments[t.Project]))
                    .GroupBy(t => string.Join('\n', t.Environment.Select(v => $"{v.Key}={v.Value}")), StringComparer.Ordinal))
                {
                    if (summary is { Canceled: true })
                    {
                        break;
                    }
                    RunSummary part = await runner!.RunAsync(
                        [.. group.Select(t => t.Test)],
                        UiThread.Marshal<IReadOnlyList<TestOutcome>>(Apply),
                        log: UiThread.Marshal(log),
                        debugger: debug ? new DebugLauncher(this) : null,
                        environment: group.First().Environment,
                        cancel: stop.Token);
                    summary = summary is null ? part : summary.And(part);
                }
                // A runner may tell an outcome on another thread behind the run's end: every one is in before the end is.
                Apply(summary!.Outcomes);
            }
            finally
            {
                // However the run ends, a runner's failure included, what it began and never heard of is no longer running.
                Tree.End();
                await Task.WhenAll(aspireRuns.Select(aspire => aspire.DisposeAsync().AsTask()));
            }
            Summary = prefix + Tally(methods, summary);
            Message?.Invoke(this, Summary);
            if (!summary.Canceled)
            {
                Ran?.Invoke(this, FirstFailed(methods) is null);
            }
            return null;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return Stopped();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return $"The tests could not run: {error.Message}";
        }
        finally
        {
            try
            {
                if (debug && EndDebugging is { } endDebugging)
                {
                    await endDebugging();
                }
            }
            finally
            {
                stopping = null;
                IsBusy = false;
                working?.Dispose();
                working = null;
                ReleaseWork();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>A run under the debugger may stop for as long as the person looks: from the debugger's start it is the
    /// session's, not work a screenshot waits out.</summary>
    private void ReleaseWork()
    {
        if (holdingWork)
        {
            holdingWork = false;
            BackgroundWork.End();
        }
    }

    private bool holdingWork;

    /// <summary>The last run's tests again, after a build.</summary>
    public Task<string?> RerunAsync(string workspace) =>
        lastRun.Count == 0 ? RunAsync(workspace) : RunAsync(workspace, [.. lastRun]);

    public Task<string?> RerunFailedAsync(string workspace)
    {
        List<TestNode> failed = [.. Tree.Methods.Where(m => m.State == TestState.Failed)];
        return failed.Count == 0 ? Task.FromResult<string?>("No test failed") : RunAsync(workspace, failed);
    }

    /// <summary>Stops what is under way, whichever step it is at: the build, finding where the tests build, finding
    /// them, or running them (what has run keeps its result).</summary>
    public void Stop()
    {
        UiThread.Check();
        stopRequested = true;
        stopping?.Cancel();
    }

    /// <summary>Ends the runner on a worker thread, since its end waits for its process; it needs no UI thread, so the
    /// process can wait for it once the window's loop has stopped.</summary>
    public ValueTask DisposeAsync()
    {
        UiThread.Check();
        ITestRunner? ending = runner;
        runner = null;
        // Not held: the window's end waits for it with no UI thread to come back to (MainWindow.Ended).
        return ending is null ? ValueTask.CompletedTask : new ValueTask(Task.Run(ending.Dispose));
    }

    /// <summary>The tests these rows run: a method's or a group's tests as a runner found them, a data row's own (its
    /// method's for a row its run made); none of a method no runner has found, or one no longer in the tree.</summary>
    private static List<TestItem> Chosen(IEnumerable<TestNode> rows)
    {
        List<TestItem> tests = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (TestNode row in rows.Where(r => !r.Gone))
        {
            IEnumerable<TestItem> its = row.Kind == TestNodeKind.Row ? row.Item is { } item ? [item] : row.Parent!.Items : row.Methods.SelectMany(m => m.Items);
            tests.AddRange(its.Where(t => seen.Add(t.Key)));
        }
        return tests;
    }

    /// <summary>The tree for the test projects: each one's tests from its code and from what was discovered in it, and
    /// what the window says while it has none.</summary>
    private void Load()
    {
        IReadOnlyList<ProjectModel> tests = Finder.Projects;
        Tree.Load([.. tests.Select(p => new ProjectTests(p.Path, Path.GetFileNameWithoutExtension(p.Path), code.GetValueOrDefault(p.Path) ?? CodeTests.None, sources.GetValueOrDefault(p.Path) ?? NoSources)
        {
            DeclaredFramework = p.TargetFrameworks is [{ } only] && !only.Contains('$', StringComparison.Ordinal) ? only : null,
        })]);
        Tree.EmptyText = folder is null ? "Open a folder or solution to see its tests"
            : awaitingProjects || Finder.IsLooking ? "Looking for tests…"
            : tests.Count == 0 ? "No test projects: none references xUnit, NUnit or MSTest"
            : "No tests in the test projects yet";
    }

    /// <summary>Lets go of another folder's runner and what was discovered in it.</summary>
    private void Forget(string? workspace)
    {
        runner?.Dispose();
        runner = null;
        discovered.Clear();
        sources = new(StringComparer.Ordinal);
        resolved = null;
        folder = workspace;
    }

    private CancellationTokenSource Begin()
    {
        CancellationTokenSource stop = new();
        stopping = stop;
        return stop;
    }

    /// <summary>What the window says of a run stopped before its tests ran; a build stopped says so itself.</summary>
    private string Stopped()
    {
        if (runner is { IsUsable: false } stuck)
        {
            // A discovery that never said it had stopped: its runner ends, its test hosts with it.
            runner = null;
            IdeErrors.Observe(Task.Run(stuck.Dispose));
        }
        Message?.Invoke(this, StoppedMessage);
        return StoppedMessage;
    }

    private async Task<string?> DiscoverCore(string workspace, bool build, CancellationToken cancel)
    {
        if (build && await this.build(cancel) is { Succeeded: false } unbuilt)
        {
            return unbuilt.Message;
        }
        cancel.ThrowIfCancellationRequested();
        if (build)
        {
            working?.Say("Finding the tests…");
        }
        if (folder != workspace)
        {
            Forget(workspace);
        }
        IReadOnlyList<ProjectModel> tests = [.. projects().Where(p => p.Exists && p.IsTestProject)];
        if (tests.Count == 0)
        {
            sources = new(StringComparer.Ordinal);
            Load();
            return "There are no test projects: none references a test SDK or adapter";
        }
        IReadOnlyList<TestAssembly> assemblies = await Assemblies(tests, workspace, cancel);
        List<TestAssembly> built = [];
        foreach (TestAssembly assembly in assemblies)
        {
            if (File.Exists(assembly.Path))
            {
                built.Add(assembly);
            }
            else
            {
                log($"{assembly.Project.Name}: {Path.GetFileName(assembly.Path)} ({assembly.Framework}) has not been built");
            }
        }
        runner ??= await startRunner(workspace, cancel);
        // Only what was built again since it was last looked through is looked through again.
        Dictionary<string, DateTime> written = built.ToDictionary(a => a.Path, a => File.GetLastWriteTimeUtc(a.Path), StringComparer.Ordinal);
        List<TestAssembly> stale = [.. built.Where(a => !discovered.TryGetValue(a.Path, out (DateTime Written, IReadOnlyList<TestItem> Tests) known) || known.Written != written[a.Path])];
        if (stale.Count > 0)
        {
            IReadOnlyList<TestItem> found = await runner.DiscoverAsync(stale, UiThread.Marshal(log), cancel);
            Dictionary<(string, string?), List<TestItem>> byAssembly = [];
            foreach (TestItem test in found)
            {
                if (!byAssembly.TryGetValue((test.Project, test.Framework), out List<TestItem>? its))
                {
                    byAssembly[(test.Project, test.Framework)] = its = [];
                }
                its.Add(test);
            }
            foreach (TestAssembly assembly in stale)
            {
                discovered[assembly.Path] = (written[assembly.Path], byAssembly.GetValueOrDefault((assembly.Project.Path, assembly.Framework)) ?? []);
            }
        }
        cancel.ThrowIfCancellationRequested();
        Dictionary<string, IReadOnlyList<TestSource>> next = new(StringComparer.Ordinal);
        foreach (IGrouping<string, TestAssembly> project in built.GroupBy(a => a.Project.Path))
        {
            List<TestSource> now = [.. project.Select(a => new TestSource(a.Framework, discovered[a.Path].Tests))];
            next[project.Key] = sources.TryGetValue(project.Key, out IReadOnlyList<TestSource>? was)
                && was.Select(s => (s.Framework, s.Tests)).SequenceEqual(now.Select(s => (s.Framework, s.Tests))) ? was : now;
        }
        sources = next;
        Load();
        int count = built.Sum(a => discovered[a.Path].Tests.Count);
        int projectCount = built.Select(a => a.Project).Distinct().Count();
        Message?.Invoke(this, $"Found {Plural.Of(count, "test")} in {Plural.Of(projectCount, "project")}");
        return null;
    }

    /// <summary>The test projects' assemblies, from MSBuild once for these projects as the folder last read them.</summary>
    private async Task<IReadOnlyList<TestAssembly>> Assemblies(IReadOnlyList<ProjectModel> tests, string workspace, CancellationToken cancel)
    {
        if (resolved is { } known && known.Projects.SequenceEqual(tests, ReferenceEqualityComparer.Instance))
        {
            return known.Assemblies;
        }
        IReadOnlyList<TestAssembly> assemblies = await TestAssemblies.ResolveAsync(tests, workspace, workspaceModel(), UiThread.Marshal(log), cancel);
        resolved = (tests, assemblies);
        return assemblies;
    }

    /// <summary>The environment a test project's tests run with: its first launch profile that runs the project, its
    /// variables; and for a project testing Avalonia headless under xunit v3, the hook that draws a failing test's windows
    /// into its result.</summary>
    private IReadOnlyDictionary<string, string> EnvironmentOf(string project)
    {
        List<TestAssembly> its = [.. resolved?.Assemblies.Where(a => a.Project.Path == project) ?? []];
        IReadOnlyDictionary<string, string> environment = its.FirstOrDefault()?.Project is { } built
            && Run.LaunchProfile.Read(built).FirstOrDefault(p => p.RunsProject) is { } profile
            ? profile.EnvironmentVariables
            : new Dictionary<string, string>();
        return its.Any(a => TestFrames.Draws(a.Path)) ? TestFrames.With(environment) : environment;
    }

    /// <summary>A Testcontainers project's environment with the engine's context's endpoint as <c>DOCKER_HOST</c>, unless its
    /// profile or the IDE's environment sets one: Testcontainers reads no Docker context of its own.</summary>
    private static IReadOnlyDictionary<string, string> WithDockerHost(IReadOnlyDictionary<string, string> environment, string? host)
    {
        if (host is null || environment.ContainsKey("DOCKER_HOST") || Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 })
        {
            return environment;
        }
        return new Dictionary<string, string>(environment) { ["DOCKER_HOST"] = host };
    }

    private static string Tally(IReadOnlyList<TestNode> methods, RunSummary summary)
    {
        List<TestOutcome> results = [.. methods.SelectMany(m => m.Results)];
        int passed = results.Count(r => r.State == TestState.Passed);
        int failed = results.Count(r => r.State == TestState.Failed);
        int skipped = results.Count(r => r.State == TestState.Skipped);
        string ending = summary.Canceled ? ", stopped" : summary.Aborted ? $", broken off{(summary.Error is { } why ? $": {why}" : "")}" : string.Empty;
        return $"Tests passed: {passed}, failed: {failed}{(skipped > 0 ? $", skipped: {skipped}" : "")} of {results.Count} in {summary.Elapsed.TotalSeconds:0.0} s{ending}";
    }

    /// <summary>The debugger for a run: the runner's test host started under it, or attached to, on the UI thread, from
    /// when the run is the debugger session's rather than work a screenshot waits for.</summary>
    private sealed class DebugLauncher(TestController owner) : ITestDebugLauncher
    {
        private readonly CancellationToken run = owner.RunCancellation;
        private bool IsCurrent => owner.IsBusy && owner.RunCancellation == run && !run.IsCancellationRequested;

        public Task<int> LaunchAsync(TestHostStart start) =>
            owner.LaunchUnderDebugger is { } launch
                ? UiThread.InvokeAsync(() =>
                {
                    if (!IsCurrent)
                    {
                        return Task.FromCanceled<int>(new CancellationToken(canceled: true));
                    }
                    owner.ReleaseWork();
                    return launch(start);
                })
                : throw new InvalidOperationException("No debugger can start the test host");

        public Task<bool> AttachAsync(int processId) =>
            owner.AttachDebugger is { } attach
                ? UiThread.InvokeAsync(() =>
                {
                    if (!IsCurrent)
                    {
                        return Task.FromResult(false);
                    }
                    owner.ReleaseWork();
                    return attach(processId);
                })
                : Task.FromResult(false);
    }
}

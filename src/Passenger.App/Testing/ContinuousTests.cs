using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>Continuous testing of a folder's tests: as files are saved, a round builds the test projects in a folder of
/// Passenger's own (UseArtifactsOutput in <see cref="Root"/>, never the person's bin or obj), copies what each built to a run's
/// own copy, instruments the modules of the folder's projects that changed there, and runs, one at a time, the tests never
/// mapped and those the change reaches (<see cref="TestReachState.Changed"/>), on the Testing Platform or VSTest as each
/// project's are, each test's lines and outcome taken into the map (<see cref="TestHitsRun"/>). A round waits while the person's build, tests, debugging or profiling needs the machine
/// (<see cref="Paused"/>), and is stopped once one begins. A UI-thread object.</summary>
public sealed class ContinuousTests : IDisposable
{
    /// <summary>What a round's work is held under: no action's, so Continuous Testing turns it off while one runs.</summary>
    private const string RoundWork = "continuous-testing-round";

    /// <summary>How long saves must pause before a round begins.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    private readonly TestController tests;
    private readonly Func<IReadOnlyList<ProjectModel>> projects;
    private readonly Func<WorkspaceModel?> workspace;
    private readonly Action<string> log;
    private readonly Debounce waiting;
    private readonly TestingPlatformRunner platform = new();

    /// <summary>VSTest, started the first time a copy of a project it runs needs it, and the copies it runs.</summary>
    private ITestRunner? vsTest;
    private readonly HashSet<string> vsTestCopies = new(PathNames.Comparer);

    /// <summary>The paths saved since the last round began; every mapped file where the map was read from an earlier session.</summary>
    private readonly HashSet<string> changed = new(PathNames.Comparer);
    private bool everyFile = true;

    /// <summary>Each file of a run's copy as copied, by its path there, with the stamp of what it was copied from.</summary>
    private readonly Dictionary<string, FileStamp> copied = new(PathNames.Comparer);

    /// <summary>Each run's copy of a test assembly: what its modules' instrumenting mapped, and its tests as last found, with
    /// the stamp of the assembly they were found in.</summary>
    private readonly Dictionary<string, (InstrumentedCode Code, FileStamp? Found, IReadOnlyList<TestItem> Tests)> runs = new(PathNames.Comparer);

    private string? folder;
    private CancellationTokenSource? round;
    private bool pending;

    public ContinuousTests(TestController tests, Func<IReadOnlyList<ProjectModel>> projects, Func<WorkspaceModel?> workspace, Action<string> log)
    {
        this.tests = tests;
        this.projects = projects;
        this.workspace = workspace;
        this.log = log;
        waiting = new Debounce(Quiet, BeginAsync);
    }

    /// <summary>The window's work, which a round shows itself in from its first stage to its end.</summary>
    public ActionWork? Work { get; init; }

    /// <summary>Why the machine is the person's now (their build, tests, debugging or profiling), which no round runs beside;
    /// null while it is free.</summary>
    public Func<Words?> Paused { get; set; } = () => null;

    /// <summary>Why the folder's code may not run (it is not trusted); null while it may.</summary>
    public Func<Words?> Refused { get; set; } = () => null;

    /// <summary>Where its builds and runs' copies are: the folder's own, made by the first round.</summary>
    public string? Root { get; set; }

    public bool IsOn { get; private set; }

    /// <summary>Whether a round is under way.</summary>
    public bool IsRunning => round is not null;

    /// <summary>Which tests run which lines, as mapped so far.</summary>
    public TestReachState Map { get; private set; } = new();

    /// <summary>The map changed: the gutter's marks follow, and the folder's state keeps it.</summary>
    public event EventHandler? MapChanged;

    /// <summary>It was turned on or off.</summary>
    public event EventHandler? Turned;

    /// <summary>How many rounds have ended, and the tests the last one ran.</summary>
    public int Rounds { get; private set; }

    public IReadOnlyList<TestItem> LastRan { get; private set; } = [];

    /// <summary>What the last round came to, in words.</summary>
    public Words Said { get; private set; } = Words.Verbatim(string.Empty);

    /// <summary>A folder's continuous testing, its map as kept, on or off; no round until a save or its turning on.</summary>
    public void Open(string? opened, TestReachState map)
    {
        UiThread.Check();
        Stop();
        folder = opened;
        Map = map;
        everyFile = true;
        changed.Clear();
        copied.Clear();
        runs.Clear();
        MapChanged?.Invoke(this, EventArgs.Empty);
        if (Root is { } root)
        {
            // The folder opened now: its cache is not one a later start prunes.
            IdeErrors.Observe(BackgroundWork.Run(() => Opened(root)));
        }
    }

    /// <summary>How long a folder's cache stays once no window has opened the folder.</summary>
    public static readonly TimeSpan KeptUnopened = TimeSpan.FromDays(30);

    /// <summary>The state folders whose caches this process has pruned.</summary>
    private static readonly HashSet<string> Pruned = new(PathNames.Comparer);

    /// <summary>Once a process, the caches under <paramref name="caches"/> of folders not opened for <see cref="KeptUnopened"/>
    /// removed, off the window's thread.</summary>
    public static void PruneOnce(string caches)
    {
        lock (Pruned)
        {
            if (!Pruned.Add(caches))
            {
                return;
            }
        }
        IdeErrors.Observe(BackgroundWork.Run(() =>
        {
            if (!Directory.Exists(caches))
            {
                return;
            }
            foreach (string cache in Directory.EnumerateDirectories(caches))
            {
                // Set against the folder's own time, which each opening of its folder sets.
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(cache) > KeptUnopened)
                {
                    Removed(cache);
                }
            }
        }));
    }

    /// <summary>A folder's cache marked as opened now, where it has one.</summary>
    private static void Opened(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow);
            }
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            // Left as it was: a later start may prune it a little early, and the next round builds it again.
        }
    }

    /// <summary>A cache removed, what a program still holds of it (on Windows) left for a later prune.</summary>
    private static void Removed(string cache)
    {
        try
        {
            Directory.Delete(cache, recursive: true);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Held by a test host as it ends: its folder ages until a later start prunes it.
        }
    }

    /// <summary>The folder's cache removed, once no round uses it: continuous testing was turned off.</summary>
    private void DropCache()
    {
        copied.Clear();
        runs.Clear();
        if (Root is { } root)
        {
            IdeErrors.Observe(BackgroundWork.Run(() => Removed(root)));
        }
    }

    /// <summary>Turns it on, a round begun at once, or off, the round under way stopped.</summary>
    public void Turn(bool on)
    {
        UiThread.Check();
        if (on == IsOn)
        {
            return;
        }
        IsOn = on;
        if (on)
        {
            waiting.Restart();
        }
        else
        {
            Stop();
            if (round is null)
            {
                DropCache();
            }
        }
        Turned?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Files the folder's watcher heard saved, written or gone, or <paramref name="every"/> where it lost what: a
    /// round once saves pause, which looks at every mapped file where what changed is not known, a save of a file a round
    /// gave back among them.</summary>
    public void FilesChanged(IEnumerable<string> paths, bool every = false)
    {
        UiThread.Check();
        string[] heard = [.. paths.Where(p => !IsOwn(p))];
        changed.UnionWith(heard);
        everyFile |= every;
        if (IsOn && (heard.Length > 0 || every))
        {
            waiting.Restart();
        }
    }

    /// <summary>The person's build, tests, debugging or profiling began or ended: a round under way stops for it, and one
    /// put off for it begins once it has ended.</summary>
    public void MachineChanged()
    {
        UiThread.Check();
        if (Paused() is not null)
        {
            round?.Cancel();
        }
        else if (IsOn && pending && round is null)
        {
            waiting.Restart();
        }
    }

    /// <summary>Stops the round under way and drops the one waiting.</summary>
    public void Stop()
    {
        waiting.Stop();
        round?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        if (vsTest is { } ending)
        {
            vsTest = null;
            // Not held: its end waits for vstest.console, which no screenshot or test waits out.
            IdeErrors.Observe(BackgroundWork.Unheld(ending.Dispose));
        }
    }

    /// <summary>Whether a path is its own output, which its builds write and no round follows (a state folder inside the
    /// open one is heard by its watcher, which names paths as the disk does).</summary>
    private bool IsOwn(string path) => (canonicalRoot ?? Root) is { } root && PathNames.IsUnder(path, root);

    /// <summary><see cref="Root"/> as the disk names it, once a round has made it.</summary>
    private string? canonicalRoot;

    private async Task BeginAsync()
    {
        if (!IsOn || folder is null)
        {
            return;
        }
        if (round is not null || Paused() is not null)
        {
            pending = true;
            return;
        }
        pending = false;
        if (Refused() is { } refused)
        {
            Said = refused;
            return;
        }
        using CancellationTokenSource stop = new();
        round = stop;
        Working? work = Work?.Begin(RoundWork, TestingWords.ContinuousBuildingAll);
        using CancellationTokenRegistration stopped = work?.Stopping.Register(stop.Cancel) ?? default;
        BackgroundWork.Begin();
        bool told = false;
        try
        {
            Words said = await RoundAsync(folder, work, stop.Token);
            Said = said;
            work?.Tell(true, said);
            told = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Put off for the person's work, or stopped: the changes stay for the next round.
            pending = IsOn;
            Said = Paused() is { } why ? TestingWords.ContinuousWaits(why) : TestingWords.ContinuousStopped;
        }
        catch (Exception failed) when (failed is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            Said = TestingWords.ContinuousCouldNotRun(Words.Verbatim(failed.Message));
            log(Said.English);
        }
        finally
        {
            if (!told)
            {
                work?.Tell(false, Said);
            }
            work?.Dispose();
            round = null;
            Rounds++;
            BackgroundWork.End();
        }
        if (!IsOn)
        {
            // Turned off as it ran: its cache goes now that nothing of the round uses it.
            DropCache();
        }
        // Paths a round gave back wait for the next save; only one heard while it ran begins another.
        if (IsOn && pending && Paused() is null)
        {
            waiting.Restart();
        }
    }

    /// <summary>One round, the files saved since the last taken for it: given back for the next where it ends before its map
    /// is kept.</summary>
    private async Task<Words> RoundAsync(string opened, Working? work, CancellationToken cancel)
    {
        string[] saved = [.. changed];
        bool every = everyFile;
        changed.Clear();
        everyFile = false;
        bool kept = false;
        try
        {
            (Words said, kept) = await RoundAsync(opened, saved, every, work, cancel);
            return said;
        }
        finally
        {
            if (!kept)
            {
                changed.UnionWith(saved);
                everyFile |= every;
            }
        }
    }

    /// <summary>A round: the tests built, copied, their changed modules instrumented, found, and those to run run; what it came
    /// to, and whether the map took it.</summary>
    private async Task<(Words Said, bool Kept)> RoundAsync(string opened, string[] saved, bool every, Working? work, CancellationToken cancel)
    {
        string given = Root ?? throw new InvalidOperationException("Continuous testing has no folder of its own");
        // Named as the disk names it, as MSBuild names what it builds there.
        string root = await BackgroundWork.Run(() =>
        {
            string made = PathNames.Canonical(Directory.CreateDirectory(given).FullName);
            Opened(made);
            return made;
        }, cancel);
        canonicalRoot = root;
        WorkspaceModel? model = workspace();
        List<ProjectModel> testProjects = [.. projects().Where(p => p is { Exists: true, IsTestProject: true, IsScriptGame: false, GodotFolder: null })];
        if (testProjects.Count == 0)
        {
            LastRan = [];
            return (TestingWords.ContinuousNoProjects, true);
        }
        string built = Path.Combine(root, "build");
        foreach (ProjectModel project in testProjects)
        {
            Checked(cancel);
            work?.Say(TestingWords.ContinuousBuilding(project.Name));
            ChildResult build = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["build", project.Path, "-nologo", "-nodeReuse:false", PropertySwitch.Of(MSBuildNames.UseArtifactsOutput, "true"), PropertySwitch.Of(MSBuildNames.ArtifactsPath, built),
                    .. model?.BuildArgumentsFor(project.Path) ?? []], opened), cancel: cancel);
            if (!build.Succeeded)
            {
                string why = string.Join("; ", build.Output.Split('\n').Where(l => l.Contains(": error ", StringComparison.Ordinal)).Select(l => l.Trim()).Distinct().Take(3));
                LastRan = [];
                return (TestingWords.ContinuousUnbuilt(project.Name, why.Length > 0 ? Words.Verbatim(why) : ExitCodes.Words(build.ExitCode)), false);
            }
        }
        Checked(cancel);
        work?.Say(TestingWords.ContinuousFindingBuilt);
        Dictionary<string, string> own = new(StringComparer.OrdinalIgnoreCase) { [MSBuildNames.UseArtifactsOutput] = "true", [MSBuildNames.ArtifactsPath] = built };
        IReadOnlyList<TestAssembly> assemblies = await TestAssemblies.ResolveAsync(testProjects, opened, model, log, cancel, own);
        List<TestAssembly> copies = [];
        foreach (TestAssembly assembly in assemblies)
        {
            Checked(cancel);
            if (!PathNames.IsUnder(assembly.Path, built))
            {
                log($"{assembly.Project.Name} builds where its project names, not in Passenger's own folder, so it is not tested continuously");
                continue;
            }
            string copy = Path.Combine(root, "run", $"{assembly.Project.Name}-{FileNames.Hash(assembly.Project.Path, 8)}", assembly.Framework);
            TestAssembly run = assembly with { Path = Path.Combine(copy, Path.GetFileName(assembly.Path)) };
            (string[] renewed, bool platformApplication) = await BackgroundWork.Run(() =>
            {
                List<string> made = Copy(Path.GetDirectoryName(assembly.Path)!, copy);
                // The modules copied anew with their symbols, of which Coverlet takes those whose sources are on the disk: the folder's own.
                string[] modules = [.. made.Where(m => m.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !PathNames.Same(m, run.Path) && File.Exists(Path.ChangeExtension(m, ".pdb")))
                    .Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct(StringComparer.Ordinal)];
                return (modules, TestingPlatformRunner.IsApplication(run.Path));
            }, cancel);
            if (!platformApplication)
            {
                vsTestCopies.Add(run.Path);
            }
            (InstrumentedCode Code, FileStamp? Found, IReadOnlyList<TestItem> Tests) known = runs.GetValueOrDefault(run.Path, (InstrumentedCode.None, null, []));
            if (renewed.Length > 0)
            {
                work?.Say(TestingWords.ContinuousInstrumenting(Plural.Listed(Words.Names(renewed)), assembly.Project.Name));
                InstrumentedCode? code = null;
                try
                {
                    code = await TestHitsRun.InstrumentAsync(run.Path, renewed, Path.Combine(root, "hits"), log, cancel);
                }
                finally
                {
                    if (code is null)
                    {
                        Uncopied(copy, renewed);
                    }
                }
                known = (known.Code.With(code ?? throw new InvalidOperationException($"{assembly.Project.Name}'s code could not be instrumented")), known.Found, known.Tests);
            }
            runs[run.Path] = known;
            copies.Add(run);
        }
        if (copies.Count == 0)
        {
            LastRan = [];
            return (TestingWords.ContinuousBuiltNone, true);
        }
        Checked(cancel);
        work?.Say(TestingWords.ContinuousFinding);
        List<TestAssembly> unfound = [.. copies.Where(c => runs[c.Path].Found != FileStamp.Of(c.Path))];
        foreach (bool onVsTest in (bool[])[false, true])
        {
            List<TestAssembly> its = [.. unfound.Where(c => vsTestCopies.Contains(c.Path) == onVsTest)];
            if (its.Count == 0)
            {
                continue;
            }
            ITestRunner runner = onVsTest ? vsTest ??= await VsTestRunner.StartAsync(opened, cancel) : platform;
            FoundTests found = await runner.DiscoverAsync(its, log, cancel);
            foreach (TestAssembly assembly in its)
            {
                // One its runner could not look through is asked again next round, its tests as last found meanwhile.
                bool through = !found.Unfound.Any(u => PathNames.Same(u.Assembly.Path, assembly.Path));
                runs[assembly.Path] = (runs[assembly.Path].Code, through ? FileStamp.Of(assembly.Path) : null,
                    through ? [.. found.Tests.Where(t => t.Project == assembly.Project.Path && t.Framework == assembly.Framework)] : runs[assembly.Path].Tests);
            }
        }
        foreach (IGrouping<string, TestAssembly> project in copies.GroupBy(c => c.Project.Path, StringComparer.Ordinal))
        {
            tests.FoundContinuously(project.Key, [.. project.Select(c => new TestSource(c.Framework, runs[c.Path].Tests))]);
        }
        Checked(cancel);
        // What changed reaches: the files saved, or every file mapped where the map is an earlier session's.
        IReadOnlyList<string> looked = every ? [.. Map.Files.Keys, .. saved] : saved;
        Dictionary<string, uint[]> now = await BackgroundWork.Run(() => Hashes(looked.Distinct(PathNames.Comparer)), cancel);
        HashSet<string> reached = [.. now.SelectMany(f => Map.Changed(f.Key, f.Value))];
        List<TestItem> chosen = [];
        foreach (TestAssembly copy in copies)
        {
            chosen.AddRange(runs[copy.Path].Tests.Where(t => !Map.Tests.ContainsKey(IdentityOf(t)) || reached.Contains(IdentityOf(t))
                || (t.Path is { } file && now.TryGetValue(file, out uint[]? its) && Map.Differs(file, its))));
        }
        if (chosen.Count == 0)
        {
            LastRan = [];
            await KeepAsync(new(StringComparer.Ordinal), [], now, InstrumentedCode.None, cancel);
            return (TestingWords.ContinuousNoneReached, true);
        }
        return await RunAsync(copies, chosen, now, work, cancel);
    }

    /// <summary>The tests chosen run one at a time over their copies, the tree showing them as they run, the map taking each
    /// once every run ended whole; whether it took them, a run broken off leaving the change for the next round.</summary>
    private async Task<(Words Said, bool Kept)> RunAsync(List<TestAssembly> copies, List<TestItem> chosen, Dictionary<string, uint[]> now, Working? work, CancellationToken cancel)
    {
        string? brokeOff = null;
        // Each test's lines, told on the run's own threads as each test ends.
        Dictionary<string, (Dictionary<string, HashSet<int>> Lines, string? File)> ran = new(StringComparer.Ordinal);
        List<TestOutcome> outcomes = [];
        HashSet<TestOutcome> applied = new(ReferenceEqualityComparer.Instance);
        void Apply(IReadOnlyList<TestOutcome> told) => tests.ApplyContinuous([.. told.Where(applied.Add)]);
        DateTimeOffset began = DateTimeOffset.Now;
        tests.BeginContinuous(chosen);
        int done = 0;
        try
        {
            foreach (TestAssembly copy in copies)
            {
                List<TestItem> its = [.. chosen.Where(t => runs[copy.Path].Tests.Contains(t))];
                if (its.Count == 0)
                {
                    continue;
                }
                Checked(cancel);
                InstrumentedCode code = runs[copy.Path].Code;
                void Ran(TestItem test, Dictionary<string, HashSet<int>> lines)
                {
                    lock (ran)
                    {
                        string identity = IdentityOf(test);
                        (Dictionary<string, HashSet<int>> Lines, string? File) was = ran.GetValueOrDefault(identity, (new(PathNames.Comparer), test.Path));
                        foreach ((string file, HashSet<int> numbers) in lines)
                        {
                            if (!was.Lines.TryGetValue(file, out HashSet<int>? have))
                            {
                                was.Lines[file] = have = [];
                            }
                            have.UnionWith(numbers);
                        }
                        ran[identity] = was;
                    }
                    UiThread.Post(() => work?.Say(TestingWords.ContinuousRanOf(++done, chosen.Count)));
                }
                RunSummary summary = vsTestCopies.Contains(copy.Path) && vsTest is { } vs
                    ? await TestHitsRun.RunVsTestAsync(vs, its, code, Path.Combine(Root!, "hits"), UiThread.Marshal<IReadOnlyList<TestOutcome>>(Apply),
                        // VSTest names the test that ended by its full name, which each of its rows shares.
                        (name, lines) =>
                        {
                            if ((its.FirstOrDefault(t => t.FullyQualifiedName == name) ?? its.FirstOrDefault(t => t.Method == TestItem.MethodOf(name))) is { } test)
                            {
                                Ran(test, lines);
                            }
                        }, UiThread.Marshal(log), cancel)
                    : await TestHitsRun.RunAsync(platform, its, code, new Dictionary<string, string>(), UiThread.Marshal<IReadOnlyList<TestOutcome>>(Apply), Ran,
                        UiThread.Marshal(log), cancel);
                // A runner may tell an outcome on another thread behind the run's end: every one is in before the end is.
                Apply(summary.Outcomes);
                outcomes.AddRange(summary.Outcomes);
                if (summary.Aborted)
                {
                    brokeOff = $"Continuous testing's run broke off: {summary.Error}";
                    log(brokeOff);
                }
                cancel.ThrowIfCancellationRequested();
            }
            if (brokeOff is not null)
            {
                return (brokeOff, false);
            }
            // Kept once every test has run again, so a round stopped partway leaves the map as it was for the next.
            Dictionary<string, (Dictionary<string, HashSet<int>> Lines, string? File)> told;
            lock (ran)
            {
                told = new(ran, StringComparer.Ordinal);
            }
            // A test that ran where the hook told nothing of it is mapped running nothing, so it is not run again each round.
            foreach (TestOutcome outcome in outcomes)
            {
                told.TryAdd(IdentityOf(outcome.Test), (new(PathNames.Comparer), outcome.Test.Path));
            }
            await KeepAsync(told, outcomes, now, copies.Aggregate(InstrumentedCode.None, (all, copy) => all.With(runs[copy.Path].Code)), cancel);
        }
        finally
        {
            tests.EndContinuous(chosen, began);
            LastRan = chosen;
        }
        return (TestingWords.ContinuousRan(chosen.Count, outcomes.Count(o => o.State == TestState.Passed), outcomes.Count(o => o.State == TestState.Failed)), true);
    }

    /// <summary>The tests run so far taken into the map, each with its worst outcome, and the files they ran or could run and
    /// those looked at as they read now, read off the window's thread.</summary>
    private async Task KeepAsync(Dictionary<string, (Dictionary<string, HashSet<int>> Lines, string? File)> ran, List<TestOutcome> outcomes, Dictionary<string, uint[]> now,
        InstrumentedCode code, CancellationToken cancel)
    {
        Dictionary<string, TestState> worst = new(StringComparer.Ordinal);
        foreach (TestOutcome outcome in outcomes)
        {
            string identity = IdentityOf(outcome.Test);
            worst[identity] = worst.TryGetValue(identity, out TestState had) && had == TestState.Failed ? had : outcome.State;
        }
        DateTimeOffset mapped = DateTimeOffset.Now;
        Dictionary<string, ReachedTest> taken = new(StringComparer.Ordinal);
        foreach ((string identity, (Dictionary<string, HashSet<int>> lines, string? file)) in ran)
        {
            taken[identity] = new ReachedTest
            {
                Mapped = mapped,
                State = worst.GetValueOrDefault(identity, TestState.NotRun),
                File = file,
                Lines = lines.ToDictionary(l => l.Key, l => Stretches.Of(l.Value), StringComparer.Ordinal),
            };
        }
        IReadOnlyDictionary<string, IReadOnlyList<int>> coverable = code.Coverable();
        string[] files = [.. taken.Values.SelectMany(t => t.Lines.Keys).Concat(taken.Values.Select(t => t.File).OfType<string>())
            .Concat(coverable.Keys.Where(f => !Map.Files.ContainsKey(f))).Where(f => !now.ContainsKey(f)).Distinct(PathNames.Comparer)];
        foreach ((string path, uint[] hashes) in await BackgroundWork.Run(() => Hashes(files), cancel))
        {
            now[path] = hashes;
        }
        Map = Map.With(taken, now, coverable);
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A test's method by the identity the map knows it by: its project's and its own full name, its rows and
    /// frameworks one.</summary>
    public static string IdentityOf(TestItem test) => $"{test.Project}::{test.Method}";

    /// <summary>A map's identity as its project and method.</summary>
    public static (string Project, string Method) Named(string identity)
    {
        int split = identity.LastIndexOf("::", StringComparison.Ordinal);
        return split < 0 ? (string.Empty, identity) : (identity[..split], identity[(split + 2)..]);
    }

    /// <summary>A build's output copied to its run's copy, each file whose stamp moved since it was copied; the files copied.
    /// A worker's.</summary>
    private List<string> Copy(string from, string to)
    {
        List<string> made = [];
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (FileStamp.Of(file) is not { } stamp || (copied.TryGetValue(target, out FileStamp was) && was == stamp && File.Exists(target)))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            lock (copied)
            {
                copied[target] = stamp;
            }
            made.Add(target);
        }
        return made;
    }

    /// <summary>Modules of a run's copy taken for never copied, so the next round copies them again and instruments them then:
    /// their instrumenting failed or was stopped.</summary>
    private void Uncopied(string copy, IReadOnlyList<string> modules)
    {
        lock (copied)
        {
            foreach (string target in copied.Keys.Where(t => PathNames.IsUnder(t, copy) && modules.Contains(Path.GetFileNameWithoutExtension(t), StringComparer.Ordinal)).ToList())
            {
                copied.Remove(target);
            }
        }
    }

    /// <summary>A hash of each line of each file as the disk has it, none for a file gone. A worker's.</summary>
    private static Dictionary<string, uint[]> Hashes(IEnumerable<string> files)
    {
        Dictionary<string, uint[]> read = new(PathNames.Comparer);
        foreach (string path in files)
        {
            try
            {
                read[path] = File.Exists(path) ? TestReachState.LinesOf(File.ReadAllText(path)) : [];
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                // Held by another program as it is read: its tests are looked at in the next round, which its write brings.
            }
        }
        return read;
    }

    /// <summary>Stops the round where the person's work needs the machine.</summary>
    private void Checked(CancellationToken cancel)
    {
        if (Paused() is not null)
        {
            round?.Cancel();
        }
        cancel.ThrowIfCancellationRequested();
    }
}

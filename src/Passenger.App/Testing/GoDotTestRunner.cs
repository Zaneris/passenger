using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>GoDotTest's tests run as GoDotTest runs them, inside the game: Godot on the game's project (the one its test
/// hosts' GODOT_BIN names, <see cref="GodotTestRuns"/>), from the scene that runs its tests (<paramref name="startOf"/>,
/// <see cref="GoDotTests.StartOf"/>; a game none does refused before Godot starts), given <c>--run-tests</c> for the chosen
/// suite or test and <c>--quit-on-finish</c>. Each test's end is told as its line is heard (its output, or under Debug its
/// debug session's), timed from its start; once Godot ends, its log of the run gives each outcome whole, a failure with its
/// exception and frames, and what it printed (<see cref="GoDotTests"/>), a test it ran beside them told too. Debugged, the
/// game starts under the debugger as Debug starts it. Its tests are those the code declares (<paramref name="declared"/>,
/// by the game's project).</summary>
public sealed class GoDotTestRunner(Func<string, IReadOnlyList<TestItem>> declared, Func<string, Task<GoDotTests.Start>>? startOf = null) : ITestRunner
{
    public bool IsUsable => true;

    /// <summary>GoDotTest's tests are found from the code, never from a build.</summary>
    public Task<IReadOnlyList<TestItem>> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyList<TestItem>>([]);

    /// <summary>Runs each game's tests in as few runs of it as GoDotTest takes them (<see cref="GoDotTests.Runs"/>), a game at
    /// a time, outcomes told as each run ends.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, GodotStart? godot = null, CancellationToken cancel = default)
    {
        RunSummary? summary = null;
        foreach (IGrouping<string, TestItem> project in tests.GroupBy(t => t.Project, StringComparer.Ordinal))
        {
            List<TestItem> chosen = [.. project];
            GoDotTests.Start start = startOf is null ? GoDotTests.Start.Main : await startOf(project.Key);
            if (start.Never)
            {
                string never = Refusals.GoDotTestStart(Path.GetFileNameWithoutExtension(project.Key), start) ?? string.Empty;
                log?.Invoke(never);
                RunSummary refused = new(TimeSpan.Zero, false, true, never);
                summary = summary is null ? refused : summary.And(refused);
                continue;
            }
            IReadOnlyList<TestItem> code = declared(project.Key);
            List<GoDotTests.Named> all = [.. code.Select(t => GoDotTests.Named.Of(t.Method))];
            foreach ((string argument, IReadOnlyList<GoDotTests.Named> runs) in GoDotTests.Runs([.. chosen.Select(t => GoDotTests.Named.Of(t.Method))], all))
            {
                HashSet<GoDotTests.Named> told = [.. runs];
                RunSummary part = await RunGameAsync(project.Key, start.Scene, argument, [.. chosen.Where(t => told.Contains(GoDotTests.Named.Of(t.Method)))], code, outcomes, log, debugger,
                    environment ?? new Dictionary<string, string>(), godot ?? GodotStart.Headless, cancel);
                summary = summary is null ? part : summary.And(part);
                if (part.Canceled)
                {
                    return summary;
                }
            }
        }
        return summary ?? new RunSummary(TimeSpan.Zero, cancel.IsCancellationRequested, false, null);
    }

    /// <summary>One run of a game from a scene (its main scene where none is named) with its <c>--run-tests</c> argument,
    /// telling these tests, and any other it ran as the test of <paramref name="code"/> it is, or one no row shows.</summary>
    private static async Task<RunSummary> RunGameAsync(string project, string? scene, string runTests, IReadOnlyList<TestItem> tests, IReadOnlyList<TestItem> code,
        Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log, ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string> environment, GodotStart start, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        string? godot = environment.GetValueOrDefault(GodotTestRuns.BinaryVariable) ?? Environment.GetEnvironmentVariable(GodotTestRuns.BinaryVariable);
        if (godot is not { Length: > 0 })
        {
            string none = $"GoDotTest's tests cannot run: {Run.GodotLaunch.NotFound}";
            log?.Invoke(none);
            return new RunSummary(TimeSpan.Zero, false, true, none);
        }
        if (Godot.GodotScripts.ProjectOf(project) is not { } folder)
        {
            string none = $"GoDotTest's tests cannot run: {PathNames.Shown(project, null)} is in no Godot project (no project.godot beside it or above)";
            log?.Invoke(none);
            return new RunSummary(TimeSpan.Zero, false, true, none);
        }
        string output = Path.Combine(Path.GetTempPath(), $"passenger-godottest-{Guid.NewGuid():N}.log");
        List<string> leading = [.. start.Leading, "--path", folder];
        List<string> arguments = [.. leading, .. scene is null ? Array.Empty<string>() : [GoDotTests.SceneFlag, scene], "--log-file", output, runTests, GoDotTests.QuitOnFinish];
        ILookup<string, TestItem> byKey = tests.ToLookup(t => GoDotTests.Named.Of(t.Method).Key, StringComparer.Ordinal);
        Telling telling = new(began, byKey, outcomes);
        string said;
        try
        {
            if (debugger is not null)
            {
                Task ended = await debugger.DebugGameAsync(new TestHostStart(godot, arguments, folder, environment), folder, leading.Count, csharp: true, telling.Heard);
                await ended.WaitAsync(cancel);
                said = "Godot ended";
            }
            else
            {
                ProcessStartInfo run = ChildProcesses.Command(godot, arguments, folder);
                foreach ((string name, string value) in environment)
                {
                    run.Environment[name] = value;
                }
                // Not held: read for as long as the game runs, which a wait for the IDE to be quiet would otherwise wait out whole.
                ChildRead<string> ran = await ChildProcesses.RunAsync(run, output => BackgroundWork.Unheld(() => HearAsync(output, telling, cancel)), cancel: cancel);
                said = $"Godot ended with exit code {ran.ExitCode}: {ChildResult.FirstLineOf(ran.Error.Length > 0 ? ran.Error : ran.Output)}";
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            GodotTestRuns.Delete(output, "GoDotTest's log");
            return new RunSummary(Stopwatch.GetElapsedTime(began), true, false, null);
        }
        IReadOnlyDictionary<string, GoDotTests.Result> results;
        try
        {
            results = await BackgroundWork.Run(() => File.Exists(output) ? GoDotTests.Read(File.ReadAllText(output)) : new Dictionary<string, GoDotTests.Result>());
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            string unsaid = $"GoDotTest's results could not be read: {unread.Message}";
            log?.Invoke(unsaid);
            return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, unsaid);
        }
        finally
        {
            GodotTestRuns.Delete(output, "GoDotTest's log");
        }
        if (results.Count == 0)
        {
            string none = $"GoDotTest ran no test: {said}. The game runs its tests where its first scene's script calls GoTest.RunTests for {GoDotTests.RunTests}";
            log?.Invoke(none);
            return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, none);
        }
        List<TestOutcome> told = [];
        foreach (TestItem test in tests)
        {
            string key = GoDotTests.Named.Of(test.Method).Key;
            if (results.TryGetValue(key, out GoDotTests.Result? result))
            {
                bool heard = telling.Took(key, out TimeSpan took);
                told.Add(Outcome(test, result.State, took) with
                {
                    Again = heard,
                    Message = Together(byKey[key], key) is { } together ? result.Message is { } own ? $"{own}\n{together}" : together : result.Message,
                    StackTrace = result.StackTrace,
                    Output = result.Output,
                });
            }
        }
        List<TestOutcome> beside = [.. results.Where(r => !byKey.Contains(r.Key)).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => Beside(project, code, r.Key, r.Value))];
        told.AddRange(beside);
        outcomes(told);
        if (GodotTestRuns.RanBeside(GoDotTests.Framework, beside) is { } alsoRan)
        {
            log?.Invoke(alsoRan);
        }
        return new RunSummary(Stopwatch.GetElapsedTime(began), false, false, null) { Outcomes = told };
    }

    /// <summary>The outcome of a test a run told that none chosen is: the test of the code it is, else one of its suite's and
    /// method's names, which no row shows.</summary>
    private static TestOutcome Beside(string project, IReadOnlyList<TestItem> code, string key, GoDotTests.Result result)
    {
        TestItem test = code.FirstOrDefault(t => GoDotTests.Named.Of(t.Method).Key == key)
            ?? new TestItem(project, key.Replace("::", ".", StringComparison.Ordinal), key[(key.LastIndexOf(':') + 1)..]) { Framework = GoDotTests.Framework };
        return Outcome(test, result.State, TimeSpan.Zero) with { Message = result.Message, StackTrace = result.StackTrace, Output = result.Output };
    }

    /// <summary>The game's output heard to its end, each line told (<see cref="Telling"/>); what it printed is read whole
    /// from its log at the end.</summary>
    private static async Task<string> HearAsync(Stream output, Telling telling, CancellationToken cancel)
    {
        using StreamReader reader = new(output, leaveOpen: true);
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            telling.Heard(line);
        }
        return string.Empty;
    }

    /// <summary>A run's lines as the game writes them, its output's or its debug session's: each test's end told at once,
    /// timed from its start (<see cref="GoDotTests.Progress"/>), and kept for the outcomes its log gives at the end.</summary>
    private sealed class Telling(long began, ILookup<string, TestItem> byKey, Action<IReadOnlyList<TestOutcome>> outcomes)
    {
        private readonly GoDotTests.Progress progress = new();
        private readonly Dictionary<string, TimeSpan> took = new(StringComparer.Ordinal);
        private readonly Lock gate = new();

        public void Heard(string line)
        {
            lock (gate)
            {
                if (progress.Heard(line, Stopwatch.GetElapsedTime(began)) is ({ } key, GoDotTests.Ending state, TimeSpan time) && byKey.Contains(key))
                {
                    took[key] = time;
                    outcomes([.. byKey[key].Select(t => Outcome(t, state, time))]);
                }
            }
        }

        /// <summary>Whether a test's end was heard and told, and its time.</summary>
        public bool Took(string key, out TimeSpan time)
        {
            lock (gate)
            {
                return took.TryGetValue(key, out time);
            }
        }
    }

    /// <summary>A test's outcome as GoDotTest ended it.</summary>
    private static TestOutcome Outcome(TestItem test, GoDotTests.Ending state, TimeSpan took) => new(test, state switch
    {
        GoDotTests.Ending.Passed => TestState.Passed,
        GoDotTests.Ending.Failed => TestState.Failed,
        _ => TestState.Skipped,
    }, took)
    {
        DisplayName = test.DisplayName,
    };

    /// <summary>Why one result stands for several tests: GoDotTest names a suite by its class's own name, so classes of one
    /// name in different namespaces run together and are told as one; null for a name only one test has.</summary>
    private static string? Together(IEnumerable<TestItem> named, string key) =>
        named.Select(t => t.Method[..t.Method.LastIndexOf('.')]).Distinct(StringComparer.Ordinal).ToList() is { Count: > 1 } classes
            ? $"GoDotTest names {Plural.Listed(classes)} alike ({key.Split("::")[0]}), so it ran them together and told one result for {key}"
            : null;

    public void Dispose()
    {
    }
}

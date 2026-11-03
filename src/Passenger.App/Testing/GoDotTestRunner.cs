using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>GoDotTest's tests run as GoDotTest runs them, inside the game: Godot on the game's project (the one its test
/// hosts' GODOT_BIN names, <see cref="GodotTestRuns"/>), given <c>--run-tests</c> for the chosen suite or test and
/// <c>--quit-on-finish</c>, its output kept in a log file of the run's, from which each test's outcome is read back, a
/// failure with its exception and frames, and what it printed as its output (<see cref="GoDotTests"/>). Debugged, the game
/// starts under the debugger as Debug starts it. Its tests are those the code declares (<paramref name="declared"/>, by
/// the game's project), its build the run's.</summary>
public sealed class GoDotTestRunner(Func<string, IReadOnlyList<TestItem>> declared) : ITestRunner
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
            List<GoDotTests.Named> all = [.. declared(project.Key).Select(t => GoDotTests.Named.Of(t.Method))];
            foreach ((string argument, IReadOnlyList<GoDotTests.Named> runs) in GoDotTests.Runs([.. chosen.Select(t => GoDotTests.Named.Of(t.Method))], all))
            {
                HashSet<GoDotTests.Named> told = [.. runs];
                RunSummary part = await RunGameAsync(project.Key, argument, [.. chosen.Where(t => told.Contains(GoDotTests.Named.Of(t.Method)))], outcomes, log, debugger,
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

    /// <summary>One run of a game with its <c>--run-tests</c> argument, telling these tests.</summary>
    private static async Task<RunSummary> RunGameAsync(string project, string runTests, IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes,
        Action<string>? log, ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string> environment, GodotStart start, CancellationToken cancel)
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
        List<string> arguments = [.. leading, "--log-file", output, runTests, GoDotTests.QuitOnFinish];
        string said;
        try
        {
            if (debugger is not null)
            {
                Task ended = await debugger.DebugGameAsync(new TestHostStart(godot, arguments, folder, environment), folder, leading.Count, csharp: true);
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
                ChildResult ran = await ChildProcesses.RunAsync(run, cancel: cancel);
                said = $"Godot ended with exit code {ran.ExitCode}: {ChildResult.FirstLineOf(ran.Error.Length > 0 ? ran.Error : ran.Output)}";
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Delete(output);
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
            Delete(output);
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
            if (results.TryGetValue(GoDotTests.Named.Of(test.Method).Key, out GoDotTests.Result? result))
            {
                told.Add(new TestOutcome(test, result.State switch
                {
                    GoDotTests.Ending.Passed => TestState.Passed,
                    GoDotTests.Ending.Failed => TestState.Failed,
                    _ => TestState.Skipped,
                }, TimeSpan.Zero)
                {
                    DisplayName = test.DisplayName,
                    Message = result.Message,
                    StackTrace = result.StackTrace,
                    Output = result.Output,
                });
            }
        }
        outcomes(told);
        return new RunSummary(Stopwatch.GetElapsedTime(began), false, false, null) { Outcomes = told };
    }

    /// <summary>A run's log let go of; one Godot still holds (Windows) is left for the system's temporary folder.</summary>
    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Unit tests: GoDotTest's log {path} is left: {kept.Message}");
        }
    }

    public void Dispose()
    {
    }
}

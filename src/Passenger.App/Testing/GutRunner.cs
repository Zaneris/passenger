using System.Diagnostics;
using System.Xml;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>GUT's tests run as GUT's command line runs them: headless Godot on the project (the one its test hosts' GODOT_BIN
/// names, <see cref="GodotTestRuns"/>), the chosen scripts given, GUT's JUnit XML read back for each test's outcome, its
/// failure's line linked, and what it printed under each test as its output; a project whose global classes Godot has yet
/// to import (GutTest among them) imported first, headless. Debugged, the run starts under the GDScript debugger instead,
/// its breakpoints set from the run's start. Its tests are those the code declares (<see cref="GutScripts"/>).</summary>
public sealed class GutRunner : ITestRunner
{
    public bool IsUsable => true;

    /// <summary>GUT's tests are found from the code, never from a build.</summary>
    public Task<IReadOnlyList<TestItem>> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default) =>
        Task.FromResult<IReadOnlyList<TestItem>>([]);

    /// <summary>Runs each project's tests in one run of GUT, a project at a time, outcomes told as each run ends.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default)
    {
        RunSummary? summary = null;
        foreach (IGrouping<string, TestItem> project in tests.GroupBy(t => t.Project, StringComparer.Ordinal))
        {
            RunSummary part = await RunProjectAsync(Path.GetDirectoryName(project.Key)!, [.. project], outcomes, log, debugger, environment ?? new Dictionary<string, string>(), cancel);
            summary = summary is null ? part : summary.And(part);
            if (part.Canceled)
            {
                break;
            }
        }
        return summary ?? new RunSummary(TimeSpan.Zero, cancel.IsCancellationRequested, false, null);
    }

    private static async Task<RunSummary> RunProjectAsync(string folder, IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log,
        ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string> environment, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        string? godot = environment.GetValueOrDefault(GodotTestRuns.BinaryVariable) ?? Environment.GetEnvironmentVariable(GodotTestRuns.BinaryVariable);
        if (godot is not { Length: > 0 })
        {
            string none = $"GUT's tests cannot run: {Run.GodotLaunch.NotFound}";
            log?.Invoke(none);
            return new RunSummary(TimeSpan.Zero, false, true, none);
        }
        if (await BackgroundWork.Run(() => NeedsImport(folder), cancel) && await ImportAsync(godot, folder, environment, log, cancel) is { } unimported)
        {
            return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, unimported);
        }
        string results = Path.Combine(Path.GetTempPath(), $"passenger-gut-{Guid.NewGuid():N}.xml");
        List<string> arguments = ["--headless", "--path", folder, .. Arguments(folder, tests, results)];
        string output = string.Empty;
        try
        {
            if (debugger is not null)
            {
                Task ended = await debugger.DebugScriptsAsync(new TestHostStart(godot, arguments, folder, environment), folder, Leading);
                await ended.WaitAsync(cancel);
                if (!File.Exists(results))
                {
                    const string unsaid = "GUT wrote no results: its Godot ended before it told them (the Debug window has what it wrote)";
                    log?.Invoke(unsaid);
                    return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, unsaid);
                }
            }
            else
            {
                ProcessStartInfo start = ChildProcesses.Command(godot, arguments, folder);
                foreach ((string name, string value) in environment)
                {
                    start.Environment[name] = value;
                }
                ChildResult ran = await ChildProcesses.RunAsync(start, cancel: cancel);
                output = ran.Output;
                if (!File.Exists(results))
                {
                    string said = $"GUT wrote no results (Godot ended with exit code {ran.ExitCode}): {ChildResult.FirstLineOf(ran.Error.Length > 0 ? ran.Error : ran.Output)}";
                    log?.Invoke(said);
                    return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, said);
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new RunSummary(Stopwatch.GetElapsedTime(began), true, false, null);
        }
        IReadOnlyList<TestOutcome> told;
        try
        {
            told = await BackgroundWork.Run(() => Outcomes(File.ReadAllText(results), folder, tests, output));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or XmlException)
        {
            string said = $"GUT's results could not be read: {unread.Message}";
            log?.Invoke(said);
            return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, said);
        }
        finally
        {
            File.Delete(results);
        }
        outcomes(told);
        return new RunSummary(Stopwatch.GetElapsedTime(began), false, false, null) { Outcomes = told };
    }

    /// <summary>How many of a run's arguments come before GUT's script: the debugger's go there.</summary>
    private const int Leading = 3;

    /// <summary>A test's script, inner class and function.</summary>
    private static GutRuns.Chosen Parts(string folder, TestItem test)
    {
        int dot = test.Method.LastIndexOf('.');
        (string script, string? inner) = GutScripts.ScriptOf(folder, test.Method[..dot], test.Path);
        return new GutRuns.Chosen(GutScripts.ResourceOf(folder, script), inner, test.Method[(dot + 1)..]);
    }

    /// <summary>GUT's command line for these tests of a project (<see cref="GutRuns.Arguments"/>).</summary>
    private static IReadOnlyList<string> Arguments(string folder, IReadOnlyList<TestItem> tests, string results) =>
        GutRuns.Arguments([.. tests.Select(t => Parts(folder, t))], results);

    /// <summary>Each test's outcome as GUT's results and output give it (<see cref="GutRuns.Read"/>), a failure's line told
    /// as a frame of the test's file, which the window links; a test GUT told nothing of left out.</summary>
    private static IReadOnlyList<TestOutcome> Outcomes(string xml, string folder, IReadOnlyList<TestItem> tests, string output)
    {
        IReadOnlyDictionary<string, GutRuns.Result> results = GutRuns.Read(xml, output);
        List<TestOutcome> told = [];
        foreach (TestItem test in tests)
        {
            GutRuns.Chosen chosen = Parts(folder, test);
            if (!results.TryGetValue(chosen.Key, out GutRuns.Result? result))
            {
                continue;
            }
            string script = GutScripts.ScriptOf(folder, test.Method[..test.Method.LastIndexOf('.')], test.Path).Path;
            told.Add(new TestOutcome(test, result.Status switch
            {
                "pass" or "no asserts" => TestState.Passed,
                "fail" => TestState.Failed,
                "pending" or "skipped" => TestState.Skipped,
                _ => TestState.NotRun,
            }, result.Time)
            {
                DisplayName = chosen.Method,
                Message = result.Message,
                StackTrace = result is { Status: "fail", Line: > 0 } ? $"   at {test.Method}() in {script}:line {result.Line}" : null,
                Output = result.Output,
            });
        }
        return told;
    }

    /// <summary>Whether Godot has yet to import the project's global classes as they now are: its class cache missing, or
    /// older than a GDScript file or project.godot. A worker's.</summary>
    private static bool NeedsImport(string folder)
    {
        string cache = Path.Combine(folder, ".godot", "global_script_class_cache.cfg");
        if (!File.Exists(cache))
        {
            return true;
        }
        DateTime imported = File.GetLastWriteTimeUtc(cache);
        return File.GetLastWriteTimeUtc(Path.Combine(folder, "project.godot")) > imported
            || GutScripts.Files(folder).Any(path => File.GetLastWriteTimeUtc(path) > imported);
    }

    /// <summary>Godot's headless import of the project, what it writes over of the person's editor put back; null once
    /// done, else why it failed.</summary>
    private static async Task<string?> ImportAsync(string godot, string folder, IReadOnlyDictionary<string, string> environment, Action<string>? log, CancellationToken cancel)
    {
        ProcessStartInfo start = ChildProcesses.Command(godot, ["--headless", "--path", folder, "--import"], folder);
        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }
        ChildResult imported = await Godot.GodotScripts.PuttingBackAsync(folder, () => ChildProcesses.RunAsync(start, cancel: cancel));
        if (imported.Succeeded)
        {
            return null;
        }
        string said = $"Godot could not import {PathNames.Shown(folder, null)} for GUT (exit code {imported.ExitCode}): {ChildResult.FirstLineOf(imported.Error.Length > 0 ? imported.Error : imported.Output)}";
        log?.Invoke(said);
        return said;
    }

    public void Dispose()
    {
    }
}

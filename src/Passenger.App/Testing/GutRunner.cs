using System.Diagnostics;
using System.Text;
using System.Xml;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>GUT's tests run as GUT's command line runs them: Godot on the project (the one its test hosts' GODOT_BIN names,
/// <see cref="GodotTestRuns"/>), with the arguments Settings gives each start of it, headless unless the person asks for its
/// window (<see cref="GodotStart"/>), the chosen scripts given, a pre-run hook leaving them the chosen tests alone where those
/// are not whole scripts (<see cref="GutRuns.Hook"/>), GUT's JUnit XML read back for each test's outcome, its failure's line
/// linked, and what it printed under each test as its output, a test it ran beside them told too; the project imported first
/// where Godot has yet to (GutTest is a global class), headless (<see cref="GodotTestRuns.RunAsync"/>). Debugged, the run
/// starts under the GDScript debugger, its breakpoints set from the run's start, and in a C# game under .NET's too, so the C#
/// a test calls stops at its breakpoints. Its tests are those the code declares (<paramref name="declared"/>, by the
/// project, <see cref="GutScripts"/>).</summary>
public sealed class GutRunner(Func<string, IReadOnlyList<TestItem>>? declared = null) : ITestRunner
{
    public bool IsUsable => true;

    /// <summary>GUT's tests are found from the code, never from a build.</summary>
    public Task<FoundTests> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default) =>
        Task.FromResult(FoundTests.All([]));

    /// <summary>Runs each project's tests in one run of GUT, a project at a time, outcomes told as each run ends.</summary>
    public async Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, GodotStart? godot = null, CancellationToken cancel = default)
    {
        RunSummary? summary = null;
        foreach (IGrouping<string, TestItem> project in tests.GroupBy(t => t.Project, StringComparer.Ordinal))
        {
            RunSummary part = await RunProjectAsync(project.Key, [.. project], declared?.Invoke(project.Key) ?? [], outcomes, log, debugger, environment ?? new Dictionary<string, string>(),
                godot ?? GodotStart.Headless, cancel);
            summary = summary is null ? part : summary.And(part);
            if (part.Canceled)
            {
                break;
            }
        }
        return summary ?? new RunSummary(TimeSpan.Zero, cancel.IsCancellationRequested, false, null);
    }

    private static async Task<RunSummary> RunProjectAsync(string project, IReadOnlyList<TestItem> tests, IReadOnlyList<TestItem> declared, Action<IReadOnlyList<TestOutcome>> outcomes,
        Action<string>? log, ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string> environment, GodotStart start, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        string folder = Path.GetDirectoryName(project)!;
        string? hook = await BackgroundWork.Run(() => WriteHook(folder, tests, declared), cancel);
        try
        {
            GodotTestRuns.Asked asked = new(GutScripts.Framework, folder, ".xml", results => GutRuns.Arguments([.. tests.Select(t => Parts(folder, t))], results, hook))
            {
                CSharp = debugger is not null && await BackgroundWork.Run(() => Directory.EnumerateFiles(folder, "*.csproj").Any()),
                KeepsOutput = true,
            };
            return await GodotTestRuns.RunAsync(asked, ended => ToldAsync(ended, project, folder, tests, began, outcomes, log), began, log, debugger, environment, start, cancel);
        }
        finally
        {
            if (hook is not null)
            {
                GodotTestRuns.Delete(hook, "GUT's pre-run hook");
            }
        }
    }

    /// <summary>What GUT's run of these tests of a project came to, by the results it wrote.</summary>
    private static async Task<RunSummary> ToldAsync(GodotTestRuns.Ended ended, string project, string folder, IReadOnlyList<TestItem> tests, long began,
        Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log)
    {
        if (!File.Exists(ended.Written))
        {
            string unsaid = ended.Debugged ? "GUT wrote no results: its Godot ended before it told them (the Debug window has what it wrote)" : $"GUT wrote no results: {ended.Said}";
            log?.Invoke(unsaid);
            return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, unsaid);
        }
        IReadOnlyList<TestOutcome> told;
        try
        {
            told = await BackgroundWork.Run(() => Outcomes(File.ReadAllText(ended.Written), project, folder, tests, ended.Output));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or XmlException)
        {
            string said = $"GUT's results could not be read: {unread.Message}";
            log?.Invoke(said);
            return new RunSummary(Stopwatch.GetElapsedTime(began), false, true, said);
        }
        outcomes(told);
        HashSet<TestItem> asked = new(tests, TestItem.ById);
        if (GodotTestRuns.RanBeside(GutScripts.Framework, [.. told.Where(o => !asked.Contains(o.Test))]) is { } beside)
        {
            log?.Invoke(beside);
        }
        return new RunSummary(Stopwatch.GetElapsedTime(began), false, false, null) { Outcomes = told };
    }

    /// <summary>A test's script, inner class and function.</summary>
    private static GutRuns.Chosen Parts(string folder, TestItem test)
    {
        int dot = test.Method.LastIndexOf('.');
        (string script, string? inner) = GutScripts.ScriptOf(folder, test.Method[..dot], test.Path);
        return new GutRuns.Chosen(GodotResPaths.Of(folder, script) ?? script, inner, test.Method[(dot + 1)..]);
    }

    /// <summary>The pre-run hook that keeps these tests of a project (<see cref="GutRuns.Hook"/>), written where the system
    /// keeps temporary files; null where they are whole scripts, which need none. A worker's: it reads and writes the disk.</summary>
    private static string? WriteHook(string folder, IReadOnlyList<TestItem> tests, IReadOnlyList<TestItem> declared)
    {
        if (GutRuns.Kept([.. tests.Select(t => Parts(folder, t))], [.. declared.Select(t => Parts(folder, t))]) is not { } kept)
        {
            return null;
        }
        string config = Path.Combine(folder, GutScripts.ConfigFile);
        string hook = GodotTestRuns.TemporaryFile(GutScripts.Framework, ".gd");
        AtomicFile.Write(hook, Encoding.UTF8.GetBytes(GutRuns.Hook(kept, File.Exists(config) ? GutScripts.PreRunScript(File.ReadAllText(config)) : null)));
        return hook;
    }

    /// <summary>Each test's outcome as GUT's results and output give it (<see cref="GutRuns.Read"/>), a failure's line told
    /// as a frame of the file declaring the test, which the window links; a chosen test GUT told nothing of left out, and every
    /// other test it told added, whose result a run owes the person whether or not a row shows it.</summary>
    public static IReadOnlyList<TestOutcome> Outcomes(string xml, string project, string folder, IReadOnlyList<TestItem> tests, string output)
    {
        IReadOnlyDictionary<string, GutRuns.Result> results = GutRuns.Read(xml, output);
        List<TestOutcome> told = [];
        HashSet<string> chosen = new(StringComparer.Ordinal);
        foreach (TestItem test in tests)
        {
            GutRuns.Chosen parts = Parts(folder, test);
            chosen.Add(parts.Key);
            if (results.TryGetValue(parts.Key, out GutRuns.Result? result))
            {
                told.Add(Outcome(test, parts, result, test.DeclaredIn ?? GutScripts.ScriptOf(folder, test.Method[..test.Method.LastIndexOf('.')], test.Path).Path));
            }
        }
        foreach ((string key, GutRuns.Result result) in results.Where(r => !chosen.Contains(r.Key)).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            GutRuns.Chosen parts = GutRuns.Chosen.Of(key);
            if (GodotResPaths.FileOf(folder, parts.Script) is not { } script)
            {
                continue;
            }
            string type = GutScripts.TypeOf(folder, script) + (parts.Inner is { } inner ? $"+{inner}" : string.Empty);
            told.Add(Outcome(new TestItem(project, $"{type}.{parts.Method}", parts.Method) { Framework = GutScripts.Framework, Path = script, Line = result.Line }, parts, result, script));
        }
        return told;
    }

    /// <summary>A test's outcome from its result.</summary>
    private static TestOutcome Outcome(TestItem test, GutRuns.Chosen parts, GutRuns.Result result, string script) => new(test, result.Status switch
    {
        "pass" or "no asserts" => TestState.Passed,
        "fail" => TestState.Failed,
        "pending" or "skipped" => TestState.Skipped,
        _ => TestState.NotRun,
    }, result.Time)
    {
        DisplayName = parts.Method,
        Message = result.Message,
        StackTrace = result is { Status: "fail", Line: > 0 } ? $"   at {test.Method}() in {script}:line {result.Line}" : null,
        Output = result.Output,
    };

    public void Dispose()
    {
    }
}

using System.Diagnostics;
using System.Text;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>What a Godot project's tests run with: GdUnit4's adapter starts the tests that need the engine on the Godot its
/// test host's GODOT_BIN names, which is the one Passenger runs the game on unless the person names their own (in the IDE's
/// environment, a launch profile or the run settings), and Godot keeps its user folders where a test gives them. Every
/// framework's Godot takes the arguments Settings gives each start of it (<see cref="GodotStart"/>), and GUT's and GoDotTest's
/// runs go one way (<see cref="RunAsync"/>).</summary>
public static class GodotTestRuns
{
    /// <summary>The variable GdUnit4's adapter takes Godot's executable from.</summary>
    public const string BinaryVariable = "GODOT_BIN";

    /// <summary>What a Godot project's test hosts are given: the variables, the user folders a test gives Godot among them,
    /// what a headless Godot takes where its tests run headless, and GODOT_BIN naming any Godot 4 found, which runs GUT's
    /// GDScript; why none would run C# where it would not; and how each Godot a run starts is started.</summary>
    public sealed record Given(IReadOnlyDictionary<string, string> Variables, string? NoCSharp, GodotStart Start);

    /// <summary>What a Godot project's test hosts are given (<see cref="Given"/>), the tests GUT and GoDotTest run in a game
    /// shown in its window only where <paramref name="windowed"/> says. A worker's: finding Godot reads the disk and the
    /// login shell's PATH.</summary>
    public static Given Variables(string? setting, IReadOnlyDictionary<string, string> userFolders, bool windowed = false)
    {
        GodotStart start = new(GodotLaunch.Chosen(setting)?.Arguments ?? [], windowed);
        Dictionary<string, string> variables = new(GodotExecutables.EnvironmentOf(start.Leading, userFolders), StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable(BinaryVariable) is { Length: > 0 })
        {
            return new Given(variables, null, start);
        }
        GodotFound? found = GodotLaunch.Found(setting);
        if (found is { WhyNotScripts: null })
        {
            variables[BinaryVariable] = found.Executable;
        }
        return new Given(variables, found is { WhyNot: null } ? null : $"GdUnit4's tests that need Godot's engine cannot run: {found?.WhyNot ?? GodotLaunch.NotFound}", start);
    }

    /// <summary>What a framework asks of a run of its tests in Godot: its name, the Godot project's folder, the extension of the
    /// file it has Godot write (its results, its log), and its arguments after the project's, given that file's path; whether
    /// the C# its tests call is debugged too, each line Godot writes handed to <see cref="Heard"/> as it comes (under Debug, as
    /// its debug session hears it), and whether its output is kept whole for <see cref="Ended.Output"/>.</summary>
    public sealed record Asked(string Framework, string Folder, string Extension, Func<string, IReadOnlyList<string>> Arguments)
    {
        public bool CSharp { get; init; }

        public Action<string>? Heard { get; init; }

        public bool KeepsOutput { get; init; }
    }

    /// <summary>How a run's Godot ended: the file it was to write, its output where kept, whether it ran under the debugger, and
    /// what it said as it ended.</summary>
    public sealed record Ended(string Written, string Output, bool Debugged, string Said);

    /// <summary>A framework's run of tests in Godot (GUT's, GoDotTest's): on the Godot GODOT_BIN names (the test host's, else
    /// the IDE's own), the project imported first where it needs it (<see cref="GodotScripts.ImportIfNeededAsync"/>), Godot
    /// started on the project's folder with the arguments Settings gives it, headless unless its window is asked for
    /// (<see cref="GodotStart"/>), and the framework's, under the debugger where <paramref name="debugger"/> is given; its
    /// outcome what <paramref name="told"/> makes of its end, the file it wrote let go of after. Timed from <paramref name="began"/>.</summary>
    public static async Task<RunSummary> RunAsync(Asked asked, Func<Ended, Task<RunSummary>> told, long began, Action<string>? log, ITestDebugLauncher? debugger,
        IReadOnlyDictionary<string, string> environment, GodotStart start, CancellationToken cancel)
    {
        if ((environment.GetValueOrDefault(BinaryVariable) ?? Environment.GetEnvironmentVariable(BinaryVariable)) is not { Length: > 0 } godot)
        {
            return Refused($"{asked.Framework}'s tests cannot run: {GodotLaunch.NotFound}", TimeSpan.Zero, log);
        }
        string written = TemporaryFile(asked.Framework, asked.Extension);
        try
        {
            Ended ended;
            try
            {
                if (await GodotScripts.ImportIfNeededAsync(godot, start.Arguments, asked.Folder, environment, cancel) is { } unimported)
                {
                    return Refused($"{asked.Framework}'s tests cannot run: {unimported}", Stopwatch.GetElapsedTime(began), log);
                }
                ended = await RunGodotAsync(godot, asked, written, debugger, environment, start, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return new RunSummary(Stopwatch.GetElapsedTime(began), true, false, null);
            }
            return await told(ended);
        }
        finally
        {
            Delete(written, $"{asked.Framework}'s results");
        }
    }

    /// <summary>Godot run, or debugged, to its end on the framework's arguments.</summary>
    private static async Task<Ended> RunGodotAsync(string godot, Asked asked, string written, ITestDebugLauncher? debugger, IReadOnlyDictionary<string, string> environment,
        GodotStart start, CancellationToken cancel)
    {
        List<string> leading = [.. start.Leading, "--path", asked.Folder];
        List<string> arguments = [.. leading, .. asked.Arguments(written)];
        if (debugger is not null)
        {
            Task ended = await debugger.DebugGameAsync(new TestHostStart(godot, arguments, asked.Folder, environment), asked.Folder, leading.Count, asked.CSharp, asked.Heard);
            await ended.WaitAsync(cancel);
            return new Ended(written, string.Empty, true, "Godot ended");
        }
        ProcessStartInfo run = ChildProcesses.Command(godot, arguments, asked.Folder);
        foreach ((string name, string value) in environment)
        {
            run.Environment[name] = value;
        }
        // Not held: read for as long as the game runs, which a wait for the IDE to be quiet would otherwise wait out whole.
        ChildRead<string> ran = await ChildProcesses.RunAsync(run, output => BackgroundWork.Unheld(() => HearAsync(output, asked, cancel)), cancel: cancel);
        return new Ended(written, ran.Output, false, $"Godot ended with {new ChildResult(ran.ExitCode, ran.Output, ran.Error).Ended}");
    }

    /// <summary>Godot's output heard to its end, each line handed on, and kept whole where the framework asks.</summary>
    private static async Task<string> HearAsync(Stream output, Asked asked, CancellationToken cancel)
    {
        using StreamReader reader = new(output, leaveOpen: true);
        StringBuilder? kept = asked.KeepsOutput ? new() : null;
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            asked.Heard?.Invoke(line);
            kept?.Append(line).Append('\n');
        }
        return kept?.ToString() ?? string.Empty;
    }

    /// <summary>A run that cannot go on, saying why.</summary>
    private static RunSummary Refused(string why, TimeSpan took, Action<string>? log)
    {
        log?.Invoke(why);
        return new RunSummary(took, false, true, why);
    }

    /// <summary>A file of a framework's run, named for it, where the system keeps temporary files.</summary>
    public static string TemporaryFile(string framework, string extension) =>
        Path.Combine(Path.GetTempPath(), $"passenger-{framework.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}");

    /// <summary>What a run says of the tests it ran beside the chosen ones, each failure with its words: a framework's run of a
    /// script or suite takes what the code does not declare, which no row shows (a test a script inherits); null for none.</summary>
    public static string? RanBeside(string framework, IReadOnlyList<TestOutcome> others)
    {
        if (others.Count == 0)
        {
            return null;
        }
        IEnumerable<string> failed = others.Where(o => o.State == TestState.Failed).Select(o => $"\n  {o.Test.FullyQualifiedName} failed{(o.Message is { } why ? $": {why}" : string.Empty)}");
        return $"{framework} also ran {Plural.Of(others.Count, "test")} no row shows, {others.Count(o => o.State == TestState.Failed)} failed{string.Concat(failed)}";
    }

    /// <summary>A file a run wrote let go of; one Godot still holds (Windows) is left for the system's temporary folder.</summary>
    public static void Delete(string path, string what)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Unit tests: {what} {path} is left: {kept.Message}");
        }
    }

    /// <summary>A test project's environment with the Godot variables it does not set itself.</summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> godot)
    {
        if (godot.Keys.All(environment.ContainsKey))
        {
            return environment;
        }
        Dictionary<string, string> merged = new(environment, StringComparer.Ordinal);
        foreach ((string name, string value) in godot)
        {
            merged.TryAdd(name, value);
        }
        return merged;
    }
}

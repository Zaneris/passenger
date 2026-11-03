using System.Text.RegularExpressions;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>GoDotTest's tests as a Godot C# game runs them: its game, started with <c>--run-tests</c>, runs them inside
/// itself and says how each went in Godot's output. A game carries GoDotTest where its project references its package; its
/// tests are the methods its code marks [Test], each class a suite named by its own name. The runs that take the chosen
/// tests, and each test's result read back from what GoDotTest printed, its colours none.</summary>
public static partial class GoDotTests
{
    /// <summary>The test framework a GoDotTest test is told as having been found by, which <see cref="GoDotTestRunner"/> runs.</summary>
    public const string Framework = "GoDotTest";

    /// <summary>GoDotTest's package, which a game references to run its tests.</summary>
    public const string Package = "Chickensoft.GoDotTest";

    /// <summary>Whether a project is a Godot C# game carrying GoDotTest.</summary>
    public static bool Carried(ProjectModel project) =>
        project is { IsScriptGame: false, GodotFolder: not null } && project.Packages.Any(p => p.Name.Equals(Package, StringComparison.OrdinalIgnoreCase));

    /// <summary>A test as GoDotTest names it: its class's own name (its suite) and its method's.</summary>
    public sealed record Named(string Suite, string Method)
    {
        /// <summary>A test's names from its full name ("Game.Tests.SpinnerTest.Spins", a nested class after a '+').</summary>
        public static Named Of(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            string type = fullName[..dot];
            return new Named(type[(type.LastIndexOfAny(['.', '+']) + 1)..], fullName[(dot + 1)..]);
        }

        /// <summary>How GoDotTest's output names it ("SpinnerTest::Spins").</summary>
        public string Key => $"{Suite}::{Method}";
    }

    /// <summary>The runs of a game that take the chosen tests of the <paramref name="declared"/> ones: one of every suite
    /// where all are chosen, else one a suite, the suite's one test where only one of it is chosen (GoDotTest takes one
    /// suite or test a run). Each with the <c>--run-tests</c> it is given and the chosen tests it tells.</summary>
    public static IReadOnlyList<(string Argument, IReadOnlyList<Named> Tests)> Runs(IReadOnlyList<Named> chosen, IReadOnlyList<Named> declared)
    {
        HashSet<Named> picked = [.. chosen];
        if (declared.All(picked.Contains))
        {
            return [(RunTests, [.. picked])];
        }
        return [.. chosen.GroupBy(t => t.Suite, StringComparer.Ordinal).Select(suite => suite.Distinct().ToList() switch
        {
            [{ } one] => ($"{RunTests}={one.Suite}.{one.Method}", (IReadOnlyList<Named>)[one]),
            List<Named> several => ($"{RunTests}={suite.Key}", several),
        })];
    }

    /// <summary>The flag that has a game run its tests.</summary>
    public const string RunTests = "--run-tests";

    /// <summary>The flag that has GoDotTest quit the game once its tests have run.</summary>
    public const string QuitOnFinish = "--quit-on-finish";

    /// <summary>How a test ended as GoDotTest tells it.</summary>
    public enum Ending
    {
        Passed,
        Failed,
        Skipped,
    }

    /// <summary>How a test ran, why it failed (an exception's type and words, or a [Setup]'s or [Cleanup]'s that ran with
    /// it), the frames it failed at, and what it printed.</summary>
    public sealed record Result(Ending State, string? Message, string? StackTrace, string? Output);

    /// <summary>Each test's result by its <see cref="Named.Key"/>, from Godot's output of a run: GoDotTest's line for each
    /// test's start and end, what came between as its output, and the failures its final report tells. A [Setup]'s failure
    /// fails the test it ran before, a [Cleanup]'s the one it ran after, a [SetupAll]'s or [CleanupAll]'s every test of its
    /// suite; a test that started and never ended (Godot ended under it) fails saying so.</summary>
    public static IReadOnlyDictionary<string, Result> Read(string output)
    {
        Dictionary<string, (Ending? State, List<string> Output)> ran = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> bySuite = new(StringComparer.Ordinal);
        List<(Named Method, string Type, int Before)> lifecycle = [];
        Dictionary<string, (List<string> Message, List<string> Exception)> reports = new(StringComparer.Ordinal);
        string? running = null;
        (List<string> Message, List<string> Exception)? report = null;
        bool inException = false;
        foreach (string line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            Match said = Said().Match(line);
            if (!said.Success)
            {
                if (report is { } open)
                {
                    (inException ? open.Exception : open.Message).Add(line);
                }
                else if (running is not null)
                {
                    ran[running].Output.Add(line);
                }
                continue;
            }
            string words = said.Groups["words"].Value;
            if (Event().Match(words) is not { Success: true } happened)
            {
                if (report is { } open && words == "Exception:")
                {
                    inException = true;
                }
                else if (report is { } reported && inException && reported.Exception.Count == 0)
                {
                    reported.Exception.Add(words);
                }
                else
                {
                    report = null;
                }
                continue;
            }
            Named method = new(happened.Groups["suite"].Value, happened.Groups["method"].Value);
            string type = happened.Groups["type"].Value;
            string what = happened.Groups["what"].Value;
            if (happened.Groups["error"].Success)
            {
                (List<string> Message, List<string> Exception) told = ([happened.Groups["error"].Value], []);
                reports[method.Key] = told;
                report = told;
                inException = false;
                continue;
            }
            report = null;
            if (type != "Test")
            {
                if (what.StartsWith("Test failed", StringComparison.Ordinal))
                {
                    lifecycle.Add((method, type, bySuite.GetValueOrDefault(method.Suite)?.Count ?? 0));
                }
                continue;
            }
            if (what.StartsWith("Test started", StringComparison.Ordinal))
            {
                running = method.Key;
                ran[running] = (null, []);
                if (!bySuite.TryGetValue(method.Suite, out List<string>? its))
                {
                    bySuite[method.Suite] = its = [];
                }
                its.Add(method.Key);
                continue;
            }
            Ending state = what.StartsWith("Test passed", StringComparison.Ordinal) ? Ending.Passed
                : what.StartsWith("Test failed", StringComparison.Ordinal) ? Ending.Failed : Ending.Skipped;
            ran[method.Key] = (state, ran.TryGetValue(method.Key, out (Ending? State, List<string> Output) was) ? was.Output : []);
            running = running == method.Key ? null : running;
        }
        Dictionary<string, Result> results = new(StringComparer.Ordinal);
        foreach ((string key, (Ending? state, List<string> printed)) in ran)
        {
            string? shown = string.Join('\n', printed).Trim('\n') is { Length: > 0 } text ? text : null;
            (string? message, string? stack) = state == Ending.Failed && reports.TryGetValue(key, out (List<string> Message, List<string> Exception) told) ? Failure(told.Message, told.Exception) : (null, null);
            results[key] = state is { } ended
                ? new Result(ended, message, stack, shown)
                : new Result(Ending.Failed, "The test never ended: Godot ended as it ran", null, shown);
        }
        foreach ((Named method, string type, int before) in lifecycle)
        {
            List<string> its = bySuite.GetValueOrDefault(method.Suite) ?? [];
            IEnumerable<string> failed = type switch
            {
                "Setup" => its.Skip(before).Take(1),
                "Cleanup" => before > 0 ? [its[before - 1]] : [],
                "SetupAll" or "CleanupAll" => its,
                _ => [],
            };
            (string? message, string? stack) = reports.TryGetValue(method.Key, out (List<string> Message, List<string> Exception) told) ? Failure(told.Message, told.Exception) : (null, null);
            foreach (string key in failed.Where(k => results[k].State != Ending.Failed))
            {
                results[key] = results[key] with
                {
                    State = Ending.Failed,
                    Message = $"Its [{type}] method {method.Method} failed{(message is null ? string.Empty : $": {message}")}",
                    StackTrace = stack,
                };
            }
        }
        return results;
    }

    /// <summary>A failure's words and frames: the exception's type and message, else what GoDotTest said of it, and its
    /// frames from the first.</summary>
    private static (string? Message, string? StackTrace) Failure(List<string> message, List<string> exception)
    {
        int frames = exception.FindIndex(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        List<string> head = frames < 0 ? exception : exception[..frames];
        string? words = string.Join('\n', head.Count > 0 ? head : message).Trim() is { Length: > 0 } said ? said : null;
        string? stack = frames < 0 ? null : string.Join('\n', exception[frames..]).TrimEnd();
        return (words, stack);
    }

    /// <summary>A line GoDotTest's log printed (its level and log's name, "Info (GoTest): ", ahead of its words), however
    /// .NET's tracing headed it.</summary>
    [GeneratedRegex(@"\(GoTest\): (?<words>.*)$")]
    private static partial Regex Said();

    /// <summary>GoDotTest's words of a test method's start, end or failure: its status mark, suite, method and kind.</summary>
    [GeneratedRegex(@"^> (?:OK|!!|\^\^) >> (?<suite>[^\s:]+)::(?<method>[^\s\[]+) \[(?<type>\w+)\] > (?<what>Test (?:passed|failed|skipped|started)!.*|Error occurred: (?<error>.*))$")]
    private static partial Regex Event();
}

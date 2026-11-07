using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>GoDotTest's tests as a Godot C# game runs them: its game, started with <c>--run-tests</c>, runs them inside
/// itself and says how each went in Godot's output. A game carries GoDotTest where its project references its package; its
/// tests are the methods its code marks [Test], each class a suite named by its own name. The scene a run starts in, the
/// runs that take the chosen tests, each test's end as its lines come, and each test's result read back from what GoDotTest
/// printed, its colours none.</summary>
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

    /// <summary>The runs of a game that take the chosen tests of the <paramref name="declared"/> ones and no other: one of every
    /// suite where all are chosen, else one of each suite chosen whole and one of each test of a suite chosen in part
    /// (GoDotTest takes one suite or test a run, and a suite's run is every test of it). Each with the <c>--run-tests</c> it is
    /// given and the chosen tests it tells.</summary>
    public static IReadOnlyList<(string Argument, IReadOnlyList<Named> Tests)> Runs(IReadOnlyList<Named> chosen, IReadOnlyList<Named> declared)
    {
        HashSet<Named> picked = [.. chosen];
        if (declared.Count > 0 && declared.All(picked.Contains))
        {
            return [(RunTests, [.. picked])];
        }
        ILookup<string, Named> suites = declared.ToLookup(t => t.Suite, StringComparer.Ordinal);
        List<(string Argument, IReadOnlyList<Named> Tests)> runs = [];
        foreach (IGrouping<string, Named> suite in chosen.Distinct().GroupBy(t => t.Suite, StringComparer.Ordinal))
        {
            if (suites.Contains(suite.Key) && suites[suite.Key].Count() > 1 && suites[suite.Key].All(picked.Contains))
            {
                runs.Add(($"{RunTests}={suite.Key}", [.. suite]));
            }
            else
            {
                runs.AddRange(suite.Select(one => ($"{RunTests}={one.Suite}.{one.Method}", (IReadOnlyList<Named>)[one])));
            }
        }
        return runs;
    }

    /// <summary>Of a game's tests (<paramref name="chosen"/> and <paramref name="declared"/> by full name), the classes of each
    /// suite GoDotTest cannot run as chosen, by the suite: it knows a class by its own name alone, so of classes sharing one a
    /// test's run takes the first in the assembly and a suite's run all of them, and its output tells them apart nowhere. A
    /// suite chosen whole, every class of it, runs as a suite.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Alike(IReadOnlyList<string> chosen, IReadOnlyList<string> declared)
    {
        HashSet<string> picked = new(chosen, StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<string>> alike = new(StringComparer.Ordinal);
        foreach (IGrouping<string, string> suite in declared.GroupBy(test => Named.Of(test).Suite, StringComparer.Ordinal))
        {
            List<string> classes = [.. suite.Select(test => test[..test.LastIndexOf('.')]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            if (classes.Count > 1 && suite.Any(picked.Contains) && !suite.All(picked.Contains))
            {
                alike[suite.Key] = classes;
            }
        }
        return alike;
    }

    /// <summary>Where a game's run of its tests starts: the scene its run settings name (<see cref="SceneParameter"/>), else
    /// its main scene (<see cref="Scene"/> null) where a script of it reaches GoTest.RunTests, else the test scene that reaches
    /// it, GoDotTest's own under <c>res://test/</c> first; or why none can (<see cref="Problem"/>), as nothing would run them.</summary>
    public sealed record Start(string? Scene, StartProblem Problem = StartProblem.None)
    {
        public static readonly Start Main = new((string?)null);

        /// <summary>Whether no run of the tests can start.</summary>
        public bool Never => Problem != StartProblem.None;

        /// <summary>The run settings file that named the scene, where one did.</summary>
        public string? Settings { get; init; }
    }

    /// <summary>Why a game's tests cannot start: no scene of it runs them, or the scene its run settings name is not the
    /// game's or runs none.</summary>
    public enum StartProblem
    {
        None,
        NoScene,
        ChosenMissing,
        ChosenRunsNone,
    }

    /// <summary>The run settings' parameter a person names a game's test scene by, a res:// path, beside GdUnit4's settings
    /// in the same file (<c>&lt;TestRunParameters&gt;&lt;Parameter name="GoDotTestScene" value="res://test/Tests.tscn" /&gt;</c>).</summary>
    public const string SceneParameter = "GoDotTestScene";

    /// <summary>The test scene a run settings file names (<see cref="SceneParameter"/>); null where it names none, or does not
    /// read as XML.</summary>
    public static string? ChosenScene(string runSettings)
    {
        try
        {
            return System.Xml.Linq.XDocument.Parse(runSettings).Root?.Element("TestRunParameters")?.Elements("Parameter")
                .FirstOrDefault(p => (string?)p.Attribute("name") == SceneParameter)?.Attribute("value")?.Value.Trim() is { Length: > 0 } scene ? scene : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The folder GoDotTest's README makes a game's test scene in.</summary>
    public const string TestFolder = "res://test/";

    /// <summary>The flag Godot starts a scene of the project by, in place of its main scene.</summary>
    public const string SceneFlag = "--scene";

    /// <summary>Where a game's tests run from (<see cref="Start"/>), by its scenes and the C# each script of their nodes runs
    /// (<paramref name="code"/>, by res:// path; null for one not read), read with the game's symbols (<paramref name="parsing"/>:
    /// a call under its #if RUN_TESTS is one), and the scene its run settings name (<paramref name="chosen"/>): a scene reaches
    /// GoTest.RunTests where a script of it calls it, or changes to a scene it names that does.</summary>
    public static Start StartOf(GodotScenes scenes, Func<string, string?> code, string? chosen = null, CSharpParseOptions? parsing = null)
    {
        Dictionary<string, string?> read = new(StringComparer.Ordinal);
        Dictionary<string, bool> calls = new(StringComparer.Ordinal);
        string? Code(string script) => read.TryGetValue(script, out string? known) ? known : read[script] = code(script);
        bool Calls(string script) => calls.TryGetValue(script, out bool known) ? known : calls[script] = Code(script) is { } text && RunsTests(text, parsing);
        bool Reaches(string scene, HashSet<string> seen)
        {
            if (!seen.Add(scene) || scenes.Tree(scene) is not { } root)
            {
                return false;
            }
            foreach (string script in root.Below().Select(n => n.Script).OfType<string>().Where(s => s.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal))
            {
                if (Calls(script) || Code(script) is { } text && ScenesNamed(text, parsing).Any(next => Reaches(next, seen)))
                {
                    return true;
                }
            }
            return false;
        }
        if (chosen is not null)
        {
            return scenes.Files.TryGetValue(chosen, out GodotFile? sceneFile) && sceneFile.Header?.Tag == "gd_scene"
                ? Reaches(chosen, []) ? new Start(chosen) : new Start(chosen, StartProblem.ChosenRunsNone)
                : new Start(chosen, StartProblem.ChosenMissing);
        }
        string? main = scenes.Defined.SettingOf("application", "run/main_scene")?.Text is { } named && named.StartsWith(GodotUids.Scheme, StringComparison.Ordinal)
            ? scenes.ResOfUid(named) : scenes.Defined.SettingOf("application", "run/main_scene")?.Text;
        if (main is not null && Reaches(main, []))
        {
            return Start.Main;
        }
        foreach (string scene in scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").Select(f => f.Key)
            .OrderBy(k => k.StartsWith(TestFolder, StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal))
        {
            if (scene != main && Reaches(scene, []))
            {
                return new Start(scene);
            }
        }
        return new Start(null, StartProblem.NoScene);
    }

    /// <summary>Whether C# calls GoDotTest's GoTest.RunTests, as its syntax says.</summary>
    public static bool RunsTests(string code, CSharpParseOptions? parsing = null) =>
        CSharpParsing.Parse(code, parsing ?? CSharpParsing.Default).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "RunTests", Expression: var owner }
            && owner is IdentifierNameSyntax { Identifier.ValueText: "GoTest" } or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GoTest" });

    /// <summary>The scenes C# names by a string of a res:// path, which it may change to.</summary>
    private static IEnumerable<string> ScenesNamed(string code, CSharpParseOptions? parsing) =>
        CSharpParsing.Parse(code, parsing ?? CSharpParsing.Default).GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken))
            .Select(t => t.ValueText).Where(v => v.StartsWith("res://", StringComparison.Ordinal) && v.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase));

    /// <summary>A run's tests as GoDotTest's lines of them are heard: each test's start, then its end with the time between,
    /// from when each line came (<paramref name="at"/>, since the run began).</summary>
    public sealed class Progress
    {
        private readonly Dictionary<string, TimeSpan> started = new(StringComparer.Ordinal);

        /// <summary>The test a line says ended, how, and how long it ran; null for any other line.</summary>
        public (string Key, Ending State, TimeSpan Took)? Heard(string line, TimeSpan at)
        {
            if (Said().Match(line) is not { Success: true } said || Event().Match(said.Groups["words"].Value) is not { Success: true } happened
                || happened.Groups["type"].Value != "Test" || happened.Groups["error"].Success)
            {
                return null;
            }
            string key = new Named(happened.Groups["suite"].Value, happened.Groups["method"].Value).Key;
            string what = happened.Groups["what"].Value;
            if (what.StartsWith("Test started", StringComparison.Ordinal))
            {
                started[key] = at;
                return null;
            }
            Ending state = what.StartsWith("Test passed", StringComparison.Ordinal) ? Ending.Passed
                : what.StartsWith("Test failed", StringComparison.Ordinal) ? Ending.Failed : Ending.Skipped;
            return (key, state, started.Remove(key, out TimeSpan began) ? at - began : TimeSpan.Zero);
        }
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

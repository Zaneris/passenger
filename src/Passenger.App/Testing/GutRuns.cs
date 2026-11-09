using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Passenger.App.Testing;

/// <summary>A run of GUT's tests as GUT's command line takes it and tells it: the arguments that run the chosen scripts and
/// write GUT's JUnit XML, the pre-run hook that leaves them the chosen tests alone, and each test's result read back from
/// that XML and what GUT printed (its colours off).</summary>
public static partial class GutRuns
{
    /// <summary>GUT's command line, as a project carries it.</summary>
    public const string CommandLine = "res://addons/gut/gut_cmdln.gd";

    /// <summary>A test chosen: its script as Godot names it ("res://test/test_math.gd"), its inner class (null for the
    /// script's own) and its function.</summary>
    public sealed record Chosen(string Script, string? Inner, string Method)
    {
        /// <summary>Its script and inner class as GUT's collector names them ("res://test/test_math.gd.TestInner").</summary>
        public string Suite => Inner is null ? Script : $"{Script}.{Inner}";

        /// <summary>Its suite and name as GUT's results key them (<see cref="Read"/>).</summary>
        public string Key => $"{Suite[Passenger.Workspace.GodotResPaths.Scheme.Length..]}::{Method}";

        /// <summary>A test from the key GUT's results name it by.</summary>
        public static Chosen Of(string key)
        {
            int names = key.LastIndexOf("::", StringComparison.Ordinal);
            string suite = key[..names];
            int dot = suite.LastIndexOf('.');
            bool inner = !suite.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) && dot >= 0;
            return new Chosen($"res://{(inner ? suite[..dot] : suite)}", inner ? suite[(dot + 1)..] : null, key[(names + 2)..]);
        }
    }

    /// <summary>A test's result: GUT's status ("pass", "fail", "pending", "skipped", "no asserts"), its time, the words of
    /// its failure or why it is pending (GUT's "at line N" left out), that line (from one; zero where GUT gave none) and
    /// what GUT printed under it.</summary>
    public sealed record Result(string Status, TimeSpan Time, string? Message, int Line, string? Output);

    /// <summary>GUT's arguments for these tests, its JUnit XML written to <paramref name="results"/>: their scripts alone, the
    /// folders and choices a .gutconfig.json names emptied, and where given <paramref name="hook"/>, the pre-run hook
    /// (<see cref="Hook"/>) that leaves them the tests chosen.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<Chosen> tests, string results, string? hook = null) =>
        // An option given empty overrides the config's: its folders' scripts, or a test, script or inner class it picks, run otherwise.
        ["-s", CommandLine, "-gexit", "-gdisable_colors", $"-gjunit_xml_file={results.Replace('\\', '/')}",
            $"-gtest={string.Join(',', tests.Select(t => t.Script).Distinct(StringComparer.Ordinal))}", "-gdir=", "-gselect=", "-gunit_test_name=", "-ginner_class=",
            .. hook is null ? Array.Empty<string>() : [$"-gpre_run_script={hook.Replace('\\', '/')}"]];

    /// <summary>What GUT is to keep of the scripts of these tests, by each <see cref="Chosen.Suite"/> chosen: its tests' names
    /// where only some of it is chosen, none where all it declares are, so what it inherits runs with them; null where every
    /// suite of those scripts is chosen whole (<paramref name="declared"/>, what the project's code declares), which GUT's
    /// <c>-gtest</c> takes alone.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? Kept(IReadOnlyList<Chosen> tests, IReadOnlyList<Chosen> declared)
    {
        HashSet<Chosen> picked = [.. tests];
        HashSet<string> scripts = new(tests.Select(t => t.Script), StringComparer.Ordinal);
        ILookup<string, Chosen> suites = declared.Where(t => scripts.Contains(t.Script)).ToLookup(t => t.Suite, StringComparer.Ordinal);
        bool Whole(string suite) => suites.Contains(suite) && suites[suite].All(picked.Contains);
        if (tests.All(t => Whole(t.Suite)) && suites.All(s => Whole(s.Key)))
        {
            return null;
        }
        return tests.GroupBy(t => t.Suite, StringComparer.Ordinal).ToDictionary(suite => suite.Key,
            suite => Whole(suite.Key) ? [] : (IReadOnlyList<string>)[.. suite.Select(t => t.Method).Distinct(StringComparer.Ordinal)], StringComparer.Ordinal);
    }

    /// <summary>A GUT pre-run hook that takes out of its run all but <paramref name="kept"/> (<see cref="Kept"/>), as GUT's own
    /// filters cannot: each takes one name, and every test or inner class whose name holds it. A hook on the command line
    /// stands in for the project's own (<paramref name="own"/>, <see cref="GutScripts.PreRunScript"/>), so it runs that next.</summary>
    public static string Hook(IReadOnlyDictionary<string, IReadOnlyList<string>> kept, string? own)
    {
        JsonObject suites = new();
        foreach ((string suite, IReadOnlyList<string> names) in kept)
        {
            suites[suite] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        }
        // JSON's literals are GDScript's, a string's escapes included.
        return $$"""
            extends "res://addons/gut/hook_script.gd"

            const KEPT := {{suites.ToJsonString()}}
            const OWN := {{JsonValue.Create(own ?? string.Empty).ToJsonString()}}


            func run():
            	var collector = gut.get_test_collector()
            	var scripts := []
            	for script in collector.scripts:
            		var suite := str(script.path)
            		if str(script.inner_class_name) != "":
            			suite += "." + str(script.inner_class_name)
            		if KEPT.has(suite):
            			var names: Array = KEPT[suite]
            			if not names.is_empty():
            				script.tests = script.tests.filter(func(test): return names.has(test.name))
            			scripts.append(script)
            	collector.scripts = scripts
            	if OWN != "":
            		var hook = load(OWN).new()
            		hook.gut = gut
            		await hook.run()
            		if hook.should_abort():
            			abort()

            """;
    }

    /// <summary>Each test's result by its <see cref="Chosen.Key"/>, from GUT's JUnit XML (a suite per script or inner class,
    /// "test/test_math.gd" or "test/test_math.gd.TestInner") and what it printed under each test.</summary>
    public static IReadOnlyDictionary<string, Result> Read(string xml, string output)
    {
        IReadOnlyDictionary<string, string> printed = Printed(output);
        Dictionary<string, Result> results = new(StringComparer.Ordinal);
        foreach (XElement suite in XDocument.Parse(xml).Descendants("testsuite"))
        {
            string name = (string?)suite.Attribute("name") ?? string.Empty;
            foreach (XElement test in suite.Elements("testcase"))
            {
                string key = $"{name}::{(string?)test.Attribute("name")}";
                string status = (string?)test.Attribute("status") ?? string.Empty;
                TimeSpan time = double.TryParse((string?)test.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
                string? said = (string?)test.Element("failure") ?? (string?)test.Element("skipped");
                int line = said is not null && AtLine().Match(said) is { Success: true } at && int.TryParse(at.Groups["line"].Value, CultureInfo.InvariantCulture, out int number)
                    ? Math.Max(number, 0) : 0;
                string? message = said is null ? status == "no asserts" ? "The test made no assertions" : null : AtLine().Replace(said, string.Empty).TrimEnd();
                results[key] = new Result(status, time, message, line, printed.GetValueOrDefault(key));
            }
        }
        return results;
    }

    /// <summary>What GUT printed under each test, by its suite and name: the lines after its "* name" until the next test,
    /// script (GUT heads each with its res:// path) or tally.</summary>
    private static IReadOnlyDictionary<string, string> Printed(string output)
    {
        Dictionary<string, string> printed = new(StringComparer.Ordinal);
        string? suite = null;
        string? test = null;
        List<string> lines = [];
        foreach (string line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            bool heading = line.StartsWith(Passenger.Workspace.GodotResPaths.Scheme, StringComparison.Ordinal);
            bool next = line.StartsWith("* ", StringComparison.Ordinal);
            if (heading || next || Tally().IsMatch(line))
            {
                Keep();
                test = null;
            }
            if (heading)
            {
                suite = line.Trim()[Passenger.Workspace.GodotResPaths.Scheme.Length..];
            }
            else if (next && suite is not null)
            {
                test = line[2..].Trim();
            }
            else if (test is not null)
            {
                lines.Add(line);
            }
        }
        Keep();
        return printed;

        void Keep()
        {
            if (suite is not null && test is not null && string.Join('\n', lines).Trim('\n') is { Length: > 0 } said)
            {
                printed[$"{suite}::{test}"] = said;
            }
            lines.Clear();
        }
    }

    [GeneratedRegex(@"\s*at line (?<line>-?\d+)\s*$")]
    private static partial Regex AtLine();

    [GeneratedRegex(@"^\d+/\d+ passed\.$")]
    private static partial Regex Tally();
}

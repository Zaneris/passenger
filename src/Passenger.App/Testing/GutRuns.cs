using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Passenger.App.Testing;

/// <summary>A run of GUT's tests as GUT's command line takes it and tells it: the arguments that run the chosen tests and
/// write GUT's JUnit XML, and each test's result read back from that XML and what GUT printed (its colours off).</summary>
public static partial class GutRuns
{
    /// <summary>GUT's command line, as a project carries it.</summary>
    public const string CommandLine = "res://addons/gut/gut_cmdln.gd";

    /// <summary>A test chosen: its script as Godot names it ("res://test/test_math.gd"), its inner class (null for the
    /// script's own) and its function.</summary>
    public sealed record Chosen(string Script, string? Inner, string Method)
    {
        /// <summary>Its suite and name as GUT's results key them (<see cref="Read"/>).</summary>
        public string Key => $"{Script["res://".Length..]}{(Inner is null ? string.Empty : $".{Inner}")}::{Method}";
    }

    /// <summary>A test's result: GUT's status ("pass", "fail", "pending", "skipped", "no asserts"), its time, the words of
    /// its failure or why it is pending (GUT's "at line N" left out), that line (from one; zero where GUT gave none) and
    /// what GUT printed under it.</summary>
    public sealed record Result(string Status, TimeSpan Time, string? Message, int Line, string? Output);

    /// <summary>GUT's arguments for these tests, its JUnit XML written to <paramref name="results"/>: their scripts, and where
    /// they are one test, or one inner class's, that test or class alone (GUT takes any whose name holds the one given; a
    /// test it runs beside them is no result asked for).</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<Chosen> tests, string results)
    {
        List<string> arguments = ["-s", CommandLine, "-gexit", "-gdisable_colors", $"-gjunit_xml_file={results.Replace('\\', '/')}",
            $"-gtest={string.Join(',', tests.Select(t => t.Script).Distinct(StringComparer.Ordinal))}"];
        if (tests is [{ } one])
        {
            arguments.Add($"-gunit_test_name={one.Method}");
        }
        if (tests.Select(t => (t.Script, t.Inner)).Distinct().ToList() is [(_, { } inner)])
        {
            arguments.Add($"-ginner_class={inner}");
        }
        return arguments;
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
            bool heading = line.StartsWith("res://", StringComparison.Ordinal);
            bool next = line.StartsWith("* ", StringComparison.Ordinal);
            if (heading || next || Tally().IsMatch(line))
            {
                Keep();
                test = null;
            }
            if (heading)
            {
                suite = line.Trim()["res://".Length..];
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

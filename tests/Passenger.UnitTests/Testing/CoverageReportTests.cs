using System.Text;
using System.Xml.Linq;
using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>What Coverlet's JSON reports come to, from their text alone (as Coverlet writes them for a library two test
/// projects run): two reports' runs of one method merged, each branch taken where either took it; a state machine's and a
/// lambda's lines counted as the overload they are written in; and the merged coverage written as Cobertura, its rates and
/// a line's branches as other tools read them. And the map of which tests run which lines: the places a test counted read
/// as the lines they stand for, and a file's change reaching only the tests that ran what it changed.</summary>
public class CoverageReportTests
{
    private const string Measure = "/src/Shapes/Measure.cs";

    /// <summary>Sign's if, a test assembly taking each of its branches, its last line run by the first alone.</summary>
    private static string Signed(int taken, int last) =>
        $"{{\"Shapes.dll\":{{\"{Measure}\":{{\"Shapes.Measure\":{{\"System.Int32 Shapes.Measure::Sign(System.Int32)\":{{" +
        $"\"Lines\":{{\"9\":1,\"11\":{last}}},\"Branches\":[" +
        $"{{\"Line\":9,\"Offset\":7,\"EndOffset\":9,\"Path\":0,\"Ordinal\":0,\"Hits\":{(taken == 0 ? 1 : 0)},\"IsReachable\":true}}," +
        $"{{\"Line\":9,\"Offset\":7,\"EndOffset\":14,\"Path\":1,\"Ordinal\":1,\"Hits\":{(taken == 1 ? 1 : 0)},\"IsReachable\":true}}," +
        "{\"Line\":9,\"Offset\":20,\"EndOffset\":22,\"Path\":0,\"Ordinal\":2,\"Hits\":0,\"IsReachable\":false}]}}}}}";

    /// <summary>An async Run with a lambda in it beside an overload of the same name, and a constructor.</summary>
    private const string Jobs =
        "{\"Shapes.dll\":{\"/src/Shapes/Jobs.cs\":{" +
        "\"Shapes.Jobs\":{\"System.Threading.Tasks.Task Shapes.Jobs::Run(System.Int32)\":{\"Lines\":{\"5\":1},\"Branches\":[]}," +
        "\"System.Void Shapes.Jobs::Run()\":{\"Lines\":{\"12\":0,\"13\":0},\"Branches\":[]}," +
        "\"System.Void Shapes.Jobs::.ctor()\":{\"Lines\":{\"3\":1},\"Branches\":[]}}," +
        "\"Shapes.Jobs/<Run>d__1\":{\"System.Void Shapes.Jobs/<Run>d__1::MoveNext()\":{\"Lines\":{\"6\":1,\"7\":1,\"9\":0},\"Branches\":[]}}," +
        "\"Shapes.Jobs/<>c\":{\"System.Int32 Shapes.Jobs/<>c::<Run>b__1_0(System.Int32)\":{\"Lines\":{\"8\":1},\"Branches\":[]}}}}}";

    [Fact]
    public void Coverage_merges_by_branch_counts_by_member_writes_as_cobertura_and_maps_tests_to_lines() => Cases.All(
        ("two test assemblies runs of one method take its branches together", Two_test_assemblies_runs_of_one_method_take_its_branches_together),
        ("a state machines and a lambdas lines count as the overload they are written in", A_state_machines_and_a_lambdas_lines_count_as_the_overload_they_are_written_in),
        ("the cobertura written gives the rates and each lines branches taken", The_cobertura_written_gives_the_rates_and_each_lines_branches_taken),
        ("the places a test counted read as the lines they stand for", The_places_a_test_counted_read_as_the_lines_they_stand_for),
        ("a files change reaches only the tests that ran a line it changed or stood beside", A_files_change_reaches_only_the_tests_that_ran_a_line_it_changed_or_stood_beside),
        ("a tests lines are carried to where they stand once another tests run maps the file again", A_tests_lines_are_carried_to_where_they_stand_once_another_tests_run_maps_the_file_again));

    private static void Two_test_assemblies_runs_of_one_method_take_its_branches_together()
    {
        CoverageReport report = Read(Signed(taken: 0, last: 1), Signed(taken: 1, last: 0));
        Assert.Equal(new[] { new CoveredLine(9, 2, 2, 2), new CoveredLine(11, 1, 0, 0) }, report.In(Measure));
        Assert.Equal(CoverageState.Covered, report.In(Measure)[0].State);
        Assert.Equal(new CoverageCounts(2, 2, 2, 2), report.Counts);
    }

    private static void A_state_machines_and_a_lambdas_lines_count_as_the_overload_they_are_written_in()
    {
        CoverageNode type = Assert.Single(Assert.Single(Assert.Single(Read(Jobs).Projects).Children).Children);
        Assert.Equal(("Jobs", 3, new CoverageCounts(8, 5, 0, 0)), (type.Name, type.Line, type.Counts));
        Assert.Equal(["Jobs 3: 1 of 1", "Run(Int32) 5: 4 of 5", "Run() 12: 0 of 2"],
            type.Children.Select(m => $"{m.Name} {m.Line}: {m.Counts.LinesRun} of {m.Counts.Lines}"));
    }

    private static void The_cobertura_written_gives_the_rates_and_each_lines_branches_taken()
    {
        string written = Read(Signed(taken: 0, last: 0)).ToCobertura();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", written, StringComparison.Ordinal);
        XElement root = XDocument.Parse(written).Root!;
        Assert.Equal(("0.5", "0.5", "1", "2"), ((string?)root.Attribute("line-rate"), (string?)root.Attribute("branch-rate"),
            (string?)root.Attribute("lines-covered"), (string?)root.Attribute("lines-valid")));
        XElement type = Assert.Single(root.Descendants("class"));
        Assert.Equal(("Shapes.Measure", Measure), ((string?)type.Attribute("name"), (string?)type.Attribute("filename")));
        XElement method = Assert.Single(type.Descendants("method"));
        Assert.Equal(("Sign", "(System.Int32)"), ((string?)method.Attribute("name"), (string?)method.Attribute("signature")));
        XElement line = type.Element("lines")!.Elements("line").First();
        Assert.Equal(("9", "True", "50% (1/2)"), ((string?)line.Attribute("number"), (string?)line.Attribute("branch"), (string?)line.Attribute("condition-coverage")));
    }

    /// <summary>Two modules' maps, one count of the first standing for lines 3 to 5 less a nested count's 4, another for 4, a
    /// branch for 9; the second module's one count for its file's line 2.</summary>
    private static void The_places_a_test_counted_read_as_the_lines_they_stand_for()
    {
        const string Map = "{\"modules\":[{\"module\":\"Shapes\",\"path\":\"/b/Shapes.dll\",\"hits\":\"/h/Shapes_1\",\"documents\":[\"/src/A.cs\",\"/src/B.cs\"]," +
            "\"places\":[[0,3,5],[0,4],[1,9]]},{\"module\":\"Paths\",\"path\":\"/b/Paths.dll\",\"hits\":\"/h/Paths_1\",\"documents\":[\"/src/C.cs\"],\"places\":[[0,2]]}]}";
        InstrumentedCode code = InstrumentedCode.Of(Encoding.UTF8.GetBytes(Map), path => path)!;
        Dictionary<string, HashSet<int>> ran = code.Ran(new Dictionary<string, int[]> { ["/h/Shapes_1"] = [0, 2], ["/h/Paths_1"] = [], ["/h/Gone_1"] = [0] });
        Assert.Equal(["/src/A.cs: 3 5", "/src/B.cs: 9"], ran.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}: {string.Join(' ', f.Value.Order())}"));
        Assert.Equal([3, 4, 5], code.Coverable()["/src/A.cs"]);
        Assert.Equal([3, 3, 9, 1], Stretches.Of([5, 9, 3, 4, 4]));
    }

    private static void A_files_change_reaches_only_the_tests_that_ran_a_line_it_changed_or_stood_beside()
    {
        TestReachState map = Mapped(("Edits", [2]), ("Stays", [5]), ("Ends", [7]));
        Assert.Equal(["Edits"], map.Changed(Shapes, TestReachState.LinesOf("a\nB\nc\nd\ne\nf\ng\n")));
        Assert.Equal(["Stays"], map.Changed(Shapes, TestReachState.LinesOf("a\nb\nc\nd\ne\nnew\nf\ng\n")));
        Assert.Empty(map.Changed(Shapes, TestReachState.LinesOf(Text)));
        Assert.Empty(map.Changed("/src/Unmapped.cs", TestReachState.LinesOf("x\n")));
    }

    private static void A_tests_lines_are_carried_to_where_they_stand_once_another_tests_run_maps_the_file_again()
    {
        uint[] now = TestReachState.LinesOf("top\na\nB\nc\nd\ne\nf\ng\n");
        TestReachState map = Mapped(("Edits", [2]), ("Stays", [5]));
        Assert.Equal(["Edits"], map.Changed(Shapes, now));
        TestReachState after = map.With(new Dictionary<string, ReachedTest> { ["Edits"] = Reached([3]) }, new Dictionary<string, uint[]> { [Shapes] = now });
        Assert.Equal([3, 1], after.Tests["Edits"].Lines[Shapes]);
        Assert.Equal([6, 1], after.Tests["Stays"].Lines[Shapes]);
        Assert.Equal(["Stays"], after.Running(Shapes, 6));
        Assert.Empty(after.Changed(Shapes, now));
    }

    private const string Shapes = "/src/Shapes.cs";

    private const string Text = "a\nb\nc\nd\ne\nf\ng\n";

    /// <summary>A map of Shapes.cs as <see cref="Text"/> reads, each test having run the lines given.</summary>
    private static TestReachState Mapped(params (string Test, int[] Lines)[] tests) =>
        new TestReachState().With(tests.ToDictionary(t => t.Test, t => Reached(t.Lines)), new Dictionary<string, uint[]> { [Shapes] = TestReachState.LinesOf(Text) });

    private static ReachedTest Reached(int[] lines) => new() { State = TestState.Passed, Mapped = DateTimeOffset.UnixEpoch, Lines = new() { [Shapes] = Stretches.Of(lines) } };

    /// <summary>Reports merged, each source file named as the report names it.</summary>
    private static CoverageReport Read(params string[] reports) =>
        CoverageReport.Of(reports.Select((json, i) => ($"coverage{i}.json", (Func<byte[]>)(() => Encoding.UTF8.GetBytes(json)))), DateTimeOffset.UnixEpoch, file => file,
            unread => Assert.Fail(unread));
}

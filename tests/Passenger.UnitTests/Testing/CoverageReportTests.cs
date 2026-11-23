using System.Text;
using System.Xml.Linq;
using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>What Coverlet's JSON reports come to, from their text alone (as Coverlet writes them for a library two test
/// projects run): two reports' runs of one method merged, each branch taken where either took it; a state machine's and a
/// lambda's lines counted as the overload they are written in; and the merged coverage written as Cobertura, its rates and
/// a line's branches as other tools read them.</summary>
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
    public void Coverlets_reports_merge_by_branch_count_by_member_and_write_as_cobertura() => Cases.All(
        ("two test assemblies runs of one method take its branches together", Two_test_assemblies_runs_of_one_method_take_its_branches_together),
        ("a state machines and a lambdas lines count as the overload they are written in", A_state_machines_and_a_lambdas_lines_count_as_the_overload_they_are_written_in),
        ("the cobertura written gives the rates and each lines branches taken", The_cobertura_written_gives_the_rates_and_each_lines_branches_taken));

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

    /// <summary>Reports merged, each source file named as the report names it.</summary>
    private static CoverageReport Read(params string[] reports) =>
        CoverageReport.Of(reports.Select((json, i) => ($"coverage{i}.json", (Func<byte[]>)(() => Encoding.UTF8.GetBytes(json)))), DateTimeOffset.UnixEpoch, file => file,
            unread => Assert.Fail(unread));
}

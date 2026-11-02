using Passenger.App.Testing;

namespace Passenger.UnitTests.Godot;

/// <summary>A run of GUT's tests as Passenger asks it of GUT's command line, and each test's result as it reads GUT's JUnit
/// XML and what GUT printed back.</summary>
public class GutRunsTests
{
    private static readonly GutRuns.Chosen Adds = new("res://test/unit/test_math.gd", null, "test_adds");

    [Fact]
    public void One_test_runs_by_its_script_and_its_name_its_results_written_where_asked()
    {
        Assert.Equal(["-s", GutRuns.CommandLine, "-gexit", "-gdisable_colors", "-gjunit_xml_file=C:/runs/out.xml", "-gtest=res://test/unit/test_math.gd",
            "-gunit_test_name=test_adds"], GutRuns.Arguments([Adds], @"C:\runs\out.xml"));
    }

    [Fact]
    public void Tests_of_one_inner_class_run_by_its_name_and_tests_of_several_scripts_by_their_scripts_alone()
    {
        IReadOnlyList<string> inner = GutRuns.Arguments([Adds with { Inner = "TestInner" }, new("res://test/unit/test_math.gd", "TestInner", "test_two")], "/out.xml");
        Assert.Contains("-ginner_class=TestInner", inner);
        Assert.DoesNotContain(inner, a => a.StartsWith("-gunit_test_name", StringComparison.Ordinal));
        IReadOnlyList<string> scripts = GutRuns.Arguments([Adds, new("res://test/test_b.gd", null, "test_b"), Adds with { Method = "test_more" }], "/out.xml");
        Assert.Contains("-gtest=res://test/unit/test_math.gd,res://test/test_b.gd", scripts);
        Assert.DoesNotContain(scripts, a => a.StartsWith("-gunit_test_name", StringComparison.Ordinal) || a.StartsWith("-ginner_class", StringComparison.Ordinal));
    }

    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites name="GutTests" failures="1" tests="4" >
          <testsuite name="test/unit/test_math.gd" tests="3" failures="1" skipped="0" time="0.1" >
              <testcase name="test_adds" assertions="1" status="fail" classname="test/unit/test_math.gd" time="0.25" >
              <failure message="failed"><![CDATA[[2] expected to equal [3]:  adds
                    at line 9]]></failure></testcase>
              <testcase name="test_pending" assertions="0" status="pending" classname="test/unit/test_math.gd" time="0" >
              <skipped message="pending"><![CDATA[not yet]]></skipped></testcase>
              <testcase name="test_silent" assertions="0" status="no asserts" classname="test/unit/test_math.gd" time="0" >
              </testcase>
          </testsuite>
          <testsuite name="test/unit/test_math.gd.TestInner" tests="1" failures="0" skipped="0" time="0.1" >
              <testcase name="test_inner" assertions="1" status="pass" classname="test/unit/test_math.gd.TestInner" time="0.1" >
              </testcase>
          </testsuite>
        </testsuites>
        """;

    private const string Output = "\nres://test/unit/test_math.gd\n* test_adds\nadding\n    [Failed]:  [2] expected to equal [3]:  adds\n      at line 9\n"
        + "* test_pending\n1/3 passed.\n\n\nres://test/unit/test_math.gd.TestInner\n* test_inner\ninside\n1/1 passed.\n";

    [Fact]
    public void A_failure_is_read_with_its_words_its_line_its_time_and_what_GUT_printed_under_it()
    {
        IReadOnlyDictionary<string, GutRuns.Result> results = GutRuns.Read(Xml, Output);
        GutRuns.Result failed = results[Adds.Key];
        Assert.Equal(new GutRuns.Result("fail", TimeSpan.FromSeconds(0.25), "[2] expected to equal [3]:  adds", 9,
            "adding\n    [Failed]:  [2] expected to equal [3]:  adds\n      at line 9"), failed);
    }

    [Fact]
    public void A_pending_test_says_why_one_that_asserts_nothing_says_so_and_an_inner_class_test_is_its_own()
    {
        IReadOnlyDictionary<string, GutRuns.Result> results = GutRuns.Read(Xml, Output);
        Assert.True(results[(Adds with { Method = "test_pending" }).Key] is { Status: "pending", Message: "not yet", Line: 0, Output: null },
            $"A pending test was read as {results[(Adds with { Method = "test_pending" }).Key]}");
        Assert.Equal("The test made no assertions", results[(Adds with { Method = "test_silent" }).Key].Message);
        Assert.True(results[new GutRuns.Chosen("res://test/unit/test_math.gd", "TestInner", "test_inner").Key] is { Status: "pass", Output: "inside" },
            "The inner class's test was not read as its own");
        Assert.Equal(4, results.Count);
    }
}

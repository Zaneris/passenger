using Passenger.App.Testing;

namespace Passenger.UnitTests.Godot;

/// <summary>A run of GUT's tests as Passenger asks it of GUT's command line, and each test's result as it reads GUT's JUnit
/// XML and what GUT printed back.</summary>
public class GutRunsTests
{
    private static readonly GutRuns.Chosen Adds = new("res://test/unit/test_math.gd", null, "test_adds");

    private static readonly GutRuns.Chosen Holds = Adds with { Method = "test_adds_two" };
    private static readonly GutRuns.Chosen Turns = Adds with { Method = "test_turns" };
    private static readonly GutRuns.Chosen Inner = Adds with { Inner = "TestInner" };
    private static readonly GutRuns.Chosen InnerToo = Adds with { Inner = "TestInnerToo" };

    [Fact]
    public void A_run_names_its_scripts_and_its_results_and_picks_nothing_by_a_name_GUT_would_take_any_test_holding()
    {
        Assert.Equal(["-s", GutRuns.CommandLine, "-gexit", "-gdisable_colors", "-gjunit_xml_file=C:/runs/out.xml", "-gtest=res://test/unit/test_math.gd",
            "-gdir=", "-gselect=", "-gunit_test_name=", "-ginner_class="], GutRuns.Arguments([Adds], @"C:\runs\out.xml"));
        IReadOnlyList<string> scripts = GutRuns.Arguments([Adds, new("res://test/test_b.gd", null, "test_b"), Turns], "/out.xml");
        Assert.Contains("-gtest=res://test/unit/test_math.gd,res://test/test_b.gd", scripts);
    }

    [Fact]
    public void A_hook_is_given_on_the_command_line_by_its_path_as_Godot_writes_one()
    {
        Assert.Equal("-gpre_run_script=C:/runs/hook.gd", GutRuns.Arguments([Adds], "/out.xml", @"C:\runs\hook.gd")[^1]);
        Assert.DoesNotContain(GutRuns.Arguments([Adds], "/out.xml"), a => a.StartsWith("-gpre_run_script", StringComparison.Ordinal));
    }

    [Fact]
    public void A_test_chosen_alone_keeps_it_alone_though_another_test_holds_its_name()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? kept = GutRuns.Kept([Adds], [Adds, Holds, Turns]);
        Assert.NotNull(kept);
        Assert.Equal(["test_adds"], kept[Adds.Suite]);
    }

    [Fact]
    public void Two_tests_of_a_scripts_three_keep_those_two()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? kept = GutRuns.Kept([Adds, Turns], [Adds, Holds, Turns]);
        Assert.NotNull(kept);
        Assert.Equal(["test_adds", "test_turns"], kept[Adds.Suite]);
    }

    [Fact]
    public void A_test_of_an_inner_class_keeps_that_class_alone_of_those_alike_and_of_its_script()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? kept = GutRuns.Kept([Inner], [Adds, Turns, Inner, InnerToo]);
        Assert.NotNull(kept);
        Assert.Equal(Inner.Suite, Assert.Single(kept).Key);
    }

    [Fact]
    public void A_suite_chosen_whole_beside_one_chosen_in_part_keeps_what_it_inherits_too()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? kept = GutRuns.Kept([Inner, Adds], [Adds, Turns, Inner]);
        Assert.NotNull(kept);
        Assert.Empty(kept[Inner.Suite]);
        Assert.Equal(["test_adds"], kept[Adds.Suite]);
    }

    [Fact]
    public void Whole_scripts_want_no_hook_whichever_of_their_classes_hold_the_tests()
    {
        GutRuns.Chosen other = new("res://test/test_b.gd", null, "test_b");
        Assert.Null(GutRuns.Kept([Adds, Turns, Inner, other], [Adds, Turns, Inner, other]));
        Assert.Null(GutRuns.Kept([other], [Adds, Turns, Inner, other]));
    }

    [Fact]
    public void A_test_the_code_no_longer_declares_is_kept_by_name_rather_than_run_with_its_whole_script()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? kept = GutRuns.Kept([Adds], []);
        Assert.NotNull(kept);
        Assert.Equal(["test_adds"], kept[Adds.Suite]);
    }

    [Fact]
    public void The_hook_names_what_it_keeps_in_literals_GDScript_reads_and_the_projects_own_hook()
    {
        string hook = GutRuns.Hook(new Dictionary<string, IReadOnlyList<string>> { ["res://test/a \"b\" \\c.gd"] = ["test_a"], ["res://test/d.gd.TestInner"] = [] }, "res://own.gd");
        Assert.Contains("extends \"res://addons/gut/hook_script.gd\"", hook, StringComparison.Ordinal);
        Assert.Contains("const KEPT := {\"res://test/a \\u0022b\\u0022 \\\\c.gd\":[\"test_a\"],\"res://test/d.gd.TestInner\":[]}", hook, StringComparison.Ordinal);
        Assert.Contains("const OWN := \"res://own.gd\"", hook, StringComparison.Ordinal);
        Assert.Contains("const OWN := \"\"", GutRuns.Hook(new Dictionary<string, IReadOnlyList<string>> { ["res://a.gd"] = ["test_a"] }, null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test/unit/test_math.gd::test_adds", "res://test/unit/test_math.gd", null, "test_adds")]
    [InlineData("test/unit/test_math.gd.TestInner::test_adds", "res://test/unit/test_math.gd", "TestInner", "test_adds")]
    [InlineData("test/a.gd.b/test_c.gd::test_d", "res://test/a.gd.b/test_c.gd", null, "test_d")]
    public void A_test_GUT_told_is_read_back_from_its_key(string key, string script, string? inner, string method)
    {
        GutRuns.Chosen read = GutRuns.Chosen.Of(key);
        Assert.Equal(new GutRuns.Chosen(script, inner, method), read);
        Assert.Equal(key, read.Key);
    }

    /// <summary>GUT's command line takes an option given empty over its .gutconfig.json's, which would otherwise add every
    /// script of the folders it names to the run, or pick the tests, scripts and inner classes it names.</summary>
    [Fact]
    public void A_run_empties_the_folders_and_choices_a_gut_config_names_so_the_chosen_scripts_run_alone()
    {
        IReadOnlyList<string> arguments = GutRuns.Arguments([Adds, new("res://test/test_b.gd", null, "test_b")], "/out.xml");
        foreach (string emptied in new[] { "-gdir=", "-gselect=", "-gunit_test_name=", "-ginner_class=" })
        {
            Assert.True(arguments.Contains(emptied), $"GUT would take its config's {emptied[2..^1]} beside the chosen scripts: {string.Join(' ', arguments)}");
        }
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

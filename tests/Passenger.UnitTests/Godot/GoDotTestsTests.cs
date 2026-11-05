using Passenger.App.Testing;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>The runs of a game Passenger asks GoDotTest for, the scene a run starts in, each test's end as its line is
/// heard, and each test's result as it reads back what GoDotTest printed into Godot's log of a run.</summary>
public class GoDotTestsTests
{
    private static readonly GoDotTests.Named Adds = new("SpinnerTest", "Adds");
    private static readonly GoDotTests.Named Spins = new("SpinnerTest", "Spins");
    private static readonly GoDotTests.Named Turns = new("TurnTest", "Turns");
    private static readonly GoDotTests.Named Stops = new("TurnTest", "Stops");

    [Fact]
    public void A_test_is_named_by_its_class_alone_as_GoDotTest_names_its_suite()
    {
        Assert.Equal(Spins, GoDotTests.Named.Of("Game.Tests.SpinnerTest.Spins"));
        Assert.Equal(new GoDotTests.Named("Inner", "Spins"), GoDotTests.Named.Of("Game.Tests.Outer+Inner.Spins"));
        Assert.Equal("SpinnerTest::Spins", Spins.Key);
    }

    [Fact]
    public void Every_test_chosen_runs_in_one_run_of_the_game_given_no_suite()
    {
        IReadOnlyList<(string Argument, IReadOnlyList<GoDotTests.Named> Tests)> runs = GoDotTests.Runs([Spins, Adds, Turns, Stops], [Adds, Spins, Turns, Stops]);
        Assert.Equal("--run-tests", Assert.Single(runs).Argument);
        Assert.Equal(4, runs[0].Tests.Count);
    }

    [Fact]
    public void A_suite_chosen_whole_runs_alone_and_a_test_alone_where_only_it_is_chosen_of_its_suite()
    {
        IReadOnlyList<(string Argument, IReadOnlyList<GoDotTests.Named> Tests)> runs = GoDotTests.Runs([Adds, Spins, Turns], [Adds, Spins, Turns, Stops]);
        Assert.Equal(["--run-tests=SpinnerTest", "--run-tests=TurnTest.Turns"], runs.Select(r => r.Argument));
        Assert.Equal([Adds, Spins], runs[0].Tests);
        Assert.Equal([Turns], runs[1].Tests);
    }

    /// <summary>A suite's run is every test of it: choosing two of three by it would run the third, unasked and untold.</summary>
    [Fact]
    public void Tests_chosen_of_part_of_a_suite_run_one_by_one_and_the_rest_of_it_not_at_all()
    {
        GoDotTests.Named third = Spins with { Method = "Stops" };
        IReadOnlyList<(string Argument, IReadOnlyList<GoDotTests.Named> Tests)> runs = GoDotTests.Runs([Adds, Spins], [Adds, Spins, third]);
        Assert.Equal(["--run-tests=SpinnerTest.Adds", "--run-tests=SpinnerTest.Spins"], runs.Select(r => r.Argument));
        Assert.Equal([Adds], runs[0].Tests);
        Assert.Equal([Spins], runs[1].Tests);
    }

    [Fact]
    public void A_test_chosen_twice_runs_once()
    {
        Assert.Equal(["--run-tests=SpinnerTest.Adds"], GoDotTests.Runs([Adds, Adds], [Adds, Spins]).Select(r => r.Argument));
    }

    /// <summary>With no tests declared to compare with, all the chosen are no reason to run the game's every suite.</summary>
    [Fact]
    public void Tests_the_code_no_longer_declares_run_by_name_and_never_as_the_whole_game()
    {
        Assert.Equal(["--run-tests=SpinnerTest.Adds", "--run-tests=SpinnerTest.Spins"], GoDotTests.Runs([Adds, Spins], []).Select(r => r.Argument));
    }

    /// <summary>A run's log as Godot 4.7 writes it with GoDotTest 2.0: a test passing with what it printed, one failing,
    /// a suite whose [Setup] fails before a test that passes, and the final report of the failures.</summary>
    private const string Log = """
        Godot Engine v4.7.2.stable.mono.official.ed1daf0bf - https://godotengine.org

        Info (GoTest): > ^^ >> Started testing! :3
        Info (GoTest): > ^^ >> SpinnerTest > Test suite started! :3
        Info (GoTest): > ^^ >> SpinnerTest::Adds [Test] > Test started! :3
        adding
        Info (GoTest): > OK >> SpinnerTest::Adds [Test] > Test passed! :)
        Info (GoTest): > ^^ >> SpinnerTest::Spins [Test] > Test started! :3
        spinning 2
        Info (GoTest): > !! >> SpinnerTest::Spins [Test] > Test failed! :(
        Info (GoTest): > !! >> SpinnerTest > Test suite error. Aborting! :(
        Info (GoTest): > ^^ >> SetupTest > Test suite started! :3
        Info (GoTest): > !! >> SetupTest::Setup [Setup] > Test failed! :(
        Info (GoTest): > ^^ >> SetupTest::AfterSetup [Test] > Test started! :3
        Info (GoTest): > OK >> SetupTest::AfterSetup [Test] > Test passed! :)
        Info (GoTest): > ^^ >> SetupTest::Skipped [Test] > Test skipped! :|
        Info (GoTest): > ^^ >> SetupTest::Crashes [Test] > Test started! :3
        Info (GoTest): > !! >> SetupTest > Test suite error. Aborting! :(
        Info (GoTest): > !! >> Finished testing! :(
        Info (GoTest): > !! >> SpinnerTest::Spins [Test] > Error occurred: two turns are not three
        second line
         Error: 0 : Error (GoTest): Exception:
         Error: 0 : Error (GoTest): System.InvalidOperationException: two turns are not three
        second line
           at Spinner.Tests.SpinnerTest.Spins() in /game/test/SpinnerTest.cs:line 25
           at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
        Info (GoTest): > !! >> SetupTest::Setup [Setup] > Error occurred: setup broke
         Error: 0 : Error (GoTest): Exception:
         Error: 0 : Error (GoTest): System.InvalidOperationException: setup broke
           at Spinner.Tests.SetupTest.Setup() in /game/test/SpinnerTest.cs:line 35
        Info (GoTest): > !! >> Test results: Passed: 2 | Failed: 2 | Skipped: 1

        """;

    [Fact]
    public void A_passing_test_keeps_what_it_printed_and_a_failing_one_its_exception_and_frames()
    {
        IReadOnlyDictionary<string, GoDotTests.Result> results = GoDotTests.Read(Log);
        Assert.Equal(new GoDotTests.Result(GoDotTests.Ending.Passed, null, null, "adding"), results["SpinnerTest::Adds"]);
        GoDotTests.Result spins = results["SpinnerTest::Spins"];
        Assert.Equal(GoDotTests.Ending.Failed, spins.State);
        Assert.Equal("System.InvalidOperationException: two turns are not three\nsecond line", spins.Message);
        Assert.StartsWith("   at Spinner.Tests.SpinnerTest.Spins() in /game/test/SpinnerTest.cs:line 25\n", spins.StackTrace, StringComparison.Ordinal);
        Assert.Equal("spinning 2", spins.Output);
        Assert.Equal(GoDotTests.Ending.Skipped, results["SetupTest::Skipped"].State);
    }

    [Fact]
    public void A_failed_setup_fails_the_test_it_ran_before_and_a_test_that_never_ended_fails_saying_so()
    {
        IReadOnlyDictionary<string, GoDotTests.Result> results = GoDotTests.Read(Log);
        GoDotTests.Result after = results["SetupTest::AfterSetup"];
        Assert.Equal(GoDotTests.Ending.Failed, after.State);
        Assert.Equal("Its [Setup] method Setup failed: System.InvalidOperationException: setup broke", after.Message);
        Assert.Contains("SpinnerTest.cs:line 35", after.StackTrace, StringComparison.Ordinal);
        Assert.False(results.ContainsKey("SetupTest::Setup"), "A [Setup] method was told as a test of its own");
        Assert.Equal("The test never ended: Godot ended as it ran", results["SetupTest::Crashes"].Message);
    }

    [Fact]
    public void A_cleanup_fails_the_test_it_ran_after_and_a_setup_all_every_test_of_its_suite()
    {
        IReadOnlyDictionary<string, GoDotTests.Result> results = GoDotTests.Read("""
            Info (GoTest): > !! >> TurnTest::Prepare [SetupAll] > Test failed! :(
            Info (GoTest): > ^^ >> TurnTest::Turns [Test] > Test started! :3
            Info (GoTest): > OK >> TurnTest::Turns [Test] > Test passed! :)
            Info (GoTest): > ^^ >> TurnTest::Stops [Test] > Test started! :3
            Info (GoTest): > OK >> TurnTest::Stops [Test] > Test passed! :)
            Info (GoTest): > ^^ >> SpinnerTest::Adds [Test] > Test started! :3
            Info (GoTest): > OK >> SpinnerTest::Adds [Test] > Test passed! :)
            Info (GoTest): > !! >> SpinnerTest::TidyUp [Cleanup] > Test failed! :(
            Info (GoTest): > ^^ >> SpinnerTest::Spins [Test] > Test started! :3
            Info (GoTest): > OK >> SpinnerTest::Spins [Test] > Test passed! :)
            Info (GoTest): > !! >> TurnTest::Prepare [SetupAll] > Error occurred: no turns
            """);
        Assert.Equal("Its [SetupAll] method Prepare failed: no turns", results["TurnTest::Turns"].Message);
        Assert.Equal(GoDotTests.Ending.Failed, results["TurnTest::Stops"].State);
        Assert.Equal("Its [Cleanup] method TidyUp failed", results["SpinnerTest::Adds"].Message);
        Assert.Equal(GoDotTests.Ending.Passed, results["SpinnerTest::Spins"].State);
    }

    /// <summary>Each test's end is heard as its line comes, timed from its start's line, a test skipped with no start untimed;
    /// a [Setup]'s line, a failure's report and the rest tell nothing.</summary>
    [Fact]
    public void Each_tests_end_is_heard_as_its_line_comes_timed_from_its_start()
    {
        GoDotTests.Progress progress = new();
        string[] lines = Log.Split('\n');
        List<(string, GoDotTests.Ending, TimeSpan)> heard = [];
        for (int at = 0; at < lines.Length; at++)
        {
            if (progress.Heard(lines[at], TimeSpan.FromSeconds(at)) is { } ended)
            {
                heard.Add(ended);
            }
        }
        Assert.Equal([("SpinnerTest::Adds", GoDotTests.Ending.Passed, TimeSpan.FromSeconds(2)), ("SpinnerTest::Spins", GoDotTests.Ending.Failed, TimeSpan.FromSeconds(2)),
            ("SetupTest::AfterSetup", GoDotTests.Ending.Passed, TimeSpan.FromSeconds(1)), ("SetupTest::Skipped", GoDotTests.Ending.Skipped, TimeSpan.Zero)], heard);
    }

    private const string Project = "config_version=5\n\n[application]\n\nconfig/name=\"Spinner\"\nrun/main_scene=\"res://main.tscn\"\n";

    private static string SceneOf(string script) =>
        $"[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"{script}\" id=\"1\"]\n\n[node name=\"Root\" type=\"Node\"]\nscript = ExtResource(\"1\")\n";

    private const string Runs = "using Chickensoft.GoDotTest;\n\npublic partial class Tests : Godot.Node\n{\n    public override void _Ready() => _ = GoTest.RunTests(GetType().Assembly, this);\n}\n";

    private const string Plays = "public partial class Main : Godot.Node\n{\n    // GoTest.RunTests(...) is never called.\n    public override void _Ready() => Godot.GD.Print(\"GoTest.RunTests\");\n}\n";

    /// <summary>Where a game's tests start, by its main scene's script and the scenes under test/.</summary>
    private static GoDotTests.Start StartOf(string main, string? tests, string? other = null)
    {
        Dictionary<string, GodotFile> files = new() { ["res://main.tscn"] = GodotFile.Parse(SceneOf("res://Main.cs")) };
        Dictionary<string, string> code = new() { ["res://Main.cs"] = main };
        if (tests is not null)
        {
            files["res://test/Tests.tscn"] = GodotFile.Parse(SceneOf("res://test/Tests.cs"));
            code["res://test/Tests.cs"] = tests;
        }
        if (other is not null)
        {
            files["res://a/Other.tscn"] = GodotFile.Parse(SceneOf("res://a/Other.cs"));
            code["res://a/Other.cs"] = other;
        }
        GodotScenes scenes = new("/game", files, texts: new Dictionary<string, string> { ["res://project.godot"] = Project });
        return GoDotTests.StartOf(scenes, res => code.GetValueOrDefault(res));
    }

    [Fact]
    public void A_main_scene_whose_script_runs_the_tests_or_changes_to_a_scene_that_does_starts_the_run()
    {
        Assert.Equal(GoDotTests.Start.Main, StartOf(Runs, null));
        Assert.Equal(GoDotTests.Start.Main, StartOf("public partial class Main : Godot.Node\n{\n    public override void _Ready() => GetTree().ChangeSceneToFile(\"res://test/Tests.tscn\");\n}\n", Runs));
    }

    [Fact]
    public void A_test_scene_under_test_runs_the_tests_where_the_main_scene_does_not_before_any_other_scene()
    {
        Assert.Equal(new GoDotTests.Start("res://test/Tests.tscn"), StartOf(Plays, Runs, other: Runs));
        Assert.Equal(new GoDotTests.Start("res://a/Other.tscn"), StartOf(Plays, null, other: Runs));
    }

    /// <summary>A comment or a string naming GoTest.RunTests calls nothing: such a game never runs its tests.</summary>
    [Fact]
    public void A_game_no_scene_of_which_calls_GoTest_RunTests_never_runs_its_tests()
    {
        Assert.Equal(new GoDotTests.Start(null, GoDotTests.StartProblem.NoScene), StartOf(Plays, Plays));
        Assert.False(GoDotTests.RunsTests(Plays));
        Assert.True(GoDotTests.RunsTests("class T { void M() => Chickensoft.GoDotTest.GoTest.RunTests(null!, null!); }"));
    }

    /// <summary>A main scene's script calling GoTest.RunTests under #if RUN_TESTS, as Chickensoft's game template has it, runs
    /// the tests where the game's symbols define RUN_TESTS, and none where they do not.</summary>
    [Fact]
    public void A_call_under_the_games_own_symbol_runs_the_tests_where_its_project_defines_it()
    {
        const string guarded = "public partial class Main : Godot.Node\n{\n    public override void _Ready()\n    {\n#if RUN_TESTS\n"
            + "        _ = Chickensoft.GoDotTest.GoTest.RunTests(GetType().Assembly, this);\n#endif\n    }\n}\n";
        Dictionary<string, GodotFile> files = new() { ["res://main.tscn"] = GodotFile.Parse(SceneOf("res://Main.cs")) };
        Dictionary<string, string> code = new() { ["res://Main.cs"] = guarded };
        GodotScenes scenes = new("/game", files, texts: new Dictionary<string, string> { ["res://project.godot"] = Project });
        Assert.Equal(GoDotTests.Start.Main, GoDotTests.StartOf(scenes, code.GetValueOrDefault, parsing: Passenger.Languages.CSharp.CSharpParsing.Defining(["DEBUG", "RUN_TESTS"], null)));
        Assert.Equal(GoDotTests.StartProblem.NoScene, GoDotTests.StartOf(scenes, code.GetValueOrDefault).Problem);
    }

    /// <summary>A run settings file names a game's test scene by its GoDotTestScene parameter, which then starts the run where
    /// it runs the tests, and is refused as not the game's or as running none.</summary>
    [Fact]
    public void The_run_settings_name_the_test_scene_which_is_refused_where_missing_or_running_none()
    {
        Assert.Equal("res://ci/Runner.tscn", GoDotTests.ChosenScene(
            "<RunSettings><GdUnit4><Parameters>--headless</Parameters></GdUnit4><TestRunParameters><Parameter name=\"GoDotTestScene\" value=\" res://ci/Runner.tscn \" /></TestRunParameters></RunSettings>"));
        Assert.Null(GoDotTests.ChosenScene("<RunSettings><TestRunParameters><Parameter name=\"Other\" value=\"x\" /></TestRunParameters></RunSettings>"));
        Assert.Null(GoDotTests.ChosenScene("not xml"));

        Dictionary<string, GodotFile> files = new()
        {
            ["res://main.tscn"] = GodotFile.Parse(SceneOf("res://Main.cs")),
            ["res://test/Tests.tscn"] = GodotFile.Parse(SceneOf("res://test/Tests.cs")),
            ["res://ci/Plays.tscn"] = GodotFile.Parse(SceneOf("res://Main.cs")),
        };
        Dictionary<string, string> code = new() { ["res://Main.cs"] = Plays, ["res://test/Tests.cs"] = Runs };
        GodotScenes scenes = new("/game", files, texts: new Dictionary<string, string> { ["res://project.godot"] = Project });
        Assert.Equal(new GoDotTests.Start("res://test/Tests.tscn"), GoDotTests.StartOf(scenes, code.GetValueOrDefault, "res://test/Tests.tscn"));
        Assert.Equal(GoDotTests.StartProblem.ChosenMissing, GoDotTests.StartOf(scenes, code.GetValueOrDefault, "res://ci/Gone.tscn").Problem);
        Assert.Equal(GoDotTests.StartProblem.ChosenRunsNone, GoDotTests.StartOf(scenes, code.GetValueOrDefault, "res://ci/Plays.tscn").Problem);
    }
}

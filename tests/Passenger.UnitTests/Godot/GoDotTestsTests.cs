using Passenger.App.Testing;

namespace Passenger.UnitTests.Godot;

/// <summary>The runs of a game Passenger asks GoDotTest for, and each test's result as it reads back what GoDotTest printed
/// into Godot's log of a run.</summary>
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
    public void A_suite_runs_alone_and_a_test_alone_where_only_it_is_chosen_of_its_suite()
    {
        IReadOnlyList<(string Argument, IReadOnlyList<GoDotTests.Named> Tests)> runs = GoDotTests.Runs([Adds, Spins, Turns], [Adds, Spins, Turns, Stops]);
        Assert.Equal(["--run-tests=SpinnerTest", "--run-tests=TurnTest.Turns"], runs.Select(r => r.Argument));
        Assert.Equal([Adds, Spins], runs[0].Tests);
        Assert.Equal([Turns], runs[1].Tests);
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
}

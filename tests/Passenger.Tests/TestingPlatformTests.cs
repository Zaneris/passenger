using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Settings;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Test projects on Microsoft.Testing.Platform with no VSTest adapter, run through the platform's own protocol:
/// an xUnit v3 project and an MSTest project on MSTest's runner show their tests from code, Run All runs them in their own
/// programs and marks each, a theory's rows land under it, Stop ends a run and its test host, and a solution with a
/// VSTest project too shows and runs both.</summary>
public sealed class TestingPlatformTests : TestingPlatformFolders, IClassFixture<RestoredFolders>
{
    /// <summary>Where each step of a flow takes its own projects from.</summary>
    private readonly RestoredFolders restored;

    public TestingPlatformTests(RestoredFolders restored)
        : base(restored) => this.restored = restored;

    /// <summary>The gate's Testing Platform runs as one flow: a theory's rows found and made by a run landing under
    /// their method, Stop ending a run and its test host keeping what ran, and a VSTest and a Testing Platform project
    /// shown in one tree and run together.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_gates_testing_platform_runs_hold_as_one_flow()
    {
        Flow.StepApart("a theorys rows found by the platform and made by its run land under their method", () => new TestingPlatformTests(restored), t => t.A_theorys_rows_found_by_the_platform_and_made_by_its_run_land_under_their_method());
        Flow.StepApart("Stop ends a run on the platform and its test host keeping what ran", () => new TestingPlatformTests(restored), t => t.Stop_ends_a_run_on_the_platform_and_its_test_host_keeping_what_ran());
        Flow.StepApart("a solution of a vstest project and an mstest project on the platform shows both in one tree and run all runs both", () => new TestingPlatformTests(restored), t => t.A_solution_of_a_vstest_project_and_an_mstest_project_on_the_platform_shows_both_in_one_tree_and_run_all_runs_both());
    }

    /// <summary>A project on the platform alone shows its tests from code and Run All reports each outcome; a project's run
    /// settings reach its tests on MSTest's runner and on one that reads none; a run with coverage measures a VSTest
    /// project's and a platform project's tests over one library as one.</summary>
    [AvaloniaFact]
    public void A_project_on_the_platform_is_shown_from_code_run_whole_and_given_its_run_settings()
    {
        Flow.StepApart("a run with coverage of a vstest and a platform project marks the lines both ran in the gutter counts them in the coverage window and exports them",
            () => new TestingPlatformCoverageTests(restored), t => t.A_run_with_coverage_of_a_vstest_and_a_platform_project_marks_the_lines_both_ran_counts_them_and_exports_them());
        Flow.StepApart("continuous testing asked once at the first run reruns only the tests that ran a line saved and marks each line with how they ended, and nothing once off",
            () => new TestingPlatformCoverageTests(restored), t => t.Continuous_testing_asked_once_reruns_only_the_tests_that_ran_a_line_saved_and_nothing_once_off());
        Flow.StepApart("an xUnit v3 project on the platform alone shows its tests from code and Run All reports pass, fail and skip with output and times",
            () => new TestingPlatformTests(restored), t => t.An_xunit_v3_project_on_the_platform_alone_shows_its_tests_from_code_and_run_all_reports_pass_fail_and_skip_with_output_and_times());
        Flow.StepApart("a project's run settings reach its tests on MSTest's runner and their variables those of a runner that reads none",
            () => new TestingPlatformSettingsTests(restored), t => t.A_projects_run_settings_reach_its_tests_on_mstests_runner_and_their_variables_those_of_a_runner_that_reads_none());
    }

    private void An_xunit_v3_project_on_the_platform_alone_shows_its_tests_from_code_and_run_all_reports_pass_fail_and_skip_with_output_and_times()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(["Sums.Tests", "  Sums.Tests", "    SumTests", "      Doubles", "      Not_yet", "      Takes_a_while", "      Two_and_two_make_five", "      Two_and_two_make_four"],
            Shown(window));
        Assert.Contains(new TestMark(4, TestMarkKind.NotRun), editor.TestMarks);
        Assert.Contains(new TestMark(7, TestMarkKind.NotRun), editor.TestMarks);
        IdeErrors.Flush();
        string runnerLog = Path.Combine(IdeErrors.LogFolder, "vstest.log");
        int logged = File.Exists(runnerLog) ? Ui.Shared(runnerLog).Length : 0;

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Two_and_two_make_four")?.State is TestState.Passed or TestState.Failed, "the run of every test", TimeSpan.FromSeconds(180));
        TestNode four = Method(window, "Two_and_two_make_four")!;
        Assert.True(four.State == TestState.Passed, $"An xUnit v3 test on the Testing Platform alone did not pass, so the project was not run through the platform's protocol:\n{four.Details}\n{window.BottomPanel.Tests.Status}");
        Assert.Equal("Tests passed: 4, failed: 1, skipped: 1 of 6", System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, $" in {SaidWhile.Pattern}$", string.Empty));
        Assert.Contains("adding two and two", four.Details, StringComparison.Ordinal);
        TestNode five = Method(window, "Two_and_two_make_five")!;
        Assert.Equal(TestState.Failed, five.State);
        Assert.Contains("SumTests.cs:line 18", five.Details, StringComparison.Ordinal);
        TestNode skipped = Method(window, "Not_yet")!;
        Assert.Equal(TestState.Skipped, skipped.State);
        Assert.Contains("later", skipped.Details, StringComparison.Ordinal);
        Assert.True(Method(window, "Takes_a_while")!.Duration >= TimeSpan.FromMilliseconds(60),
            $"A test that sleeps 80 ms took {Method(window, "Takes_a_while")!.Duration.TotalMilliseconds} ms by the tree, which has not the platform's time for it");
        Assert.Equal(TestState.Failed, window.Tests.Tree.Roots[0].State);
        Assert.Contains(new TestMark(7, TestMarkKind.Passed), editor.TestMarks);
        Assert.Contains(new TestMark(15, TestMarkKind.Failed), editor.TestMarks);
        // The test host said what it found, in the runner's log.
        IdeErrors.Flush();
        Assert.Matches(@"Discovery complete in Sums\.Tests\.dll \(net10\.0\): 6 tests found", Ui.Shared(runnerLog)[Math.Min(logged, Ui.Shared(runnerLog).Length)..]);
        window.Close();
    }

    private void A_theorys_rows_found_by_the_platform_and_made_by_its_run_land_under_their_method()
    {
        // xUnit cannot list a theory's rows of a record before it runs them: the run makes them.
        Write(folder, "ShapeTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic sealed record Shape(int Sides);\n\npublic class ShapeTests\n{\n" +
            "    public static IEnumerable<object[]> Shapes => [[new Shape(3)], [new Shape(4)]];\n\n" +
            "    [Theory]\n    [MemberData(nameof(Shapes))]\n    public void Has_sides(Shape shape) => Assert.True(shape.Sides > 2);\n}\n");
        MainWindow window = Open();
        Tree(window);
        TestNode doubles = Method(window, "Doubles")!;
        TestNode sides = Method(window, "Has_sides")!;

        Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [doubles, sides]), "the theories' run", TimeSpan.FromSeconds(180));
        Assert.Same(doubles, Method(window, "Doubles"));
        Assert.Equal(["Doubles(value: 1, twice: 2)", "Doubles(value: 2, twice: 4)"], doubles.Children.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(doubles.Children, r => Assert.Equal(TestState.Passed, r.State));
        Assert.Equal(["Has_sides(shape: Shape { Sides = 3 })", "Has_sides(shape: Shape { Sides = 4 })"], sides.Children.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(sides.Children, r => Assert.Equal(TestState.Passed, r.State));
        Assert.Equal(TestState.NotRun, Method(window, "Two_and_two_make_four")!.State);

        window.Tests.Tree.Toggle(doubles);
        TestNode row = doubles.Children[0];
        Assert.Equal(window.Tests.Tree.Rows.IndexOf(doubles) + 1, window.Tests.Tree.Rows.IndexOf(row));
        Tree(window).SelectedItem = row;
        Ui.Settle();
        Assert.Equal("Passed", Details(window).Document.Snapshot.ToString());
        window.Close();
    }

    private void Stop_ends_a_run_on_the_platform_and_its_test_host_keeping_what_ran()
    {
        string host = Path.Combine(folder, "running.txt");
        Write(folder, "SlowTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SlowTests\n{\n    [Fact]\n    public void Waits()\n    {\n" +
            $"        File.WriteAllText(@\"{host}\", Environment.ProcessId.ToString());\n" +
            "        Thread.Sleep(TimeSpan.FromMinutes(5));\n    }\n}\n");
        MainWindow window = Open();

        Assert.True(window.Perform(AppActions.RunAllTests));
        int testHost = Processes.WrittenId(host, "the test host running the tests");
        Assert.True(window.Perform(AppActions.StopTests));
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run to stop", TimeSpan.FromSeconds(60));
        Assert.True(Processes.Gone(testHost, TimeSpan.FromSeconds(30)), $"The test host ({Processes.Describe(testHost)}) still ran after Stop");
        Assert.EndsWith(", stopped", window.BottomPanel.Tests.Status, StringComparison.Ordinal);
        Assert.Equal(TestState.NotRun, Method(window, "Waits")!.State);
        window.Close();
    }

    private void A_solution_of_a_vstest_project_and_an_mstest_project_on_the_platform_shows_both_in_one_tree_and_run_all_runs_both()
    {
        string solution = NewFolder();
        Write(solution, "Vs.Tests/Vs.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
            $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
        Write(solution, "Vs.Tests/AddTests.cs", "using Xunit;\n\nnamespace Vs.Tests;\n\npublic class AddTests\n{\n    [Fact]\n    public void Adds() => Assert.Equal(3, 1 + 2);\n}\n");
        // MSTest on its own runner: an executable with no test SDK, which vstest.console could not run.
        Write(solution, "Store.Tests/Store.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
            "    <EnableMSTestRunner>true</EnableMSTestRunner>\n  </PropertyGroup>\n  <ItemGroup>\n" +
            $"    <PackageReference Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestRunner}\" />\n    <PackageReference Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestRunner}\" />\n  </ItemGroup>\n</Project>\n");
        Write(solution, "Store.Tests/StoreTests.cs",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Store.Tests;\n\n[TestClass]\npublic class StoreTests\n{\n" +
            "    [TestMethod]\n    public void Keeps() => Assert.AreEqual(2, 1 + 1);\n\n" +
            "    [TestMethod]\n    [DataRow(1)]\n    [DataRow(2)]\n    public void Counts(int items) => Assert.IsTrue(items > 0);\n\n" +
            "    [TestMethod]\n    [DataRow(1, DisplayName = \"Repeats (same)\")]\n    [DataRow(2, DisplayName = \"Repeats (same)\")]\n" +
            "    [DataRow(3, DisplayName = \"Repeats (same)\")]\n    public void Repeats(int items) => Assert.IsTrue(items > 0);\n}\n");
        Write(solution, "Suite.slnx", "<Solution>\n  <Project Path=\"Vs.Tests/Vs.Tests.csproj\" />\n  <Project Path=\"Store.Tests/Store.Tests.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(solution, "Suite.slnx"));
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Tree(window);
        Ui.WaitFor(() => Method(window, "Keeps") is not null && Method(window, "Adds") is not null, "both projects' tests read from the code");

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Keeps")?.State is not (null or TestState.NotRun or TestState.Running), "the run of every test", TimeSpan.FromSeconds(240));
        Assert.True(Method(window, "Adds")!.State == TestState.Passed, $"The VSTest project's test did not pass:\n{Method(window, "Adds")!.Details}\n{window.BottomPanel.Tests.Status}");
        Assert.True(Method(window, "Keeps")!.State == TestState.Passed, $"The MSTest project on the platform's runner did not pass:\n{Method(window, "Keeps")!.Details}\n{window.BottomPanel.Tests.Status}");
        Assert.Equal("Tests passed: 7, failed: 0 of 7", System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, $" in {SaidWhile.Pattern}$", string.Empty));
        window.Tests.Tree.Toggle(Method(window, "Counts")!);
        window.Tests.Tree.Toggle(Method(window, "Repeats")!);
        // Rows of one name are told apart by the order they came in.
        Assert.Equal(
            [
                "Vs.Tests", "  Vs.Tests", "    AddTests", "      Adds",
                "Store.Tests", "  Store.Tests", "    StoreTests", "      Counts", "        Counts (1)", "        Counts (2)", "      Keeps",
                "      Repeats", "        Repeats (same)", "        Repeats (same) (2)", "        Repeats (same) (3)",
            ],
            Shown(window));
        window.Close();
    }
}

/// <summary>What a project's run settings file says reaches its tests on the platform: MSTest's runner reads a parameter
/// from them as VSTest's adapter does, and a variable reaches xUnit v3's tests too, whose runner reads no run settings. A
/// step of <see cref="TestingPlatformTests"/>' flow.</summary>
public sealed class TestingPlatformSettingsTests : TestingPlatformFolders
{
    public TestingPlatformSettingsTests(RestoredFolders restored)
        : base(restored)
    {
    }

    internal void A_projects_run_settings_reach_its_tests_on_mstests_runner_and_their_variables_those_of_a_runner_that_reads_none()
    {
        const string Named = "    <RunSettingsFilePath>$(MSBuildProjectDirectory)/../settled.runsettings</RunSettingsFilePath>\n  </PropertyGroup>";
        string solution = NewFolder();
        Write(solution, "settled.runsettings", NUnitFolders.RunSettings);
        Write(solution, "Store.Tests/Store.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
            $"    <EnableMSTestRunner>true</EnableMSTestRunner>\n{Named}\n  <ItemGroup>\n" +
            $"    <PackageReference Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestRunner}\" />\n    <PackageReference Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestRunner}\" />\n  </ItemGroup>\n</Project>\n");
        Write(solution, "Store.Tests/StoreTests.cs",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Store.Tests;\n\n[TestClass]\npublic class StoreTests\n{\n    public TestContext TestContext { get; set; } = null!;\n\n" +
            "    [TestMethod]\n    public void Reads_the_parameter() => Assert.AreEqual(\"the parameter\", TestContext.Properties[\"who\"]);\n\n" +
            "    [TestMethod]\n    public void Reads_the_variable() => Assert.AreEqual(\"the run settings\", System.Environment.GetEnvironmentVariable(\"SETTLED_BY\"));\n}\n");
        Write(solution, "Sums.Tests/Sums.Tests.csproj", XunitProject.Replace("  </PropertyGroup>", Named, StringComparison.Ordinal));
        Write(solution, "Sums.Tests/SumTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Reads_it_too() => Assert.Equal(\"the run settings\", Environment.GetEnvironmentVariable(\"SETTLED_BY\"));\n}\n");
        Write(solution, "Suite.slnx", "<Solution>\n  <Project Path=\"Store.Tests/Store.Tests.csproj\" />\n  <Project Path=\"Sums.Tests/Sums.Tests.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(solution, "Suite.slnx"));
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Tree(window);
        Ui.WaitFor(() => Method(window, "Reads_the_parameter") is not null && Method(window, "Reads_it_too") is not null, "both projects' tests read from the code");

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Reads_it_too")?.State is not (null or TestState.NotRun or TestState.Running), "the run of every test", TimeSpan.FromSeconds(240));
        foreach (string test in new[] { "Reads_the_parameter", "Reads_the_variable", "Reads_it_too" })
        {
            Assert.True(Method(window, test)!.State == TestState.Passed, $"{test} ran without what its project's run settings say:\n{Method(window, test)!.Details}");
        }
        window.Close();
    }
}

/// <summary>Coverage measured as a run of tests runs, a step of <see cref="TestingPlatformTests"/>' flow: a VSTest project's
/// tests through Coverlet's collector and a platform project's under Coverlet's console over a copy of its output, both over
/// one library, whose branches the two runs take together.</summary>
public sealed class TestingPlatformCoverageTests : TestingPlatformFolders
{
    /// <summary>The library both projects test: a method each runs, one whose two branches each runs one of and whose last
    /// line one leaves a branch of untaken, and one neither runs.</summary>
    private const string Measured =
        "namespace Shapes;\n\npublic static class Measure\n{\n    public static int Twice(int value) => value * 2;\n\n" +
        "    public static int Sign(int value)\n    {\n        if (value > 0)\n        {\n            return 1;\n        }\n        return value < 0 ? -1 : 0;\n    }\n\n" +
        "    public static int Never(int value)\n    {\n        return value - 1;\n    }\n}\n";

    public TestingPlatformCoverageTests(RestoredFolders restored)
        : base(restored)
    {
    }

    /// <summary>The solution of a VSTest project and a Testing Platform project over one library, restored: its folder and the
    /// library's file.</summary>
    private (string Solution, string Shapes) Suite()
    {
        string solution = NewFolder();
        string shapes = Write(solution, "Shapes/Measure.cs", Measured);
        Write(solution, "Shapes/Shapes.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        const string Library = "  <ItemGroup>\n    <ProjectReference Include=\"../Shapes/Shapes.csproj\" />\n  </ItemGroup>\n</Project>";
        Write(solution, "Vs.Tests/Vs.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n" +
            $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n{Library}\n");
        Write(solution, "Vs.Tests/MeasureTests.cs",
            "using Xunit;\n\nnamespace Vs.Tests;\n\npublic class MeasureTests\n{\n    [Fact]\n    public void Doubles() => Assert.Equal(4, Shapes.Measure.Twice(2));\n\n" +
            "    [Fact]\n    public void Signs_a_positive() => Assert.Equal(1, Shapes.Measure.Sign(5));\n}\n");
        Write(solution, "Store.Tests/Store.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
            "    <EnableMSTestRunner>true</EnableMSTestRunner>\n  </PropertyGroup>\n  <ItemGroup>\n" +
            $"    <PackageReference Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestRunner}\" />\n    <PackageReference Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestRunner}\" />\n  </ItemGroup>\n{Library}\n");
        Write(solution, "Store.Tests/SignTests.cs",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Store.Tests;\n\n[TestClass]\npublic class SignTests\n{\n" +
            "    [TestMethod]\n    public void Signs_a_negative() => Assert.AreEqual(-1, Shapes.Measure.Sign(-5));\n\n" +
            "    [TestMethod]\n    public void Doubles_too() => Assert.AreEqual(6, Shapes.Measure.Twice(3));\n}\n");
        Write(solution, "Suite.slnx", "<Solution>\n  <Project Path=\"Shapes/Shapes.csproj\" />\n  <Project Path=\"Vs.Tests/Vs.Tests.csproj\" />\n  <Project Path=\"Store.Tests/Store.Tests.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(solution, "Suite.slnx"));
        return (solution, shapes);
    }

    internal void Continuous_testing_asked_once_reruns_only_the_tests_that_ran_a_line_saved_and_nothing_once_off()
    {
        (string solution, string shapes) = Suite();
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Unseen = false };
        window.Show();
        window.OpenFolder(solution);
        Ui.Settle();
        Tree(window);
        Ui.WaitFor(() => Method(window, "Signs_a_negative") is not null, "the platform project's tests read from the code");

        // The person's first run of tests in the folder's repository asks, without taking the keys, whether to test continuously.
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => window.Questions.Showing(Declared.ContinuousTesting) is not null, "the question whether to test continuously at the first run of tests");
        ConfirmDialog asked = window.Questions.Showing(Declared.ContinuousTesting)!;
        Assert.False(asked.IsActive, "The question took the keys from what the person was doing");
        Assert.DoesNotContain(asked.GetVisualDescendants().OfType<Button>(), b => b.IsDefault);
        Ui.ClickButton(asked, "Turn On");
        Assert.True(window.Questions.Answered(Declared.ContinuousTesting), "Turn On was not kept as the repository's answer");
        Ui.WaitFor(() => !window.Tests.IsBusy && window.Continuous is { Rounds: > 0, IsRunning: false } && window.Continuous.Map.Tests.Count > 0,
            () => $"continuous testing's first round, after the person's run: {window.Continuous.Said}", TimeSpan.FromSeconds(300));
        Assert.Equal(["Doubles", "Doubles_too", "Signs_a_negative", "Signs_a_positive"], window.Continuous.LastRan.Select(t => t.Method[(t.Method.LastIndexOf('.') + 1)..]).Order());

        // Each line of the library shows how the tests that run it ended; a press on one lists them.
        window.OpenFile(shapes);
        Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Reach is { Count: > 0 }, () => $"the library's lines marked from the map: {window.Continuous.Said}");
        Assert.True(document.Reach!.At(12) is { State: ReachState.Passed, Tests: [{ } negative] } && negative.EndsWith("Signs_a_negative", StringComparison.Ordinal),
            $"Sign's last line, which Signs_a_negative alone runs, was marked {document.Reach.At(12)}");
        Assert.True(document.Reach.At(8) is { State: ReachState.Passed, Tests.Length: 2 }, $"Sign's if, which both projects' tests of Sign run, was marked {document.Reach.At(8)}");
        Assert.Equal(ReachState.Unreached, document.Reach.At(17)?.State);
        CodeEditor editor = window.EditorArea.CodeEditor;
        double x = Enumerable.Range(0, 400).First(x => editor.IsOnGutterColumn(editor.Gutter!.Coverage, new Avalonia.Point(x, 0))) + 3;
        Ui.Click(editor, new Avalonia.Point(x, editor.PointOf(document.Text.Snapshot.GetLineStart(12)).Y + (editor.LineHeight / 2)));
        Assert.Equal(["Line 13, run by 1 test", "Signs_a_negative (passed)", "Run This Test"],
            Assert.IsType<MenuFlyout>(window.ReachMenu).Items.OfType<MenuItem>().Select(i => i.Header as string));
        window.ReachMenu!.Hide();

        // A line saved changed reruns the one test that ran it of either project's, which now fails, and the lines it runs say so.
        int rounds = window.Continuous.Rounds;
        Typing.Replace(window, "value < 0 ? -1", "value < 0 ? -2");
        Assert.True(window.Perform(AppActions.Save));
        Ui.WaitFor(() => window.Continuous is { IsRunning: false } && window.Continuous.Rounds > rounds && window.Continuous.LastRan.Count > 0,
            () => $"the round the save brought: {window.Continuous.Said}", TimeSpan.FromSeconds(300));
        Assert.Equal(["Signs_a_negative"], window.Continuous.LastRan.Select(t => t.Method[(t.Method.LastIndexOf('.') + 1)..]));
        Ui.WaitFor(() => document.Reach?.At(12)?.State == ReachState.Failed, () => $"Sign's last line marked failed: {document.Reach?.At(12)}");
        Assert.Equal(ReachState.Passed, document.Reach!.At(4)?.State);

        // The person's next run asks nothing.
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy, () => $"the person's second run: {window.BottomPanel.Tests.Status}", TimeSpan.FromSeconds(300));
        Assert.Null(window.Questions.Showing(Declared.ContinuousTesting));

        // Turned off, a save runs nothing, and what it built for the folder is gone.
        Assert.True(window.Perform(AppActions.ContinuousTesting));
        Assert.False(window.Continuous.IsOn);
        Ui.WaitFor(() => !Directory.Exists(window.Continuous.Root!), () => $"the folder's cache, {window.Continuous.Root}, removed as continuous testing was turned off");
        rounds = window.Continuous.Rounds;
        Typing.Replace(window, "value < 0 ? -2", "value < 0 ? -1");
        Assert.True(window.Perform(AppActions.Save));
        Ui.Hold(TimeSpan.FromSeconds(3), "the folder's watcher hearing the save");
        Ui.Settle();
        Assert.Equal(rounds, window.Continuous.Rounds);
        Assert.Null(document.Reach);
        window.Close();
    }

    internal void A_run_with_coverage_of_a_vstest_and_a_platform_project_marks_the_lines_both_ran_counts_them_and_exports_them()
    {
        (string solution, string shapes) = Suite();
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        window.OpenFile(shapes);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Tree(window);
        Ui.WaitFor(() => Method(window, "Doubles") is not null && Method(window, "Signs_a_negative") is not null, "both projects' tests read from the code");

        Assert.True(window.Perform(AppActions.CoverAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && window.Tests.Coverage is not null, () => $"the run with coverage: {window.BottomPanel.Tests.Status}", TimeSpan.FromSeconds(300));
        Assert.True(window.BottomPanel.Tests.Status.EndsWith("; 70% of lines (7 of 10) covered", StringComparison.Ordinal),
            $"The run with coverage did not say what both projects' tests ran of the library's ten lines of code: {window.BottomPanel.Tests.Status}\n{string.Join("\n", window.Tests.Unmeasured)}");
        EditorGutter gutter = editor.Gutter!;
        CoveredLines lines = gutter.Coverage.Lines!;
        // Lines from zero: Twice's, Sign's if (one branch each project's), Sign's last (its zero never) and Never's.
        Assert.Equal(CoverageState.Covered, lines.At(4)?.State);
        Assert.True(lines.At(8) is { State: CoverageState.Covered, Branches: 2, BranchesTaken: 2 },
            $"Sign's if, each of whose branches one project's test took, was not marked whole: {lines.At(8)}, so the two runs' branches were not taken together");
        Assert.True(lines.At(12) is { State: CoverageState.Partial, Branches: 2, BranchesTaken: 1 }, $"Sign's last line, whose zero no test takes, was marked {lines.At(12)}");
        Assert.Equal(CoverageState.Missed, lines.At(17)?.State);
        Assert.Null(lines.At(5));
        Assert.True(gutter.Coverage.Width(new Passenger.Editor.GutterFont(8, 16)) > 0, "The gutter took no room for the file's coverage");

        // The Coverage window came forward with the run's figures, the library alone measured.
        Ui.WaitFor(() => window.DockHost.IsShown(ToolWindowIds.Coverage), "the Coverage window brought forward by the run");
        CoverageView view = window.CoverageWindow;
        Assert.Equal("70% of lines (7 of 10), 75% of branches (3 of 4) run.", view.Summary);
        view.ExpandAll();
        Ui.Settle();
        Assert.Equal(["Shapes 70% 7 of 10 75%", "  Shapes 70% 7 of 10 75%", "    Measure 70% 7 of 10 75%", "      Never 0% 0 of 3 —", "      Sign 100% 6 of 6 75%", "      Twice 100% 1 of 1 —"],
            view.Rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Node.Name} {CoverageCounts.Percent(r.Node.Counts.LineRate)} {r.Node.Counts.LinesRun} of {r.Node.Counts.Lines} {CoverageCounts.Percent(r.Node.Counts.BranchRate)}"));
        view.Sort(1);
        Assert.Equal("Never", view.Rows[3].Node.Name);
        view.Filter = "Twice";
        Ui.Settle();
        Assert.Equal(["Shapes", "Shapes", "Measure", "Twice"], view.Rows.Select(r => r.Node.Name));
        view.Filter = string.Empty;

        // The marks keep to their code as lines are typed above it.
        editor.Focus();
        Ui.PlaceAfter(editor, "public static class Measure\n{");
        Ui.Press(window, "ENTER");
        Assert.Null(lines.At(4));
        Assert.Equal(CoverageState.Covered, lines.At(5)?.State);
        Assert.Equal(CoverageState.Missed, lines.At(18)?.State);

        // Exported as one Cobertura report, where the person names.
        string exported = Path.Combine(solution, "coverage.cobertura.xml");
        window.PickCoverageExport = () => Task.FromResult<string?>(exported);
        Assert.True(window.Perform(AppActions.ExportCoverage));
        Ui.WaitFor(() => File.Exists(exported), () => $"the coverage exported: the window said {window.Status}");
        System.Xml.Linq.XElement report = System.Xml.Linq.XDocument.Load(exported).Root!;
        Assert.Equal(("0.7", "0.75"), ((string?)report.Attribute("line-rate"), (string?)report.Attribute("branch-rate")));
        Assert.Equal(shapes, (string?)report.Descendants("class").Single(c => (string?)c.Attribute("name") == "Shapes.Measure").Attribute("filename"));

        Assert.True(window.Perform(AppActions.ClearCoverage));
        Assert.Null(gutter.Coverage.Lines);
        Assert.Empty(view.Rows);

        // Coverlet measures a copy of the platform project's output, so a run stopped partway leaves none of the build's instrumented.
        string built = Directory.EnumerateFiles(Path.Combine(solution, "Store.Tests", "bin"), "Shapes.dll", SearchOption.AllDirectories).Single();
        FileStamp? before = FileStamp.Of(built);
        Assert.True(window.Perform(AppActions.CoverAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && window.Tests.Coverage is not null, () => $"the second run with coverage: {window.BottomPanel.Tests.Status}", TimeSpan.FromSeconds(300));
        Assert.True(FileStamp.Of(built) == before, "The run with coverage wrote the platform project's built Shapes.dll, where Coverlet measures a copy of it");
        window.Close();
    }
}

/// <summary>Debug Tests on Microsoft.Testing.Platform: a breakpoint inside a test stops it, and Stop as the test host
/// starts under the debugger ends the run saying why. Debugged through the window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class TestingPlatformDebuggingTests : TestingPlatformFolders, IClassFixture<RestoredFolders>
{
    /// <summary>Where each step of a flow takes its own projects from.</summary>
    private readonly RestoredFolders restored;

    public TestingPlatformDebuggingTests(RestoredFolders restored)
        : base(restored) => this.restored = restored;

    /// <summary>Debug Tests at the caret stops at a breakpoint inside a test on the Testing Platform, a debugged run stops at a
    /// test's breakpoint and its own exception but never at a skip, and Stop as a debugged run's test host starts leaves no
    /// test running and says why.</summary>
    [AvaloniaFact]
    public void Debug_Tests_on_the_Testing_Platform_stops_inside_a_test_and_Stop_as_its_host_starts_leaves_none_running()
    {
        Flow.StepApart<TestingPlatformDebuggingTests>("debug tests at the caret stops at a breakpoint inside a test on the platform", () => new TestingPlatformDebuggingTests(restored), t =>
        {
            t.Debug_tests_at_the_caret_stops_at_a_breakpoint_inside_a_test_on_the_platform();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<TestingPlatformDebuggingTests>("a debugged run stops at a test's breakpoint and its own exception but never at a skip", () => new TestingPlatformDebuggingTests(restored), t =>
        {
            t.A_debugged_run_stops_at_a_tests_breakpoint_and_its_own_exception_but_never_at_a_skip();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<TestingPlatformDebuggingTests>("stop as a debugged run's test host starts leaves no test running and says why", () => new TestingPlatformDebuggingTests(restored), t =>
        {
            t.Stop_as_a_debugged_runs_test_host_starts_leaves_no_test_running_and_says_why();
            OpenWindows.CloseAll();
        });
    }

    private void Debug_tests_at_the_caret_stops_at_a_breakpoint_inside_a_test_on_the_platform()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(10, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);

        Ui.Run(window, AppActions.DebugTests);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames.Count > 0, "the stop in the test", TimeSpan.FromSeconds(180));
        Assert.Contains(new LineMark(10, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains("Two_and_two_make_four", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => !window.Tests.IsBusy, "the debugged run to end", TimeSpan.FromSeconds(60));
        Assert.Equal(TestState.Passed, Method(window, "Two_and_two_make_four")!.State);
        Assert.Equal(TestState.NotRun, Method(window, "Two_and_two_make_five")!.State);
        Ui.WaitFor(() => window.Debugger.Session is null || window.Debugger.Session.State == DebugState.Ended, "the test host to end once its run was done");
        window.Close();
    }

    /// <summary>A class's tests debugged with Any exception on, as every folder has it, in whatever order xUnit runs them: the
    /// run stops at one's breakpoint and at the exception another's own code leaves to xUnit, each once, and never at a third's
    /// Assert.Skip, which xUnit catches to tell the test skipped (TestOutcomeSignals); then the run ends, each test told.</summary>
    private void A_debugged_run_stops_at_a_tests_breakpoint_and_its_own_exception_but_never_at_a_skip()
    {
        string tests = Write(folder, "OutcomeTests.cs", "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class OutcomeTests\n{\n" +
            "    [Fact]\n    public void Skips_as_it_runs() => Assert.Skip(\"not on this machine\");\n\n" +
            "    [Fact]\n    public void Throws_its_own() => throw new InvalidOperationException(\"its own\");\n\n" +
            "    [Fact]\n    public void Stops_at_its_breakpoint()\n    {\n        int reached = 1;\n        Assert.Equal(1, reached);\n    }\n}\n");
        MainWindow window = Open();
        window.OpenFile(tests);
        Ui.Settle();
        Ui.WaitFor(() => Method(window, "Skips_as_it_runs") is not null, "the outcome tests read from the code");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(15, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        editor.GoTo(new TextPosition(4, 13));
        Ui.Run(window, AppActions.DebugTests);
        List<string> stops = [];
        // The stops taken so far, counted as each is taken: the next may come while Resume settles.
        int taken = 0;
        while (true)
        {
            Ui.WaitFor(() => !window.Tests.IsBusy || (window.Debugger.Session is { State: DebugState.Paused, Stops: int now } && now > taken && window.Debugger.Stop is not null),
                () => $"the debugged run to stop or end after {(stops.Count == 0 ? "no stop" : Plural.Listed(stops))}: {window.BottomPanel.Tests.Status}\n{Ui.Said(window)}", TimeSpan.FromSeconds(180));
            if (!window.Tests.IsBusy)
            {
                break;
            }
            taken = window.Debugger.Session!.Stops;
            string top = window.Debugger.Stop!.Top?.Name ?? "no frame";
            stops.Add(window.Debugger.Session!.StopReason == "exception" ? $"{window.Debugger.Stop.Exception} in {top}" : $"{window.Debugger.Session.StopReason} in {top}");
            Assert.True(stops.Count <= 2 && !top.Contains("Skips_as_it_runs", StringComparison.Ordinal),
                $"The debugged run stopped at {Plural.Listed(stops)}, where it stops at the breakpoint and the test's own exception alone, never at a skip xUnit tells");
            Ui.Run(window, AppActions.Resume);
        }
        Assert.True(stops.Count == 2 && stops.Any(s => s.StartsWith("breakpoint in ", StringComparison.Ordinal) && s.Contains("Stops_at_its_breakpoint", StringComparison.Ordinal))
                && stops.Any(s => s.Contains("InvalidOperationException", StringComparison.Ordinal) && s.Contains("Throws_its_own", StringComparison.Ordinal)),
            $"The debugged run stopped at {Plural.Listed(stops)}, not once at the breakpoint and once at the test's own exception");
        Assert.True(Method(window, "Skips_as_it_runs")!.State == TestState.Skipped, $"The test that skips as it runs is {Method(window, "Skips_as_it_runs")!.State}, not skipped");
        Assert.Equal(TestState.Failed, Method(window, "Throws_its_own")!.State);
        Assert.Equal(TestState.Passed, Method(window, "Stops_at_its_breakpoint")!.State);
        window.Close();
    }

    /// <summary>A debugged run ends when Stop ends its test host's start under the debugger (held at its start here, by a
    /// diagnostic port of the test's own that the project's launch profile names): no test, group or run icon is left
    /// running, and the Unit Tests window and the status bar say why the run ended.</summary>
    private void Stop_as_a_debugged_runs_test_host_starts_leaves_no_test_running_and_says_why()
    {
        using Debugging.Helpers.StartHold hold = new();
        Write(folder, "Properties/launchSettings.json",
            $"{{ \"profiles\": {{ \"Sums\": {{ \"commandName\": \"Project\", \"environmentVariables\": {System.Text.Json.JsonSerializer.Serialize(hold.Environment)} }} }} }}");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(10, 8));
        Task<int> held = hold.Held(TimeSpan.FromMinutes(3));
        OwnProcess host = default;
        try
        {
            // Not settled: the start holds the window's background work while the debugger waits for the test host.
            Assert.True(window.Perform(AppActions.DebugTests), $"Debug Tests did not start: {Ui.Said(window)}");
            host = OwnProcess.Of(Ui.AwaitPumped(held, "the build, then the test host's runtime to come to the hold", TimeSpan.FromMinutes(3)));
            TestNode four = Method(window, "Two_and_two_make_four")!;
            Assert.Equal(TestState.Running, four.State);

            Assert.True(window.Perform(AppActions.Stop), $"Stop did not run while the debugger started the test host: {Ui.Said(window)}");
            Ui.PumpUntil(() => !window.Tests.IsBusy, "the debugged run to end once Stop ended its test host's start", TimeSpan.FromSeconds(30));
            Ui.Settle();
            List<string> running = [.. Every(window.Tests.Tree.Roots).Where(n => n.State == TestState.Running).Select(n => n.Name)];
            Assert.True(running.Count == 0, $"A run stopped as its test host started left these shown as running: {string.Join(", ", running)}");
            Assert.Equal(TestState.NotRun, four.State);
            Assert.DoesNotContain(editor.TestMarks, m => m.Kind == TestMarkKind.Running);
            string? why = window.BottomPanel.Tests.Status;
            Assert.True(why is not null && System.Text.RegularExpressions.Regex.IsMatch(why, $@"^Tests passed: 0, failed: 0 of 0 in {SaidWhile.Pattern}, stopped$"),
                $"A debugged run Stop ended as its test host started did not say it was stopped, but '{why}' (the status bar '{window.Status}')");
            Assert.True(window.Status == why, $"The status bar's last word on the stopped run is '{window.Status}', where the Unit Tests window says '{why}'");
            string debugged = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.True(debugged.Contains("Debugging Tests was stopped as it started", StringComparison.Ordinal),
                $"The Debug window does not say its test host was stopped as it started: '{debugged}'");
            Assert.Null(window.Debugger.Session);
        }
        finally
        {
            host.Kill();
        }
        window.Close();

        static IEnumerable<TestNode> Every(IEnumerable<TestNode> nodes) => nodes.SelectMany(n => Every(n.Children).Prepend(n));
    }
}

/// <summary>The xUnit v3 project on the platform alone each test takes, restored, the folders it writes, and the helpers
/// that open the window on them and read the Unit Tests window.</summary>
public abstract class TestingPlatformFolders : IDisposable
{
    /// <summary>xUnit v3 on the platform alone: its own program, with no test SDK or VSTest adapter.</summary>
    internal static readonly string XunitProject =
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
        $"    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"xunit.v3\" Version=\"{TestPackages.XunitApp}\" />\n  </ItemGroup>\n</Project>\n";

    protected readonly string folder;
    protected readonly string source;
    private readonly List<string> folders = [];

    protected TestingPlatformFolders(RestoredFolders restored)
    {
        folder = PathNames.Canonical(restored.Take("mtp-", made =>
        {
            string project = PathNames.Canonical(made);
            Write(project, "Sums.Tests.csproj", XunitProject);
            Write(project, "SumTests.cs",
                "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests(ITestOutputHelper output)\n{\n" +
                "    [Fact]\n    public void Two_and_two_make_four()\n    {\n        output.WriteLine(\"adding two and two\");\n        int sum = 2 + 2;\n        Assert.Equal(4, sum);\n    }\n\n" +
                "    [Fact]\n    public void Two_and_two_make_five()\n    {\n        Assert.Equal(5, 2 + 2);\n    }\n\n" +
                "    [Theory]\n    [InlineData(1, 2)]\n    [InlineData(2, 4)]\n    public void Doubles(int value, int twice) => Assert.Equal(twice, value * 2);\n\n" +
                "    [Fact(Skip = \"later\")]\n    public void Not_yet() { }\n\n" +
                "    [Fact]\n    public void Takes_a_while() => Thread.Sleep(80);\n}\n");
            return project;
        }));
        source = Path.Combine(folder, "SumTests.cs");
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        foreach (string made in folders)
        {
            TestFolders.Delete(made);
        }
    }

    /// <summary>A window on the test's project with its tests' file open and shown in the Unit Tests window, with no C#
    /// language server, which finding, running and debugging tests ask nothing of.</summary>
    protected MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(source);
        Ui.Settle();
        Tree(window);
        Ui.WaitFor(() => Method(window, "Two_and_two_make_four") is not null, "the tests read from the code");
        return window;
    }

    protected string NewFolder()
    {
        string made = PathNames.Canonical(TestFolders.Create("mtp-"));
        folders.Add(made);
        return made;
    }

    protected static string Write(string root, string relative, string text) => TestFolders.Write(root, relative, text);

    /// <summary>The Unit Tests window's rows as a person reads them, indented by depth.</summary>
    protected static List<string> Shown(MainWindow window) => [.. window.Tests.Tree.Rows.Select(r => new string(' ', r.Depth * 2) + r.Name)];

    protected static ListBox Tree(MainWindow window)
    {
        window.Perform(AppActions.ShowTests);
        Ui.Settle();
        return window.BottomPanel.Tests.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, window.Tests.Tree.Rows));
    }

    /// <summary>The Unit Tests window's details of the chosen row, beside the tree.</summary>
    protected static CodeEditor Details(MainWindow window) =>
        window.BottomPanel.Tests.GetVisualDescendants().OfType<CodeEditor>().First(e => e.IsReadOnly);

    protected static TestNode? Method(MainWindow window, string name) =>
        window.Tests.Tree.Roots.SelectMany(r => r.Methods).FirstOrDefault(m => m.Name == name);
}

/// <summary>What a Testing Platform test host wrote last, quoted in the failure the runner says once the host ends.</summary>
[Collection(Beside.Name)]
public sealed class TestHostLastWordsTests
{
    private const string LastWords = "the test host's last words";

    /// <summary>A test host that writes a last line and exits at once has that line in its failure every time, and one
    /// whose connection comes after its end is heard is said to have connected.</summary>
    [Fact]
    public async Task A_test_hosts_last_line_and_late_connection_are_heard_however_it_ends()
    {
        await Flow.StepApartAsync<TestHostLastWordsTests>("a test host that writes a last line and exits at once has that line in its failure every time", () => new TestHostLastWordsTests(), t => t.A_test_host_that_writes_a_last_line_and_exits_at_once_has_that_line_in_its_failure_every_time());
        await Flow.StepApartAsync<TestHostLastWordsTests>("a test host whose connection comes after its end is heard is said to have connected", () => new TestHostLastWordsTests(), t => t.A_test_host_whose_connection_comes_after_its_end_is_heard_is_said_to_have_connected());
    }

    /// <summary>A test host that connects, writes more than a pipe holds and then one last line, and exits at once, before
    /// it answers the runner, has that last line in the failure the runner says, every one of twenty times beside two busy
    /// processes: what it wrote is read to its end before it is quoted, never stopping at an earlier line.</summary>
    private async Task A_test_host_that_writes_a_last_line_and_exits_at_once_has_that_line_in_its_failure_every_time()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string program = Passenger.Tests.Debugging.Helpers.Debuggees.Program("PassengerCases");
        TestAssembly assembly = new(new ProjectModel(Path.ChangeExtension(program, ".csproj"), "PassengerCases", "Exe", ["net10.0"], [], [], true), "net10.0", program);
        using TestingPlatformRunner runner = new();
        List<System.Diagnostics.Process> busy = ChildOutputTests.Busy();
        try
        {
            for (int round = 1; round <= 20; round++)
            {
                List<string> said = [];
                FoundTests found = await runner.DiscoverAsync([assembly], line =>
                {
                    lock (said)
                    {
                        said.Add(line);
                    }
                }, cancel);
                string failure;
                lock (said)
                {
                    failure = string.Join("\n", said);
                }
                Assert.True(found.Tests.Count == 0 && failure.Contains("(exit code 3)", StringComparison.Ordinal) && failure.EndsWith($"\n{LastWords}", StringComparison.Ordinal),
                    $"Round {round}: the test host that wrote '{LastWords}' as it exited was said to fail with '{failure}'");
            }
        }
        finally
        {
            ChildOutputTests.End(busy);
        }
    }

    /// <summary>A test host that connects, writes and ends at once, whose connection the runner is handed only after it has
    /// heard the host end, as a loaded machine may hand the two over, is said to have ended after it connected, with its
    /// last line: whether it connected is its connection's to say, never the order the runner heard the two in.</summary>
    private async Task A_test_host_whose_connection_comes_after_its_end_is_heard_is_said_to_have_connected()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string program = Passenger.Tests.Debugging.Helpers.Debuggees.Program("PassengerCases");
        TestAssembly assembly = new(new ProjectModel(Path.ChangeExtension(program, ".csproj"), "PassengerCases", "Exe", ["net10.0"], [], [], true), "net10.0", program);
        using TestingPlatformRunner runner = new() { HoldsConnectionsUntilEnded = true };
        List<string> said = [];
        FoundTests found = await runner.DiscoverAsync([assembly], line =>
        {
            lock (said)
            {
                said.Add(line);
            }
        }, cancel);
        string failure;
        lock (said)
        {
            failure = string.Join("\n", said);
        }
        Assert.True(found.Tests.Count == 0 && failure.StartsWith("PassengerCases (net10.0) ended before it answered initialize (exit code 3):\n", StringComparison.Ordinal)
            && failure.EndsWith($"\n{LastWords}", StringComparison.Ordinal),
            $"The test host that connected before it ended, its connection heard after its end, was said to fail with '{failure}'");
    }
}

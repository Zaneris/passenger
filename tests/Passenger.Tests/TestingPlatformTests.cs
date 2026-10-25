using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Test projects on Microsoft.Testing.Platform with no VSTest adapter, run through the platform's own protocol:
/// an xUnit v3 project and an MSTest project on MSTest's runner show their tests from code, Run All runs them in their own
/// programs and marks each, a theory's rows land under it, Stop ends a run and its test host, and a solution with a
/// VSTest project too shows and runs both.</summary>
public sealed class TestingPlatformTests : TestingPlatformFolders, IClassFixture<RestoredFolders>
{
    public TestingPlatformTests(RestoredFolders restored)
        : base(restored)
    {
    }

    [AvaloniaFact]
    public void An_xunit_v3_project_on_the_platform_alone_shows_its_tests_from_code_and_run_all_reports_pass_fail_and_skip_with_output_and_times()
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
        Assert.Equal("Tests passed: 4, failed: 1, skipped: 1 of 6", System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, @" in \d+\.\d s$", string.Empty));
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

    [AvaloniaFact]
    public void A_theorys_rows_found_by_the_platform_and_made_by_its_run_land_under_their_method()
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

    [AvaloniaFact]
    public void Stop_ends_a_run_on_the_platform_and_its_test_host_keeping_what_ran()
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

    [AvaloniaFact]
    public void A_solution_of_a_vstest_project_and_an_mstest_project_on_the_platform_shows_both_in_one_tree_and_run_all_runs_both()
    {
        string solution = NewFolder();
        Write(solution, "Vs.Tests/Vs.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
            "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
        Write(solution, "Vs.Tests/AddTests.cs", "using Xunit;\n\nnamespace Vs.Tests;\n\npublic class AddTests\n{\n    [Fact]\n    public void Adds() => Assert.Equal(3, 1 + 2);\n}\n");
        // MSTest on its own runner: an executable with no test SDK, which vstest.console could not run.
        Write(solution, "Store.Tests/Store.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
            "    <EnableMSTestRunner>true</EnableMSTestRunner>\n  </PropertyGroup>\n  <ItemGroup>\n" +
            "    <PackageReference Include=\"MSTest.TestFramework\" Version=\"4.5.1\" />\n    <PackageReference Include=\"MSTest.TestAdapter\" Version=\"4.5.1\" />\n  </ItemGroup>\n</Project>\n");
        Write(solution, "Store.Tests/StoreTests.cs",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Store.Tests;\n\n[TestClass]\npublic class StoreTests\n{\n" +
            "    [TestMethod]\n    public void Keeps() => Assert.AreEqual(2, 1 + 1);\n\n" +
            "    [TestMethod]\n    [DataRow(1)]\n    [DataRow(2)]\n    public void Counts(int items) => Assert.IsTrue(items > 0);\n}\n");
        Write(solution, "Suite.slnx", "<Solution>\n  <Project Path=\"Vs.Tests/Vs.Tests.csproj\" />\n  <Project Path=\"Store.Tests/Store.Tests.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(solution, "Suite.slnx"));
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Tree(window);
        Ui.WaitFor(() => Method(window, "Keeps") is not null && Method(window, "Adds") is not null, "both projects' tests read from the code");

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Keeps")?.State is not (null or TestState.NotRun or TestState.Running), "the run of every test", TimeSpan.FromSeconds(240));
        Assert.True(Method(window, "Adds")!.State == TestState.Passed, $"The VSTest project's test did not pass:\n{Method(window, "Adds")!.Details}\n{window.BottomPanel.Tests.Status}");
        Assert.True(Method(window, "Keeps")!.State == TestState.Passed, $"The MSTest project on the platform's runner did not pass:\n{Method(window, "Keeps")!.Details}\n{window.BottomPanel.Tests.Status}");
        Assert.Equal("Tests passed: 4, failed: 0 of 4", System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, @" in \d+\.\d s$", string.Empty));
        window.Tests.Tree.Toggle(Method(window, "Counts")!);
        Assert.Equal(
            [
                "Vs.Tests", "  Vs.Tests", "    AddTests", "      Adds",
                "Store.Tests", "  Store.Tests", "    StoreTests", "      Counts", "        Counts (1)", "        Counts (2)", "      Keeps",
            ],
            Shown(window));
        window.Close();
    }
}

/// <summary>Debug Tests on Microsoft.Testing.Platform: a breakpoint inside a test stops it, and Stop as the test host
/// starts under the debugger ends the run saying why. Debugged through the window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class TestingPlatformDebuggingTests : TestingPlatformFolders, IClassFixture<RestoredFolders>
{
    public TestingPlatformDebuggingTests(RestoredFolders restored)
        : base(restored)
    {
    }

    [AvaloniaFact]
    public void Debug_tests_at_the_caret_stops_at_a_breakpoint_inside_a_test_on_the_platform()
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

    /// <summary>A debugged run ends when Stop ends its test host's start under the debugger (held at its start here, by a
    /// diagnostic port of the test's own that the project's launch profile names): no test, group or run icon is left
    /// running, and the Unit Tests window and the status bar say why the run ended.</summary>
    [AvaloniaFact]
    public void Stop_as_a_debugged_runs_test_host_starts_leaves_no_test_running_and_says_why()
    {
        using Debugging.Helpers.StartHold hold = new();
        Write(folder, "Properties/launchSettings.json",
            $"{{ \"profiles\": {{ \"Sums\": {{ \"commandName\": \"Project\", \"environmentVariables\": {System.Text.Json.JsonSerializer.Serialize(hold.Environment)} }} }} }}");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(10, 8));
        Task<int> held = hold.Held(TimeSpan.FromMinutes(3));
        int host = 0;
        try
        {
            // Not settled: the start holds the window's background work while the debugger waits for the test host.
            Assert.True(window.Perform(AppActions.DebugTests), $"Debug Tests did not start: {Ui.Said(window)}");
            Ui.PumpUntil(() => held.IsCompleted, "the build, then the test host's runtime to come to the hold", TimeSpan.FromMinutes(3));
            host = held.Result;
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
            Assert.True(why is not null && System.Text.RegularExpressions.Regex.IsMatch(why, @"^Tests passed: 0, failed: 0 of 0 in \d+\.\d s, stopped$"),
                $"A debugged run Stop ended as its test host started did not say it was stopped, but '{why}' (the status bar '{window.Status}')");
            Assert.True(window.Status == why, $"The status bar's last word on the stopped run is '{window.Status}', where the Unit Tests window says '{why}'");
            string debugged = window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.True(debugged.Contains("Debugging Tests was stopped as it started", StringComparison.Ordinal),
                $"The Debug window does not say its test host was stopped as it started: '{debugged}'");
            Assert.Null(window.Debugger.Session);
        }
        finally
        {
            Processes.Kill(host);
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
    private const string XunitProject =
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <OutputType>Exe</OutputType>\n" +
        "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"xunit.v3\" Version=\"4.0.2\" />\n  </ItemGroup>\n</Project>\n";

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

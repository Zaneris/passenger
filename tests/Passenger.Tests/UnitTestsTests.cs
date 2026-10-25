using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Unit testing from the window: the Unit Tests window shows a solution's tests from their code as it opens and
/// follows them as they are typed, Run All finds and runs a project's tests and marks each where the code put it, and Run
/// Tests at the caret runs that test alone. The project is xUnit's, run through the SDK's own VSTest. The windows start no
/// C# language server, which finding, running and showing tests ask nothing of.</summary>
public sealed class UnitTestsTests : UnitTestsFolders, IClassFixture<RestoredFolders>
{
    public UnitTestsTests(RestoredFolders restored)
        : base(restored)
    {
    }

    /// <summary>Run All, stopped during its build and pressed again, marks each test, the Unit Tests window kept in front
    /// through its build, the status bar's tally a link back to it; a failed test's stack trace in its details links to the
    /// line that failed; Run Tests at the caret runs that test alone; and a build that fails brings the Build window forward
    /// with its error.</summary>
    [AvaloniaFact]
    public void Run_all_marks_each_test_a_failure_links_to_its_line_run_at_the_caret_runs_that_test_alone_and_a_failed_build_brings_the_build_window_forward()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Contains(new TestMark(4, TestMarkKind.NotRun), editor.TestMarks);
        Assert.Contains(new TestMark(7, TestMarkKind.NotRun), editor.TestMarks);

        Assert.True(window.Perform(AppActions.RunAllTests));
        Assert.True(window.IsBuilding, "Run All did not build first");
        window.Perform(AppActions.Stop);
        Ui.WaitFor(() => !window.Tests.IsBusy, "the runner to hear the build was stopped");
        Assert.False(window.IsBuilding);
        Assert.Equal("Build stopped", window.Status);

        string runnerLog = Path.Combine(IdeErrors.LogFolder, "vstest.log");
        IdeErrors.Flush();
        int logged = Ui.Shared(runnerLog).Length;
        // Run All has no key of Rider's: the Unit Tests window's button runs it.
        Assert.True(window.Perform(AppActions.RunAllTests));
        Assert.Equal("Building Sums.Tests.csproj…", window.Status);
        Assert.True(window.IsBuilding && window.DockHost.IsShown(ToolWindowIds.Tests), "Run All's build took the Unit Tests window's place as it started");
        Assert.False(window.Perform(AppActions.RunTests) || window.Perform(AppActions.RunAllTests), "A second test run started while the first one worked");
        Assert.StartsWith("Already under way: ", window.Actions.WhyNot(AppActions.RunAllTests, window.Actions.Context()), StringComparison.Ordinal);
        Avalonia.Controls.Button runAll = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window.BottomPanel.Tests).OfType<Avalonia.Controls.Button>()
            .Single(b => ActionButtons.GetAction(b) == AppActions.RunAllTests);
        Assert.True(ActionButtons.IsWorking(runAll), "The Unit Tests window's Run All should show the run working, as the toolbar's buttons show theirs");
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Two_and_two_make_four")?.State == TestState.Passed, "the run of every test", TimeSpan.FromSeconds(120));
        Assert.Null(window.Actions.Work.Of(AppActions.RunTests));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Tests), "After Run All the Unit Tests window was no longer in front, so its results had to be shown again");
        // vstest.console ran under the SDK's dotnet, which a VSTest package without the constructor for it fails here.
        Assert.Equal("Tests passed: 3, failed: 1, skipped: 1 of 5", System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, @" in \d+\.\d s$", string.Empty));
        // The runner's log in the logs folder has what it found and how the run went.
        IdeErrors.Flush();
        string run = Ui.Shared(runnerLog)[Math.Min(logged, Ui.Shared(runnerLog).Length)..];
        Assert.Matches(@"Discovery complete: \d+ tests? found", run);
        Assert.Matches(@"Run complete: \d+ results? in \d+\.\d\d s", run);
        Assert.Equal(TestState.Failed, Method(window, "Two_and_two_make_five")!.State);
        Assert.Equal(TestState.Passed, Method(window, "Doubles")!.State);
        Assert.Equal(2, Method(window, "Doubles")!.Results.Count);
        Assert.Equal(TestState.Skipped, Method(window, "Not_yet")!.State);
        Assert.Contains("SumTests.cs:line 17", Method(window, "Two_and_two_make_five")!.Details, StringComparison.Ordinal);
        Assert.Equal(TestState.Failed, window.Tests.Tree.Roots[0].State);
        Assert.Contains(new TestMark(7, TestMarkKind.Passed), editor.TestMarks);
        Assert.Contains(new TestMark(14, TestMarkKind.Failed), editor.TestMarks);
        Assert.Contains(new TestMark(4, TestMarkKind.Failed), editor.TestMarks);
        window.ShowToolWindow(ToolWindowIds.Problems);
        Ui.Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StatusMessage"));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Tests), $"A click on \"{window.Status}\" in the status bar did not bring the Unit Tests window back");

        ListBox tree = Tree(window);
        tree.SelectedItem = Method(window, "Two_and_two_make_five");
        Ui.Settle();
        CodeEditor details = Details(window);
        string shown = Text(details);
        Assert.True(shown.Contains("SumTests.cs:line 17", StringComparison.Ordinal), $"The failed test's details have no frame at its line:\n{shown}");
        // The path's start, which the pane shows however long the path is.
        int frame = shown.IndexOf("Two_and_two_make_five() in ", StringComparison.Ordinal) + "Two_and_two_make_five() in ".Length + 1;
        // Away from the file's line first, so the click is what brings it back there.
        editor.GoTo(new TextPosition(0, 0));
        Avalonia.Point at = details.TranslatePoint(details.PointOf(frame) + new Avalonia.Vector(2, details.LineHeight / 2), window)!.Value;
        window.MouseMove(at);
        Ui.Settle();
        Assert.True(details.HoveredLink is not null, $"The pointer rests on the frame's file in the details, but it is no link:\n{shown}");
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.Equal(PathNames.Canonical(Source), window.EditorArea.Active?.Path);
        Assert.Equal(new TextPosition(16, 0), window.EditorArea.CodeEditor.CaretPosition);

        TestOutcome four = Method(window, "Two_and_two_make_four")!.Results[0];
        TestOutcome five = Method(window, "Two_and_two_make_five")!.Results[0];
        window.EditorArea.CodeEditor.GoTo(new TextPosition(9, 8));
        Ui.Run(window, AppActions.RunTests);
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Two_and_two_make_four")!.Results.FirstOrDefault() is { } fresh && fresh != four, "the run of one test", TimeSpan.FromSeconds(60));
        Assert.Equal(TestState.Passed, Method(window, "Two_and_two_make_four")!.State);
        Assert.Same(five, Method(window, "Two_and_two_make_five")!.Results[0]);
        Assert.Equal(TestState.Failed, Method(window, "Two_and_two_make_five")!.State);

        Write("Broken.cs", "namespace Sums.Tests;\n\npublic static class Broken\n{\n    public static int Count => \"none\";\n}\n");
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run to end at its build", TimeSpan.FromSeconds(120));
        Assert.StartsWith("Build failed", window.Status, StringComparison.Ordinal);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Build), "The tests' build failed, but the Build window did not come forward with its error as Rider's does");
        Assert.True(window.StatusPlace?.Name == "see it in Problems, Build", $"\"{window.Status}\" in the status bar goes to '{window.StatusPlace?.Name}', not to the build's errors in Problems");
        Assert.Contains("Broken.cs(5,32): error CS0029", window.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Below_a_comment_holding_line_breaks_only_roslyn_counts_each_tests_icon_and_menu_stay_on_its_line()
    {
        // Roslyn counts four more lines in the comment than the editor does.
        File.WriteAllText(Source, File.ReadAllText(Source).Replace("public class SumTests",
            "/* Pasted:\u2028a line separator,\u2029a paragraph separator,\u0085a next line,\ra carriage return. */\npublic class SumTests", StringComparison.Ordinal));
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Contains(new TestMark(5, TestMarkKind.NotRun), editor.TestMarks);
        Assert.Contains(new TestMark(8, TestMarkKind.NotRun), editor.TestMarks);
        Assert.Contains(new TestMark(15, TestMarkKind.NotRun), editor.TestMarks);

        int name = editor.Document.Snapshot.ToString().IndexOf("Two_and_two_make_five", StringComparison.Ordinal) + 2;
        Ui.RightClick(editor, editor.PointOf(name) + new Vector(1, editor.LineHeight / 2));
        Avalonia.Controls.ContextMenu menu = window.OpenContextMenu ?? throw new InvalidOperationException("No context menu opened");
        Assert.Contains("Run 'Two_and_two_make_five'", menu.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => i.Header as string));
        menu.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Run_until_failure_repeats_a_test_until_it_fails_and_shows_that_run()
    {
        // Passes twice, then fails: it counts its runs in a file of its own.
        string count = Path.Combine(Folder, "runs.txt");
        Write("FlakyTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class FlakyTests\n{\n    [Fact]\n    public void Flaky()\n    {\n" +
            $"        string path = @\"{count}\";\n" +
            "        int run = (File.Exists(path) ? int.Parse(File.ReadAllText(path)) : 0) + 1;\n        File.WriteAllText(path, run.ToString());\n" +
            "        Assert.True(run < 3, $\"failed on its run {run}\");\n    }\n}\n");
        MainWindow window = Open();
        Task<string?> found = window.Tests.DiscoverAsync(window.Folder!);
        Ui.WaitFor(() => found.IsCompleted, "the tests to be found", TimeSpan.FromSeconds(120));
        Assert.Null(found.Result);

        window.Perform(AppActions.ShowTests);
        Avalonia.Controls.ListBox tree = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.BottomPanel.Tests)
            .OfType<Avalonia.Controls.ListBox>().First(l => ReferenceEquals(l.ItemsSource, window.Tests.Tree.Rows));
        Ui.WaitFor(() => window.Tests.Tree.Rows.Any(r => r.Name == "FlakyTests"), "the flaky test's class in the tree");
        if (window.Tests.Tree.Rows.First(r => r.Name == "FlakyTests") is { IsExpanded: false } flaky)
        {
            window.Tests.Tree.Toggle(flaky);
        }
        tree.SelectedItem = window.Tests.Tree.Rows.First(r => r.Name == "Flaky");
        Ui.ChooseFromMenu(tree, "Run Until Failure");
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Flaky")?.State == TestState.Failed, "the run that fails", TimeSpan.FromSeconds(180));
        Assert.Equal(3, window.Tests.Runs);
        Assert.Equal("Flaky failed on run 3", window.Status);
        Assert.Contains("failed on its run 3", Method(window, "Flaky")!.Details, StringComparison.Ordinal);
        Assert.Same(Method(window, "Flaky"), tree.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_built_for_two_frameworks_runs_under_each_and_shows_each_ones_results_under_it()
    {
        // The frameworks come from a property, as MSBuild evaluates them, not as the project file writes them.
        string solution = NewFolder();
        Write(solution, "Frameworks.Tests/Frameworks.Tests.csproj", TestProject($"{TestProjects.Framework};net11.0"));
        Write(solution, "Frameworks.Tests/RuntimeTests.cs",
            "using Xunit;\n\nnamespace Frameworks.Tests;\n\npublic class RuntimeTests\n{\n    [Fact]\n    public void Runs_on_eleven() => Assert.Equal(11, Environment.Version.Major);\n\n" +
            "    [Fact]\n    public void Runs_anywhere() { }\n}\n");
        Restore.Run(Path.Combine(solution, "Frameworks.Tests"));
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run of every test", TimeSpan.FromSeconds(180));
        TestNode project = Assert.Single(window.Tests.Tree.Roots);
        Assert.Equal([TestProjects.Framework, "net11.0"], project.Children.Select(c => c.Name));
        Assert.All(project.Children, c => Assert.Equal(TestNodeKind.Framework, c.Kind));
        Assert.Equal(TestState.Failed, Under(TestProjects.Framework, "Runs_on_eleven").State);
        Assert.Equal(TestState.Passed, Under("net11.0", "Runs_on_eleven").State);
        Assert.Equal(TestState.Passed, Under(TestProjects.Framework, "Runs_anywhere").State);
        Assert.Equal(TestState.Passed, Under("net11.0", "Runs_anywhere").State);
        Assert.Equal(TestState.Failed, project.State);
        Assert.Equal(TestState.Passed, project.Children[1].State);
        Assert.Equal(["Frameworks.Tests", TestProjects.Framework, "Frameworks.Tests", "RuntimeTests", "Runs_anywhere", "Runs_on_eleven", "net11.0"],
            window.Tests.Tree.Rows.Take(7).Select(r => r.Name));
        window.Close();

        TestNode Under(string framework, string method) => project.Children.Single(c => c.Name == framework).Methods.Single(m => m.Name == method);
    }

    [AvaloniaFact]
    public void Stop_in_the_unit_tests_window_ends_a_run_while_it_builds_and_while_it_finds_the_tests()
    {
        // Finding this theory's rows waits until it is stopped, once it has said which test host looks for them.
        string host = Path.Combine(Folder, "discovering.txt");
        Write("SlowDiscoveryTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SlowDiscoveryTests\n{\n    public static IEnumerable<object[]> Rows\n    {\n        get\n        {\n" +
            $"            File.WriteAllText(@\"{host}\", Environment.ProcessId.ToString());\n" +
            "            Thread.Sleep(TimeSpan.FromMinutes(5));\n            yield return new object[] { 1 };\n        }\n    }\n\n" +
            "    [Theory]\n    [MemberData(nameof(Rows))]\n    public void Slow(int value) => Assert.Equal(1, value);\n}\n");
        MainWindow window = Open();
        Assert.True(window.Perform(AppActions.ShowTests));
        Ui.Settle();
        Button stop = StopButton(window);
        Assert.False(stop.IsEffectivelyEnabled, "The Unit Tests window's Stop could be pressed with no run going on");
        Assert.True(stop.GetVisualDescendants().OfType<PathIcon>().Single().Opacity < 1, "The Unit Tests window's Stop looked ready to press with no run going on");
        Assert.False(window.Perform(AppActions.StopTests), "Stop Unit Tests ran with no run going on");

        Assert.True(window.Perform(AppActions.RunAllTests));
        Assert.True(window.IsBuilding, "Run All did not build first");
        Assert.True(stop.IsEffectivelyEnabled, "The Unit Tests window's Stop was disabled while Run All built");
        // The build's dotnet command starts once the project host has held its design-time builds off.
        Ui.PumpUntil(() => window.Builder.ProcessId is not null, "the build's dotnet command to start");
        int? build = window.Builder.ProcessId;
        Assert.True(window.Perform(AppActions.StopTests));
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run to stop while it builds");
        Assert.False(stop.IsEffectivelyEnabled, "The Unit Tests window's Stop could still be pressed once the run it stopped had ended");
        Assert.False(window.IsBuilding);
        Assert.True(build is not { } building || Processes.Gone(building), "The build went on after Stop");
        Assert.Equal("Build stopped", window.BottomPanel.Tests.Status);

        Assert.True(window.Perform(AppActions.RunAllTests));
        int testHost = Processes.WrittenId(host, "the test host finding the tests");
        Assert.True(window.Perform(AppActions.StopTests));
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run to stop while it finds the tests", TimeSpan.FromSeconds(60));
        Assert.True(Processes.Gone(testHost, TimeSpan.FromSeconds(30)), $"The test host finding the tests ({Processes.Describe(testHost)}) still ran after Stop");
        Assert.Equal(TestController.StoppedMessage, window.BottomPanel.Tests.Status);
        List<TestNode> shown = [.. window.Tests.Tree.Roots.SelectMany(r => r.Methods)];
        Assert.Contains(shown, m => m.Name == "Slow");
        Assert.All(shown, m => Assert.Equal(TestState.NotRun, m.State));
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_in_one_file_among_many_open_reads_and_marks_that_files_tests_and_no_others()
    {
        string solution = NewFolder();
        List<string> paths = [];
        for (int i = 0; i < 12; i++)
        {
            StringBuilder file = new("using Xunit;\n\nnamespace Many.Tests;\n\n");
            file.Append(System.Globalization.CultureInfo.InvariantCulture, $"public class File{i}Tests\n{{\n");
            for (int test = 0; test < 3; test++)
            {
                file.Append(System.Globalization.CultureInfo.InvariantCulture, $"    [Fact]\n    public void Case_{test}() {{ }}\n\n");
            }
            paths.Add(Write(solution, $"File{i}Tests.cs", file.Append("}\n").ToString()));
        }
        MainWindow window = Ui.OpenFolder(solution, autosaves: false);
        foreach (string path in paths)
        {
            window.OpenFile(path);
        }
        Ui.Settle();
        List<Passenger.App.Workspace.OpenDocument> open = [.. window.EditorArea.Documents];
        Assert.Equal(12, open.Count);
        Ui.WaitFor(() => open.All(d => d.TestMarks.Count == 4), "every file's run icons");
        Passenger.App.Workspace.OpenDocument typed = window.EditorArea.Active!;
        List<Passenger.App.Workspace.OpenDocument> others = [.. open.Where(d => d != typed)];
        Dictionary<Passenger.App.Workspace.OpenDocument, int> marked = others.ToDictionary(d => d, _ => 0);
        foreach (Passenger.App.Workspace.OpenDocument other in others)
        {
            other.PropertyChanged += (_, e) => marked[other] += e.PropertyName == nameof(other.TestMarks) ? 1 : 0;
        }
        Dictionary<Passenger.App.Workspace.OpenDocument, int> reads = others.ToDictionary(d => d, window.TestGutter.Reads);

        // A test typed into the file shown, a key at a time with pauses, then left long enough for anything waiting on one.
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.ToString().LastIndexOf('}'));
        editor.Focus();
        foreach (string key in new[] { "[", "Fact", "]", "\n", "public void Added(", ") ", "{", " }", "\n" })
        {
            window.KeyTextInput(key);
            Ui.Hold(TimeSpan.FromMilliseconds(40), "a pause between keys, as a person types");
        }
        Ui.WaitFor(() => typed.TestMarks.Count == 5, "the new test's run icon");
        Ui.Hold(TimeSpan.FromMilliseconds(400), "long enough for anything waiting on a pause in typing to run");
        Ui.Settle();
        Assert.Contains(new TestMark(editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("Added", StringComparison.Ordinal)), TestMarkKind.NotRun), typed.TestMarks);
        Assert.True(others.All(d => marked[d] == 0), $"Typing in {typed.Name} gave other files their run icons again: {string.Join(", ", others.Where(d => marked[d] > 0).Select(d => $"{d.Name} {marked[d]} times"))}");
        Assert.True(others.All(d => window.TestGutter.Reads(d) == reads[d]), $"Typing in {typed.Name} read other files' tests again: {string.Join(", ", others.Where(d => window.TestGutter.Reads(d) != reads[d]).Select(d => d.Name))}");
        Assert.True(window.TestGutter.Reads(typed) > 0, "The file typed in was never read for its tests");
        window.Close();
    }

    [AvaloniaFact]
    public void Run_unit_tests_on_a_solution_folder_chosen_in_the_explorer_runs_the_tests_of_its_projects_alone()
    {
        string solution = NewFolder();
        Write(solution, "tests/Fast.Tests/Fast.Tests.csproj", TestProject(TestProjects.Framework));
        Write(solution, "tests/Fast.Tests/FastTests.cs", "using Xunit;\n\nnamespace Fast.Tests;\n\npublic class FastTests\n{\n    [Fact]\n    public void Quick() { }\n}\n");
        Write(solution, "slow/Slow.Tests/Slow.Tests.csproj", TestProject(TestProjects.Framework));
        Write(solution, "slow/Slow.Tests/SlowTests.cs", "using Xunit;\n\nnamespace Slow.Tests;\n\npublic class SlowTests\n{\n    [Fact]\n    public void Patient() { }\n}\n");
        Write(solution, "Suite.slnx", "<Solution>\n  <Folder Name=\"/tests/\">\n    <Project Path=\"tests/Fast.Tests/Fast.Tests.csproj\" />\n  </Folder>\n  <Project Path=\"slow/Slow.Tests/Slow.Tests.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(solution, "Suite.slnx"));
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);

        Passenger.App.Explorer.ExplorerNode folder = window.ExplorerView.Tree.Rows.First(r => r.Kind == Passenger.App.Explorer.NodeKind.SolutionFolder && r.Name == "tests");
        Ui.ClickRow(window, folder);
        Ui.Run(window, AppActions.RunTests);
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Quick")?.State == TestState.Passed, "the solution folder's tests to run", TimeSpan.FromSeconds(180));
        Assert.True(Method(window, "Patient")?.State == TestState.NotRun, "Run Unit Tests on the tests folder ran the tests of a project outside it too");
        window.Close();
    }

    [AvaloniaFact]
    public void A_solution_never_built_shows_its_xunit_nunit_and_mstest_tests_from_their_code_as_it_opens()
    {
        string solution = NewFolder();
        Write(solution, "Library/Library.csproj", TestProjects.Library());
        Write(solution, "Library/Sum.cs", "namespace Library;\n\npublic static class Sum\n{\n    public static int Of(int a, int b) => a + b;\n}\n");
        Write(solution, "Xunit.Tests/Xunit.Tests.csproj", TestProject(TestProjects.Framework));
        Write(solution, "Xunit.Tests/SumTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Adds() { }\n\n    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n" +
            "    public void Doubles(int value) { }\n\n    public void Helper() { }\n}\n\npublic abstract class BaseTests\n{\n    [Fact]\n    public void Inherited() { }\n}\n");
        Write(solution, "NUnit.Tests/NUnit.Tests.csproj", PackagesProject("NUnit", "NUnit3TestAdapter"));
        Write(solution, "NUnit.Tests/ParserTests.cs",
            "using NUnit.Framework;\n\nnamespace Parsing.Tests;\n\n[TestFixture]\npublic class ParserTests\n{\n    [Test]\n    public void Reads() { }\n\n" +
            "    [TestCase(1)]\n    [TestCase(2)]\n    public void Counts(int lines) { }\n}\n");
        Write(solution, "MSTest.Tests/MSTest.Tests.csproj", PackagesProject("MSTest"));
        Write(solution, "MSTest.Tests/StoreTests.cs",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Store.Tests;\n\n[TestClass]\npublic class StoreTests\n{\n    [TestMethod]\n    public void Keeps() { }\n}\n");
        Write(solution, "Tests.slnx", "<Solution>\n  <Project Path=\"Library/Library.csproj\" />\n  <Project Path=\"Xunit.Tests/Xunit.Tests.csproj\" />\n" +
            "  <Project Path=\"NUnit.Tests/NUnit.Tests.csproj\" />\n  <Project Path=\"MSTest.Tests/MSTest.Tests.csproj\" />\n</Solution>\n");

        // No C# language server, whose restore would ask nuget.org for the floating NUnit and MSTest versions every run.
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Tree(window);
        // A theory is one test, and an abstract class's tests show only under the classes deriving from it.
        Assert.Equal(
            [
                "Xunit.Tests", "  Sums.Tests", "    SumTests", "      Adds", "      Doubles",
                "NUnit.Tests", "  Parsing.Tests", "    ParserTests", "      Counts", "      Reads",
                "MSTest.Tests", "  Store.Tests", "    StoreTests", "      Keeps",
            ],
            Shown(window));
        Assert.False(window.IsBuilding, "Showing the tests started a build");
        TestNode doubles = Method(window, "Doubles")!;
        Assert.Empty(doubles.Children);
        Assert.Equal((Path.Combine(solution, "Xunit.Tests", "SumTests.cs"), 13), (doubles.Path, doubles.Line));

        Write(solution, "Xunit.Tests/MoreTests.cs", "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class MoreTests\n{\n    [Fact]\n    public void Written_elsewhere() { }\n}\n");
        File.Delete(Path.Combine(solution, "MSTest.Tests", "StoreTests.cs"));
        Ui.WaitFor(() => Method(window, "Written_elsewhere") is not null && Method(window, "Keeps") is null, "the tree to follow a file written and one deleted by another program");
        Assert.DoesNotContain("MSTest.Tests", Shown(window));
        TreeKeysCheck.Press(window, window.Tests.Tree.Rows, window.Tests.Tree.Rows.First(r => r.Name == "Sums.Tests"), "the Unit Tests window");

        string plain = NewFolder();
        Write(plain, "Library/Library.csproj", TestProjects.Library());
        window.OpenFolder(plain);
        Ui.Settle();
        Assert.Empty(window.Tests.Tree.Rows);
        Assert.Contains("No test projects: none references xUnit, NUnit or MSTest", ShownTexts(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Run_all_lands_on_the_tests_shown_from_code_with_a_theorys_rows_under_it_and_a_test_it_never_ran_stays()
    {
        // xUnit cannot list a theory's rows of a record before it runs them: they come from the run.
        Write("ShapeTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic sealed record Shape(int Sides);\n\npublic class ShapeTests\n{\n" +
            "    public static IEnumerable<object[]> Shapes => [[new Shape(3)], [new Shape(4)]];\n\n" +
            "    [Theory]\n    [MemberData(nameof(Shapes))]\n    public void Has_sides(Shape shape) => Assert.True(shape.Sides > 2);\n}\n");
        MainWindow window = Open();
        window.Autosaves = false;
        Tree(window);
        TestNode four = Method(window, "Two_and_two_make_four")!;
        TestNode five = Method(window, "Two_and_two_make_five")!;
        TestNode doubles = Method(window, "Doubles")!;
        TestNode sides = Method(window, "Has_sides")!;
        Assert.All([four, five, doubles, sides], m => Assert.Equal(TestState.NotRun, m.State));
        Assert.Empty(doubles.Children);

        Assert.True(window.Perform(AppActions.RunAllTests));
        Assert.True(window.IsBuilding, "Run All did not build first");
        // Typed while the run builds what was saved, so the run never hears of it.
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Selections = SelectionSet.Single(Text(editor).LastIndexOf('}'));
        editor.Focus();
        foreach (string key in new[] { "[", "Fact", "]", "\n", "public void Typed_during_the_run(", ") ", "{", " }", "\n" })
        {
            window.KeyTextInput(key);
            Ui.Hold(TimeSpan.FromMilliseconds(40), "a pause between keys, as a person types");
        }
        Ui.WaitFor(() => !window.Tests.IsBusy && four.State == TestState.Passed, "the run of every test", TimeSpan.FromSeconds(120));

        Assert.Same(four, Method(window, "Two_and_two_make_four"));
        Assert.Same(doubles, Method(window, "Doubles"));
        Assert.Same(sides, Method(window, "Has_sides"));
        Assert.Equal(TestState.Failed, five.State);
        Assert.Equal(TestState.Skipped, Method(window, "Not_yet")!.State);
        Assert.Equal(["Doubles(value: 1, twice: 2)", "Doubles(value: 2, twice: 4)"], doubles.Children.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(doubles.Children, r => Assert.Equal(TestState.Passed, r.State));
        Assert.Equal(TestState.Passed, doubles.State);
        Assert.Equal(["Has_sides(shape: Shape { Sides = 3 })", "Has_sides(shape: Shape { Sides = 4 })"], sides.Children.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.All(sides.Children, r => Assert.Equal(TestState.Passed, r.State));
        TestNode typed = Method(window, "Typed_during_the_run") ?? throw new InvalidOperationException("The test typed during the run left the tree when the run ended");
        Assert.Equal(TestState.NotRun, typed.State);
        Assert.Empty(typed.Results);

        window.Tests.Tree.Toggle(doubles);
        ListBox tree = Tree(window);
        TestNode row = doubles.Children[0];
        Assert.Equal(window.Tests.Tree.Rows.IndexOf(doubles) + 1, window.Tests.Tree.Rows.IndexOf(row));
        tree.SelectedItem = row;
        Ui.Settle();
        Assert.Equal("Passed", Text(Details(window)));
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_a_test_adds_it_to_the_tree_renaming_or_removing_one_follows_and_a_test_that_ran_keeps_its_result()
    {
        MainWindow window = Open();
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Two_and_two_make_four")?.State == TestState.Passed, "the run of every test", TimeSpan.FromSeconds(120));
        TestNode four = Method(window, "Two_and_two_make_four")!;
        TestOutcome ran = four.Results[0];
        CodeEditor editor = window.EditorArea.CodeEditor;

        editor.Selections = SelectionSet.Single(Text(editor).LastIndexOf('}'));
        editor.Focus();
        foreach (string key in new[] { "[", "Fact", "]", "\n", "public void Added(", ") ", "{", " }", "\n" })
        {
            window.KeyTextInput(key);
            Ui.Hold(TimeSpan.FromMilliseconds(40), "a pause between keys, as a person types");
        }
        Ui.WaitFor(() => Method(window, "Added") is not null, "the typed test in the tree");
        Assert.Equal(TestState.NotRun, Method(window, "Added")!.State);

        int five = Text(editor).IndexOf("make_five", StringComparison.Ordinal) + "make_".Length;
        editor.Selections = SelectionSet.Single(new Selection(five, five + "five".Length));
        window.KeyTextInput("six");
        Ui.WaitFor(() => Method(window, "Two_and_two_make_six") is not null && Method(window, "Two_and_two_make_five") is null, "the renamed test in the tree by its new name alone");

        int from = Text(editor).IndexOf("    [Fact(Skip", StringComparison.Ordinal);
        int to = Text(editor).IndexOf("Not_yet() { }\n", StringComparison.Ordinal) + "Not_yet() { }\n".Length;
        editor.Selections = SelectionSet.Single(new Selection(from, to));
        Ui.Press(window, "Back");
        Ui.WaitFor(() => Method(window, "Not_yet") is null, "the removed test gone from the tree");

        Assert.Same(four, Method(window, "Two_and_two_make_four"));
        Assert.Equal(TestState.Passed, four.State);
        Assert.Same(ran, four.Results[0]);
        Assert.Equal(TestState.Passed, Method(window, "Doubles")!.State);
        Assert.Equal(2, Method(window, "Doubles")!.Children.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Group_by_categories_shows_each_test_under_its_xunit_traits_nunit_categories_and_mstest_test_categories()
    {
        string solution = NewFolder();
        Write(solution, "Xunit.Tests/Xunit.Tests.csproj", TestProject(TestProjects.Framework));
        Write(solution, "Xunit.Tests/SumTests.cs",
            "using Xunit;\n\nnamespace Sums.Tests;\n\n[Trait(\"Category\", \"Slow\")]\npublic class SumTests\n{\n    [Fact]\n    [Trait(\"Owner\", \"Ann\")]\n    public void Adds() { }\n\n" +
            "    [Fact]\n    public void Subtracts() { }\n}\n\npublic class PlainTests\n{\n    [Fact]\n    public void Plain() { }\n}\n");
        Write(solution, "NUnit.Tests/NUnit.Tests.csproj", PackagesProject("NUnit", "NUnit3TestAdapter"));
        Write(solution, "NUnit.Tests/ParserTests.cs",
            "using NUnit.Framework;\n\nnamespace Parsing.Tests;\n\n[TestFixture]\npublic class ParserTests\n{\n    [Test]\n    [Category(\"Fast\")]\n    public void Reads() { }\n}\n");
        Write(solution, "MSTest.Tests/MSTest.Tests.csproj", PackagesProject("MSTest"));
        Write(solution, "MSTest.Tests/StoreTests.cs",
            "using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace Store.Tests;\n\n[TestClass]\npublic class StoreTests\n{\n    [TestMethod]\n" +
            "    [TestCategory(\"Fast\")]\n    [TestCategory(\"Slow\")]\n    public void Keeps() { }\n}\n");
        Write(solution, "Tests.slnx", "<Solution>\n  <Project Path=\"Xunit.Tests/Xunit.Tests.csproj\" />\n  <Project Path=\"NUnit.Tests/NUnit.Tests.csproj\" />\n" +
            "  <Project Path=\"MSTest.Tests/MSTest.Tests.csproj\" />\n</Solution>\n");
        // No C# language server, nor its restore of the floating versions from nuget.org: the categories are read from code.
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Tree(window);
        Ui.WaitFor(() => Method(window, "Keeps") is not null, "the tests read from the code");

        // Each test under every category it has (its class's too), a trait not named Category by name and value.
        GroupBy(window, "Categories");
        Assert.Equal(
            [
                "Fast", "  Parsing.Tests.ParserTests", "    Reads", "  Store.Tests.StoreTests", "    Keeps",
                "Owner [Ann]", "  Sums.Tests.SumTests", "    Adds",
                "Slow", "  Store.Tests.StoreTests", "    Keeps", "  Sums.Tests.SumTests", "    Adds", "    Subtracts",
                "<Uncategorized>", "  Sums.Tests.PlainTests", "    Plain",
            ],
            Shown(window));
        Assert.True(Count(window, "All Tests") == "5", $"A test under two categories was counted twice: All says {Count(window, "All Tests")} of 5 tests");

        GroupBy(window, "Project");
        Assert.Equal(
            [
                "Xunit.Tests", "  Owner [Ann]", "    Sums.Tests.SumTests", "      Adds", "  Slow", "    Sums.Tests.SumTests", "      Adds", "      Subtracts",
                "  <Uncategorized>", "    Sums.Tests.PlainTests", "      Plain",
                "NUnit.Tests", "  Fast", "    Parsing.Tests.ParserTests", "      Reads",
                "MSTest.Tests", "  Fast", "    Store.Tests.StoreTests", "      Keeps", "  Slow", "    Store.Tests.StoreTests", "      Keeps",
            ],
            Shown(window));
        Ui.Click(ToolbarControl<Button>(window, "Group By"));
        MenuItem ticked = Assert.Single(window.BottomPanel.Tests.OpenMenu!.Items.OfType<MenuItem>(), i => i is { ToggleType: MenuItemToggleType.Radio, IsChecked: true });
        Assert.Equal("Project and Categories", ticked.Header);
        window.BottomPanel.Tests.OpenMenu!.Close();

        GroupBy(window, "Projects and Namespaces");
        Assert.Equal(
            [
                "Xunit.Tests", "  Sums.Tests", "    PlainTests", "      Plain", "    SumTests", "      Adds", "      Subtracts",
                "NUnit.Tests", "  Parsing.Tests", "    ParserTests", "      Reads",
                "MSTest.Tests", "  Store.Tests", "    StoreTests", "      Keeps",
            ],
            Shown(window));
        window.Close();
    }

    [AvaloniaFact]
    public void After_a_run_the_failed_filter_shows_the_failures_alone_as_many_as_it_counts_and_a_modifier_adds_the_ignored()
    {
        MainWindow window = Open();
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Two_and_two_make_four")?.State == TestState.Passed, "the run of every test", TimeSpan.FromSeconds(120));
        Tree(window);
        // Each outcome's filter counts a theory's rows each, as the run's tally does; one no test has is hidden.
        Assert.Equal(["5", "3", "1", "1"], new[] { "All Tests", "Passed Tests", "Failed Tests", "Ignored Tests" }.Select(f => Count(window, f)));
        Assert.False(Filter(window, "Tests Not Run").IsVisible, "The filter for tests not run shows though every test ran");

        window.Tests.Tree.Toggle(window.Tests.Tree.Rows.Single(r => r.Name == "SumTests"));
        Ui.Click(Filter(window, "Failed Tests"));
        Assert.Equal(["Sums.Tests", "  Sums.Tests", "    SumTests", "      Two_and_two_make_five"], Shown(window));
        Assert.Equal(int.Parse(Count(window, "Failed Tests"), System.Globalization.CultureInfo.CurrentCulture), ShownTests(window));
        Assert.Equal([true, false], new[] { "Failed Tests", "All Tests" }.Select(f => Filter(window, f).IsChecked == true));

        Ui.Click(Filter(window, "Ignored Tests"), modifiers: OperatingSystem.IsMacOS() ? Avalonia.Input.RawInputModifiers.Meta : Avalonia.Input.RawInputModifiers.Control);
        Assert.Equal(["Sums.Tests", "  Sums.Tests", "    SumTests", "      Not_yet", "      Two_and_two_make_five"], Shown(window));
        Assert.Equal(2, ShownTests(window));

        Ui.Click(Filter(window, "Passed Tests"));
        Assert.Equal(
            ["Sums.Tests", "  Sums.Tests", "    SumTests", "      Doubles", "        Doubles(value: 1, twice: 2)", "        Doubles(value: 2, twice: 4)", "      Two_and_two_make_four"],
            Shown(window));
        Assert.Equal(int.Parse(Count(window, "Passed Tests"), System.Globalization.CultureInfo.CurrentCulture), ShownTests(window));

        Ui.Click(Filter(window, "All Tests"));
        Assert.Equal(5, ShownTests(window));
        Assert.Empty(window.Tests.Tree.Outcomes);
        window.Close();
    }

    [AvaloniaFact]
    public void Find_in_the_unit_tests_window_narrows_the_tree_to_the_tests_named_and_the_rows_above_them()
    {
        Write("ParseTests.cs", "using Xunit;\n\nnamespace Sums.Parsing;\n\npublic class ParseTests\n{\n    [Fact]\n    public void Reads_two() { }\n\n    [Fact]\n    public void Reads_four() { }\n}\n");
        MainWindow window = Open();
        ListBox tree = Tree(window);
        Ui.WaitFor(() => Method(window, "Reads_four") is not null, "the tests read from the code");

        Ui.Click(tree.ContainerFromItem(window.Tests.Tree.Rows.Single(r => r.Name == "SumTests"))!);
        Ui.Run(window, AppActions.Find);
        window.KeyTextInput("FOUR");
        Ui.Settle();
        Assert.Equal(["Sums.Tests", "  Sums.Parsing", "    ParseTests", "      Reads_four", "  Sums.Tests", "    SumTests", "      Two_and_two_make_four"], Shown(window));

        Ui.Run(window, AppActions.Find);
        window.KeyTextInput("parsetests");
        Ui.Settle();
        Assert.Equal(["Sums.Tests", "  Sums.Parsing", "    ParseTests", "      Reads_four", "      Reads_two"], Shown(window));

        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Empty(window.Tests.Tree.Rows);
        Assert.Contains("No matching tests", ShownTexts(window));
        Ui.Press(window, "Escape");
        Assert.Equal(6, ShownTests(window));
        window.Close();
    }

    [AvaloniaFact]
    public void The_grouping_and_outcomes_chosen_come_back_when_the_folder_opens_again_and_another_folder_has_its_own()
    {
        string settings = NewFolder();
        string other = NewFolder();
        Write(other, "Other.Tests/Other.Tests.csproj", TestProject(TestProjects.Framework));
        Write(other, "Other.Tests/OtherTests.cs", "using Xunit;\n\nnamespace Other.Tests;\n\npublic class OtherTests\n{\n    [Fact]\n    public void Elsewhere() { }\n}\n");
        MainWindow first = Keeping(settings, Folder);
        Ui.WaitFor(() => Method(first, "Doubles") is not null, "the tests read from the code");
        GroupBy(first, "Categories");
        Ui.Click(Filter(first, "Tests Not Run"));
        first.Close();

        MainWindow second = Keeping(settings, Folder);
        Ui.WaitFor(() => Method(second, "Doubles") is not null, "the tests read from the code");
        Assert.Equal([TestGroupLevel.Category], second.Tests.Tree.Grouping);
        Assert.True(Filter(second, "Tests Not Run").IsChecked, "The filter chosen for tests not run was not chosen when the folder opened again");
        Assert.Equal(
            ["<Uncategorized>", "  Sums.Tests.SumTests", "    Doubles", "    Not_yet", "    Two_and_two_make_five", "    Two_and_two_make_four"],
            Shown(second));

        second.OpenFolder(other);
        Ui.WaitFor(() => Method(second, "Elsewhere") is not null, "the other folder's tests");
        Assert.Equal(TestGroupings.Default, second.Tests.Tree.Grouping);
        Assert.True(Filter(second, "All Tests").IsChecked, "Another folder opened with the first folder's filter");
        Assert.Equal(["Other.Tests", "  Other.Tests", "    OtherTests", "      Elsewhere"], Shown(second));
        Task reopened = second.OpenFolder(Folder);
        Ui.WaitFor(() => reopened.IsCompleted, "the first folder to open again");
        Assert.Equal([TestGroupLevel.Category], second.Tests.Tree.Grouping);
        Assert.Equal([TestState.NotRun], second.Tests.Tree.Outcomes);
        second.Close();
    }
}

/// <summary>Debug Tests from the window, on <see cref="UnitTestsFolders"/>' Sums.Tests. Debugged through the window, so the
/// class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class UnitTestsDebuggingTests : UnitTestsFolders, IClassFixture<RestoredFolders>
{
    public UnitTestsDebuggingTests(RestoredFolders restored)
        : base(restored)
    {
    }

    /// <summary>Debug Tests at the caret stops at a breakpoint inside the test; a failing test debugged stops at its
    /// assertion in the test, not inside the assertion library; and the project's launch profile gives its variables to its
    /// tests, run and debugged.</summary>
    [AvaloniaFact]
    public void Debug_tests_stops_at_a_breakpoint_in_a_test_and_at_a_failed_assertion_in_the_test_and_the_launch_profile_gives_its_variables_run_and_debugged()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "Properties"));
        Write("Properties/launchSettings.json",
            "{ \"profiles\": { \"Sums\": { \"commandName\": \"Project\", \"environmentVariables\": { \"PASSENGER_GREETING\": \"from the profile\" } } } }");
        Write("GreetingTests.cs", "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class GreetingTests\n{\n    [Fact]\n" +
            "    public void The_profile_greets() => Assert.Equal(\"from the profile\", Environment.GetEnvironmentVariable(\"PASSENGER_GREETING\"));\n}\n");
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(10, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);

        Ui.Run(window, AppActions.DebugTests);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames.Count > 0, "the stop in the test", TimeSpan.FromSeconds(120));
        Assert.Contains(new LineMark(10, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains("Two_and_two_make_four", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run to end", TimeSpan.FromSeconds(60));
        Assert.Equal(TestState.Passed, Method(window, "Two_and_two_make_four")!.State);

        // xunit.assert's PDB names its sources under /_/src, so under Just My Code the stop is shown at the test's line.
        editor.GoTo(new TextPosition(15, 8));
        Ui.Run(window, AppActions.DebugTests);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop is not null, "the stop on the failed assertion", TimeSpan.FromSeconds(120));
        Assert.Equal("exception", window.Debugger.Session!.StopReason);
        Assert.True(window.Debugger.Stop!.Top?.Name.Contains("Two_and_two_make_five", StringComparison.Ordinal) == true,
            $"The stop showed {window.Debugger.Stop.Top?.Name} at {window.Debugger.Stop.Top?.Source?.Path}, not the test whose assertion failed");
        Assert.Contains(new LineMark(16, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Tests.IsBusy, "the run to end", TimeSpan.FromSeconds(60));

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "The_profile_greets")?.State is TestState.Passed or TestState.Failed, "the run of every test", TimeSpan.FromSeconds(120));
        TestNode greets = Method(window, "The_profile_greets")!;
        Assert.True(greets.State == TestState.Passed, $"A test ran without its project's launch profile's variables, which Rider gives it:\n{greets.Details}");
        Ui.Launched(window, window.Tests.RunAsync(window.Folder!, [greets], debug: true), "the test debugged", TimeSpan.FromSeconds(120));
        Assert.True(greets.State == TestState.Passed, $"A test debugged ran without its project's launch profile's variables:\n{greets.Details}");
        window.Close();
    }
}

/// <summary>The Unit Tests window as thousands of results come in: no frame held long, and what the person closed or chose
/// kept so. Held to a frame's time, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class UnitTestsResponsivenessTests : UnitTestsFolders
{
    public UnitTestsResponsivenessTests()
        : base(null)
    {
    }

    [AvaloniaFact]
    public void Thousands_of_results_come_in_without_holding_up_the_window_and_what_was_closed_or_chosen_stays_so()
    {
        // Many.Tests: 3,000 tests in 30 classes, one in ten failing; Few.Tests: two that pass.
        string solution = NewFolder();
        StringBuilder many = new("using Xunit;\n\nnamespace Many.Tests;\n");
        for (int batch = 0; batch < 30; batch++)
        {
            many.Append(System.Globalization.CultureInfo.InvariantCulture, $"\npublic class Batch{batch:00}\n{{\n");
            for (int test = 0; test < 100; test++)
            {
                int n = batch * 100 + test;
                string body = n % 10 == 7 ? $"Assert.True(false, \"case {n} failed\")" : "Assert.True(true)";
                many.Append(System.Globalization.CultureInfo.InvariantCulture, $"    [Fact]\n    public void Case_{n:0000}() => {body};\n");
            }
            many.Append("}\n");
        }
        Write(solution, "Many.Tests/Many.Tests.csproj", TestProject(TestProjects.Framework));
        Write(solution, "Many.Tests/ManyTests.cs", many.ToString());
        Write(solution, "Few.Tests/Few.Tests.csproj", TestProject(TestProjects.Framework));
        Write(solution, "Few.Tests/FewTests.cs", "using Xunit;\n\nnamespace Few.Tests;\n\npublic class FewTests\n{\n    [Fact]\n    public void One() { }\n\n    [Fact]\n    public void Two() { }\n}\n");
        Write(solution, "Tests.slnx", "<Solution>\n  <Project Path=\"Many.Tests/Many.Tests.csproj\" />\n  <Project Path=\"Few.Tests/Few.Tests.csproj\" />\n</Solution>\n");
        Restore.Run(Path.Combine(solution, "Tests.slnx"));

        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Case_2999")?.State == TestState.Passed, "the first run of every test", TimeSpan.FromSeconds(180));
        Assert.Equal(3002, window.Tests.Tree.Roots.SelectMany(r => r.Methods).Count(m => m.State is TestState.Passed or TestState.Failed));
        ListBox tree = Tree(window);
        CodeEditor details = Details(window);

        window.Tests.Tree.Toggle(window.Tests.Tree.Roots.Single(r => r.Name == "Few.Tests"));
        Assert.DoesNotContain(window.Tests.Tree.Rows, r => r.Name == "FewTests");
        TestNode project = window.Tests.Tree.Roots.Single(r => r.Name == "Many.Tests");
        tree.SelectedItem = project;
        Ui.Settle();
        Assert.Contains("case 7 failed", Text(details), StringComparison.Ordinal);
        int rewrites = 0;
        details.Document.Changed += (_, _) => rewrites++;
        Ui.ChooseFromMenu(tree, "Run");

        // No frame takes long while the results come in, measured against the one that marked all 3,000 running.
        List<double> arriving = [];
        double marking = 0;
        DateTime limit = DateTime.UtcNow + TimeSpan.FromMinutes(3).Scaled();
        rewrites = 0;
        while (window.Tests.IsBusy && DateTime.UtcNow < limit)
        {
            int before = project.Methods.Count(m => m.State == TestState.Running);
            Stopwatch frame = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            double took = frame.Elapsed.TotalMilliseconds;
            if (before > 0)
            {
                arriving.Add(took);
            }
            else if (project.Methods.Any(m => m.State == TestState.Running))
            {
                marking = took;
            }
            Waiting.For(TimeSpan.FromMilliseconds(10), "results to come in between the frames measured");
        }
        Ui.Settle();
        Assert.False(window.Tests.IsBusy, "The run of 3,000 tests had not ended after 3 minutes");
        Assert.Equal(3000, project.Methods.Count(m => m.Results.Count > 0));
        double longest = arriving.DefaultIfEmpty().Max();
        Assert.True(longest <= Math.Max(500, 20 * marking),
            $"While 3,000 results came in, one frame held the window for {longest:0} ms (marking them all running took {marking:0} ms; {arriving.Count} frames)");
        Assert.True(rewrites <= 300, $"The chosen project's details were written {rewrites} times while 3,000 results came in");

        TestNode failing = Method(window, "Case_0107")!;
        tree.SelectedItem = failing;
        Ui.Settle();
        Assert.Contains("case 107 failed", Text(details), StringComparison.Ordinal);
        TestNode above = window.Tests.Tree.Rows.Single(r => r.Name == "Batch00");
        foreach (bool open in new[] { false, true })
        {
            window.Tests.Tree.Toggle(above);
            Ui.Settle();
            Assert.Equal(open, window.Tests.Tree.Rows.Any(r => r.Name == "Case_0000"));
            Assert.Same(failing, tree.SelectedItem);
            Assert.Contains("case 107 failed", Text(details), StringComparison.Ordinal);
        }

        TestItem found = failing.Items[0];
        TestOutcome last = failing.Results[0];
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Case_0107")?.Results.FirstOrDefault() is { } fresh && fresh != last, "Run All again", TimeSpan.FromSeconds(180));
        Assert.False(window.Tests.Tree.Roots.Single(r => r.Name == "Few.Tests").IsExpanded, "Run All opened the project that was closed");
        Assert.DoesNotContain(window.Tests.Tree.Rows, r => r.Name == "FewTests");
        Assert.Equal("Case_0107", (tree.SelectedItem as TestNode)?.Name);
        Assert.Contains("case 107 failed", Text(details), StringComparison.Ordinal);
        Assert.Same(found, Method(window, "Case_0107")!.Items[0]);
        window.Close();
    }

    /// <summary>An Enter typed above the tests of an open test file moves their rows to their new lines, nothing made again:
    /// in a solution of 3,000 tests it allocates on the UI thread about what it does in one of three. The rows, and Go to
    /// Source from them, are then at the tests' new lines.</summary>
    [AvaloniaFact]
    public void An_enter_above_the_tests_of_a_file_in_a_solution_of_thousands_moves_their_rows_and_costs_what_it_does_among_a_few()
    {
        const string Few = "using Xunit;\n\nnamespace Few.Tests;\n\npublic class FewTests\n{\n    [Fact]\n    public void One() { }\n\n    [Fact]\n    public void Two() { }\n\n    [Fact]\n    public void Three() { }\n}\n";
        StringBuilder many = new("using Xunit;\n\nnamespace Few.Tests;\n");
        for (int batch = 0; batch < 30; batch++)
        {
            many.Append(System.Globalization.CultureInfo.InvariantCulture, $"\npublic class Batch{batch:00}\n{{\n");
            for (int test = 0; test < 100; test++)
            {
                many.Append(System.Globalization.CultureInfo.InvariantCulture, $"    [Fact]\n    public void Case_{(batch * 100) + test:0000}() {{ }}\n");
            }
            many.Append("}\n");
        }
        string small = NewFolder();
        Write(small, "Few.Tests/Few.Tests.csproj", TestProject(TestProjects.Framework));
        Write(small, "Few.Tests/FewTests.cs", Few);
        string large = NewFolder();
        Write(large, "Few.Tests/Few.Tests.csproj", TestProject(TestProjects.Framework));
        Write(large, "Few.Tests/FewTests.cs", Few);
        Write(large, "Few.Tests/ManyTests.cs", many.ToString());

        (long among, bool keptAmong) = Enters(small, 3);
        (long amid, bool keptAmid) = Enters(large, 3003);
        TestContext.Current.TestOutputHelper?.WriteLine($"An Enter above the tests allocated {among / 1024} KB on the UI thread among 3 tests, {amid / 1024} KB among 3,003");
        Assert.True(amid <= among + (64 * 1024),
            $"An Enter above the tests of a file allocated {amid / 1024} KB on the UI thread in a solution of 3,003 tests, against {among / 1024} KB in one of 3: the tree was made again");
        Assert.True(keptAmong && keptAmid, "Lines put in above a file's tests made the Unit Tests window's tree again");

        // The least an Enter above the tests allocated on the UI thread, and whether the tree's methods stayed.
        (long Least, bool Kept) Enters(string solution, int tests)
        {
            MainWindow window = Ui.OpenFolder(solution, languageServer: false);
            Tree(window);
            Ui.WaitFor(() => window.Tests.Tree.Roots.Sum(r => r.Methods.Count()) == tests, $"the tree's {tests} tests");
            window.OpenFile(Path.Combine(solution, "Few.Tests", "FewTests.cs"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Passenger.App.Workspace.OpenDocument file = window.EditorArea.Active!;
            Ui.WaitFor(() => file.TestMarks.Count == 4, "the file's run icons");
            IReadOnlyList<TestNode> methods = window.Tests.Tree.Methods;
            editor.Selections = SelectionSet.Single(Few.IndexOf("public class", StringComparison.Ordinal));
            editor.Focus();
            Ui.Settle();
            long least = long.MaxValue;
            const int Keys = 5;
            for (int i = 0; i < Keys; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                Ui.Settle();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            TextSnapshot text = editor.Document.Snapshot;
            int two = text.GetLineFromOffset(text.ToString().IndexOf("void Two", StringComparison.Ordinal));
            Ui.WaitFor(() => file.TestMarks.Contains(new TestMark(two, TestMarkKind.NotRun)), "Two's run icon at its new line");
            bool kept = ReferenceEquals(methods, window.Tests.Tree.Methods);
            TestNode moved = Method(window, "Two")!;
            Assert.Equal(two + 1, moved.Line);
            ListBox tree = Tree(window);
            tree.SelectedItem = moved;
            editor.Selections = SelectionSet.Single(0);
            Ui.ChooseFromMenu(tree, "Go to Source");
            Assert.Equal(two, editor.Document.Snapshot.GetLineFromOffset(editor.Selections.Primary.Caret));
            window.Close();
            return (least, kept);
        }
    }
}

/// <summary>The Sums.Tests project a unit testing test takes, restored, the folders it writes for solutions of its own, and
/// the helpers that open the window on them and read the Unit Tests window.</summary>
public abstract class UnitTestsFolders : IDisposable
{
    /// <summary>Where a class takes Sums.Tests from: none for a class whose tests write solutions of their own.</summary>
    private readonly RestoredFolders? restored;

    /// <summary>The test's own Sums.Tests project, once it has taken one.</summary>
    private string? taken;

    protected UnitTestsFolders(RestoredFolders? restored) => this.restored = restored;

    /// <summary>The test's own Sums.Tests, restored: taken the first time it is asked for, so a test with a solution of its
    /// own restores none.</summary>
    protected string Folder => taken ??= (restored ?? throw new InvalidOperationException("This class takes no Sums.Tests")).Take("tests-", made =>
    {
        File.WriteAllText(Path.Combine(made, "Sums.Tests.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
            "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(made, "SumTests.cs"),
            "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four()\n    {\n        int sum = 2 + 2;\n        Assert.Equal(4, sum);\n    }\n\n" +
            "    [Fact]\n    public void Two_and_two_make_five()\n    {\n        Assert.Equal(5, 2 + 2);\n    }\n\n    [Theory]\n    [InlineData(1, 2)]\n    [InlineData(2, 4)]\n    public void Doubles(int value, int twice) => Assert.Equal(twice, value * 2);\n\n" +
            "    [Fact(Skip = \"later\")]\n    public void Not_yet() { }\n}\n");
        return made;
    });

    protected string Source => Path.Combine(PathNames.Canonical(Folder), "SumTests.cs");

    private readonly List<string> folders = [];

    public void Dispose()
    {
        if (taken is not null)
        {
            TestFolders.Delete(taken);
        }
        foreach (string made in folders)
        {
            TestFolders.Delete(made);
        }
    }

    /// <summary>The window a person uses, as the app makes it: one that keeps its state, here in the test's own folder; with
    /// no C# language server, as <see cref="Open"/>.</summary>
    protected static MainWindow Keeping(string settings, string open)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(open);
        Ui.Settle();
        Tree(window);
        return window;
    }

    /// <summary>A control of the Unit Tests window's toolbar, by the tip it shows.</summary>
    protected static T ToolbarControl<T>(MainWindow window, string tip) where T : Control =>
        window.BottomPanel.Tests.GetVisualDescendants().OfType<T>().FirstOrDefault(c => ToolTip.GetTip(c) is string text && text.StartsWith(tip, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The Unit Tests window's toolbar has no {tip}");

    protected static ToggleButton Filter(MainWindow window, string tests) => ToolbarControl<ToggleButton>(window, $"Filter {tests}");

    /// <summary>How many tests an outcome's filter says have it.</summary>
    protected static string Count(MainWindow window, string tests) => Filter(window, tests).GetVisualDescendants().OfType<TextBlock>().Single().Text!;

    /// <summary>The tests the tree shows: data rows, and methods without them.</summary>
    protected static int ShownTests(MainWindow window) =>
        window.Tests.Tree.Rows.Count(r => r.Kind == TestNodeKind.Row || r is { Kind: TestNodeKind.Method, HasChildren: false });

    /// <summary>Chooses an item of the toolbar's Group By menu.</summary>
    protected static void GroupBy(MainWindow window, string item)
    {
        Ui.Click(ToolbarControl<Button>(window, "Group By"));
        Ui.Choose(window.BottomPanel.Tests.OpenMenu, item);
    }

    protected string NewFolder()
    {
        string made = PathNames.Canonical(TestFolders.Create("tests-"));
        folders.Add(made);
        return made;
    }

    protected static string TestProject(string frameworks) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
        (frameworks.Contains(';', StringComparison.Ordinal)
            ? $"    <TestFrameworks>{frameworks}</TestFrameworks>\n    <TargetFrameworks>$(TestFrameworks)</TargetFrameworks>\n"
            : $"    <TargetFramework>{frameworks}</TargetFramework>\n") +
        "    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n" +
        "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
        "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n";

    /// <summary>A test project of another framework's packages, never restored here.</summary>
    protected static string PackagesProject(params string[] packages) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n" +
        "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n" +
        string.Concat(packages.Select(p => $"    <PackageReference Include=\"{p}\" Version=\"*\" />\n")) + "  </ItemGroup>\n</Project>\n";

    /// <summary>The Unit Tests window's rows as a person reads them, indented by depth.</summary>
    protected static List<string> Shown(MainWindow window) => [.. window.Tests.Tree.Rows.Select(r => new string(' ', r.Depth * 2) + r.Name)];

    /// <summary>The words the Unit Tests window shows.</summary>
    protected static List<string?> ShownTexts(MainWindow window) =>
        [.. window.BottomPanel.Tests.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    protected static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    protected static string Write(string root, string relative, string text) => TestFolders.Write(root, relative, text);

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

    /// <summary>The Unit Tests window's Stop, on its toolbar.</summary>
    protected static Button StopButton(MainWindow window) =>
        window.BottomPanel.Tests.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Stop", StringComparison.Ordinal));

    /// <summary>A window on the test's Sums.Tests with its tests' file open, with no C# language server, which finding,
    /// running and debugging tests ask nothing of.</summary>
    protected MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(Folder, languageServer: false);
        window.OpenFile(Source);
        Ui.Settle();
        return window;
    }

    protected string Write(string relative, string text) => TestFolders.Write(PathNames.Canonical(Folder), relative, text);
}

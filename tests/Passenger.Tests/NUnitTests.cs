using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Testing;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>NUnit as people install it: every way of installing it makes the same rows of its cases, its named cases and
/// its fixtures' instances, and on its own runner it shows from code, tells each result and runs what the caret and a row
/// choose.</summary>
public sealed class NUnitTests : NUnitFolders
{
    /// <summary>Every kind of case NUnit makes, each failing or passing as <see cref="Rows"/> below says.</summary>
    private const string Cases = """
        using NUnit.Framework;

        namespace Cases.Tests;

        public class Arithmetic
        {
            public static readonly object[] Pairs = [new object[] { "ab", 2 }, new object[] { "abc", 3 }];

            public static readonly TestCaseData[] Titled =
            [
                new TestCaseData(1, 1).SetName("OneIsOne"),
                new TestCaseData(1, 2).SetName("OneIsTwo"),
                new TestCaseData(2, 2).SetName("Two.Is.Two"),
                new TestCaseData(3, 3).SetName("{m}_{0}"),
            ];

            private const string Spelled = "Spelled";

            [Test]
            public void Plain() { }

            [Test]
            [Ignore("later")]
            public void Later() { }

            [TestCase(1, 2)]
            [TestCase(2, 3)]
            public void Each(int a, int b) => Assert.That(a + 1, Is.EqualTo(b));

            [TestCase("a.b(c")]
            [TestCase("d")]
            public void Words(string word) => Assert.That(word, Is.Not.Empty);

            [TestCaseSource(nameof(Pairs))]
            public void Sourced(string word, int letters) => Assert.That(word, Has.Length.EqualTo(letters));

            [TestCase(1, 1, 2, TestName = "OnePlusOne")]
            [TestCase(2, 2, 5, TestName = "TwoPlusTwoIsFive")]
            public void Sums(int a, int b, int sum) => Assert.That(a + b, Is.EqualTo(sum));

            [TestCaseSource(nameof(Titled))]
            public void Compared(int a, int b)
            {
                Assert.That(a, Is.EqualTo(b));
            }

            [TestCase(1, TestName = Spelled)]
            public void Lone(int a) => Assert.That(a, Is.EqualTo(1));
        }

        public class Inherited
        {
            public static readonly TestCaseData[] Titled = [new TestCaseData(1).SetName("Passed_on")];

            [TestCaseSource(nameof(Titled))]
            public void Held(int value) => Assert.That(value, Is.EqualTo(1));
        }

        public class Inheriting : Inherited;

        [TestFixture(1)]
        [TestFixture(-1)]
        public class Parameterised(int value)
        {
            public static readonly TestCaseData[] Titled = [new TestCaseData(2).SetName("Times_two")];

            [Test]
            public void Positive() => Assert.That(value, Is.GreaterThan(0));

            [TestCaseSource(nameof(Titled))]
            public void Doubled(int by) => Assert.That(value * by, Is.GreaterThan(0));
        }

        [TestFixture(typeof(int))]
        [TestFixture(typeof(string))]
        public class Typed<T>
        {
            [Test]
            public void Named() => Assert.That(typeof(T).Name, Is.Not.Empty);
        }

        [TestFixture("a.b")]
        [TestFixture("x(y")]
        public class Worded(string word)
        {
            [TestCase(1)]
            [TestCase(2)]
            public void Long(int at) => Assert.That(word.Length, Is.GreaterThan(at));
        }

        """;

    /// <summary>What every test of <see cref="Cases"/> comes to, as <see cref="Ended"/> says it.</summary>
    private static readonly string[] Rows =
    [
        "Compared_3: Passed", "OneIsOne: Passed", "OneIsTwo: Failed", "Two.Is.Two: Passed", "Each(1,2): Passed", "Each(2,3): Passed", "Later: Skipped", "Spelled: Passed", "Plain: Passed",
        "Sourced(\"ab\",2): Passed", "Sourced(\"abc\",3): Passed", "OnePlusOne: Passed", "TwoPlusTwoIsFive: Failed", "Words(\"a.b(c\"): Passed", "Words(\"d\"): Passed",
        "Passed_on: Passed", "Passed_on: Passed", "Parameterised(-1).Times_two: Failed", "Parameterised(1).Times_two: Passed", "Parameterised(-1).Positive: Failed",
        "Parameterised(1).Positive: Passed", "Typed<Int32>.Named: Passed", "Typed<String>.Named: Passed",
        "Worded(\"a.b\").Long(1): Passed", "Worded(\"a.b\").Long(2): Passed", "Worded(\"x(y\").Long(1): Passed", "Worded(\"x(y\").Long(2): Passed",
    ];

    [AvaloniaTheory]
    [InlineData(NUnitInstall.OnVSTest)]
    [InlineData(NUnitInstall.OnItsRunner)]
    [InlineData(NUnitInstall.FourOnVSTest)]
    public void Every_way_of_installing_nunit_makes_the_same_rows_of_its_cases_named_cases_and_fixtures_instances(NUnitInstall install)
    {
        MainWindow window = Ui.OpenFolder(Restored(install, "Cases.cs", Cases), languageServer: false);
        Tree(window);
        Ui.WaitFor(() => Method(window, "Plain") is not null, "the tests read from their code");

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Named") is not null, "the run of every test", TimeSpan.FromSeconds(180));
        Assert.Equal("Tests passed: 22, failed: 4, skipped: 1 of 27", Tally(window));
        Assert.Equal(Rows, Ended(window));
        Assert.Equal(["27", "22", "4", "1"], new[] { "All Tests", "Passed Tests", "Failed Tests", "Ignored Tests" }.Select(f => Count(window, f)));
        Assert.Contains("TwoPlusTwoIsFive: Failed", Method(window, "Sums")!.Details, StringComparison.Ordinal);
        Assert.Contains("OneIsTwo: Failed", Method(window, "Compared")!.Details, StringComparison.Ordinal);
        Assert.Contains("Parameterised(-1).Positive: Failed", Method(window, "Positive")!.Details, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A case named by its data shows under its method and runs alone from its row, the class that inherits a named
    /// case holding one of its own, whichever way NUnit is installed.</summary>
    [AvaloniaTheory]
    [InlineData(NUnitInstall.OnVSTest)]
    [InlineData(NUnitInstall.OnItsRunner)]
    [InlineData(NUnitInstall.FourOnVSTest)]
    public void A_case_named_by_its_data_is_a_row_of_its_method_that_runs_alone(NUnitInstall install)
    {
        MainWindow window = Ui.OpenFolder(Restored(install, "Cases.cs", Cases), languageServer: false);
        ListBox tree = Tree(window);
        Ui.WaitFor(() => Method(window, "Compared") is not null, "the tests read from their code");
        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Named") is not null, "the run of every test", TimeSpan.FromSeconds(180));

        TestNode compared = Method(window, "Compared")!;
        Assert.Equal(["Compared_3", "OneIsOne", "OneIsTwo", "Two.Is.Two"], compared.Children.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Cases.Tests.Inherited.Held", "Cases.Tests.Inheriting.Held"],
            window.Tests.Tree.Methods.Where(m => m.Name == "Held").Select(m => m.FullName).Order(StringComparer.Ordinal));
        if (!compared.IsExpanded)
        {
            window.Tests.Tree.Toggle(compared);
        }
        TestNode failing = compared.Children.Single(r => r.Name == "OneIsTwo");
        TestOutcome before = failing.Results[0];
        tree.SelectedItem = failing;
        Ui.ChooseFromMenu(tree, "Run");
        Ui.WaitFor(() => !window.Tests.IsBusy && !ReferenceEquals(failing.Results.FirstOrDefault(), before), "the run of the row chosen", TimeSpan.FromSeconds(120));
        Assert.Equal("Tests passed: 0, failed: 1 of 1", Tally(window));
        Assert.Equal(TestState.Failed, failing.State);
        window.Close();
    }

    /// <summary>On NUnit's own runner the tests are read from their code before a build and marked beside it; a run gives each
    /// its result, a failure's message and place, a skip's reason, what a test wrote and a mark; the test at the caret runs
    /// its every row, and a row chosen runs alone.</summary>
    [AvaloniaFact]
    public void An_nunit_project_on_its_own_runner_shows_its_tests_from_code_and_tells_each_result_and_runs_the_caret_and_a_row_alone()
    {
        MainWindow window = Opened(Restored(NUnitInstall.OnItsRunner, SumTestsFile, SumTests));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(["Sums.Tests", "  Sums.Tests", "    SumTests", "      Adds", "      Doubles", "      Fails", "      Not_yet"], Shown(window));
        Assert.Contains(new TestMark(7, TestMarkKind.NotRun), editor.TestMarks);
        Assert.Contains(new TestMark(15, TestMarkKind.NotRun), editor.TestMarks);
        Assert.False(window.IsBuilding, "Showing the tests started a build");

        Assert.True(window.Perform(AppActions.RunAllTests));
        Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Adds")?.State is TestState.Passed or TestState.Failed, "the run of every test", TimeSpan.FromSeconds(180));
        TestNode adds = Method(window, "Adds")!;
        Assert.True(adds.State == TestState.Passed, $"An NUnit test on its own runner did not pass, so the project was not run through the platform's protocol:\n{adds.Details}\n{window.BottomPanel.Tests.Status}");
        Assert.Equal("Tests passed: 3, failed: 1, skipped: 1 of 5", Tally(window));
        Assert.Contains("adding two and two", adds.Details, StringComparison.Ordinal);
        TestNode fails = Method(window, "Fails")!;
        Assert.Equal(TestState.Failed, fails.State);
        Assert.Contains("Expected: 5", fails.Details, StringComparison.Ordinal);
        Assert.Contains("SumTests.cs:line 18", fails.Details, StringComparison.Ordinal);
        TestNode skipped = Method(window, "Not_yet")!;
        Assert.Equal(TestState.Skipped, skipped.State);
        Assert.Contains("later", skipped.Details, StringComparison.Ordinal);
        Assert.Contains(new TestMark(7, TestMarkKind.Passed), editor.TestMarks);
        Assert.Contains(new TestMark(15, TestMarkKind.Failed), editor.TestMarks);

        TestNode doubles = Method(window, "Doubles")!;
        Assert.Equal(["Doubles(1,2)", "Doubles(2,4)"], doubles.Children.Select(r => r.Name));
        TestNode first = doubles.Children[0];
        TestNode second = doubles.Children[1];
        TestOutcome before = first.Results[0];
        editor.GoTo(new TextPosition(26, 8));
        Ui.Run(window, AppActions.RunTests);
        Ui.WaitFor(() => !window.Tests.IsBusy && !ReferenceEquals(first.Results.FirstOrDefault(), before), "the run of the test at the caret", TimeSpan.FromSeconds(120));
        Assert.Equal("Tests passed: 2, failed: 0 of 2", Tally(window));

        if (!doubles.IsExpanded)
        {
            window.Tests.Tree.Toggle(doubles);
        }
        before = second.Results[0];
        TestOutcome other = first.Results[0];
        ListBox tree = Tree(window);
        tree.SelectedItem = second;
        Ui.ChooseFromMenu(tree, "Run");
        Ui.WaitFor(() => !window.Tests.IsBusy && !ReferenceEquals(second.Results.FirstOrDefault(), before), "the run of the row chosen", TimeSpan.FromSeconds(120));
        Assert.Equal("Tests passed: 1, failed: 0 of 1", Tally(window));
        Assert.Same(other, first.Results[0]);
        window.Close();
    }

    /// <summary>The tests' tally, less the time they took.</summary>
    private static string Tally(MainWindow window) =>
        System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, @" in \d+\.\d s$", string.Empty);

    /// <summary>What each test came to, a method's rows in place of the method, by method and then row.</summary>
    private static List<string> Ended(MainWindow window) =>
        [.. window.Tests.Tree.Methods.OrderBy(m => m.FullName, StringComparer.Ordinal)
            .SelectMany(m => m.Children.Count == 0 ? new[] { m } : [.. m.Children.OrderBy(r => r.Name, StringComparer.Ordinal)])
            .Select(test => $"{test.Name}: {test.State}")];
}

/// <summary>Debug Tests on NUnit, beside VSTest's SDK and on its own runner: a breakpoint inside a test stops it, and only
/// that test runs. Debugged through the window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class NUnitDebuggingTests : NUnitFolders
{
    [AvaloniaTheory]
    [InlineData(NUnitInstall.OnVSTest)]
    [InlineData(NUnitInstall.OnItsRunner)]
    public void Debug_tests_at_the_caret_stops_at_a_breakpoint_inside_an_nunit_test(NUnitInstall install)
    {
        MainWindow window = Opened(Restored(install, SumTestsFile, SumTests));
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(10, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);

        Ui.Run(window, AppActions.DebugTests);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames.Count > 0, "the stop in the test", TimeSpan.FromSeconds(180));
        Assert.Contains(new LineMark(10, LineMarkKind.ExecutionPoint), editor.LineMarks);
        Assert.Contains("Adds", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);

        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => !window.Tests.IsBusy, "the debugged run to end", TimeSpan.FromSeconds(60));
        Assert.Equal(TestState.Passed, Method(window, "Adds")!.State);
        Assert.Equal(TestState.NotRun, Method(window, "Fails")!.State);
        Ui.WaitFor(() => window.Debugger.Session is null || window.Debugger.Session.State == DebugState.Ended, "the test host to end once its run was done");
        window.Close();
    }
}

/// <summary>How a test project takes NUnit.</summary>
public enum NUnitInstall
{
    /// <summary>NUnit and its adapter beside VSTest's SDK.</summary>
    OnVSTest,

    /// <summary>NUnit's own runner on the Testing Platform: an executable with no test SDK.</summary>
    OnItsRunner,

    /// <summary>NUnit 4 and the adapter of its day beside VSTest's SDK.</summary>
    FourOnVSTest,
}

/// <summary>The NUnit project each test writes for itself, and the window on it.</summary>
public abstract class NUnitFolders : UnitTestsFolders
{
    protected const string SumTestsFile = "SumTests.cs";

    /// <summary>A test of each ending, the lines the tests name being those of this text.</summary>
    protected const string SumTests = """
        using NUnit.Framework;

        namespace Sums.Tests;

        public class SumTests
        {
            [Test]
            public void Adds()
            {
                TestContext.Out.WriteLine("adding two and two");
                int sum = 2 + 2;
                Assert.That(sum, Is.EqualTo(4));
            }

            [Test]
            public void Fails()
            {
                Assert.That(2 + 2, Is.EqualTo(5));
            }

            [Test]
            [Ignore("later")]
            public void Not_yet() { }

            [TestCase(1, 2)]
            [TestCase(2, 4)]
            public void Doubles(int value, int twice) => Assert.That(value * 2, Is.EqualTo(twice));
        }

        """;

    protected NUnitFolders()
        : base(null)
    {
    }

    /// <summary>A folder of its own holding a project that takes NUnit as given, with the code given, restored.</summary>
    protected string Restored(NUnitInstall install, string file, string code)
    {
        string folder = NewFolder();
        Write(folder, "Sums.Tests.csproj", Project(install));
        Write(folder, file, code);
        Restore.Run(folder);
        return folder;
    }

    /// <summary>A window on a project of <see cref="SumTests"/> with its file open and shown in the Unit Tests window, with
    /// no C# language server, which finding, running and debugging tests ask nothing of.</summary>
    protected static MainWindow Opened(string folder)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, SumTestsFile));
        Ui.Settle();
        Tree(window);
        Ui.WaitFor(() => Method(window, "Adds") is not null, "the tests read from the code");
        return window;
    }

    private static string Project(NUnitInstall install) => install switch
    {
        NUnitInstall.OnItsRunner => Project(
            "    <OutputType>Exe</OutputType>\n    <EnableNUnitRunner>true</EnableNUnitRunner>\n", TestPackages.NUnit, TestPackages.NUnitAdapter, testSdk: false),
        NUnitInstall.FourOnVSTest => Project(string.Empty, TestPackages.NUnitFour, TestPackages.NUnitFourAdapter, testSdk: true),
        _ => Project(string.Empty, TestPackages.NUnit, TestPackages.NUnitAdapter, testSdk: true),
    };

    private static string Project(string properties, string nunit, string adapter, bool testSdk) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n{properties}  </PropertyGroup>\n  <ItemGroup>\n" +
        (testSdk ? $"    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n" : string.Empty) +
        $"    <PackageReference Include=\"NUnit\" Version=\"{nunit}\" />\n    <PackageReference Include=\"NUnit3TestAdapter\" Version=\"{adapter}\" />\n  </ItemGroup>\n</Project>\n";
}

using Passenger.App.Testing;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The Unit Tests model over 3,000 tests of a project whose path is 5,000 characters long: a runner's catalog, the
/// tree's load, a regrouping, a run and an earlier session's results each make less than the path's size a test, so none
/// copies the path, or a key holding it, for each test. What the model keeps it makes, so a bound on what it makes bounds
/// what it keeps (#1247).</summary>
[Collection(Beside.Name)]
public sealed class TestModelMemoryTests
{
    private const int Classes = 250;
    private const string Framework = "net11.0";

    private static readonly string Project = $"/{new string('p', 5_000)}/Sums.Tests.csproj";

    private static long PathBytes => Project.Length * sizeof(char);

    [Fact]
    public void A_runner_s_catalog_of_thousands_of_tests_tells_and_finds_them_with_no_copy_of_their_project_s_path()
    {
        List<TestItem> found = Found();
        TestAssembly assembly = new(new ProjectModel(Project, "Sums.Tests", "Exe", [Framework], [], [], true), Framework, "/bin/Sums.Tests.dll");
        string[] ids = [.. found.Select((_, at) => $"id{at}")];
        object[] handles = [.. found.Select(_ => new object())];
        (TestAssembly Assembly, int Test)[] looked = [.. found.Select((_, at) => (assembly, at))];
        TestCatalog<object> catalog = new();
        IReadOnlyList<TestItem> told = [];

        MakesNoCopy("Telling a runner's tests", found.Count, () => told = catalog.Tell([assembly], looked, at => ids[at], (_, at) => found[at], (_, at) => handles[at]));

        Assert.Equal(found.Count, told.Count);
        _ = catalog.Of(told[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int known = 0;
        for (int at = 0; at < told.Count; at++)
        {
            known += catalog.Of(told[at]) == handles[at] ? 1 : 0;
        }
        long asked = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(found.Count, known);
        Assert.True(asked < told.Count, $"Finding each of {told.Count:N0} tests a runner told made {asked:N0} bytes: a run of thousands of tests makes a key for each it asks of its runner");
    }

    [Fact]
    public void The_tests_tree_loads_regroups_and_runs_thousands_of_tests_with_no_copy_of_their_project_s_path()
    {
        List<TestItem> found = Found();
        ProjectTests project = new(Project, "Sums.Tests", Code(), [new TestSource(Framework, found)]);
        List<TestOutcome> outcomes = [.. found.Select(t => new TestOutcome(t, TestState.Passed, TimeSpan.FromMilliseconds(1)))];
        TestTree tree = new();

        MakesNoCopy("Loading the tree", found.Count, () => tree.Load([project]));
        Assert.All(found, t => Assert.NotNull(tree.Node(t)));
        MakesNoCopy("Grouping the tree by namespace alone", found.Count, () => tree.Group([TestGroupLevel.Namespace]));
        MakesNoCopy("Grouping the tree by project and namespace again", found.Count, () => tree.Group(TestGroupings.Default));
        MakesNoCopy("A run of every test", found.Count, () =>
        {
            tree.Begin(found);
            tree.Apply(outcomes);
            tree.End();
        });

        Assert.Equal(found.Count, tree.Counts()[(int)TestState.Passed]);
    }

    [Fact]
    public void The_tests_tree_takes_an_earlier_session_s_results_of_thousands_of_methods_with_no_copy_of_their_project_s_path()
    {
        List<TestItem> found = Found();
        ProjectTests project = new(Project, "Sums.Tests", Code(), [new TestSource(Framework, found)]);
        Dictionary<string, KeptRun> runs = new(StringComparer.Ordinal);
        foreach (string method in found.Select(t => t.FullyQualifiedName).Distinct(StringComparer.Ordinal))
        {
            runs[$"|{Project}::{method}"] = new KeptRun
            {
                Ran = DateTimeOffset.UnixEpoch,
                Results = [new KeptResult { FullyQualifiedName = method, DisplayName = method, Framework = Framework, State = TestState.Failed, Duration = TimeSpan.FromMilliseconds(1) }],
            };
        }
        TestTree tree = new();
        tree.Load([project]);

        MakesNoCopy("Taking an earlier session's results", runs.Count, () => tree.Keep(runs));

        Assert.Equal(TestState.Failed, tree.Node(found[0])?.State);
    }

    private static void MakesNoCopy(string what, int tests, Action act)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        act();
        long made = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(made / tests < PathBytes, $"{what} made {made / tests:N0} bytes a test, against {PathBytes:N0} bytes of its project's path: the Unit Tests model "
            + "copies the path, or a key holding it, for each test, and keeps megabytes of them in a solution of thousands of tests");
    }

    /// <summary>A runner's tests: in each class eight methods and two theories of two data rows.</summary>
    private static List<TestItem> Found()
    {
        List<TestItem> found = [];
        for (int c = 0; c < Classes; c++)
        {
            string file = $"/src/Sums.Tests/C{c}Tests.cs";
            for (int m = 0; m < 8; m++)
            {
                string name = $"Sums.Tests.C{c}Tests.Adds{m}";
                found.Add(new TestItem(Project, name, name) { Framework = Framework, Path = file, Line = m + 1 });
            }
            for (int t = 0; t < 2; t++)
            {
                string name = $"Sums.Tests.C{c}Tests.Doubles{t}";
                for (int value = 1; value <= 2; value++)
                {
                    string shown = $"{name}(value: {value})";
                    found.Add(new TestItem(Project, name, shown) { Framework = Framework, Row = TestItem.RowOf(name, shown), Path = file, Line = 10 + t });
                }
            }
        }
        return found;
    }

    /// <summary>The same methods as the code declares them.</summary>
    private static CodeTests Code()
    {
        List<TestItem> tests = [];
        Dictionary<string, IReadOnlySet<string>> byFile = new(StringComparer.Ordinal);
        for (int c = 0; c < Classes; c++)
        {
            string file = $"/src/Sums.Tests/C{c}Tests.cs";
            List<string> names = [.. Enumerable.Range(0, 8).Select(m => $"Adds{m}"), "Doubles0", "Doubles1"];
            tests.AddRange(names.Select((name, at) => new TestItem(Project, $"Sums.Tests.C{c}Tests.{name}", name) { Path = file, Line = at + 1 }));
            byFile[file] = new HashSet<string>(names, StringComparer.Ordinal);
        }
        return new CodeTests(tests, byFile);
    }
}

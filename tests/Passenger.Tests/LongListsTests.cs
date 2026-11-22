using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>The IDE's long lists scrolled with the wheel as a person scrolls them, each against a short one of its kind:
/// a step down a long list allocates on the UI thread what it does down a short one, and the rows on screen are handed
/// other items as the list scrolls, none made again.</summary>
public sealed class LongListsTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (string folder in folders)
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A scroll step down a list of thousands hands its rows other items, never making a row again: the
    /// matches in Find in Files, the commits of the Git log and the tests of the Unit Tests window, each a list of its
    /// own kind of row.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_long_list_hands_its_rows_other_items_never_making_one_again()
    {
        Flow.StepApart<LongListsTests>("a scroll step down a thousand matches in files hands the rows other matches never making one again", t => t.A_scroll_step_down_a_thousand_matches_in_files_hands_the_rows_other_matches_never_making_one_again());
        Flow.StepApart<LongListsTests>("a scroll step down two thousand commits of the git log hands the rows other commits never making one again", t => t.A_scroll_step_down_two_thousand_commits_of_the_git_log_hands_the_rows_other_commits_never_making_one_again());
        Flow.StepApart<LongListsTests>("a scroll step down a thousand tests of the unit tests window hands the rows other tests never making one again", t => t.A_scroll_step_down_a_thousand_tests_of_the_unit_tests_window_hands_the_rows_other_tests_never_making_one_again());
    }

    /// <summary>A word found 100 times and one found a thousand, ten to a file, both searched for in one window.</summary>
    private void A_scroll_step_down_a_thousand_matches_in_files_hands_the_rows_other_matches_never_making_one_again()
    {
        string root = Folder();
        TestFolders.WriteAll(root, Lines("total", 100).Concat(Lines("count", FileSearch.MostMatches)));
        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.FileIndex.Count == (100 + FileSearch.MostMatches) / 10 && !window.FileIndex.IsWalking, "the folder's files indexed");
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Holds("Find in Files", "matches", 100, FileSearch.MostMatches, matches =>
            {
                string word = matches == 100 ? "total" : "count";
                Ui.Search(popup, word);
                Assert.Equal(matches, popup.Matches.Count);
                Ui.Settle();
                return (popup.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<SearchLine>), () => { });
            });
        }
        finally
        {
            window.Close();
        }

        static IEnumerable<(string, string)> Lines(string word, int matches) => Enumerable.Range(0, matches / 10)
            .Select(file => ($"{word}{file:000}.cs", string.Concat(Enumerable.Range(0, 10).Select(i => $"int {word}{i} = {file};\n"))));
    }

    private void A_scroll_step_down_two_thousand_commits_of_the_git_log_hands_the_rows_other_commits_never_making_one_again()
    {
        Holds("The Git log", "commits", 100, 2000, commits =>
        {
            string root = Repository(commits, files: 1);
            MainWindow window = Git(root);
            Ui.Landed(() => window.GitLog.Rows.Count == commits, $"the log's {commits} commits");
            return (window, window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>));
        });
    }

    /// <summary>A test class of 100 tests, then the same class written again with a thousand, in one window.</summary>
    private void A_scroll_step_down_a_thousand_tests_of_the_unit_tests_window_hands_the_rows_other_tests_never_making_one_again()
    {
        string root = Folder();
        TestFolders.Write(root, "Sums.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n"
            + $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n"
            + $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n</Project>\n");
        TestFolders.Write(root, "SumTests.cs", Sums(100));
        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        try
        {
            window.Perform(AppActions.ShowTests);
            Holds("The Unit Tests window", "tests", 100, 1000, tests =>
            {
                if (tests != 100)
                {
                    TestFolders.Write(root, "SumTests.cs", Sums(tests));
                }
                // The code written again reaches the window once the folder watcher hears of it.
                Ui.WaitFor(() => window.Tests.Tree.Rows.Count(r => r.Kind == Passenger.App.Testing.TestNodeKind.Method) == tests, $"the {tests} tests found in the code");
                Ui.Settle();
                return (window.BottomPanel.Tests.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, window.Tests.Tree.Rows)), () => { });
            });
        }
        finally
        {
            window.Close();
        }

        static string Sums(int tests) => "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n"
            + string.Concat(Enumerable.Range(0, tests).Select(i => $"    [Fact]\n    public void Sum{i:0000}() {{ }}\n\n")) + "}\n";
    }

    /// <summary>Holds a list of many items to what one of few costs a wheel step, and both to make no row again, each in a
    /// window of its own, closed once scrolled.</summary>
    private static void Holds(string list, string items, int few, int many, Func<int, (MainWindow Window, ListBox List)> open) =>
        Holds(list, items, few, many, count =>
        {
            (MainWindow window, ListBox shown) = open(count);
            return (shown, window.Close);
        });

    /// <summary>Holds a list of many items to what one of few costs a wheel step, and both to make no row again: each shown
    /// by <paramref name="open"/>, whose Done is called once it is scrolled.</summary>
    internal static void Holds(string list, string items, int few, int many, Func<int, (ListBox List, Action Done)> open)
    {
        (long fewStep, int fewMade) = Scrolls(few);
        (long manyStep, int manyMade) = Scrolls(many);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{list}: a wheel step allocated {fewStep / 1024} KB on the UI thread over {few} {items}, {manyStep / 1024} KB over {many}; a scroll past the rows shown made {fewMade} and {manyMade} again");
        Assert.True(fewMade == 0 && manyMade == 0,
            $"Scrolling {list.ToLowerInvariant()} made {fewMade} rows of {few} {items} and {manyMade} of {many} again, where each row is kept and handed another: "
            + "every item scrolled into view was built and styled anew");
        Assert.True(manyStep <= fewStep + (64 * 1024),
            $"A wheel step down {list.ToLowerInvariant()} allocated {manyStep / 1024} KB on the UI thread over {many} {items}, against {fewStep / 1024} KB over {few}: a step costs the list's length");

        (long Least, int Made) Scrolls(int count)
        {
            (ListBox shown, Action done) = open(count);
            (long Least, int Made) scrolled = Scrolled(shown);
            done();
            return scrolled;
        }
    }

    /// <summary>The least the UI thread allocated for a wheel step down a list, of five after one step more, and how many
    /// rows a scroll past every row first shown made again: rows shown beyond one to each container the list had. The
    /// step more is not counted: a list grown shorter since its rows were laid out holds the rows past its new foot until
    /// it scrolls, so its first step brings in a row fewer than every step after.</summary>
    internal static (long Least, int Made) Scrolled(ListBox list)
    {
        TopLevel top = TopLevel.GetTopLevel(list) ?? throw new Xunit.Sdk.XunitException("The list is not shown");
        Point middle = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), top)
            ?? throw new Xunit.Sdk.XunitException("The list is not laid out");
        top.MouseWheel(middle, new Vector(0, -1));
        Ui.Settle();
        long least = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            top.MouseWheel(middle, new Vector(0, -1));
            Ui.Settle();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        HashSet<Control> containers = [];
        HashSet<Control> rows = [];
        Seen();
        HashSet<object?> shown = [.. list.GetRealizedContainers().Select(c => c.DataContext)];
        for (int i = 0; i < 100 && list.GetRealizedContainers().Any(c => shown.Contains(c.DataContext)); i++)
        {
            top.MouseWheel(middle, new Vector(0, -1));
            Ui.Settle();
            Seen();
        }
        Assert.False(list.GetRealizedContainers().Any(c => shown.Contains(c.DataContext)), "A hundred wheel steps down the list left a row first shown in view");
        return (least, rows.Count - containers.Count);

        void Seen()
        {
            foreach (Control container in list.GetRealizedContainers())
            {
                containers.Add(container);
                rows.Add((container as ListBoxItem)?.Presenter?.Child ?? throw new Xunit.Sdk.XunitException($"A {container.GetType().Name} of the list shows no row"));
            }
        }
    }

    private string Folder()
    {
        string folder = PathNames.Canonical(TestFolders.Create("lists-"));
        folders.Add(folder);
        return folder;
    }

    /// <summary>A repository of commits on main, each adding <paramref name="earlier"/> files, the last adding
    /// <paramref name="files"/>, and more branches at main, so many of each name given: its history alone, which is all the
    /// lists read, none of its files checked out (a sparse checkout of nothing).</summary>
    private string Repository(int commits, int files, (string Name, int Count)[]? branches = null, int earlier = 1)
    {
        string root = Folder();
        TestGit.Run(root, "init", "-q", "-b", "main");
        StringBuilder stream = new();
        for (int commit = 0; commit < commits; commit++)
        {
            string subject = $"Commit {commit}";
            stream.Append(CultureInfo.InvariantCulture, $"commit refs/heads/main\ncommitter Passenger Tests <tests@passenger.invalid> {1_700_000_000 + (commit * 60)} +0000\n");
            stream.Append(CultureInfo.InvariantCulture, $"data {Encoding.UTF8.GetByteCount(subject)}\n{subject}\n");
            for (int file = 0; file < (commit == commits - 1 ? files : earlier); file++)
            {
                string text = $"class C{commit}x{file} {{ }}\n";
                stream.Append(CultureInfo.InvariantCulture, $"M 644 inline src/Area{file / 100}/C{commit}x{file}.cs\ndata {Encoding.UTF8.GetByteCount(text)}\n{text}\n");
            }
        }
        foreach ((string name, int count) in branches ?? [])
        {
            for (int branch = 0; branch < count; branch++)
            {
                stream.Append(CultureInfo.InvariantCulture, $"reset refs/heads/{name}{branch:0000}\nfrom refs/heads/main\n\n");
            }
        }
        ChildResult imported = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", ["fast-import", "--quiet"], root), stream.ToString(),
            TimeSpan.FromMinutes(1).Scaled())), "git fast-import", TimeSpan.FromMinutes(1) + TestFolders.Spare);
        Assert.True(imported.Succeeded, $"git fast-import made no repository of {commits} commits: {imported.Error}");
        TestGit.Run(root, "sparse-checkout", "set", "--no-cone", "/none");
        TestGit.Run(root, "reset", "-q", "--hard");
        return root;
    }

    /// <summary>The window on a repository with its Git window shown.</summary>
    private static MainWindow Git(string root)
    {
        MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
        window.Perform(AppActions.ShowGit);
        Ui.Settle();
        return window;
    }
}

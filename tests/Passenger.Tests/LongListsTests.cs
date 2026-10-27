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

    /// <summary>A word found 100 times and one found a thousand, ten to a file, both searched for in one window.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_matches_in_files_hands_the_rows_other_matches_never_making_one_again()
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
                popup.Text = word;
                // The box tells of its new text after the frame, which puts a search begun before it off until a pause.
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Task searching = popup.SearchAsync();
                Ui.WaitFor(() => searching.IsCompleted && popup.Searching.IsCompleted && popup.Status != "Searching…", $"the search for {word}");
                Assert.Equal(matches, popup.Matches.Count);
                Ui.Settle();
                return (popup.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<SearchMatch>), () => { });
            });
        }
        finally
        {
            window.Close();
        }

        static IEnumerable<(string, string)> Lines(string word, int matches) => Enumerable.Range(0, matches / 10)
            .Select(file => ($"{word}{file:000}.cs", string.Concat(Enumerable.Range(0, 10).Select(i => $"int {word}{i} = {file};\n"))));
    }

    [AvaloniaFact]
    public void A_scroll_step_down_two_thousand_commits_of_the_git_log_hands_the_rows_other_commits_never_making_one_again()
    {
        Holds("The Git log", "commits", 100, 2000, commits =>
        {
            string root = Repository(commits, files: 1);
            MainWindow window = Git(root);
            Ui.Landed(() => window.GitLog.Rows.Count == commits, $"the log's {commits} commits");
            return (window, window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>));
        });
    }

    /// <summary>A commit of 100 files and one of a thousand, each chosen in turn in one window's log.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_commits_thousand_files_hands_the_rows_other_files_never_making_one_again()
    {
        string root = Repository(commits: 2, files: 1000, earlier: 100);
        MainWindow window = Git(root);
        try
        {
            Ui.Landed(() => window.GitLog.Rows.Count == 2, "the log's two commits");
            ListBox log = window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>);
            Holds("A commit's files", "files", 100, 1000, files =>
            {
                // The log lists the newest first: the commit of a thousand files, then the one of 100.
                Ui.Click(Ui.Row(log, window.GitLog.Rows[files == 100 ? 1 : 0]));
                Ui.Landed(() => window.GitLog.Files.Count == files, $"the commit's {files} files");
                return (window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<FileChange>), () => { });
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>100 branches and a thousand of another name in one repository, each listed in turn by the panel's search.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_branches_hands_the_rows_other_branches_never_making_one_again()
    {
        string root = Repository(commits: 1, files: 1, branches: [("few", 100), ("many", 1000)]);
        MainWindow window = Git(root);
        try
        {
            BranchesPanel panel = window.GitBranches;
            Ui.Landed(() => panel.Branches.Count > 1100, "the repository's 1,100 branches");
            Holds("The branches", "branches", 100, 1000, branches =>
            {
                string name = branches == 100 ? "few" : "many";
                panel.SearchText = name;
                Ui.Landed(() => panel.Rows.Count(r => r.Branch is not null) == branches, $"the {branches} branches named {name}, where the search shows {panel.Rows.Count(r => r.Branch is not null)}");
                return (panel.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<BranchNode>), () => { });
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A build of 100 warnings, then the same project's of a thousand, its code written again between, in one window.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_builds_thousand_problems_hands_the_rows_other_problems_never_making_one_again()
    {
        string root = Folder();
        TestFolders.Write(root, "Warned.csproj", TestProjects.Console());
        TestFolders.Write(root, "Program.cs", Program(100));
        MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
        try
        {
            ProblemsModel problems = window.BottomPanel.Problems;
            Holds("Problems", "problems", 100, 1000, warnings =>
            {
                if (warnings != 100)
                {
                    TestFolders.Write(root, "Program.cs", Program(warnings));
                }
                window.Perform(AppActions.Build);
                Ui.WaitFor(() => problems.Problems.Count == warnings, () => $"the build's {warnings} warnings, where it has {problems.Problems.Count}: {window.Status}", TimeSpan.FromMinutes(3));
                window.Perform(AppActions.ShowProblems);
                problems.Shown = ProblemsTab.Build;
                Ui.Settle();
                return (window.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, problems.Rows)), () => { });
            });
        }
        finally
        {
            window.Close();
        }

        static string Program(int warnings)
        {
            StringBuilder program = new("class Program\n{\n    static void Main()\n    {\n");
            for (int i = 0; i < warnings; i++)
            {
                program.Append(CultureInfo.InvariantCulture, $"        int unused{i};\n");
            }
            return program.Append("    }\n}\n").ToString();
        }
    }

    /// <summary>Go to File's answer for a name a hundred files have and for one a thousand have, in one window's folder.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_go_to_files_answer_over_a_thousand_files_hands_the_rows_other_files_never_making_one_again()
    {
        string root = Folder();
        TestFolders.WriteAll(root, Named("Sums", 100).Concat(Named("Lines", 1000)));
        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.FileIndex.Count == 1100 && !window.FileIndex.IsWalking, "the folder's 1,100 files indexed");
            Holds("Go to File", "files", 100, 1000, files =>
            {
                string name = files == 100 ? "Sums" : "Lines";
                Ui.Run(window, AppActions.GoToFile);
                GoToFilePopup popup = Assert.IsType<GoToFilePopup>(window.Popup);
                window.KeyTextInput(name);
                Ui.Settle();
                Assert.True(popup.Rows.Count > 40, $"Go to File found {popup.Rows.Count} of {files} files for {name}");
                return (popup.GetVisualDescendants().OfType<ListBox>().Single(), () => Ui.Press(window, "ESCAPE"));
            });
        }
        finally
        {
            window.Close();
        }

        static IEnumerable<(string, string)> Named(string name, int files) =>
            Enumerable.Range(0, files).Select(file => ($"{name}{file / 100}/{name}{file:0000}.cs", $"class {name}{file} {{ }}\n"));
    }

    /// <summary>A feed finding 100 packages for one search and a thousand for another, both searched in one window.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_packages_a_feed_found_hands_the_rows_other_packages_never_making_one_again()
    {
        string root = Folder();
        TestFolders.Write(root, "Lib.csproj", TestProjects.Library());
        MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
        try
        {
            NuGetView nuget = window.NuGet;
            nuget.Feed = new ManyPackages();
            window.Perform(AppActions.ShowNuGet);
            Ui.Settle();
            Holds("NuGet's packages", "packages", 100, 1000, packages =>
            {
                string query = packages == 100 ? "Few" : "Many";
                nuget.Query = query;
                Ui.Landed(() => nuget.Rows.Count == packages, $"the feed's {packages} packages for {query}");
                return (nuget.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, nuget.Rows)), () => { });
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file's history shown once it has 100 saves, and again once it has a thousand, in one window.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_files_thousand_saves_in_local_history_hands_the_rows_other_saves_never_making_one_again()
    {
        string root = Folder();
        TestFolders.Write(root, "Saved.cs", "class Saved\n{\n}\n");
        MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
        try
        {
            window.OpenFile(Path.Combine(root, "Saved.cs"));
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument saved = window.EditorArea.Active!;
            editor.GoTo(new Passenger.Text.TextPosition(1, 1));
            int made = 0;
            Holds("Local History", "saves", 100, 1000, saves =>
            {
                // The keys drawn in the smallest window, since a frame costs its size, and the history shown in a large one.
                (window.Width, window.Height) = (MainWindow.SmallestWidth, MainWindow.SmallestHeight);
                editor.Focus();
                for (; made < saves; made++)
                {
                    window.KeyTextInput("x");
                    // Each save written before the next key; the folder watcher hears them together, as a person's quick saves.
                    window.Perform(AppActions.Save);
                    Ui.PumpUntil(() => !saved.IsModified, $"save {made + 1} written");
                }
                (window.Width, window.Height) = (1300, 900);
                Ui.Settle();
                window.Perform(AppActions.ShowLocalHistory);
                Passenger.App.History.HistoryTab history = window.LocalHistoryWindow.Shown ?? throw new Xunit.Sdk.XunitException($"Show History showed no history: {window.Status}");
                Ui.Landed(() => history.Rows.Count >= saves, $"the file's {saves} saves, where its history lists {history.Rows.Count}");
                // Closed once scrolled, as its × closes it, so the saves after it are not each listed again.
                return (history.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<Passenger.App.History.HistoryRow>),
                    () => window.LocalHistoryWindow.Close(history));
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A test class of 100 tests, then the same class written again with a thousand, in one window.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_tests_of_the_unit_tests_window_hands_the_rows_other_tests_never_making_one_again()
    {
        string root = Folder();
        TestFolders.Write(root, "Sums.Tests.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n"
            + "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
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

    /// <summary>Find Usages of a method called a hundred times and of one called a thousand, ten calls to a file, so the
    /// rows handed on are files' as well as usages': both searches in one window, on one language server.</summary>
    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_usages_in_the_find_window_hands_the_rows_other_usages_never_making_one_again()
    {
        string root = Folder();
        TestFolders.Write(root, "Used.csproj", TestProjects.Library());
        TestFolders.Write(root, "Used.cs", "public static class Used\n{\n    public static int Few() => 1;\n\n    public static int Many() => 1;\n}\n");
        foreach ((string name, int calls) in new[] { ("Few", 100), ("Many", 1000) })
        {
            for (int file = 0; file < calls / 10; file++)
            {
                TestFolders.Write(root, $"{name}/Calls{file:000}.cs", $"static class {name}Calls{file}\n{{\n    static int All() =>\n"
                    + string.Join(" +\n", Enumerable.Repeat($"        Used.{name}()", 10)) + ";\n}\n");
            }
        }
        Restore.Run(root);
        MainWindow window = Ui.OpenFolder(root, width: 1300, height: 900);
        try
        {
            Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to load the project ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
            window.OpenFile(Path.Combine(root, "Used.cs"));
            Ui.Settle();
            FindView find = window.Find;
            Holds("The Find window", "usages", 100, 1000, usages =>
            {
                string name = usages == 100 ? "Few" : "Many";
                Ui.PlaceAfter(window.EditorArea.CodeEditor, $"int {name[..2]}");
                Ui.Run(window, AppActions.FindUsages);
                Ui.WaitFor(() => find.Shown == $"Usages of {name}" && find.Rows.Count(r => r.Usage is not null) == usages + 1,
                    () => $"{name}'s {usages} calls and declaration in the Find window, where it shows {find.Shown} with {find.Rows.Count(r => r.Usage is not null)} usages: {window.Status}");
                Ui.Settle();
                return (find.GetVisualDescendants().OfType<ListBox>().Single(), () => { });
            });
        }
        finally
        {
            window.Close();
        }
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

    /// <summary>A feed whose search for Few finds 100 packages and any other a thousand.</summary>
    private sealed class ManyPackages : Passenger.Packages.IPackageFeed
    {
        public string Name => "Many";

        public Task<IReadOnlyList<Passenger.Packages.PackageSummary>> SearchAsync(string query, bool prerelease, CancellationToken cancel)
        {
            int count = query == "Few" ? 100 : 1000;
            return Task.FromResult<IReadOnlyList<Passenger.Packages.PackageSummary>>(
                [.. Enumerable.Range(0, count).Select(i => new Passenger.Packages.PackageSummary($"{query}.Package{i:0000}", "1.0.0", $"Package {i}", "Passenger Tests", count - i))]);
        }

        public Task<IReadOnlyList<string>> VersionsAsync(string id, bool prerelease, CancellationToken cancel) => Task.FromResult<IReadOnlyList<string>>(["1.0.0"]);
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

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

    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_matches_in_files_hands_the_rows_other_matches_never_making_one_again()
    {
        Holds("Find in Files", "matches", 100, FileSearch.MostMatches, matches =>
        {
            string root = Folder();
            for (int file = 0; file < matches / 10; file++)
            {
                TestFolders.Write(root, $"Lines{file:000}.cs", string.Concat(Enumerable.Range(0, 10).Select(i => $"int count{i} = {file};\n")));
            }
            MainWindow window = Ui.OpenFolder(root, languageServer: false);
            Ui.WaitFor(() => window.FileIndex.Count > 0 && !window.FileIndex.IsWalking, "the folder's files indexed");
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            popup.Text = "count";
            Task searching = popup.SearchAsync();
            Ui.WaitFor(() => searching.IsCompleted, "the search for count");
            Assert.Equal(matches, popup.Matches.Count);
            Ui.Settle();
            return (window, popup.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<SearchMatch>));
        });
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

    [AvaloniaFact]
    public void A_scroll_step_down_a_commits_thousand_files_hands_the_rows_other_files_never_making_one_again()
    {
        Holds("A commit's files", "files", 100, 1000, files =>
        {
            string root = Repository(commits: 1, files);
            MainWindow window = Git(root);
            Ui.Landed(() => window.GitLog.Rows.Count == 1, "the log's commit");
            ListBox log = window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>);
            Ui.Click(Ui.Row(log, window.GitLog.Rows[0]));
            Ui.Landed(() => window.GitLog.Files.Count == files, $"the commit's {files} files");
            return (window, window.GitLog.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<FileChange>));
        });
    }

    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_branches_hands_the_rows_other_branches_never_making_one_again()
    {
        Holds("The branches", "branches", 100, 1000, branches =>
        {
            string root = Repository(commits: 1, files: 1, branches);
            MainWindow window = Git(root);
            Ui.Landed(() => window.GitBranches.Branches.Count >= branches, $"the {branches} branches");
            return (window, window.GitBranches.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IEnumerable<BranchNode>));
        });
    }

    /// <summary>Holds a list of many items to what one of few costs a wheel step, and both to make no row again.</summary>
    private static void Holds(string list, string items, int few, int many, Func<int, (MainWindow Window, ListBox List)> open)
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
            (MainWindow window, ListBox shown) = open(count);
            (long Least, int Made) scrolled = Scrolled(shown);
            window.Close();
            return scrolled;
        }
    }

    /// <summary>The least the UI thread allocated for a wheel step down a list, of five, and how many rows a scroll past
    /// every row first shown made again: rows shown beyond one to each container the list had.</summary>
    private static (long Least, int Made) Scrolled(ListBox list)
    {
        TopLevel top = TopLevel.GetTopLevel(list) ?? throw new Xunit.Sdk.XunitException("The list is not shown");
        Point middle = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), top)
            ?? throw new Xunit.Sdk.XunitException("The list is not laid out");
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

    /// <summary>A repository of commits on main, each adding a file, the last adding <paramref name="files"/>, and
    /// <paramref name="branches"/> more branches at main.</summary>
    private string Repository(int commits, int files, int branches = 0)
    {
        string root = Folder();
        TestGit.Run(root, "init", "-q", "-b", "main");
        StringBuilder stream = new();
        for (int commit = 0; commit < commits; commit++)
        {
            string subject = $"Commit {commit}";
            stream.Append(CultureInfo.InvariantCulture, $"commit refs/heads/main\ncommitter Passenger Tests <tests@passenger.invalid> {1_700_000_000 + (commit * 60)} +0000\n");
            stream.Append(CultureInfo.InvariantCulture, $"data {Encoding.UTF8.GetByteCount(subject)}\n{subject}\n");
            for (int file = 0; file < (commit == commits - 1 ? files : 1); file++)
            {
                string text = $"class C{commit}x{file} {{ }}\n";
                stream.Append(CultureInfo.InvariantCulture, $"M 644 inline src/Area{file / 100}/C{commit}x{file}.cs\ndata {Encoding.UTF8.GetByteCount(text)}\n{text}\n");
            }
        }
        for (int branch = 0; branch < branches; branch++)
        {
            stream.Append(CultureInfo.InvariantCulture, $"reset refs/heads/branch{branch:0000}\nfrom refs/heads/main\n\n");
        }
        ChildResult imported = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", ["fast-import", "--quiet"], root), stream.ToString(), TimeSpan.FromMinutes(1).Scaled()))
            .GetAwaiter().GetResult();
        Assert.True(imported.Succeeded, $"git fast-import made no repository of {commits} commits: {imported.Error}");
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

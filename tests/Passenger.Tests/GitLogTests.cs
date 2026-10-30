using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>The Git log over histories of hundreds and thousands of commits with merges, tags and names in other
/// scripts: each commit listed as git has it, read without a copy of git's output on the UI thread, and its graph drawn
/// with pens its theme made once.</summary>
public sealed class GitLogTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (string folder in folders)
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Reading the log again over 2,000 commits allocates on the UI thread less than a copy of git's output for
    /// the 1,900 more than over 100 would take as text, the least of three reads each; every commit is listed as git has
    /// it.</summary>
    [AvaloniaFact]
    public void Reading_two_thousand_commits_into_the_log_makes_no_copy_of_gits_output_on_the_ui_thread()
    {
        (long few, int fewText) = Read(100);
        (long many, int manyText) = Read(2000);
        long text = (manyText - fewText) * (long)sizeof(char);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Reading the log allocated {few / 1024:N0} KB on the UI thread over 100 commits, {many / 1024:N0} KB over 2,000; git's output for the 1,900 more is {text / 1024:N0} KB as text");
        Assert.True(many - few < text,
            $"Reading 1,900 commits more into the log allocated {(many - few) / 1024:N0} KB more on the UI thread, where git's output for them is {text / 1024:N0} KB as text: the window's thread made a copy of it");

        (long Least, int Text) Read(int commits)
        {
            string root = Repository(commits);
            MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
            window.Perform(AppActions.ShowGit);
            GitLogView log = window.GitLog;
            Ui.Landed(() => log.Rows.Count == commits, $"the log's {commits} commits");
            string output = TestGit.Run(root, "log", "--topo-order", "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e", "--branches", "--tags", "HEAD", "--");
            Listed(output, log);
            long least = long.MaxValue;
            for (int read = 0; read < 3; read++)
            {
                Ui.Settle();
                long before = GC.GetAllocatedBytesForCurrentThread();
                Task loading = log.LoadAsync();
                Ui.Landed(() => loading.IsCompleted, "the log read again");
                Ui.Settle();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            Listed(output, log);
            window.Close();
            return (least, output.Length);
        }
    }

    /// <summary>Scrolling the log draws other commits' parts of the graph with the pens made for its lanes' colours, none
    /// made again; the light theme makes its own once, one for each colour, and draws in its colours.</summary>
    [AvaloniaFact]
    public void The_graph_draws_its_rows_with_the_pens_its_theme_made_once_and_the_light_themes_in_its_colours()
    {
        string root = Repository(400);
        MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 400, "the log's 400 commits");
        ListBox list = log.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>);
        Assert.True(log.Rows.Any(r => r.Width > 1), "The history laid out no row in more than one lane");
        Point middle = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
        int made = GitLogView.PensMade;
        for (int step = 0; step < 20; step++)
        {
            window.MouseWheel(middle, new Vector(0, -1));
            Ui.Settle();
        }
        Assert.True(GitLogView.PensMade == made,
            $"Twenty wheel steps down the log made {GitLogView.PensMade - made} pens for the rows they brought in, where each lane's colour has its pen");
        Drawn(ThemeVariant.Dark);

        window.RequestedThemeVariant = ThemeVariant.Light;
        Ui.Settle();
        Assert.True(GitLogView.PensMade - made == 6, $"The light theme made {GitLogView.PensMade - made} pens for the graph's six colours");
        made = GitLogView.PensMade;
        Drawn(ThemeVariant.Light);
        for (int step = 0; step < 20; step++)
        {
            window.MouseWheel(middle, new Vector(0, 1));
            Ui.Settle();
        }
        Assert.True(GitLogView.PensMade == made, $"Twenty wheel steps up the log in the light theme made {GitLogView.PensMade - made} pens");
        Drawn(ThemeVariant.Light);
        window.Close();

        // The first row shown in lane 0 has its dot in the theme's first colour.
        void Drawn(ThemeVariant theme)
        {
            Control row = list.GetRealizedContainers().First(c => c.DataContext is GraphRow { Lane: 0 }
                && c.TranslatePoint(default, list) is { } top && top.Y >= 0 && top.Y + c.Bounds.Height <= list.Bounds.Height);
            Control graph = row.GetVisualDescendants().OfType<Grid>().First().Children[0];
            Color dot = Ui.Pixel(window, graph.TranslatePoint(new Point(10, graph.Bounds.Height / 2), window)!.Value);
            Assert.True(dot == Lane(theme), $"In the {theme} theme lane 0's dot is drawn {dot}, not its {Lane(theme)}");
        }
    }

    /// <summary>A merge commit chosen in the log lists what the branch it merged brought in, against its first parent, and a
    /// file of it opens its diff against that parent.</summary>
    [AvaloniaFact]
    public void A_merge_commit_lists_the_merged_branchs_files_each_opening_its_diff_against_the_first_parent()
    {
        const string Shop = "class Shop\n{\n    int count;\n}\n";
        string root = PathNames.Canonical(TestFolders.Create("gitlog-merge-"));
        folders.Add(root);
        File.WriteAllText(Path.Combine(root, "Shop.cs"), Shop);
        TestGit.Init(root);
        TestGit.Run(root, "switch", "-q", "-c", "side");
        string merged = Shop.Replace("int count", "long count", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "Shop.cs"), merged);
        File.WriteAllText(Path.Combine(root, "Side.md"), "side\n");
        TestGit.Run(root, "add", "-A");
        TestGit.Run(root, "commit", "-q", "-m", "Side");
        TestGit.Run(root, "switch", "-q", "main");
        File.WriteAllText(Path.Combine(root, "Main.md"), "main\n");
        TestGit.Run(root, "add", "-A");
        TestGit.Run(root, "commit", "-q", "-m", "Main");
        TestGit.Run(root, "merge", "-q", "--no-ff", "-m", "Merge side", "side");

        MainWindow window = Ui.OpenFolder(root, languageServer: false);
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Any(r => r.Commit.Subject == "Merge side"), "the log with the merge");
        CommitInfo merge = log.Rows.First(r => r.Commit.Subject == "Merge side").Commit;
        log.Select(merge);
        Ui.Landed(() => log.Details.Contains("Merge side", StringComparison.Ordinal), "the merge commit's details");
        string listed = string.Join(", ", log.Files.Select(f => $"{f.Name} ({f.Kind})").Order(StringComparer.Ordinal));
        Assert.True(listed == "Shop.cs (Modified), Side.md (Added)", $"The merge commit chosen in the log listed [{listed}], where the branch it merged brought in Shop.cs and Side.md");

        ListBox files = log.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, log.Files));
        Ui.DoubleClick(Ui.Row(files, log.Files.First(f => f.Name == "Shop.cs")));
        Ui.Landed(() => window.Frontmost is DiffWindow, "the diff of Shop.cs in the merge");
        DiffView diff = ((DiffWindow)window.Frontmost).View;
        Assert.True(diff.Left.Document.Snapshot.ToString() == Shop, $"The merge's diff of Shop.cs has on its left \"{diff.Left.Document.Snapshot}\", not the first parent's text");
        Assert.Equal(merged, diff.Right.Document.Snapshot.ToString());
        ((DiffWindow)window.Frontmost).Close();
        window.Close();
    }

    private static Color Lane(ThemeVariant theme) =>
        Application.Current!.TryFindResource(TokenKeys.Graph_0, theme, out object? brush) && brush is ISolidColorBrush solid
            ? solid.Color
            : throw new Xunit.Sdk.XunitException($"The {theme} theme has no {TokenKeys.Graph_0}");

    /// <summary>The log lists each commit of git's output (records ended by 0x1E, fields apart at 0x1F) as git has it: its
    /// hash, parents, author, email, time, branches and tags (less HEAD's arrow) and subject, in git's order.</summary>
    private static void Listed(string output, GitLogView log)
    {
        string[] records = [.. output.Split('\x1e').Select(r => r.Trim('\n')).Where(r => r.Length > 0)];
        Assert.Equal(records.Length, log.Rows.Count);
        for (int i = 0; i < records.Length; i++)
        {
            string[] field = records[i].Split('\x1f');
            CommitInfo commit = log.Rows[i].Commit;
            string expected = string.Join(" | ", field[0], field[1], field[2], field[3], field[4], field[5].Replace("HEAD -> ", string.Empty, StringComparison.Ordinal), field[6]);
            string listed = string.Join(" | ", commit.Hash, string.Join(' ', commit.Parents), commit.Author, commit.Email,
                commit.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), string.Join(", ", commit.Refs), commit.Subject);
            Assert.True(expected == listed, $"The log's commit {i} is listed as\n{listed}\nwhere git has it as\n{expected}");
        }
    }

    /// <summary>A repository of this many commits on main, every tenth a merge of a commit made beside it on side, each
    /// hundredth tagged, by authors with names in other scripts and subjects in them too.</summary>
    private string Repository(int commits)
    {
        string root = PathNames.Canonical(TestFolders.Create("gitlog-"));
        folders.Add(root);
        TestGit.Run(root, "init", "-q", "-b", "main");
        StringBuilder stream = new();
        int made = 0;
        int mark = 0;
        int tip = 0;
        while (made < commits)
        {
            if (tip > 0 && made % 10 == 8 && made + 2 <= commits)
            {
                Commit("side", tip, 0);
                Commit("main", 0, mark);
            }
            else
            {
                Commit("main", 0, 0);
            }
        }
        ChildResult imported = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", ["fast-import", "--quiet"], root), stream.ToString(),
            TimeSpan.FromMinutes(1).Scaled())), "git fast-import", TimeSpan.FromMinutes(1) + TestFolders.Spare);
        Assert.True(imported.Succeeded, $"git fast-import made no repository of {commits} commits: {imported.Error}");
        TestGit.Run(root, "reset", "-q", "--hard");
        return root;

        void Commit(string branch, int from, int merge)
        {
            string[] authors = ["Zoë Ångström", "Łukasz Żółć", "山田 太郎", "Ада Лавлейс", "Passenger Tests"];
            string author = authors[made % authors.Length];
            string subject = made % 3 == 0 ? $"Commit {made}: café crème, 日本語, emoji 😀" : $"Commit {made}";
            string text = $"{made}\n";
            mark++;
            stream.Append(CultureInfo.InvariantCulture, $"commit refs/heads/{branch}\nmark :{mark}\n");
            stream.Append(CultureInfo.InvariantCulture, $"author {author} <author{made % authors.Length}@passenger.invalid> {1_700_000_000 + (made * 60)} +0000\n");
            stream.Append(CultureInfo.InvariantCulture, $"committer Passenger Tests <tests@passenger.invalid> {1_700_000_000 + (made * 60)} +0000\n");
            stream.Append(CultureInfo.InvariantCulture, $"data {Encoding.UTF8.GetByteCount(subject)}\n{subject}\n");
            if (from > 0)
            {
                stream.Append(CultureInfo.InvariantCulture, $"from :{from}\n");
            }
            if (merge > 0)
            {
                stream.Append(CultureInfo.InvariantCulture, $"merge :{merge}\n");
            }
            stream.Append(CultureInfo.InvariantCulture, $"M 644 inline {branch}.txt\ndata {Encoding.UTF8.GetByteCount(text)}\n{text}\n");
            if (made % 100 == 50)
            {
                stream.Append(CultureInfo.InvariantCulture, $"reset refs/tags/v{made}\nfrom :{mark}\n\n");
            }
            if (branch == "main")
            {
                tip = mark;
            }
            made++;
        }
    }
}

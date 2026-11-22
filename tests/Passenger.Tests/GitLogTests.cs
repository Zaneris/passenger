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

    /// <summary>The Git log as one flow, each step the check one test made in an instance of its own: two thousand
    /// commits read with no copy of git's output on the window's thread, each lane's line running unbroken to the
    /// commit it joins with every row's author and date lined up, and a merge commit listing the merged branch's files,
    /// each opening its diff against the first parent.</summary>
    [AvaloniaFact]
    public void The_log_reads_thousands_of_commits_cheaply_and_draws_each_lane_unbroken_to_its_commit()
    {
        Flow.StepApart<GitLogTests>("reading two thousand commits into the log makes no copy of gits output on the ui thread", t => t.Reading_two_thousand_commits_into_the_log_makes_no_copy_of_gits_output_on_the_ui_thread());
        Flow.StepApart<GitLogTests>("each lanes line runs unbroken to the commit it joins and every rows author and date line up", t => t.Each_lanes_line_runs_unbroken_to_the_commit_it_joins_and_every_rows_author_and_date_line_up());
        Flow.StepApart<GitLogTests>("a merge commit lists the merged branchs files each opening its diff against the first parent", t => t.A_merge_commit_lists_the_merged_branchs_files_each_opening_its_diff_against_the_first_parent());
    }

    /// <summary>Reading the log again over 2,000 commits allocates on the UI thread less than a copy of git's output for
    /// the 1,900 more than over 100 would take as text, the least of three reads each; every commit is listed as git has
    /// it.</summary>
    private void Reading_two_thousand_commits_into_the_log_makes_no_copy_of_gits_output_on_the_ui_thread()
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

    /// <summary>Over a merged branch and one not merged, each lane's line runs on unbroken from row to row, in its own lane
    /// until the row of the commit it waits for, and every row's author and date stand at one place, however many lanes
    /// its part of the graph crosses.</summary>
    private void Each_lanes_line_runs_unbroken_to_the_commit_it_joins_and_every_rows_author_and_date_line_up()
    {
        string root = PathNames.Canonical(TestFolders.Create("gitlanes-"));
        folders.Add(root);
        File.WriteAllText(Path.Combine(root, "Notes.txt"), "base\n");
        TestGit.Init(root);
        void Commit(string text, string message)
        {
            File.WriteAllText(Path.Combine(root, "Notes.txt"), text);
            TestGit.Run(root, "commit", "-q", "-am", message);
        }
        TestGit.Run(root, "switch", "-q", "-c", "feature");
        Commit("feature\n", "On feature");
        TestGit.Run(root, "switch", "-q", "main");
        File.WriteAllText(Path.Combine(root, "Other.txt"), "main\n");
        TestGit.Run(root, "add", "Other.txt");
        TestGit.Run(root, "commit", "-q", "-m", "On main");
        TestGit.Run(root, "merge", "-q", "--no-edit", "feature");
        TestGit.Run(root, "switch", "-q", "-c", "topic");
        Commit("topic\n", "On topic");
        TestGit.Run(root, "switch", "-q", "main");
        Commit("main again\n", "Main again");
        MainWindow window = Ui.OpenFolder(root, languageServer: false, width: 1300, height: 900);
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Perform(AppActions.ShowGit);
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 6, "the log's six commits");
        Assert.True(log.Rows.Count(r => r.Width > 1) >= 3, $"The history laid out {log.Rows.Count(r => r.Width > 1)} rows in two lanes, where its branches make three or more");
        ListBox list = log.GetVisualDescendants().OfType<ListBox>().Single(l => l.ItemsSource is IReadOnlyList<GraphRow>);
        Ui.Settle();
        Control[] rows = [.. log.Rows.Select(r => list.GetRealizedContainers().Single(c => ReferenceEquals(c.DataContext, r)))];

        List<string> broken = [];
        for (int i = 0; i + 1 < rows.Length; i++)
        {
            Control upper = Graph(rows[i]);
            Control lower = Graph(rows[i + 1]);
            for (int lane = 0; lane < log.Rows[i].Below.Count; lane++)
            {
                if (log.Rows[i].Below[lane] is null)
                {
                    continue;
                }
                Point[] at = [Place(upper, lane, upper.Bounds.Height - 0.5), Place(lower, lane, 0.5), Place(upper, -1, upper.Bounds.Height - 0.5), Place(lower, -1, 0.5)];
                Color[] seen = Ui.Pixels(window, at);
                if (seen[0] == seen[2] || seen[1] == seen[3])
                {
                    broken.Add($"lane {lane} between '{log.Rows[i].Commit.Subject}' ({(seen[0] == seen[2] ? "not drawn at its foot" : "drawn")}) and "
                        + $"'{log.Rows[i + 1].Commit.Subject}' ({(seen[1] == seen[3] ? "not drawn at its head" : "drawn")})");
                }
            }
        }
        Assert.True(broken.Count == 0, $"The graph's lines break where one row meets the next: {string.Join("; ", broken)}");

        double[] authors = [.. rows.Select(row => Left(row, 1))];
        double[] dates = [.. rows.Select(row => Left(row, 2))];
        Assert.True(authors.Distinct().Count() == 1 && dates.Distinct().Count() == 1,
            $"The rows' authors stand at [{string.Join(", ", authors)}] and their dates at [{string.Join(", ", dates)}], where each column stands at one place");
        window.Close();

        // A lane's middle in a row's part of the graph, or (lane -1) a place right of every lane, where only its ground is.
        Point Place(Control graph, int lane, double y) =>
            graph.TranslatePoint(new Point(lane < 0 ? graph.Bounds.Width - 2 : 10 + (lane * 12), y), window)!.Value;

        // Where a row's column (1 the author's, 2 the date's) begins in the list.
        double Left(Control row, int column) =>
            row.GetVisualDescendants().OfType<Grid>().First().Children.Single(c => Grid.GetColumn(c) == column).TranslatePoint(default, list)!.Value.X;
    }

    /// <summary>A row's part of the graph.</summary>
    private static Control Graph(Control row) => row.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "Graph");

    /// <summary>A merge commit chosen in the log lists what the branch it merged brought in, against its first parent, and a
    /// file of it opens its diff against that parent.</summary>
    private void A_merge_commit_lists_the_merged_branchs_files_each_opening_its_diff_against_the_first_parent()
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
        // Enter on the row a click gave the keys shows its diff, as a double click does.
        Ui.Click(Ui.Row(files, log.Files.First(f => f.Name == "Shop.cs")));
        Ui.Press(window, "ENTER");
        OpenDiffs.Landed(window, "the diff of Shop.cs in the merge, by Enter on its row").Close();
        Ui.Settle();
        Ui.DoubleClick(Ui.Row(files, log.Files.First(f => f.Name == "Shop.cs")));
        IDiffHost shown = OpenDiffs.Landed(window, "the diff of Shop.cs in the merge");
        DiffView diff = OpenDiffs.ViewOf(shown);
        Assert.True(diff.Left.Document.Snapshot.ToString() == Shop, $"The merge's diff of Shop.cs has on its left \"{diff.Left.Document.Snapshot}\", not the first parent's text");
        Assert.Equal(merged, diff.Right.Document.Snapshot.ToString());
        shown.Close();
        window.Close();
    }

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

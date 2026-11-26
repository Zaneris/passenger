using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

public sealed class PartialCommitTests : IDisposable
{
    private const string Before = "one\ntwo\nthree\nfour\nfive\n";
    private const string After = "ONE\ntwo\nthree\nfour\nFIVE\n";
    private readonly string folder = TestFolders.Create("partial-commit-");
    private string PathOf(string name) => Path.Combine(PathNames.Canonical(folder), name);

    public PartialCommitTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(PathOf("Notes.txt"), Before);
        File.WriteAllText(PathOf("Other.txt"), "old\n");
        TestGit.Init(folder);
        File.WriteAllText(PathOf("Notes.txt"), After);
        File.WriteAllText(PathOf("Other.txt"), "new\n");
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Partial commits as one flow, each step one test's check in an instance of its own: only the changes
    /// chosen committed, a change only staged kept staged, edits keeping the choice, a deleted file or an edit after
    /// the choice refused, a hook's rejection keeping the index and file, CRLF and encodings kept, Rollback and a merge
    /// offering no part, and an unchanged file never read.</summary>
    [AvaloniaFact]
    public void A_partial_commit_takes_only_the_changes_chosen_keeping_what_the_index_and_the_file_hold_besides()
    {
        Flow.StepApart<PartialCommitTests>("a change block can be excluded and only the other is committed", t => t.A_change_block_can_be_excluded_and_only_the_other_is_committed());
        Flow.StepApart<PartialCommitTests>("a change only staged stays staged through a commit of another change of its file", t => t.A_change_only_staged_stays_staged_through_a_commit_of_another_change_of_its_file());
        Flow.StepApart<PartialCommitTests>("edits in the diff keep the changes chosen and leave a new change out", t => t.Edits_in_the_diff_keep_the_changes_chosen_and_leave_a_new_change_out());
        Flow.StepApart<PartialCommitTests>("a file deleted after partial selection is not automatically selected whole", t => t.A_file_deleted_after_partial_selection_is_not_automatically_selected_whole());
        Flow.StepApart<PartialCommitTests>("an edit after selecting changes is refused without changing the index", t => t.An_edit_after_selecting_changes_is_refused_without_changing_the_index());
        Flow.StepApart<PartialCommitTests>("a hook rejecting a partial commit keeps the index and working file", t => t.A_hook_rejecting_a_partial_commit_keeps_the_index_and_working_file());
        Flow.StepApart<PartialCommitTests>("a hook rejecting a commit of whole files keeps what the index staged of them", t => t.A_hook_rejecting_a_commit_of_whole_files_keeps_what_the_index_staged_of_them());
        Flow.StepApart<PartialCommitTests>("partial commits keep CRLF and the files encoding (false)", t => t.Partial_commits_keep_CRLF_and_the_files_encoding(false));
        Flow.StepApart<PartialCommitTests>("partial commits keep CRLF and the files encoding (true)", t => t.Partial_commits_keep_CRLF_and_the_files_encoding(true));
        Flow.StepApart<PartialCommitTests>("rollback of the chosen files is refused while a file goes in only in part", t => t.Rollback_of_the_chosen_files_is_refused_while_a_file_goes_in_only_in_part());
        Flow.StepApart<PartialCommitTests>("a merge under way offers no boxes for changes", t => t.A_merge_under_way_offers_no_boxes_for_changes());
        Flow.StepApart<PartialCommitTests>("a partial commit reads no file unchanged since the last commit", t => t.A_partial_commit_reads_no_file_unchanged_since_the_last_commit());
    }

    private void A_change_block_can_be_excluded_and_only_the_other_is_committed()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changed files");
            CommitRow row = window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Other.txt").IsChosen = false;
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            DiffView view = OpenDiffs.ViewOf(host);
            CheckBox[] boxes = Boxes(view);
            Assert.Equal(2, boxes.Length);
            Ui.Click(boxes[1]);
            Assert.Null(row.CheckState);
            Assert.Contains(window.CommitView.GetVisualDescendants().OfType<CheckBox>(), b => ReferenceEquals(b.DataContext, row) && b.IsChecked is null);
            Assert.Null(window.CommitView.Rows.OfType<CommitGroup>().Single(g => g.Name == "Changes").IsChosen);
            host.Close();
            window.CommitView.Message = "Only the first change";
            Assert.True(Ui.Landed(window.CommitView.CommitAsync(false), "the partial commit"), window.Status);
            Assert.Equal("ONE\ntwo\nthree\nfour\nfive\n", TestGit.Run(folder, "show", "HEAD:Notes.txt"));
            Assert.Equal(After, File.ReadAllText(PathOf("Notes.txt")));
            Assert.Contains("Other.txt", TestGit.Run(folder, "diff", "--name-only"), StringComparison.Ordinal);
            Assert.Contains("Notes.txt", TestGit.Run(folder, "diff", "--name-only"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A change staged and then taken back out of the working tree, beside the line a partial commit of chosen
    /// changes takes (an agent's vcs_commit's lines), stays staged once the commit is made, with the change committed taken
    /// in, as another staged file does; the change left out stays in the working tree alone.</summary>
    private void A_change_only_staged_stays_staged_through_a_commit_of_another_change_of_its_file()
    {
        const string Staged = "one\nTWO\nthree\nfour\nfive\n";
        File.WriteAllText(PathOf("Notes.txt"), Staged);
        TestGit.Run(folder, "add", "Notes.txt", "Other.txt");
        File.WriteAllText(PathOf("Notes.txt"), After);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.Landed(() => window.Vcs.Changes.Count == 2, "the changes");
            FileDiff diff = FileDiff.Compare(Before, TextSnapshot.From(After));
            CommitResult made = Ui.Landed(window.Vcs.CommitAsync([window.Vcs.Changes.Single(c => c.Name == "Notes.txt")], "The first change", false, false,
                partial: new Dictionary<string, PartialCommit> { [PathOf("Notes.txt")] = new(diff, [diff.Hunks[0]]) }), "the partial commit");
            Assert.True(made.Succeeded, made.Commit.Message);
            Assert.Equal("ONE\ntwo\nthree\nfour\nfive\n", TestGit.Run(folder, "show", "HEAD:Notes.txt"));
            Assert.True(TestGit.Run(folder, "show", ":Notes.txt") == "ONE\nTWO\nthree\nfour\nfive\n",
                $"The change only staged did not stay staged beside the one committed: the index holds\n{TestGit.Run(folder, "show", ":Notes.txt")}");
            Assert.Equal(After, File.ReadAllText(PathOf("Notes.txt")));
            Assert.Equal("Other.txt\n", TestGit.Run(folder, "diff", "--cached", "--name-only", "HEAD", "--", "Other.txt"));
        }
        finally
        {
            window.Close();
        }
    }

    private static CheckBox[] Boxes(DiffView view) =>
        [.. view.GetVisualDescendants().OfType<CheckBox>().Where(b => b.IsVisible && ToolTip.GetTip(b) is string tip && tip.StartsWith("Include change ", StringComparison.Ordinal))];

    /// <summary>A clean filter (Git LFS's, say) runs as git reads a file of the working tree again: a partial commit reads
    /// no file that is as the last commit has it, however many the tree holds.</summary>
    private void A_partial_commit_reads_no_file_unchanged_since_the_last_commit()
    {
        string cleaned = Path.Combine(TestFolders.Create("cleaned-"), "cleaned.txt");
        File.WriteAllText(PathOf("Same.txt"), "same\n");
        TestGit.Run(folder, "add", "Same.txt");
        TestGit.Run(folder, "commit", "-q", "-m", "Same", "--", "Same.txt");
        // Written before the index is, git trusts what it read of the file.
        File.SetLastWriteTimeUtc(PathOf("Same.txt"), DateTime.UtcNow.AddHours(-1));
        TestGit.Run(folder, "status", "--porcelain");
        File.WriteAllText(PathOf(".git/info/attributes"), "*.txt filter=counted\n");
        TestGit.Run(folder, "config", "filter.counted.clean", $"echo %f >>'{cleaned.Replace('\\', '/')}'; cat");
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changed files");
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            Ui.Click(Boxes(OpenDiffs.ViewOf(host))[1]);
            host.Close();
            window.CommitView.Message = "The first change";
            Assert.True(Ui.Landed(window.CommitView.CommitAsync(false), "the partial commit"), window.Status);
            Assert.Equal("ONE\ntwo\nthree\nfour\nfive\n", TestGit.Run(folder, "show", "HEAD:Notes.txt"));
            string read = File.ReadAllText(cleaned);
            Assert.Contains("Notes.txt", read, StringComparison.Ordinal);
            Assert.False(read.Contains("Same.txt", StringComparison.Ordinal), $"The partial commit read Same.txt again, as every file of the tree:\n{read}");
        }
        finally
        {
            window.Close();
        }
    }

    private void Edits_in_the_diff_keep_the_changes_chosen_and_leave_a_new_change_out()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changed files");
            CommitRow row = window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Other.txt").IsChosen = false;
            window.OpenFile(PathOf("Notes.txt"));
            Ui.Settle();
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            DiffView view = OpenDiffs.ViewOf(host);
            Assert.True(view.IsEditable, "The diff of the open file is read-only");
            Ui.Click(Boxes(view)[1]);
            Assert.Null(row.CheckState);

            foreach ((int line, string typed) in (ReadOnlySpan<(int, string)>)[(4, "X"), (0, "Y"), (2, "Z")])
            {
                Ui.Click(view.Right);
                view.Right.GoTo(new TextPosition(line, 0));
                OpenDiffs.KeysOf(host).KeyTextInput(typed);
                Ui.Settle();
            }
            Assert.Equal("YONE\ntwo\nZthree\nfour\nXFIVE\n", view.Right.Document.Snapshot.ToString());
            Assert.Equal([true, false, false], Boxes(view).Select(b => b.IsChecked == true));
            Assert.Null(row.CheckState);

            Ui.Run(window, AppActions.Save);
            host.Close();
            window.CommitView.Message = "The first change, edited";
            Assert.True(Ui.Landed(window.CommitView.CommitAsync(false), "the partial commit"), window.Status);
            Assert.Equal("YONE\ntwo\nthree\nfour\nfive\n", TestGit.Run(folder, "show", "HEAD:Notes.txt"));
            Assert.Equal("YONE\ntwo\nZthree\nfour\nXFIVE\n", File.ReadAllText(PathOf("Notes.txt")));
        }
        finally
        {
            window.Close();
        }
    }

    private void Rollback_of_the_chosen_files_is_refused_while_a_file_goes_in_only_in_part()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changed files");
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            Ui.Click(Boxes(OpenDiffs.ViewOf(host))[1]);
            host.Close();
            window.Perform(AppActions.ShowCommit);
            Ui.Settle();
            Button rollback = window.CommitView.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Rollback");
            Ui.Click(rollback);
            Assert.StartsWith("Rollback puts back whole files", window.Status, StringComparison.Ordinal);
            Assert.Equal(After, File.ReadAllText(PathOf("Notes.txt")));
            Assert.Equal("new\n", File.ReadAllText(PathOf("Other.txt")));
        }
        finally
        {
            window.Close();
        }
    }

    private void A_merge_under_way_offers_no_boxes_for_changes()
    {
        TestGit.Run(folder, "reset", "-q", "Other.txt");
        TestGit.Run(folder, "switch", "-q", "-c", "feature");
        File.WriteAllText(PathOf("Third.txt"), "third\n");
        TestGit.Run(folder, "add", "Third.txt");
        TestGit.Run(folder, "commit", "-q", "-m", "Third");
        TestGit.Run(folder, "switch", "-q", "main");
        TestGit.Run(folder, "merge", "-q", "--no-commit", "--no-ff", "feature");
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.Vcs.Merge.CommitsWhole && window.CommitView.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Notes.txt"), "the merge and its changes");
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            Assert.Empty(Boxes(OpenDiffs.ViewOf(host)));
            host.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private void A_file_deleted_after_partial_selection_is_not_automatically_selected_whole()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changes");
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            Ui.Click(Boxes(OpenDiffs.ViewOf(host))[1]);
            host.Close();
            File.Delete(PathOf("Notes.txt"));
            Ui.AwaitPumped(window.Vcs.RefreshAsync(), "the deletion read");
            CommitRow row = window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            Assert.Equal(ChangeKind.Deleted, row.Change.Kind);
            Assert.False(row.IsChosen);

            // Its diff shows no line where the file was, saying it was deleted, every line of the last commit's taken, which has the keys.
            IDiffHost deleted = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the deleted file's diff")!;
            OpenDiffs.ShowsNoFile(OpenDiffs.ViewOf(deleted), MergeSide.Right);
            Ui.Landed(() => OpenDiffs.ViewOf(deleted).Left.IsKeyboardFocusWithin, "the deleted file's diff to give the last commit's side the keys");
            deleted.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private void An_edit_after_selecting_changes_is_refused_without_changing_the_index()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.Landed(() => window.Vcs.Changes.Count == 2, "the changes");
            window.OpenFile(PathOf("Notes.txt"));
            Ui.Settle();
            FileDiff diff = FileDiff.Compare(Before, window.EditorArea.CodeEditor.Document.Snapshot);
            File.WriteAllText(PathOf("Notes.txt"), After + "later\n");
            string index = TestGit.Run(folder, "write-tree");
            CommitResult result = Ui.Landed(window.Vcs.CommitAsync([window.Vcs.Changes.Single(c => c.Name == "Notes.txt")], "Stale", false, false,
                partial: new Dictionary<string, PartialCommit> { [PathOf("Notes.txt")] = new(diff, [diff.Hunks[0]]) }), "the refused commit");
            Assert.False(result.Succeeded);
            Assert.Contains("review its diff again", result.Commit.Message, StringComparison.Ordinal);
            Assert.Equal(index, TestGit.Run(folder, "write-tree"));
        }
        finally
        {
            window.Close();
        }
    }

    private void A_hook_rejecting_a_partial_commit_keeps_the_index_and_working_file()
    {
        TestGit.Run(folder, "add", "Notes.txt");
        string hook = PathOf(".git/hooks/pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(PathOf("Notes.txt"));
            Ui.Settle();
            Ui.Landed(() => window.Vcs.Changes.Count == 2, "the changes");
            FileDiff diff = FileDiff.Compare(Before, window.EditorArea.CodeEditor.Document.Snapshot);
            string index = TestGit.Run(folder, "write-tree");
            string head = TestGit.Run(folder, "rev-parse", "HEAD");
            CommitResult result = Ui.Landed(window.Vcs.CommitAsync([window.Vcs.Changes.Single(c => c.Name == "Notes.txt")], "Rejected", false, false,
                partial: new Dictionary<string, PartialCommit> { [PathOf("Notes.txt")] = new(diff, [diff.Hunks[0]]) }), "the rejected commit");
            Assert.False(result.Succeeded);
            Assert.Equal(index, TestGit.Run(folder, "write-tree"));
            Assert.Equal(head, TestGit.Run(folder, "rev-parse", "HEAD"));
            Assert.Equal(After, File.ReadAllText(PathOf("Notes.txt")));
            Assert.Empty(Directory.GetFiles(PathOf(".git"), "passenger-commit-*"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A commit of whole files that a hook refuses leaves the index as it was: a change of a chosen file only
    /// staged, taken back out of the working tree since, is still staged, and the file's other changes are not.</summary>
    private void A_hook_rejecting_a_commit_of_whole_files_keeps_what_the_index_staged_of_them()
    {
        const string Staged = "one\nTWO\nthree\nfour\nfive\n";
        File.WriteAllText(PathOf("Notes.txt"), Staged);
        TestGit.Run(folder, "add", "Notes.txt");
        File.WriteAllText(PathOf("Notes.txt"), After);
        File.WriteAllText(PathOf("New.txt"), "new\n");
        string hook = PathOf(".git/hooks/pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.Landed(() => window.Vcs.Changes.Count == 3, "the changes");
            string head = TestGit.Run(folder, "rev-parse", "HEAD");
            CommitResult result = Ui.Landed(window.Vcs.CommitAsync([.. window.Vcs.Changes.Where(c => c.Name is "Notes.txt" or "New.txt")], "Rejected", false, false),
                "the rejected commit");
            Assert.False(result.Succeeded);
            Assert.Equal(head, TestGit.Run(folder, "rev-parse", "HEAD"));
            Assert.True(TestGit.Run(folder, "show", ":Notes.txt") == Staged,
                $"The refused commit did not leave the index as it was: it holds\n{TestGit.Run(folder, "show", ":Notes.txt")}");
            Assert.Equal("New.txt\n", TestGit.Run(folder, "ls-files", "--others", "--exclude-standard"));
            Assert.Equal(After, File.ReadAllText(PathOf("Notes.txt")));
        }
        finally
        {
            window.Close();
        }
    }

    private void Partial_commits_keep_CRLF_and_the_files_encoding(bool unicode)
    {
        System.Text.Encoding encoding = unicode ? System.Text.Encoding.Unicode : new System.Text.UTF8Encoding(false);
        const string Name = "[notes] caf\u00e9 \u5496\u5561.txt";
        string path = PathOf(Name);
        string before = Before.Replace("\n", "\r\n", StringComparison.Ordinal);
        string after = After.Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(path, before, encoding);
        TestGit.Run(folder, "add", "--", Name);
        TestGit.Run(folder, "commit", "-q", "-m", "Encoded notes");
        File.WriteAllText(path, after, encoding);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(path);
            Ui.Settle();
            Ui.Landed(() => window.Vcs.Changes.Any(c => c.Name == Name), "the encoded file");
            FileDiff diff = FileDiff.Compare(before, window.EditorArea.CodeEditor.Document.Snapshot);
            CommitResult result = Ui.Landed(window.Vcs.CommitAsync([window.Vcs.Changes.Single(c => c.Name == Name)], "One encoded change", false, false,
                partial: new Dictionary<string, PartialCommit> { [path] = new(diff, [diff.Hunks[0]]) }), "the encoded partial commit");
            Assert.True(result.Succeeded, result.Commit.Message);
            byte[] bytes = Ui.Landed(window.Vcs.Repository!.BytesAtAsync("HEAD", path), "the committed bytes")!;
            FileText text = FileText.FromBytes(bytes)!;
            Assert.Equal("ONE\r\ntwo\r\nthree\r\nfour\r\nfive\r\n", text.Text);
            Assert.Equal(encoding.CodePage, text.Encoding.CodePage);
            Assert.Equal(after, File.ReadAllText(path, encoding));
        }
        finally
        {
            window.Close();
        }
    }
}

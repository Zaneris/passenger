using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

public sealed class PartialCommitTests : IDisposable
{
    private const string Before = "one\ntwo\nthree\nfour\nfive\n";
    private const string After = "ONE\ntwo\nthree\nfour\nFIVE\n";
    private readonly string folder = TestFolders.Create("partial-commit-");
    private string PathOf(string name) => Path.Combine(folder, name);

    public PartialCommitTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(PathOf("Notes.txt"), Before);
        File.WriteAllText(PathOf("Other.txt"), "old\n");
        TestGit.Init(folder);
        File.WriteAllText(PathOf("Notes.txt"), After);
        File.WriteAllText(PathOf("Other.txt"), "new\n");
        TestGit.Run(folder, "add", "Other.txt");
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_change_block_can_be_excluded_and_only_the_other_is_committed()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changed files");
            CommitRow row = window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Other.txt").IsChosen = false;
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            DiffView view = host.View!;
            CheckBox[] boxes = [.. view.GetVisualDescendants().OfType<CheckBox>().Where(b => ToolTip.GetTip(b) as string == "Include this change in commit")];
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
            Assert.Contains("Other.txt", TestGit.Run(folder, "diff", "--cached", "--name-only"), StringComparison.Ordinal);
            Assert.Contains("Notes.txt", TestGit.Run(folder, "diff", "--name-only"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_file_deleted_after_partial_selection_is_not_automatically_selected_whole()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.Perform(AppActions.ShowCommit);
            Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == 2, "the changes");
            IDiffHost host = Ui.Landed(window.ShowDiffAsync(PathOf("Notes.txt")), "the diff")!;
            CheckBox[] boxes = [.. host.View!.GetVisualDescendants().OfType<CheckBox>().Where(b => ToolTip.GetTip(b) as string == "Include this change in commit")];
            Ui.Click(boxes[1]);
            host.Close();
            File.Delete(PathOf("Notes.txt"));
            Ui.AwaitPumped(window.Vcs.RefreshAsync(), "the deletion read");
            CommitRow row = window.CommitView.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            Assert.Equal(ChangeKind.Deleted, row.Change.Kind);
            Assert.False(row.IsChosen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_edit_after_selecting_changes_is_refused_without_changing_the_index()
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

    [AvaloniaFact]
    public void A_hook_rejecting_a_partial_commit_keeps_the_index_and_working_file()
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_commits_keep_CRLF_and_the_files_encoding(bool unicode)
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Show History over real working trees, as tabs of the Git window: a file's commits followed through a
/// rename, each opening what it did to the file; a folder's, only those that touched it; and those that changed the
/// lines chosen, counted as the last commit has them under unsaved edits. Compare with Branch or Tag puts another
/// branch's text beside the file's.</summary>
public sealed class HistoryTests : IDisposable
{
    private const string Shop = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("history-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_files_history_follows_it_through_a_rename_and_a_double_click_on_a_commit_shows_what_it_did_to_the_file()
    {
        Write("src/Shop.cs", Shop);
        Write("Notes.txt", "notes\n");
        TestGit.Init(folder);
        Write("src/Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long count");
        Git("mv", "src/Shop.cs", "src/Store.cs");
        Git("commit", "-q", "-m", "Store, not shop");
        Write("src/Store.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal).Replace("count++", "count += 2", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Twice");
        Write("Notes.txt", "more notes\n");
        Git("commit", "-q", "-am", "Notes");
        MainWindow window = Open("src/Store.cs");

        Ui.ChooseFromEditorMenu(window, "Git", "Show History");
        GitLogView history = window.GitWindow.Shown;
        Ui.Landed(() => history.Rows.Count == 4, "the four commits that changed the file, under either name");
        Assert.Equal(["Twice", "Store, not shop", "Long count", "First"], history.Rows.Select(r => r.Commit.Subject));
        Assert.Equal(["Log", "Store.cs"], window.GitWindow.Titles);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Git), "Show History left the Git window hidden");

        // Its first two commits are of the file under the name it had then, and the rename is one.
        Assert.Equal((Full("src/Shop.cs"), ChangeKind.Added), Change(history, "First"));
        Assert.Equal((Full("src/Shop.cs"), ChangeKind.Modified), Change(history, "Long count"));
        history.Select(Commit(history, "Store, not shop"));
        Ui.Landed(() => history.Files.Count == 1, "the rename's one file");
        Assert.Equal((Full("src/Store.cs"), ChangeKind.Renamed, Full("src/Shop.cs")), (history.Files[0].Path, history.Files[0].Kind, history.Files[0].From));

        DoubleClick(history, "Long count");
        Ui.Landed(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "the commit's diff of the file");
        DiffWindow diff = window.OwnedWindows.OfType<DiffWindow>().Single();
        Assert.Equal(Shop, diff.View.Left.Document.Snapshot.ToString());
        Assert.Equal(Shop.Replace("int count", "long count", StringComparison.Ordinal), diff.View.Right.Document.Snapshot.ToString());
        Assert.Single(diff.View.Hunks);
        diff.Close();

        window.GitWindow.ShowLog();
        Assert.True(window.Perform(AppActions.ShowHistory), window.Status);
        Assert.Same(history, window.GitWindow.Shown);
        Assert.Equal(["Log", "Store.cs"], window.GitWindow.Titles);
        window.Close();
    }

    [AvaloniaFact]
    public void A_folders_history_from_the_explorer_lists_only_the_commits_that_touched_it_and_only_its_files()
    {
        Write("src/A.cs", "class A { }\n");
        Write("docs/Read.md", "read\n");
        TestGit.Init(folder);
        Write("docs/Read.md", "read more\n");
        Git("commit", "-q", "-am", "Docs only");
        Write("src/B.cs", "class B { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "B");
        Write("src/A.cs", "class A { int a; }\n");
        Write("docs/Read.md", "read again\n");
        Git("commit", "-q", "-am", "Both");
        Write("docs/Read.md", "read once more\n");
        Git("commit", "-q", "-am", "Docs again");
        MainWindow window = Open(null);

        ExplorerNode src = window.ExplorerView.Tree.Rows.First(r => r.Name == "src");
        Ui.ChooseFromRowMenu(window, src, "Git", "Show History");
        GitLogView history = window.GitWindow.Shown;
        Ui.Landed(() => history.Rows.Count == 3, "the three commits that touched src");
        Assert.Equal(["Both", "B", "First"], history.Rows.Select(r => r.Commit.Subject));
        Assert.Equal(["Log", "src"], window.GitWindow.Titles);

        history.Select(Commit(history, "Both"));
        Ui.Landed(() => history.Files.Count == 1, "the one file Both changed in src");
        Assert.Equal((Full("src/A.cs"), ChangeKind.Modified), (history.Files[0].Path, history.Files[0].Kind));
        history.Select(Commit(history, "First"));
        Ui.Landed(() => history.Files.Count == 1, "the one file First added to src");
        Assert.Equal(Full("src/A.cs"), history.Files[0].Path);

        ExplorerNode docs = window.ExplorerView.Tree.Rows.First(r => r.Name == "docs");
        window.ExplorerView.Tree.Expand(docs);
        Ui.Settle();
        Ui.RightClick(Ui.Row(window.ExplorerView, window.ExplorerView.Tree.Rows.First(r => r.Name == "Read.md")));
        MenuItem git = window.ExplorerView.OpenMenu!.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Git");
        Assert.Equal(["Compare with Revision…", "Compare with Branch or Tag…", "Show History"], git.Items.OfType<MenuItem>().Select(i => i.Header as string));
        window.ExplorerView.OpenMenu.Close();
        window.GitWindow.ShowLog();
        Ui.Landed(() => window.GitLog.Rows.Count == 5, "the Log's five commits");
        window.Close();
    }

    [AvaloniaFact]
    public void Show_history_for_selection_lists_only_the_commits_that_changed_the_chosen_lines_counted_past_an_unsaved_edit()
    {
        string[] lines = [.. Enumerable.Range(1, 10).Select(i => $"line {i}")];
        Write("Lines.txt", Joined(lines));
        TestGit.Init(folder);
        // Lines 3 and 7, either side of the three chosen, change too: the editor's numbers off by one would take one in.
        foreach (int changed in new[] { 2, 3, 5, 7, 9, 6 })
        {
            lines[changed - 1] = $"line {changed}, changed";
            Write("Lines.txt", Joined(lines));
            Git("commit", "-q", "-am", $"Line {changed}");
        }
        MainWindow window = Open("Lines.txt");
        CodeEditor editor = window.EditorArea.CodeEditor;

        // A line typed above, not saved: the editor's lines 5 to 7 are the last commit's 4 to 6.
        editor.ApplyEdits([new TextEdit(0, 0, "a new first line\n")]);
        editor.GoTo(new TextPosition(4, 0));
        for (int i = 0; i < 3; i++)
        {
            Ui.Press(window, "shift+DOWN");
        }
        Ui.ChooseFromEditorMenu(window, "Git", "Show History for Selection…");
        GitLogView history = window.GitWindow.Shown;
        Ui.Landed(() => history.Rows.Count > 0, "the commits that changed the lines");
        Assert.Equal(["Line 6", "Line 5", "First"], history.Rows.Select(r => r.Commit.Subject));
        Assert.Equal(["Log", "Lines.txt, lines 5–7"], window.GitWindow.Titles);

        history.Select(Commit(history, "Line 5"));
        history.FocusRows();
        Ui.Press(window, "ENTER");
        Ui.Landed(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "Line 5's diff of the file");
        DiffWindow diff = window.OwnedWindows.OfType<DiffWindow>().Single();
        Assert.Contains("line 5\n", diff.View.Left.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("line 5, changed\n", diff.View.Right.Document.Snapshot.ToString(), StringComparison.Ordinal);
        diff.Close();

        editor.GoTo(new TextPosition(0, 2));
        Assert.True(window.Perform(AppActions.ShowHistoryForSelection));
        Ui.Settle();
        Assert.Equal("The lines chosen are not committed yet", window.Status);
        Assert.Equal(2, window.GitWindow.Titles.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Compare_with_branch_or_tag_puts_the_other_branchs_text_beside_the_files_as_it_is_now()
    {
        Write("Shop.cs", Shop);
        TestGit.Init(folder);
        Git("tag", "v1");
        Git("switch", "-q", "-c", "side");
        string side = Shop.Replace("count++", "count += 3", StringComparison.Ordinal);
        Write("Shop.cs", side);
        Git("commit", "-q", "-am", "Thrice");
        Git("switch", "-q", "main");
        Write("New.cs", "class New { }\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "New");
        MainWindow window = Open("Shop.cs");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");

        Assert.True(window.Perform(AppActions.CompareWithBranch), window.Status);
        Ui.Settle();
        ChoicePopup popup = Assert.IsType<ChoicePopup>(window.Popup);
        Assert.Equal(["main current branch", "side local branch", "v1 tag"], popup.Rows.Select(r => $"{r.Label} {r.Detail}"));
        window.KeyTextInput("side");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.Landed(() => window.OwnedWindows.OfType<DiffWindow>().Any(), "the diff against side");
        DiffWindow diff = window.OwnedWindows.OfType<DiffWindow>().Single();
        Assert.Equal("Shop.cs: side and your version", diff.Title);
        Assert.Equal(side, diff.View.Left.Document.Snapshot.ToString());
        Assert.True(ReferenceEquals(window.EditorArea.Active!.Text, diff.View.Right.Document), "The diff's right side is a copy of the open file, not the file");
        Assert.Equal(2, diff.View.Hunks.Count);
        diff.Close();

        window.OpenFile(Path.Combine(folder, "New.cs"));
        Ui.Settle();
        Assert.True(window.Perform(AppActions.CompareWithBranch), window.Status);
        Ui.Settle();
        window.KeyTextInput("side");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.Landed(() => window.Status == "New.cs is not in side", "the status bar saying side has no New.cs");
        Assert.Empty(window.OwnedWindows.OfType<DiffWindow>());
        window.Close();
    }

    private MainWindow Open(string? file)
    {
        MainWindow window = Ui.OpenFolder(folder);
        if (file is not null)
        {
            window.OpenFile(Path.Combine(folder, file));
            window.EditorArea.CodeEditor.Focus();
        }
        Ui.Settle();
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the folder's repository");
        return window;
    }

    private static CommitInfo Commit(GitLogView history, string subject) => history.Rows.Select(r => r.Commit).First(c => c.Subject == subject);

    /// <summary>A file's history's change in a commit: the file's path then, and how it changed.</summary>
    private static (string Path, ChangeKind Kind) Change(GitLogView history, string subject)
    {
        history.Select(Commit(history, subject));
        Ui.Settle();
        FileChange change = Assert.Single(history.Files);
        return (change.Path, change.Kind);
    }

    /// <summary>Double-clicks a commit of a history, as a person opens one.</summary>
    private static void DoubleClick(GitLogView history, string subject)
    {
        ListBox commits = history.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, history.Rows));
        Ui.DoubleClick(Ui.Row(commits, history.Rows.First(r => r.Commit.Subject == subject)));
    }

    private static string Joined(IEnumerable<string> lines) => string.Concat(lines.Select(l => l + "\n"));

    private string Full(string relative) => PathNames.Canonical(Path.Combine(folder, relative));

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}

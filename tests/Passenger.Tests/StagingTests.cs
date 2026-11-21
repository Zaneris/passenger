using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Staging into git's index, as other git tools see it: a diff's changes one by one, by Stage's keys at the caret and
/// each change's own button, its keys and button unstaging what the index holds whole; the Commit window's files by its
/// keys, boxes and menu, listed under Staged and Unstaged while the index holds staged changes, a commit then taking what
/// is staged alone; each stage one command of Undo in the Commit window.</summary>
public sealed class StagingTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";
    private const string Changed = "class Shop\n{\n    long count;\n\n    public void Add() { count++; }\n}\n";
    private const string FirstOnly = "class Shop\n{\n    long count;\n\n    void Add() { count++; }\n}\n";
    private const string SecondOnly = "class Shop\n{\n    int count;\n\n    public void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("staging-");

    public StagingTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
        File.WriteAllText(Path.Combine(folder, "Other.txt"), "old\n");
        TestGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Changed);
        File.WriteAllText(Path.Combine(folder, "Other.txt"), "new\n");
        File.WriteAllText(Path.Combine(folder, "New.txt"), "made\n");
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>In the Commit window's diff of a file not open, Stage's keys at a change put it into git's index alone, and
    /// the window lists the file under Staged and Unstaged, the diff's boxes for ticked changes gone; the next change's plus
    /// button stages it too, its tip naming the keys; the keys on a change the index holds whole take it out again; and
    /// Commit takes what the index holds alone, leaving the working tree as it was.</summary>
    [AvaloniaFact]
    public void Changes_staged_from_the_diff_by_key_and_button_are_what_git_holds_and_what_the_commit_takes()
    {
        MainWindow window = Open();
        try
        {
            DiffView view = DiffOf(window, "Shop.cs");
            Ui.WaitFor(() => view.HeldAt(0) == false && view.HeldAt(1) == false, "the diff to know git's index holds none of Shop.cs's changes");
            string? keys = ActionTips.Keys(AppActions.Stage);
            Assert.False(keys is null, "Stage has no keys in the keymap in force");
            Assert.Equal($"Stage change 2 ({keys})", ToolTip.GetTip(StageButton(view, "Stage change 2")));

            view.Right.Focus();
            view.Right.GoTo(new TextPosition(2, 5));
            Ui.Landed(() => view.Right.IsKeyboardFocusWithin, "the diff's side of the file to have the keys");
            Ui.Press(window, Ui.Shortcut(AppActions.Stage));
            Ui.WaitFor(() => view.HeldAt(0) == true && view.HeldAt(1) == false, "the first change held by git's index once Stage's keys staged it");
            Assert.Equal(FirstOnly, TestGit.Run(folder, "show", ":Shop.cs"));
            Ui.WaitFor(() => Groups(window) == "Staged, Unstaged, Unversioned Files", () => $"the Commit window to list Shop.cs under Staged and Unstaged, where it lists {Groups(window)}");
            Assert.Equal("Unstage change 1", Avalonia.Automation.AutomationProperties.GetName(StageButton(view, "Unstage change 1")));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<CheckBox>(), b => b.IsEffectivelyVisible);

            Ui.Click(StageButton(view, "Stage change 2"));
            Ui.WaitFor(() => view.HeldAt(1) == true, "the second change held by git's index once its own button staged it");
            Assert.Equal(Changed, TestGit.Run(folder, "show", ":Shop.cs"));

            view.Right.GoTo(new TextPosition(2, 5));
            Ui.Press(window, Ui.Shortcut(AppActions.Stage));
            Ui.WaitFor(() => view.HeldAt(0) == false, "the first change taken out of git's index by Stage's keys, as the index held it whole");
            Assert.Equal(SecondOnly, TestGit.Run(folder, "show", ":Shop.cs"));

            window.CommitView.Message = "The second change alone";
            Assert.True(Ui.Landed(window.CommitView.CommitAsync(false), "the commit of what is staged"), window.Status);
            Assert.Equal(SecondOnly, TestGit.Run(folder, "show", "HEAD:Shop.cs"));
            Assert.Equal(Changed, File.ReadAllText(Path.Combine(folder, "Shop.cs")));
            Assert.Equal(string.Empty, TestGit.Run(folder, "diff", "--cached", "--name-only").Trim());
            Ui.WaitFor(() => Groups(window) == "Changes, Unversioned Files", () => $"the Commit window to list its changes together once nothing is staged, where it lists {Groups(window)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file staged by Stage's keys in the Commit window puts the window in Staged and Unstaged, a box under Unstaged
    /// stages its file, the Commit window's Undo takes the last stage back, and Commit takes the staged file alone.</summary>
    [AvaloniaFact]
    public void Files_staged_from_the_commit_window_are_listed_apart_undone_there_and_committed_alone()
    {
        MainWindow window = Open();
        try
        {
            List<string> asked = [];
            window.Confirm = (_, text, _) =>
            {
                asked.Add(text);
                return Task.FromResult(true);
            };
            CommitView commit = window.CommitView;
            Assert.Equal("Changes, Unversioned Files", Groups(window));
            Ui.Click(RowOf(window, "Other.txt"));
            Ui.Press(window, Ui.Shortcut(AppActions.Stage));
            Ui.WaitFor(() => Groups(window) == "Staged, Unstaged, Unversioned Files", () => $"Other.txt staged by Stage's keys, where the window lists {Groups(window)}");
            Assert.Equal("Other.txt", TestGit.Run(folder, "diff", "--cached", "--name-only").Trim());
            Assert.True(Under(commit, CommitView.StagedGroup) == "Other.txt" && Under(commit, CommitView.UnstagedGroup) == "Shop.cs",
                $"Staged lists [{Under(commit, CommitView.StagedGroup)}] and Unstaged [{Under(commit, CommitView.UnstagedGroup)}]");

            CheckBox box = RowOf(window, "New.txt").GetVisualDescendants().OfType<CheckBox>().First(b => b.IsVisible);
            Assert.False(box.IsChecked, "An unversioned file's box is ticked while it is not staged");
            Ui.Click(box);
            Ui.WaitFor(() => Under(commit, CommitView.StagedGroup) == "New.txt, Other.txt", () => $"Staged to list New.txt, staged by its box, and Other.txt, where it lists [{Under(commit, CommitView.StagedGroup)}]");
            Assert.Contains("New.txt", Staged(), StringComparison.Ordinal);

            Ui.Click(RowOf(window, "Other.txt"));
            Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
            Ui.WaitFor(() => Under(commit, CommitView.StagedGroup) == "Other.txt", () => $"the Commit window's Undo to take New.txt's stage back, where Staged lists [{Under(commit, CommitView.StagedGroup)}]");
            Assert.DoesNotContain("New.txt", Staged(), StringComparison.Ordinal);
            Assert.True(asked is [{ } question] && question.Contains("Stage New.txt", StringComparison.Ordinal), $"Undo asked [{string.Join(" | ", asked)}]");

            commit.Message = "Other alone";
            Assert.True(Ui.Landed(commit.CommitAsync(false), "the commit of what is staged"), window.Status);
            Assert.Equal("Other.txt", TestGit.Run(folder, "show", "--name-only", "--format=", "HEAD").Trim());
            Assert.Equal(Changed, File.ReadAllText(Path.Combine(folder, "Shop.cs")));
            Assert.Contains("?? New.txt", TestGit.Run(folder, "status", "--porcelain"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Lines selected in a change of several stage those lines alone, the change's other lines left out of git's
    /// index, one command of Undo named for the lines; Unstage over one of them takes that line alone back out.</summary>
    [AvaloniaFact]
    public void Lines_selected_in_a_change_stage_and_unstage_alone()
    {
        string lines = Path.Combine(folder, "Lines.txt");
        File.WriteAllText(lines, "one\ntwo\nthree\nfour\n");
        TestGit.Run(folder, "add", "Lines.txt");
        TestGit.Run(folder, "commit", "-q", "-m", "Lines", "--", "Lines.txt");
        File.WriteAllText(lines, "ONE\nTWO\nTHREE\nfour\n");
        MainWindow window = Open(files: 4);
        try
        {
            DiffView view = DiffOf(window, "Lines.txt");
            Ui.WaitFor(() => view.HeldAt(0) == false, "the diff to know git's index holds none of Lines.txt's change");
            Assert.Single(view.Hunks);
            TextSnapshot text = view.Right.Document.Snapshot;
            view.Right.Focus();
            view.Right.Select(TextRange.FromBounds(text.GetLineStart(1), text.GetLineStart(3)));
            Ui.Landed(() => view.Right.IsKeyboardFocusWithin, "the diff's side of the file to have the keys");
            Ui.Press(window, Ui.Shortcut(AppActions.Stage));
            Ui.WaitFor(() => Under(window.CommitView, CommitView.StagedGroup).Contains("Lines.txt", StringComparison.Ordinal),
                () => $"Lines.txt listed as staged once two of its lines were, where Staged lists [{Under(window.CommitView, CommitView.StagedGroup)}]");
            Assert.Equal("one\nTWO\nTHREE\nfour\n", TestGit.Run(folder, "show", ":Lines.txt"));
            Assert.Equal("Stage 2 lines of Lines.txt", window.EditorArea.Commands.ToUndo(null)?.Name);
            Assert.False(view.HeldAt(0) == true, "The change is held whole by the index once two of its three lines were staged");

            view.Right.Select(TextRange.FromBounds(text.GetLineStart(1), text.GetLineStart(1) + 2));
            Assert.True(window.Perform(AppActions.Unstage), window.Status);
            Ui.WaitFor(() => window.EditorArea.Commands.ToUndo(null)?.Name == "Unstage 1 line of Lines.txt", () => $"TWO to be unstaged, where the window says {window.Status}");
            Assert.Equal("one\ntwo\nTHREE\nfour\n", TestGit.Run(folder, "show", ":Lines.txt"));
            Assert.Equal("ONE\nTWO\nTHREE\nfour\n", File.ReadAllText(lines));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The files git's index holds changes of, as git names them.</summary>
    private string Staged() => TestGit.Run(folder, "diff", "--cached", "--name-only");

    /// <summary>The folder open with no file shown, diffs set to open as tabs, the Commit window listing its changes.</summary>
    private MainWindow Open(int files = 3)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { OpenDiffsIn = DiffPlace.Tab });
        window.Perform(AppActions.ShowCommit);
        Ui.Landed(() => window.CommitView.Rows.OfType<CommitRow>().Count() == files, $"the {files} changed files listed");
        return window;
    }

    /// <summary>A file's diff, opened by a double-click on its row in the Commit window.</summary>
    private static DiffView DiffOf(MainWindow window, string name)
    {
        Ui.DoubleClick(RowOf(window, name));
        return OpenDiffs.ViewOf(OpenDiffs.Landed(window, $"{name}'s diff, from a double-click on its row in the Commit window"));
    }

    private static Control RowOf(MainWindow window, string name)
    {
        ListBox changes = window.CommitView.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, window.CommitView.Rows));
        return Ui.Row(changes, window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == name));
    }

    private static string Groups(MainWindow window) => string.Join(", ", window.CommitView.Rows.OfType<CommitGroup>().Select(g => g.Name));

    private static string Under(CommitView commit, string group) =>
        string.Join(", ", (commit.Rows.OfType<CommitGroup>().FirstOrDefault(g => g.Name == group)?.Rows ?? []).Select(r => r.Change.Name).Order(StringComparer.Ordinal));

    /// <summary>A change's stage button in the diff, by the name a screen reader says for it.</summary>
    private static Button StageButton(DiffView view, string name) =>
        view.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == name);
}

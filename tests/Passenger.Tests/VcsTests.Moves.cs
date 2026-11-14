using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Moves Passenger makes in a Git working tree are moves to git: files and folders dragged together, a project's
/// folder renamed and twelve hundred files moved at once list as renamed and commit as renames, moved in the index by one
/// git however many moved; Undo and Redo move the index with them; the status finds renames past git's own limit; a
/// moved file edited past what git finds alike commits as a rename through the moves committed first; and a moved
/// file's row names where it came from.</summary>
public sealed partial class VcsTests
{
    /// <summary>Files and a folder chosen together and dragged onto a folder in the Explorer move in the index as git mv
    /// moves them, by one git writing it: each lists as renamed, a staged edit staying staged under the new name and an
    /// edit not staged staying unstaged, a file git does not track staying untracked; Undo moves the index back with
    /// the files, git's status as it was before the drag.</summary>
    [AvaloniaFact]
    public void Files_and_a_folder_dragged_together_in_the_Explorer_move_in_the_index_by_one_git_and_undo_moves_it_back()
    {
        string tree = PathNames.Canonical(folder);
        TestFolders.Write(tree, "Basket.cs", "class Basket\n{\n    int count;\n}\n");
        TestFolders.Write(tree, "Lines/Line.cs", "class Line\n{\n    int count;\n}\n");
        TestFolders.Write(tree, "Lines/Price.cs", "class Price\n{\n    int cents;\n}\n");
        TestFolders.Write(tree, "Shelf/Shelf.cs", "class Shelf\n{\n}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Basket, lines and a shelf");
        File.WriteAllText(Path.Combine(tree, "Basket.cs"), "class Basket\n{\n    int count;\n    int staged;\n}\n");
        Git("add", "Basket.cs");
        File.AppendAllText(Path.Combine(tree, "Lines", "Line.cs"), "// not staged\n");
        TestFolders.Write(tree, "Lines/Draft.cs", "// untracked\n");
        string before = Git("status", "--porcelain");
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);
        List<(string Title, string Text)> asked = Answer(window);
        // Found before any is chosen, as finding a row reveals it, which chooses it alone.
        ExplorerNode basket = ExplorerRow(window, Path.Combine(tree, "Basket.cs"));
        ExplorerNode lines = ExplorerRow(window, Path.Combine(tree, "Lines"));
        ExplorerNode shelf = ExplorerRow(window, Path.Combine(tree, "Shelf"));
        Ui.ClickRow(window, basket);
        // The platform's key for choosing another row, which on the headless platform is Ctrl everywhere.
        Ui.Click(Ui.Row(window.ExplorerView, lines), modifiers: (RawInputModifiers)(Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control));
        Assert.Equal(["Basket.cs", "Lines"], window.ExplorerView.SelectedNodes.Select(n => n.Name).Order(StringComparer.Ordinal));

        IReadOnlyList<string> writes = IndexWrites(tree, () =>
        {
            Ui.Drag(window, Ui.Centre(Ui.Row(window.ExplorerView, basket), window), Ui.Centre(Ui.Row(window.ExplorerView, shelf), window), MouseButton.Left, RawInputModifiers.None);
            Ui.WaitFor(() => window.Vcs.Changes.Count(c => c.Kind == ChangeKind.Renamed) == 3, () => $"the three moves listed as renamed, where Git lists {Listed(window)}");
        });

        Assert.True(File.Exists(Path.Combine(tree, "Shelf", "Lines", "Draft.cs")), $"The drag moved nothing: {window.Status}");
        Assert.True(writes.Count == 1, $"The moves were written to the index by {writes.Count} gits: {string.Join("; ", writes)}");
        Assert.Equal(
            ["R  Basket.cs -> Shelf/Basket.cs", "R  Lines/Line.cs -> Shelf/Lines/Line.cs", "R  Lines/Price.cs -> Shelf/Lines/Price.cs"],
            Git("status", "--porcelain", "--untracked-files=no").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Replace("RM ", "R  ", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.True(Git("show", ":Shelf/Basket.cs").Contains("int staged;", StringComparison.Ordinal), "The staged edit of Basket.cs was not staged under its new name");
        Assert.Equal("Shelf/Lines/Line.cs", Git("diff", "--name-only").Trim());
        Assert.Equal("Shelf/Lines/Draft.cs", Git("ls-files", "--others", "--exclude-standard").Trim());
        Assert.Contains(window.Vcs.Changes, c => c.Kind == ChangeKind.Unversioned && c.Path == Path.Combine(tree, "Shelf", "Lines", "Draft.cs"));

        Ui.ClickRow(window, ExplorerRow(window, Path.Combine(tree, "Shelf", "Basket.cs")));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => window.Vcs.Changes.All(c => c.Kind != ChangeKind.Renamed) && window.Vcs.Changes.Any(c => c.Name == "Basket.cs"),
            () => $"Undo to move the files and the index back, where Git lists {Listed(window)} and the status bar says '{window.Status}'");
        Assert.Single(asked);
        Assert.True(File.Exists(Path.Combine(tree, "Lines", "Draft.cs")), "Undo left the files moved");
        Assert.Equal(before, Git("status", "--porcelain"));
        window.Close();
    }

    /// <summary>A project's folder renamed in the Explorer lists each of its files as renamed; Undo moves the index back,
    /// Redo moves it again, and the Commit window commits the files as renames.</summary>
    [AvaloniaFact]
    public void A_project_folder_renamed_in_the_Explorer_lists_and_commits_its_files_as_renames_undo_and_redo_moving_the_index()
    {
        string tree = PathNames.Canonical(folder);
        TestFolders.Write(tree, "Store/Store.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
        TestFolders.Write(tree, "Store/Order.cs", "class Order\n{\n    int total;\n}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "A store");
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);
        List<(string Title, string Text)> asked = Answer(window);
        Assert.Null(Ui.Await(window.RenameAsync(Path.Combine(tree, "Store"), "Till"), "the folder's rename"));
        string[] renamed = ["R  Store/Order.cs -> Till/Order.cs", "R  Store/Store.csproj -> Till/Store.csproj"];
        Ui.WaitFor(() => window.Vcs.Changes.Count(c => c.Kind == ChangeKind.Renamed) == 2, () => $"the folder's two files listed as renamed, where Git lists {Listed(window)}");
        Assert.Equal(renamed, Staged());

        Ui.ClickRow(window, ExplorerRow(window, Path.Combine(tree, "Till")));
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0, () => $"Undo to move the folder and the index back, where Git lists {Listed(window)} and the status bar says '{window.Status}'");
        Assert.True(Directory.Exists(Path.Combine(tree, "Store")), $"Undo left the folder renamed: {window.Status}");
        Assert.Empty(Git("status", "--porcelain"));
        Ui.ClickRow(window, ExplorerRow(window, Path.Combine(tree, "Store")));
        Ui.Run(window, EditorActions.Redo);
        Ui.WaitFor(() => window.Vcs.Changes.Count(c => c.Kind == ChangeKind.Renamed) == 2, () => $"Redo to move the folder and the index again, where Git lists {Listed(window)}");
        Assert.Equal(renamed, Staged());
        Assert.Equal(2, asked.Count);

        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Landed(() => commit.Chosen.Count(c => c.Kind == ChangeKind.Renamed) == 2, "the two renames chosen in the Commit window");
        commit.Message = "Rename the store";
        Ui.ClickButton(commit, "Commit");
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0 && !window.Vcs.IsBusy, () => $"the commit, where Git lists {Listed(window)} and the status bar says '{window.Status}'");
        Assert.Equal("Rename the store", Git("log", "-1", "--format=%s").Trim());
        Assert.False(commit.OffersMovesFirst, $"The Commit window offered to commit unchanged moves first: {commit.MovesSaid}");
        Assert.Equal(["R100\tStore/Order.cs\tTill/Order.cs", "R100\tStore/Store.csproj\tTill/Store.csproj"], Shown("HEAD"));
        window.Close();
    }

    /// <summary>Twelve hundred files moved into a folder at once, as the Explorer's Paste moves them, are moved in the index
    /// by one git, list every one as renamed in the Commit window and commit as twelve hundred renames.</summary>
    [AvaloniaFact]
    public void Twelve_hundred_files_moved_at_once_move_in_the_index_by_one_git_list_as_renamed_and_commit_as_renames()
    {
        const int Count = 1200;
        string tree = PathNames.Canonical(folder);
        string[] files = [.. Enumerable.Range(0, Count).Select(i => TestFolders.Write(tree, $"Many/F{i:D4}.cs", $"class F{i:D4}\n{{\n    int value = {i};\n}}\n"))];
        TestFolders.Write(tree, "Moved/Keep.cs", "class Keep\n{\n}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "Many files");
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);

        IReadOnlyList<string> writes = IndexWrites(tree, () =>
        {
            Assert.Equal(Count, Ui.Await(window.PutAsync([.. files.Select(PathNames.Canonical)], Path.Combine(tree, "Moved"), copy: false), "the move").Count);
            Ui.WaitFor(() => window.Vcs.Changes.Count(c => c.Kind == ChangeKind.Renamed) == Count,
                () => $"every move listed as renamed, where Git lists {window.Vcs.Changes.Count(c => c.Kind == ChangeKind.Renamed)} renamed of {window.Vcs.Changes.Count}");
        });

        Assert.True(writes.Count == 1, $"The {Count} moves were written to the index by {writes.Count} gits");
        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Count(r => r.Change.Kind == ChangeKind.Renamed && r.IsChosen) == Count, "every rename chosen in the Commit window");
        commit.Message = "Move the files";
        Ui.ClickButton(commit, "Commit");
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0 && !window.Vcs.IsBusy, () => $"the commit, where Git lists {window.Vcs.Changes.Count} files and the status bar says '{window.Status}'");
        Assert.Equal("Move the files", Git("log", "-1", "--format=%s").Trim());
        string[] shown = Shown("HEAD");
        Assert.True(shown.Length == Count && shown.All(l => l.StartsWith("R100\tMany/F", StringComparison.Ordinal)),
            $"The commit holds {shown.Count(l => l.StartsWith('R'))} renames of {shown.Length} changes: {string.Join("; ", shown.Where(l => !l.StartsWith('R')).Take(5))}");
        window.Close();
    }

    /// <summary>Twelve hundred files renamed and edited by another program and staged list every one as renamed: the
    /// status looks past git's own limit, under which it would list them as deleted and added.</summary>
    [AvaloniaFact]
    public void Twelve_hundred_files_renamed_and_edited_elsewhere_are_listed_as_renamed_past_gits_own_limit()
    {
        const int Count = 1200;
        string tree = PathNames.Canonical(folder);
        for (int i = 0; i < Count; i++)
        {
            TestFolders.Write(tree, $"Many/F{i:D4}.cs", $"class F{i:D4}\n{{\n    int first = {i};\n    int second = {i};\n    int third = {i};\n}}\n");
        }
        Git("add", "-A");
        Git("commit", "-q", "-m", "Many files");
        for (int i = 0; i < Count; i++)
        {
            string text = File.ReadAllText(Path.Combine(tree, "Many", $"F{i:D4}.cs"));
            File.Delete(Path.Combine(tree, "Many", $"F{i:D4}.cs"));
            TestFolders.Write(tree, $"Renamed/G{i:D4}.cs", text + "// edited\n");
        }
        Git("add", "-A");
        Assert.DoesNotContain(Git("status", "--porcelain").Split('\n'), l => l.StartsWith('R'));
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);

        Ui.WaitFor(() => window.Vcs.Changes.Count == Count, () => $"the {Count} files listed, where Git lists {window.Vcs.Changes.Count}");
        Assert.True(window.Vcs.Changes.All(c => c.Kind == ChangeKind.Renamed),
            $"Of {Count} files renamed and edited {window.Vcs.Changes.Count(c => c.Kind == ChangeKind.Renamed)} are listed as renamed");
        window.Close();
    }

    /// <summary>A file renamed in the Explorer and then rewritten past what git finds alike would commit as a deletion and an
    /// addition: the Commit window says so and offers to commit the move first, its message editable, and committing
    /// makes the move its own commit, a rename, and the edits a second with the person's message.</summary>
    [AvaloniaFact]
    public void A_moved_file_edited_past_what_git_finds_alike_commits_as_a_rename_through_the_moves_committed_first()
    {
        string tree = PathNames.Canonical(folder);
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);
        Assert.Null(Ui.Await(window.RenameAsync(Path.Combine(tree, "Shop.cs"), "Store.cs"), "the rename"));
        File.WriteAllText(Path.Combine(tree, "Store.cs"), "record Store(string Name, decimal Takings);\n\nstatic class Stores\n{\n    public static Store Open(string name) => new(name, 0m);\n}\n");
        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.WaitFor(() => commit.OffersMovesFirst, () => $"the offer to commit the move first, where the Commit window lists {Listed(window)}");
        Assert.Equal("Committed with its edits, Store.cs would no longer be seen by Git as renamed from Shop.cs: too much of it changed. "
            + "Committing the moves first, on their own, keeps its history.", commit.MovesSaid);
        Assert.Equal("Rename Shop.cs to Store.cs", commit.MovesMessage);
        Assert.True(commit.MovesFirst, "The moves are not committed first unless the person turns it on");

        commit.MovesMessage = "Rename the shop";
        commit.Message = "Rework the store";
        Ui.ClickButton(commit, "Commit");
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0 && !window.Vcs.IsBusy, () => $"the commits, where Git lists {Listed(window)} and the status bar says '{window.Status}'");

        Assert.Equal("Rework the store\nRename the shop\nFirst", Git("log", "-3", "--format=%s").Trim());
        Assert.Equal(["R100\tShop.cs\tStore.cs"], Shown("HEAD~1"));
        Assert.Equal(["M\tStore.cs"], Shown("HEAD"));
        Assert.Equal(["A\tStore.cs", "D\tShop.cs"], [.. Git("diff", "--name-status", "-M", "HEAD~2", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)]);
        Assert.Equal("1 move committed, then 1 file committed: Rework the store", window.Status);
        Ui.Landed(() => !commit.OffersMovesFirst, "the offer gone once the move is committed");
        window.Close();
    }

    /// <summary>The Commit window's rows of moved files name where each came from after its name: its old name where it was
    /// renamed in its folder, its old path where it went to another folder, renamed or not, the tip saying the old path
    /// whole; a file only modified names none.</summary>
    [AvaloniaFact]
    public void A_renamed_and_a_moved_files_rows_in_the_Commit_window_name_where_each_came_from()
    {
        string tree = PathNames.Canonical(folder);
        TestFolders.Write(tree, "Old/Basket.cs", "class Basket\n{\n    int count;\n}\n");
        TestFolders.Write(tree, "Old/Till.cs", "class Till\n{\n    int total;\n}\n");
        TestFolders.Write(tree, "Old/Price.cs", "class Price\n{\n    int cents;\n}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "A basket, a till and a price");
        Directory.CreateDirectory(Path.Combine(tree, "New"));
        Git("mv", "Shop.cs", "Store.cs");
        Git("mv", "Old/Basket.cs", "New/Basket.cs");
        Git("mv", "Old/Till.cs", "New/Register.cs");
        File.AppendAllText(Path.Combine(tree, "Old", "Price.cs"), "// edited\n");
        MainWindow window = Ui.OpenFolder(tree, languageServer: false);
        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.WaitFor(() => commit.Rows.OfType<CommitRow>().Count() == 4, () => $"the four changes listed in the Commit window, where Git lists {Listed(window)}");
        Avalonia.Controls.ListBox list = Find(commit, commit.Rows);

        Assert.Equal(("Store.cs  ← Shop.cs", "Renamed from Shop.cs"), Shown("Store.cs"));
        Assert.Equal(("Basket.cs  ← Old/Basket.cs  New", "Moved from Old/Basket.cs"), Shown("Basket.cs"));
        Assert.Equal(("Register.cs  ← Old/Till.cs  New", "Moved from Old/Till.cs"), Shown("Register.cs"));
        Assert.Equal(("Price.cs  Old", null), Shown("Price.cs"));
        window.Close();

        // The words a file's row shows, in order, and the tip of where it came from.
        (string Words, string? Tip) Shown(string name)
        {
            Avalonia.Controls.Control row = Ui.Row(list, commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == name));
            Avalonia.Controls.TextBlock[] words = [.. row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 })];
            return (string.Join("  ", words.Select(t => t.Text)), words.FirstOrDefault(t => t.Text!.StartsWith('←')) is { } came ? Avalonia.Controls.ToolTip.GetTip(came) as string : null);
        }
    }

    /// <summary>The git commands that write the index started in <paramref name="tree"/> while <paramref name="act"/> runs.</summary>
    private static IReadOnlyList<string> IndexWrites(string tree, Action act)
    {
        List<string> heard = [];
        void Heard(object? sender, ChildStart start)
        {
            if (start.Folder.Length > 0 && PathNames.Canonical(start.Folder) == tree && start.Command.Contains("update-index", StringComparison.Ordinal))
            {
                lock (heard)
                {
                    heard.Add(start.Command);
                }
            }
        }
        ChildProcesses.Started += Heard;
        try
        {
            act();
        }
        finally
        {
            ChildProcesses.Started -= Heard;
        }
        lock (heard)
        {
            return [.. heard];
        }
    }

    /// <summary>A row of the Explorer, its folders opened down to it.</summary>
    private static ExplorerNode ExplorerRow(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"The Explorer does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
    }

    /// <summary>The staged changes as git's short status writes them, renames' worktree edits aside.</summary>
    private string[] Staged() =>
        [.. Git("status", "--porcelain", "--untracked-files=no").Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    /// <summary>What a commit changed as git shows it, renames found.</summary>
    private string[] Shown(string commit) =>
        [.. Git("show", "--name-status", "--format=", "-M", commit).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    private static string Listed(MainWindow window) => string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Name} ({c.Kind})"));

    /// <summary>Agrees to every question the window asks, keeping each one's title and text in order.</summary>
    private static List<(string Title, string Text)> Answer(MainWindow window)
    {
        List<(string, string)> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add((title, text));
            return Task.FromResult(true);
        };
        return asked;
    }
}

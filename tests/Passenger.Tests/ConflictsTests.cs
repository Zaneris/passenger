using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Merge conflicts resolved through the window, over real working trees: a merge's conflicted files listed
/// under the Commit window's Merge Conflicts and in the Conflicts dialog, one taken whole from a side and the other
/// merged in the three-way window (its non-conflicting changes in one go, the conflict by its arrow, a simple one by
/// the magic wand, all undoable) and written, git marking each resolved and the commit finishing the merge; a rebase's
/// sides named by their branches as Update Project stops on them, and the rebase going on once they are resolved; an
/// unstash's conflict.</summary>
public sealed class ConflictsTests : IDisposable
{
    private const string Shop = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private const string Prices = "class Prices\n{\n    int Base() => 10;\n\n    int Tax() => 2;\n\n    int Discount() => 0;\n\n    int Shipping() => 5;\n\n    int Total() => Base() + Tax() - Discount() + Shipping();\n}\n";

    private readonly string folder = TestFolders.Create("conflicts-");

    public ConflictsTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop);
        File.WriteAllText(Path.Combine(folder, "Prices.cs"), Prices);
        TestGit.Init(folder);
    }, folder);

    public void Dispose() => TestFolders.Delete(folder);

    [Gate]
    [AvaloniaFact]
    public void A_merges_conflicts_are_listed_and_resolved_whole_or_in_the_merge_window_and_the_commit_finishes_the_merge()
    {
        // Both branches change Base and count; main changes Discount too, the feature Shipping.
        Git("switch", "-q", "-c", "feature");
        Write("Shop.cs", Shop.Replace("int count", "short count", StringComparison.Ordinal));
        Write("Prices.cs", Prices.Replace("Base() => 10", "Base() => 12", StringComparison.Ordinal).Replace("Shipping() => 5", "Shipping() => 7", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Feature prices");
        Git("switch", "-q", "main");
        Write("Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Write("Prices.cs", Prices.Replace("Base() => 10", "Base() => 11", StringComparison.Ordinal).Replace("Discount() => 0", "Discount() => 1", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Main prices");
        Conflicting("merge", "feature");

        MainWindow window = Open();
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<CommitGroup>().Any(g => g.Name == "Merge Conflicts"), "the Merge Conflicts group");
        CommitGroup conflicts = commit.Rows.OfType<CommitGroup>().Single(g => g.Name == "Merge Conflicts");
        Assert.Equal(["Prices.cs", "Shop.cs"], conflicts.Rows.Select(r => r.Change.Name));
        Assert.DoesNotContain(commit.Chosen, c => c.Kind == ChangeKind.Conflicted);
        Assert.Equal("Merge branch 'feature'", commit.Message);
        Assert.Equal("Resolve conflicts in order to commit", CommitRefused(window));

        Assert.True(window.Perform(AppActions.ResolveConflicts), window.Status);
        Ui.Settle();
        ConflictsDialog dialog = DialogButtonsTests.Check(Assert.IsType<ConflictsDialog>(window.Frontmost));
        Assert.Equal("Merging branch feature into branch main", dialog.Description);
        Assert.Equal(["Name", "Yours (main)", "Theirs (feature)"], dialog.Columns);
        Assert.Equal(["Prices.cs Modified Modified", "Shop.cs Modified Modified"], dialog.Rows.Select(r => $"{r.Conflict.Name} {r.Yours} {r.Theirs}"));

        dialog.Chosen = [dialog.Rows.Single(r => r.Conflict.Name == "Shop.cs")];
        Press(dialog, "Accept Theirs");
        Ui.Landed(() => dialog.Rows.Count == 1, "Shop.cs gone from the dialog's list");
        Assert.Equal(Shop.Replace("int count", "short count", StringComparison.Ordinal), Read("Shop.cs"));
        Assert.Equal(["Prices.cs"], Unmerged());

        dialog.Chosen = [dialog.Rows.Single()];
        Press(dialog, "Merge…");
        Ui.Landed(() => window.Frontmost is MergeWindow, "the merge window");
        MergeWindow merge = DialogButtonsTests.Check((MergeWindow)window.Frontmost);
        MergeView view = merge.View;
        Assert.Equal("Merge Revisions for Prices.cs", merge.Title);
        Assert.Equal(("Your version, branch main", "Changes from branch feature"), (view.Texts.Sides.LeftTitle, view.Texts.Sides.RightTitle));
        Assert.Equal(Prices, view.ResultText);
        Assert.Equal([true, false, false], view.Changes.Select(c => c.IsConflict));
        Assert.Equal((2, 1), (view.UnresolvedChanges, view.UnresolvedConflicts));

        Press(merge, "Apply All Non-Conflicting Changes");
        Assert.Contains("Discount() => 1;", view.ResultText, StringComparison.Ordinal);
        Assert.Contains("Shipping() => 7;", view.ResultText, StringComparison.Ordinal);
        Assert.Equal((0, 1), (view.UnresolvedChanges, view.UnresolvedConflicts));

        Click(merge, view, view.IconAt(0, MergeSide.Left, ignore: false));
        string merged = Prices.Replace("Base() => 10", "Base() => 11", StringComparison.Ordinal).Replace("Discount() => 0", "Discount() => 1", StringComparison.Ordinal)
            .Replace("Shipping() => 5", "Shipping() => 7", StringComparison.Ordinal);
        Assert.Equal(merged, view.ResultText);
        Assert.Equal((0, 1), (view.UnresolvedChanges, view.UnresolvedConflicts));
        Assert.Null(view.IconAt(0, MergeSide.Left, ignore: false));
        Click(merge, view, view.IconAt(0, MergeSide.Right, ignore: true));
        Assert.Equal(merged, view.ResultText);
        Assert.Equal((0, 0), (view.UnresolvedChanges, view.UnresolvedConflicts));
        Assert.Null(view.IconAt(0, MergeSide.Right, ignore: false));

        Press(merge, "Apply Changes");
        Ui.Landed(() => !merge.IsVisible && !dialog.IsVisible && window.ConflictsWindow is null, "the merge window and the dialog closed");
        Assert.True(merge.Resolved);
        Assert.Equal(merged, Read("Prices.cs"));
        Assert.Empty(Unmerged());
        Assert.DoesNotContain(window.Vcs.Changes, c => c.Kind == ChangeKind.Conflicted);

        Assert.Equal("Merge branch 'feature'", commit.Message);
        Task<bool> committed = commit.CommitAsync(push: false);
        Assert.True(Ui.Landed(committed, "the merge committed"), window.Status);
        Assert.Equal(3, Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.False(File.Exists(Path.Combine(folder, ".git", "MERGE_HEAD")), "The merge was still under way after its commit");
        Assert.Equal("Merge branch 'feature'", Git("log", "-1", "--format=%s").Trim());
        string left = Git("status", "--porcelain").Trim();
        Assert.True(left.Length == 0, $"The merge's commit left changes behind: {left}\n{Git("diff")}");
        window.Close();
    }

    /// <summary>A file one side deleted, or that both renamed apart, has nothing to merge: the Commit window's double-click
    /// and its Merge... show it chosen in the Conflicts dialog, the status bar saying which side keeps it, and there it is
    /// kept or deleted whole, never written as an empty file.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_file_a_side_deleted_is_kept_or_deleted_whole_from_the_commit_window_never_merged_into_an_empty_file()
    {
        const string Old = "class Old\n{\n}\n";
        Write("Old.cs", Old);
        Git("add", "Old.cs");
        Git("commit", "-q", "-m", "Old");
        // The feature deletes Shop.cs, changes Prices.cs and renames Old.cs; main the other way round, and renames it apart.
        Git("switch", "-q", "-c", "feature");
        Git("rm", "-q", "Shop.cs");
        Write("Prices.cs", Prices.Replace("Base() => 10", "Base() => 12", StringComparison.Ordinal));
        Git("mv", "Old.cs", "Feature.cs");
        Git("commit", "-q", "-am", "Feature");
        Git("switch", "-q", "main");
        string mainShop = Shop.Replace("int count", "long count", StringComparison.Ordinal);
        Write("Shop.cs", mainShop);
        Git("rm", "-q", "Prices.cs");
        Git("mv", "Old.cs", "Main.cs");
        Git("commit", "-q", "-am", "Main");
        Conflicting("merge", "feature");

        MainWindow window = Open();
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        ListBox changes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(commit).OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, commit.Rows));
        Ui.Landed(() => commit.Rows.OfType<CommitGroup>().Any(g => g.Name == "Merge Conflicts" && g.Rows.Count == 5), "the five files in conflict under Merge Conflicts");

        Ui.DoubleClick(Ui.Row(changes, Row(commit, "Shop.cs")));
        ConflictsDialog dialog = Chosen(window, "Shop.cs", "Modified Deleted");
        Assert.Equal("Shop.cs is only in yours, so there is nothing to merge: Accept Yours keeps it, Accept Theirs deletes it", window.Status);
        Press(dialog, "Accept Yours");
        Ui.Landed(() => !Unmerged().Contains("Shop.cs"), "Shop.cs resolved");
        Assert.Equal(mainShop, Read("Shop.cs"));
        Press(dialog, "Close");

        changes.SelectedItem = Row(commit, "Prices.cs");
        Ui.ChooseFromMenu(changes, "Merge…");
        dialog = Chosen(window, "Prices.cs", "Deleted Modified");
        Assert.Equal("Prices.cs is only in theirs, so there is nothing to merge: Accept Theirs keeps it, Accept Yours deletes it", window.Status);
        Press(dialog, "Accept Yours");
        Ui.Landed(() => !Unmerged().Contains("Prices.cs"), "Prices.cs resolved");
        Assert.False(File.Exists(Path.Combine(folder, "Prices.cs")), "Accept Yours of a file yours deleted left it on disk");
        Press(dialog, "Close");

        Ui.DoubleClick(Ui.Row(changes, Row(commit, "Old.cs")));
        dialog = Chosen(window, "Old.cs", "Deleted Deleted");
        Assert.Equal("Old.cs is in neither side, so there is nothing to merge: Accept Yours or Accept Theirs deletes it", window.Status);
        Press(dialog, "Accept Theirs");
        Ui.Landed(() => !Unmerged().Contains("Old.cs"), "Old.cs resolved");
        Assert.False(File.Exists(Path.Combine(folder, "Old.cs")), "Resolving a file both sides deleted brought it back");

        dialog.Chosen = [.. dialog.Rows];
        Press(dialog, "Accept Yours");
        Ui.Landed(() => window.ConflictsWindow is null, "the dialog closed with nothing left in conflict");
        Assert.Empty(Unmerged());
        Assert.True(window.Frontmost is not MergeWindow, "A merge window opened on a file a side lacks");

        Task<bool> committed = commit.CommitAsync(push: false);
        Assert.True(Ui.Landed(committed, "the merge committed"), window.Status);
        Assert.Equal(["Main.cs", "Shop.cs"], Git("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Old, Git("show", "HEAD:Main.cs"));
        window.Close();
    }

    /// <summary>A binary file both sides changed has no lines to merge: Merge… is off for it and, asked for, shows it chosen
    /// in the Conflicts dialog saying so, and Accept Theirs or Accept Yours keeps that side's bytes, staged as they are.</summary>
    [AvaloniaFact]
    public void A_binary_files_conflict_is_never_merged_and_accept_yours_or_theirs_keeps_that_sides_bytes()
    {
        static byte[] Image(byte mark) => [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 1, 2, mark, 0];
        string image = Path.Combine(folder, "image.png");
        string icon = Path.Combine(folder, "icon.png");
        File.WriteAllBytes(image, Image(0));
        File.WriteAllBytes(icon, Image(0));
        Git("add", "-A");
        Git("commit", "-q", "-m", "Images");
        Git("switch", "-q", "-c", "feature");
        File.WriteAllBytes(image, Image(2));
        File.WriteAllBytes(icon, Image(2));
        Git("commit", "-q", "-am", "Feature images");
        Git("switch", "-q", "main");
        File.WriteAllBytes(image, Image(1));
        File.WriteAllBytes(icon, Image(1));
        Git("commit", "-q", "-am", "Main images");
        Conflicting("merge", "feature");

        MainWindow window = Open();
        Ui.Landed(() => window.Vcs.Conflicts.Count == 2 && window.Vcs.Conflicts.All(c => c.IsBinary), "the two images in conflict, binary");
        Assert.False(Ui.Await(window.MergeAsync(PathNames.Canonical(image)), "Merge… on image.png"), "image.png was merged");
        ConflictsDialog dialog = Chosen(window, "image.png", "Modified Modified");
        Assert.Equal("image.png is binary, so it has no lines to merge: Accept Yours or Accept Theirs keeps that side's file whole", window.Status);
        Assert.True(window.Frontmost is not MergeWindow, "A merge window opened on a binary file");

        Press(dialog, "Accept Theirs");
        Ui.Landed(() => !Unmerged().Contains("image.png"), "image.png resolved");
        Assert.Equal(Image(2), File.ReadAllBytes(image));
        Assert.Equal(Git("hash-object", "image.png").Trim(), Git("rev-parse", ":image.png").Trim());

        dialog.Chosen = [dialog.Rows.Single()];
        Press(dialog, "Accept Yours");
        Ui.Landed(() => window.ConflictsWindow is null, "the dialog closed with nothing left in conflict");
        Assert.Equal(Image(1), File.ReadAllBytes(icon));
        Assert.Equal(Git("hash-object", "icon.png").Trim(), Git("rev-parse", ":icon.png").Trim());
        window.Close();
    }

    [AvaloniaFact]
    public void A_Windows_1252_file_merged_under_core_autocrlf_shows_each_sides_letters_and_is_written_in_1252_with_CRLF()
    {
        // Git keeps the file with LF and checks it out with CRLF; its letters are Windows-1252's, € and “ ” among them.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        System.Text.Encoding cp1252 = System.Text.Encoding.GetEncoding(1252);
        const string Menu = "Café: 5 €\r\nTea: 2 €\r\nCake: 3 €\r\n";
        string mine = Menu.Replace("Café: 5", "Café “noir”: 6", StringComparison.Ordinal);
        string theirs = Menu.Replace("Café: 5", "Café – au lait: 7", StringComparison.Ordinal).Replace("Cake: 3", "Cake: 4", StringComparison.Ordinal);
        string menu = Path.Combine(folder, "Menu.txt");
        Git("config", "core.autocrlf", "true");
        File.WriteAllBytes(menu, cp1252.GetBytes(Menu));
        Git("add", "Menu.txt");
        Git("commit", "-q", "-m", "Menu");
        Git("switch", "-q", "-c", "feature");
        File.WriteAllBytes(menu, cp1252.GetBytes(theirs));
        Git("commit", "-q", "-am", "Feature menu");
        Git("switch", "-q", "main");
        File.WriteAllBytes(menu, cp1252.GetBytes(mine));
        Git("commit", "-q", "-am", "Main menu");
        Conflicting("merge", "feature");

        MainWindow window = Open();
        Ui.Landed(() => window.Vcs.Conflicts.Any(c => c.Path == PathNames.Canonical(menu)), "Menu.txt in conflict");
        Task<bool> merging = window.MergeAsync(PathNames.Canonical(menu));
        Ui.Landed(() => window.Frontmost is MergeWindow, "the merge window");
        MergeWindow merge = (MergeWindow)window.Frontmost;
        MergeView view = merge.View;
        Assert.Equal((mine, Menu, theirs), (view.Texts.Left, view.ResultText, view.Texts.Right));

        Press(merge, "Apply All Non-Conflicting Changes");
        Click(merge, view, view.IconAt(0, MergeSide.Left, ignore: false));
        Click(merge, view, view.IconAt(0, MergeSide.Right, ignore: true));
        Press(merge, "Apply Changes");
        Assert.True(Ui.Landed(merging, "the merge window closed"), window.Status);
        string merged = mine.Replace("Cake: 3", "Cake: 4", StringComparison.Ordinal);
        Assert.True(File.ReadAllBytes(menu).AsSpan().SequenceEqual(cp1252.GetBytes(merged)),
            $"The merge's result was not written in Windows-1252 with CRLF: {Convert.ToHexString(File.ReadAllBytes(menu))}");
        Assert.Empty(Unmerged());
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Resolve_simple_conflicts_merges_a_lines_different_words_undo_takes_it_back_and_the_keys_take_a_side()
    {
        // One line, changed by each side in a different word.
        Git("switch", "-q", "-c", "feature");
        Write("Shop.cs", Shop.Replace("int count", "int total", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Total");
        Git("switch", "-q", "main");
        Write("Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");
        Conflicting("merge", "feature");

        MainWindow window = Open();
        ConflictsDialog dialog = window.ShowConflicts()!;
        Ui.Settle();
        dialog.Chosen = [dialog.Rows.Single()];
        Press(dialog, "Merge…");
        Ui.Landed(() => window.Frontmost is MergeWindow, "the merge window");
        MergeWindow merge = (MergeWindow)window.Frontmost;
        MergeView view = merge.View;
        Assert.Equal(1, view.UnresolvedConflicts);

        Press(merge, "Resolve Simple Conflicts");
        Assert.Equal(Shop.Replace("int count", "long total", StringComparison.Ordinal), view.ResultText);
        Assert.Equal(0, view.UnresolvedConflicts);

        view.Result.Focus();
        Ui.Press(merge, OperatingSystem.IsMacOS() ? "meta Z" : "control Z");
        Assert.Equal(Shop, view.ResultText);
        Assert.Equal(1, view.UnresolvedConflicts);

        // F7 goes to the conflict; Accept Right Side, then Left Side, by Rider's keys, resolve it.
        Ui.Press(merge, "F7");
        Assert.Equal(2, view.Result.CaretPosition.Line);
        Ui.Press(merge, OperatingSystem.IsMacOS() ? "control meta LEFT" : "control alt A");
        Assert.Equal(Shop.Replace("int count", "int total", StringComparison.Ordinal), view.ResultText);
        Assert.Equal(1, view.UnresolvedConflicts);
        Ui.Press(merge, OperatingSystem.IsMacOS() ? "control meta RIGHT" : "control alt R");
        Assert.Equal(Shop.Replace("    int count;\n", "    int total;\n    long count;\n", StringComparison.Ordinal), view.ResultText);
        Assert.Equal(0, view.UnresolvedConflicts);

        List<string> asked = [];
        window.Confirm = (title, text, yes) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        Press(merge, "Cancel");
        Ui.Landed(() => !merge.IsVisible, "the merge window closed");
        Assert.Equal(["Cancel File Merge"], asked);
        Assert.False(merge.Resolved);
        Assert.Equal(["Shop.cs"], Unmerged());
        Assert.True(dialog.IsVisible, "Cancelling the merge closed the Conflicts dialog");
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void Update_project_stopped_by_a_rebases_conflict_names_each_side_by_its_branch_and_the_rebase_goes_on_once_it_is_resolved()
    {
        string clone = TestFolders.Create("conflicts-clone-");
        Git("clone", "-q", "-c", "core.autocrlf=false", folder, clone);
        TestGit.Run(clone, "config", "user.email", "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        TestGit.Run(clone, "config", "pull.rebase", "true");
        File.WriteAllText(Path.Combine(clone, "Shop.cs"), Shop.Replace("int count", "short count", StringComparison.Ordinal));
        TestGit.Run(clone, "commit", "-q", "-am", "Short");
        Write("Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");

        MainWindow window = Ui.OpenFolder(clone);
        window.OpenFile(Path.Combine(clone, "Shop.cs"));
        Ui.Settle();
        bool asked = false;
        window.Confirm = (title, text, yes) =>
        {
            asked = text == "All conflicts have been resolved. Do you want to continue rebase?" && yes == "Continue Rebase";
            return Task.FromResult(true);
        };

        window.Perform(AppActions.UpdateProject);
        Ui.Landed(() => window.ConflictsWindow is not null, "the Conflicts dialog");
        Assert.StartsWith("Rebase stopped due to conflicts", window.Status, StringComparison.Ordinal);
        ConflictsDialog dialog = window.ConflictsWindow!;
        Assert.Equal("Rebasing branch main onto branch origin/main", dialog.Description);
        Assert.Equal(["Name", "Yours (main)", "Theirs (origin/main)"], dialog.Columns);
        Assert.Contains("<<<<<<<", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);

        // Yours is the commit being rebased, git's "theirs", on the left.
        dialog.Chosen = [dialog.Rows.Single()];
        Press(dialog, "Merge…");
        Ui.Landed(() => window.Frontmost is MergeWindow, "the merge window");
        MergeWindow merge = (MergeWindow)window.Frontmost;
        Assert.Matches("^Rebasing [0-9a-f]{8} from main$", merge.View.Texts.Sides.LeftTitle);
        Assert.Equal("Already rebased commits and commits from origin/main", merge.View.Texts.Sides.RightTitle);
        Assert.Contains("short count", merge.View.Left.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("long count", merge.View.Right.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Press(merge, "Accept Left");
        Ui.Landed(() => window.ConflictsWindow is null && asked, "the question to continue the rebase");
        Ui.Landed(() => window.Status == "Rebase successful", "the rebase's end");
        Assert.Equal(["Short", "Long", "First"], TestGit.Run(clone, "log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Shop.Replace("int count", "short count", StringComparison.Ordinal), File.ReadAllText(Path.Combine(clone, "Shop.cs")));
        Assert.Equal(MergeOperation.None, window.Vcs.Merge.Operation);
        Ui.Landed(() => window.EditorArea.Active!.Text.Snapshot.ToString() == Shop.Replace("int count", "short count", StringComparison.Ordinal), "the open file as rebased");
        window.Close();
    }

    /// <summary>A rebase stopped on a conflict, resolved, is still said in the Commit window, whose Commit refuses to commit
    /// beside it on its detached head, naming Continue Rebase, and whose Continue Rebase goes on with it.</summary>
    [AvaloniaFact]
    public void A_rebase_whose_conflict_is_resolved_is_said_in_the_commit_window_whose_commit_never_goes_beside_it()
    {
        Git("switch", "-q", "-c", "feature");
        Write("Shop.cs", Shop.Replace("int count", "short count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Short");
        Write("Notes.md", "notes\n");
        Git("add", "Notes.md");
        Git("commit", "-q", "-m", "Notes");
        Git("switch", "-q", "main");
        Write("Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");
        Git("switch", "-q", "feature");
        Conflicting("rebase", "main");

        MainWindow window = Open();
        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.Landed(() => commit.UnderWay?.StartsWith("Rebasing feature onto main", StringComparison.Ordinal) == true, "the rebase said in the Commit window");
        Assert.True(window.Perform(AppActions.ResolveConflicts), window.Status);
        Ui.Settle();
        ConflictsDialog dialog = Assert.IsType<ConflictsDialog>(window.Frontmost);
        window.Confirm = (_, _, _) => Task.FromResult(false);
        dialog.Chosen = [dialog.Rows.Single()];
        Press(dialog, "Accept Yours");
        Ui.Landed(() => window.ConflictsWindow is null && window.Vcs.Conflicts.Count == 0, "the conflict resolved and the dialog closed");
        Assert.True(commit.UnderWay?.StartsWith("Rebasing feature onto main", StringComparison.Ordinal) == true,
            $"Once its conflict was resolved the Commit window said \"{commit.UnderWay}\" of the rebase still in progress");

        string head = Git("rev-parse", "HEAD").Trim();
        commit.Message = "A commit of my own";
        string refused = CommitRefused(window);
        Assert.Contains("Continue Rebase", refused, StringComparison.Ordinal);
        Assert.Equal(head, Git("rev-parse", "HEAD").Trim());
        Assert.Equal(MergeOperation.Rebase, window.Vcs.Merge.Operation);

        Press(window, "Continue Rebase");
        Ui.Landed(() => window.Status == "Rebase successful", "the rebase's end");
        Assert.Equal(["Notes", "Short", "Long", "First"], Git("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Null(commit.UnderWay);
        window.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void An_unstash_that_conflicts_opens_the_conflicts_dialog_on_the_stash_and_accept_theirs_takes_the_stashed_change()
    {
        Write("Shop.cs", Shop.Replace("int count", "short count", StringComparison.Ordinal));
        Git("stash", "push", "-m", "Short count");
        Write("Shop.cs", Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");

        MainWindow window = Open();
        Task<UnstashPopup?> showing = window.ShowUnstashAsync();
        Task popping = Ui.Landed(showing, "the stashes")!.PopAsync();
        Ui.Landed(() => popping.IsCompleted && window.ConflictsWindow is not null, "the Conflicts dialog");
        Assert.Equal("Unstashed with conflicts: Shop.cs", window.Status);
        ConflictsDialog dialog = window.ConflictsWindow!;
        Assert.Equal("Conflicts during unstashing stash@{0}", dialog.Description);
        Assert.Equal(["Name", "Yours (main)", "Theirs"], dialog.Columns);
        Assert.Contains(">>>>>>>", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);

        dialog.Chosen = [dialog.Rows.Single()];
        Press(dialog, "Accept Theirs");
        Ui.Landed(() => window.ConflictsWindow is null, "the dialog closed with nothing left in conflict");
        string stashed = Shop.Replace("int count", "short count", StringComparison.Ordinal);
        Assert.Equal(stashed, Read("Shop.cs"));
        Assert.Equal(stashed, window.EditorArea.Active!.Text.Snapshot.ToString());
        Assert.Empty(Unmerged());
        // Git keeps a stash whose pop stopped for conflicts.
        Assert.Single(Git("stash", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        window.Close();
    }

    /// <summary>A cherry-pick stopped for its conflict is aborted by Abort Cherry-Pick once agreed to, and a revert stopped
    /// so by Abort Revert: each time HEAD and the file are as they were before it and nothing is under way. While one is
    /// under way, the other aborts and Abort Merge are greyed saying none of theirs is.</summary>
    [AvaloniaFact]
    public void A_cherry_pick_or_a_revert_stopped_for_conflicts_is_aborted_by_its_own_abort_and_head_is_as_it_was()
    {
        string longer = Shop.Replace("int count", "long count", StringComparison.Ordinal);
        Git("switch", "-q", "-c", "feature");
        Write("Shop.cs", Shop.Replace("int count", "short count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Short");
        string shorter = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "main");
        Write("Shop.cs", longer);
        Git("commit", "-q", "-am", "Long");
        Write("Shop.cs", Shop.Replace("int count", "ulong count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Unsigned");
        string head = Git("rev-parse", "HEAD").Trim();
        string unsigned = Read("Shop.cs");
        Conflicting("cherry-pick", shorter);

        MainWindow window = Open();
        List<string> asked = [];
        window.Confirm = (title, text, yes) =>
        {
            asked.Add($"{title} | {text} | {yes}");
            return Task.FromResult(true);
        };
        Ui.Landed(() => window.Vcs.Merge.Operation == MergeOperation.CherryPick, "the cherry-pick under way");
        Assert.Equal("No merge in progress", window.Actions.WhyNot(AppActions.AbortMerge, window.Actions.Context()));
        Assert.Equal("No revert in progress", window.Actions.WhyNot(AppActions.AbortRevert, window.Actions.Context()));
        Assert.True(window.Perform(AppActions.AbortCherryPick), window.Status);
        Ui.Landed(() => window.Vcs.Merge.Operation == MergeOperation.None, "the cherry-pick aborted");
        Assert.Equal(["Abort Cherry-Pick | Abort cherry-pick? | Abort"], asked);
        Assert.Equal("Abort cherry-pick succeeded", window.Status);
        Assert.Equal(head, Git("rev-parse", "HEAD").Trim());
        Assert.Equal(unsigned, Read("Shop.cs"));
        Assert.Empty(Unmerged());
        Ui.Landed(() => window.EditorArea.Active!.Text.Snapshot.ToString() == unsigned, "the open file as HEAD has it");

        // The revert of Long conflicts with Unsigned, which changed the same line after it.
        Conflicting("revert", "--no-edit", "HEAD~1");
        Ui.WaitFor(() => window.Vcs.Merge.Operation == MergeOperation.Revert, "the revert under way");
        Assert.Equal("No cherry-pick in progress", window.Actions.WhyNot(AppActions.AbortCherryPick, window.Actions.Context()));
        Assert.True(window.Perform(AppActions.AbortRevert), window.Status);
        Ui.Landed(() => window.Vcs.Merge.Operation == MergeOperation.None, "the revert aborted");
        Assert.Equal("Abort Revert | Abort revert? | Abort", asked[^1]);
        Assert.Equal("Abort revert succeeded", window.Status);
        Assert.Equal(head, Git("rev-parse", "HEAD").Trim());
        Assert.Equal(unsigned, Read("Shop.cs"));
        Assert.Empty(Unmerged());
        window.Close();
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        return window;
    }

    /// <summary>What the Commit window's Commit answered when pressed: why it would not commit.</summary>
    private static string CommitRefused(MainWindow window)
    {
        Task<bool> committing = window.CommitView.CommitAsync(push: false);
        Assert.False(Ui.Landed(committing, "the commit's answer"), "A commit was made with files still in conflict");
        return window.Status ?? string.Empty;
    }

    /// <summary>A file's row in the Commit window's Merge Conflicts.</summary>
    private static CommitRow Row(CommitView commit, string name)
    {
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == name && r.Change.Kind == ChangeKind.Conflicted), $"{name} under Merge Conflicts");
        return commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == name && r.Change.Kind == ChangeKind.Conflicted);
    }

    /// <summary>The Conflicts dialog in front with one file chosen, its sides as said, and no Merge... to press for it.</summary>
    private static ConflictsDialog Chosen(MainWindow window, string name, string sides)
    {
        Ui.Landed(() => window.ConflictsWindow is not null, $"the Conflicts dialog on {name}");
        ConflictsDialog dialog = window.ConflictsWindow!;
        Assert.True(window.Frontmost == dialog, $"The Conflicts dialog is not in front of the window, {window.Frontmost?.Title} is");
        ConflictRow row = Assert.Single(dialog.Chosen);
        Assert.Equal($"{name} {sides}", $"{row.Conflict.Name} {row.Yours} {row.Theirs}");
        Button merge = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Button>().First(b => (b.Content as string) == "Merge…");
        Assert.False(merge.IsEffectivelyEnabled, $"The Conflicts dialog offers Merge… for {name}, which a side lacks");
        return dialog;
    }

    /// <summary>On Windows the merge window's Apply into a file another program holds waits for it before git marks it
    /// resolved: while it is held the status bar says it waits for that file and git still has it in conflict, and once the
    /// program lets go the file holds the result, resolved.</summary>
    [AvaloniaFact]
    public void Merge_apply_into_a_file_another_program_holds_says_it_waits_and_resolves_it_once_the_file_is_let_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace or write a file another program holds");
        Git("switch", "-q", "-c", "feature");
        Write("Prices.cs", Prices.Replace("Base() => 10", "Base() => 12", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Feature prices");
        Git("switch", "-q", "main");
        Write("Prices.cs", Prices.Replace("Base() => 10", "Base() => 11", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Main prices");
        Conflicting("merge", "feature");

        MainWindow window = Open();
        Assert.True(window.Perform(AppActions.ResolveConflicts), window.Status);
        Ui.Settle();
        ConflictsDialog dialog = Assert.IsType<ConflictsDialog>(window.Frontmost);
        dialog.Chosen = [dialog.Rows.Single(r => r.Conflict.Name == "Prices.cs")];
        Press(dialog, "Merge…");
        Ui.Landed(() => window.Frontmost is MergeWindow, "the merge window");
        MergeWindow merge = (MergeWindow)window.Frontmost!;
        MergeView view = merge.View;
        Click(merge, view, view.IconAt(0, MergeSide.Left, ignore: false));
        Click(merge, view, view.IconAt(0, MergeSide.Right, ignore: true));
        Assert.Equal((0, 0), (view.UnresolvedChanges, view.UnresolvedConflicts));
        string merged = Prices.Replace("Base() => 10", "Base() => 11", StringComparison.Ordinal);
        List<string> said = [];
        using IDisposable following = Ui.FollowStatus(window, said);
        using (new FileStream(Path.Combine(folder, "Prices.cs"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Button apply = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(merge).OfType<Button>().First(b => (b.Content as string) == "Apply Changes");
            Ui.Click(apply, settle: false);
            Ui.PumpUntil(() => said.Any(s => s.StartsWith("Waiting", StringComparison.Ordinal) || s.EndsWith("resolved", StringComparison.Ordinal)),
                "Apply to say it waits for the held Prices.cs");
            Assert.Equal(["Prices.cs"], Unmerged());
        }
        Ui.Landed(() => !merge.IsVisible, "the merge window closed once Prices.cs was let go");
        Assert.Equal(["Waiting for Prices.cs to be saved…", "Prices.cs resolved"],
            said.Where(s => s.StartsWith("Waiting", StringComparison.Ordinal) || s.StartsWith("Prices.cs", StringComparison.Ordinal)));
        Assert.Equal(merged, Read("Prices.cs"));
        Assert.Empty(Unmerged());
        window.Close();
    }

    /// <summary>Clicks a merge window where its view draws something (a change's arrow or X), as the person clicks it.</summary>
    private static void Click(MergeWindow merge, MergeView view, Point? at)
    {
        Assert.True(at is not null, "The merge window draws no arrow or X there");
        Ui.Click(view, at.Value);
    }

    /// <summary>Presses a button of a window by its words or its tooltip, as the person clicks it.</summary>
    private static void Press(Window window, string button)
    {
        Button? found = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>()
            .FirstOrDefault(b => (b.Content as string) == button || ToolTip.GetTip(b) as string == button);
        Assert.True(found is { IsEffectivelyEnabled: true }, $"{window.Title} has no {button} to press{(found is null ? string.Empty : " that is on")}");
        Ui.Click(found!);
    }

    /// <summary>The files git still has in conflict, by name.</summary>
    private string[] Unmerged() => [.. Git("diff", "--name-only", "--diff-filter=U").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName)!];

    /// <summary>A git command that is to stop for conflicts, as a merge or a pull does.</summary>
    private void Conflicting(params string[] arguments)
    {
        ChildResult result = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command("git", arguments, folder), limit: TimeSpan.FromMinutes(1))),
            $"git {string.Join(' ', arguments)}");
        Assert.True(!result.Succeeded && Unmerged().Length > 0, $"git {string.Join(' ', arguments)} was to stop for conflicts: {result.Error}{result.Output}");
    }

    private void Write(string name, string text) => TestFolders.Write(folder, name, text);

    private string Read(string name) => File.ReadAllText(Path.Combine(folder, name));

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}

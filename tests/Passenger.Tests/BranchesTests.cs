using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>The branches panel at the Git log's left, over a real working tree with a bare repository as its origin:
/// HEAD, the local branches by directory, origin's branches and the tags, main starred; a branch chosen filtering the
/// log to its commits; and its menu's Checkout, New Branch from, Checkout and Rebase onto, Compare with, Show Diff with
/// Working Tree, Rebase onto, Merge into, Update, Push, Rename and Delete each doing what it says, a merge that
/// conflicts opening the Conflicts dialog.</summary>
public sealed class BranchesTests : IDisposable
{
    private const string Shop = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("branches-");
    private readonly string origin = TestFolders.Create("branches-origin-");

    public BranchesTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop);
        TestGit.Init(folder);
        TestGit.Run(origin, "init", "-q", "--bare", "-b", "main");
        Git("remote", "add", "origin", origin);
        Git("tag", "v1.0");
        // feature is a commit ahead of main; team/login another; both on origin, main tracking origin/main.
        Git("switch", "-q", "-c", "feature");
        Commit("Feature.cs", "class Feature { }\n", "Feature");
        Git("switch", "-q", "-c", "team/login", "main");
        Commit("Login.cs", "class Login { }\n", "Login");
        Git("switch", "-q", "main");
        Git("tag", "-a", "v2.0", "-m", "Two");
        Git("push", "-q", "-u", "origin", "main", "feature");
    }, folder, origin);

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(origin);
    }

    [AvaloniaFact]
    public void The_panel_lists_head_local_branches_by_directory_the_remotes_branches_and_the_tags_in_riders_order()
    {
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        Assert.Equal(
            [
                "HEAD (Current Branch)",
                "Local",
                "  main [current, favourite] origin/main",
                "  team",
                "    login",
                "  feature origin/feature",
                "Remote",
                "  origin",
                "    main [favourite]",
                "    feature",
                "Tags",
            ], Listed(panel));

        panel.Toggle(panel.Rows.Single(r => r.Title == "Tags"));
        Assert.Equal(["v1.0", "v2.0"], panel.Rows.SkipWhile(r => r.Title != "Tags").Skip(1).Select(r => r.Title));
        Assert.True(panel.Select("feature"));
        panel.ToggleFavorite(panel.Selected!.Branch!);
        Assert.Equal(["main", "feature", "team", "login"], panel.Rows.SkipWhile(r => r.Title != "Local").Skip(1).TakeWhile(r => r.Title != "Remote").Select(r => r.Title));
        Assert.Contains("refs/heads/feature", panel.Favorites.Favorites);

        panel.SearchText = "log";
        Ui.Settle();
        Assert.Equal(["HEAD (Current Branch)", "Local", "  team", "    login"], Listed(panel));
        panel.SearchText = string.Empty;
        Ui.Settle();
        TreeKeysCheck.Press(window, panel.Rows, panel.Rows.Single(r => r.Title == "Remote"), "the branches panel");
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_on_a_branch_filters_the_log_to_its_commits_and_the_filters_x_shows_every_branch_again()
    {
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        GitLogView log = window.GitLog;
        Ui.Landed(() => log.Rows.Count == 3, "every branch's three commits");

        Assert.True(panel.Select("feature"));
        panel.FocusRows();
        Ui.Press(window, "Enter");
        Ui.Settle();
        Assert.Equal("refs/heads/feature", log.BranchFilter);
        Assert.Equal(["Feature", "First"], log.Rows.Select(r => r.Commit.Subject));

        Assert.True(panel.Select("HEAD"));
        panel.Activate(panel.Selected!);
        Ui.Settle();
        Assert.Equal("HEAD", log.BranchFilter);
        Assert.Equal(["First"], log.Rows.Select(r => r.Commit.Subject));

        log.FilterBranch(null, all: true);
        Ui.Settle();
        Assert.Null(log.BranchFilter);
        Assert.Equal(3, log.Rows.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Riders_keys_rename_make_a_branch_from_and_delete_the_chosen_branch()
    {
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;

        // Rename: F2 on the Mac, Rename's Ctrl+R, R in Rider's Visual Studio keymap.
        Assert.True(panel.Select("team/login"));
        panel.FocusRows();
        if (OperatingSystem.IsMacOS())
        {
            Ui.Press(window, "F2");
        }
        else
        {
            Ui.Press(window, "control R , R");
        }
        NamePopup rename = Assert.IsType<NamePopup>(window.Popup);
        rename.Text = "team/signin";
        Task named = rename.SubmitAsync();
        Ui.Landed(() => named.IsCompleted, "the rename");
        Assert.Contains("team/signin", LocalBranches());

        // Ctrl+Alt+N (Cmd+Alt+N): New Branch from the chosen.
        Assert.True(panel.Select("feature"));
        panel.FocusRows();
        Ui.Press(window, OperatingSystem.IsMacOS() ? "meta alt N" : "control alt N");
        NamePopup made = Assert.IsType<NamePopup>(window.Popup);
        made.Text = "feature-two";
        named = made.SubmitAsync();
        Ui.Landed(() => named.IsCompleted, "the new branch");
        Assert.Equal("feature-two", window.Vcs.Branch);

        // Delete: feature's commit is on feature-two, checked out, so it goes without a question.
        window.Confirm = (_, _, _) => throw new InvalidOperationException("Deleting a merged branch asked first");
        Assert.True(panel.Select("feature"));
        panel.FocusRows();
        Ui.Press(window, "Delete");
        Assert.DoesNotContain("feature", LocalBranches());
        window.Close();
    }

    [AvaloniaFact]
    public void Checkout_new_branch_from_rename_delete_update_and_push_do_what_they_say()
    {
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);

        Choose(panel, rows, "origin/feature", "Checkout");
        Assert.Equal("feature", window.Vcs.Branch);
        Assert.True(File.Exists(Path.Combine(folder, "Feature.cs")), "The checkout left feature's file out");

        Choose(panel, rows, "main", "New Branch from 'main'…");
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.Equal("main", popup.Text);
        popup.Text = "hotfix";
        Task named = popup.SubmitAsync();
        Ui.Landed(() => named.IsCompleted, "the new branch");
        Assert.True(popup.Problem is null, $"The new branch was refused: {popup.Problem}");
        Assert.Equal("hotfix", window.Vcs.Branch);
        Assert.Equal(Git("rev-parse", "main").Trim(), Git("rev-parse", "HEAD").Trim());

        Choose(panel, rows, "team/login", "Rename…");
        popup = Assert.IsType<NamePopup>(window.Popup);
        popup.Text = "signin";
        named = popup.SubmitAsync();
        Ui.Landed(() => named.IsCompleted, "the rename");
        Assert.Equal(["feature", "hotfix", "main", "signin"], LocalBranches());
        Assert.Contains(panel.Rows, r => r.Title == "signin" && r.Depth == 1);

        List<string> asked = [];
        window.Confirm = (title, text, yes) =>
        {
            asked.Add($"{title}: {text}");
            return Task.FromResult(true);
        };
        Choose(panel, rows, "main", "Delete");
        Assert.Empty(asked);
        Assert.DoesNotContain("main", LocalBranches());
        Choose(panel, rows, "signin", "Delete");
        Assert.Equal(["Branch Was Not Fully Merged: The branch signin was not fully merged to hotfix. Delete it and its unmerged commits?"], asked);
        Assert.Equal(["feature", "hotfix"], LocalBranches());

        panel.Toggle(panel.Rows.Single(r => r.Title == "Tags"));
        Choose(panel, rows, "v1.0", "Delete");
        Assert.Equal(["v2.0"], Git("tag").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Choose(panel, rows, "origin/main", "Checkout");
        Assert.Equal("main", window.Vcs.Branch);
        Assert.Equal("origin/main", Git("rev-parse", "--abbrev-ref", "main@{upstream}").Trim());

        string clone = TestFolders.Create("branches-clone-");
        TestGit.Run(clone, "clone", "-q", "-b", "feature", origin, ".");
        TestGit.Run(clone, "config", "user.email", "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        File.WriteAllText(Path.Combine(clone, "Feature.cs"), "class Feature { int more; }\n");
        TestGit.Run(clone, "commit", "-q", "-am", "More");
        TestGit.Run(clone, "push", "-q", "origin", "feature");
        Git("fetch", "-q", "origin");
        Choose(panel, rows, "feature", "Update");
        Assert.Equal(Git("rev-parse", "origin/feature").Trim(), Git("rev-parse", "feature").Trim());

        Choose(panel, rows, "hotfix", "Push…");
        Assert.Equal(Git("rev-parse", "hotfix").Trim(), TestGit.Run(origin, "rev-parse", "hotfix").Trim());
        Assert.Equal("origin/hotfix", Git("rev-parse", "--abbrev-ref", "hotfix@{upstream}").Trim());
        window.Close();
    }

    [AvaloniaFact]
    public void Merge_into_rebase_onto_and_checkout_and_rebase_onto_current_move_the_branches_as_git_would()
    {
        Commit("Notes.txt", "notes\n", "Notes on main");
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);

        Choose(panel, rows, "feature", "Merge 'feature' into 'main'");
        Assert.True(Ancestor("feature", "main"), "main lacks feature's commit after the merge");
        Assert.Equal("Merged feature to main", window.Status);
        Assert.Equal("Merge branch 'feature'", Git("log", "-1", "--format=%s").Trim());

        Choose(panel, rows, "team/login", "Checkout and Rebase onto 'main'");
        Assert.Equal("team/login", window.Vcs.Branch);
        Assert.True(Ancestor("main", "team/login"), "team/login was not rebased onto main");
        Assert.Equal("Login", Git("log", "-1", "--format=%s").Trim());
        Assert.Equal("Checked out team/login and rebased it on main", window.Status);

        Git("branch", "-f", "feature", "main");
        Git("switch", "-q", "feature");
        Commit("Later.cs", "class Later { }\n", "Later");
        Git("switch", "-q", "team/login");
        Ui.Settle();
        Choose(panel, rows, "feature", "Rebase 'team/login' onto 'feature'");
        Assert.True(Ancestor("feature", "team/login"), "team/login was not rebased onto feature");
        Assert.Equal("Rebased team/login on feature", window.Status);
        window.Close();
    }

    [AvaloniaFact]
    public void Compare_with_current_lists_each_branchs_commits_the_other_lacks_and_show_diff_with_working_tree_its_files()
    {
        Commit("Notes.txt", "notes\n", "Notes on main");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop.Replace("int count", "long count", StringComparison.Ordinal));
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);

        Choose(panel, rows, "feature", "Compare with 'main'");
        Assert.Equal(["Log", "Compare main and feature"], window.GitWindow.Titles);
        GitLogView feature = window.GitWindow.Shown;
        Ui.Settle();
        Assert.Equal(["Feature"], feature.Rows.Select(r => r.Commit.Subject));
        GitLogView main = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.GitWindow).OfType<GitLogView>().Single(v => v.Range?.To == "main");
        Assert.Equal(["Notes on main"], main.Rows.Select(r => r.Commit.Subject));

        // The branches are the Log's, where the person goes back to them.
        window.GitWindow.ShowLog();
        Ui.Settle();

        Choose(panel, rows, "feature", "Show Diff with Working Tree");
        ChangesWindow changes = Assert.IsType<ChangesWindow>(window.Frontmost);
        Assert.Equal("Changes Between feature and Current Working Tree", changes.Title);
        Assert.Equal(["Feature.cs Deleted", "Notes.txt Added", "Shop.cs Modified"], changes.Files.Select(f => $"{f.Name} {f.Kind}").Order());
        changes.Select(changes.Files.Single(f => f.Name == "Shop.cs"));
        changes.Open();
        Ui.Landed(() => window.Frontmost is DiffWindow, "Shop.cs as feature has it beside the file");
        DiffWindow diff = (DiffWindow)window.Frontmost;
        Assert.Equal(Shop, diff.View.Left.Document.Snapshot.ToString());
        diff.Close();
        changes.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void A_merge_from_the_panel_that_conflicts_opens_the_conflicts_dialog()
    {
        Git("switch", "-q", "feature");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop.Replace("int count", "short count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Short");
        Git("switch", "-q", "main");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop.Replace("int count", "long count", StringComparison.Ordinal));
        Git("commit", "-q", "-am", "Long");
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;

        Choose(panel, List(panel), "feature", "Merge 'feature' into 'main'");
        Ui.Landed(() => window.ConflictsWindow is not null, "the Conflicts dialog");
        Assert.Equal("feature Merged with Conflicts: Shop.cs", window.Status);
        ConflictsDialog dialog = window.ConflictsWindow!;
        Assert.Equal("Merging branch feature into branch main", dialog.Description);
        Assert.Equal(["Shop.cs"], dialog.Rows.Select(r => r.Conflict.Name));
        Assert.Contains(">>>>>>> feature\n", File.ReadAllText(Path.Combine(folder, "Shop.cs")), StringComparison.Ordinal);
        dialog.Close();
        window.Perform(AppActions.Commit);
        Ui.Settle();
        Assert.StartsWith("Merge branch 'feature'", window.CommitView.Message, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_branch_in_the_toolbars_branch_menu_opens_its_own_menu_and_is_checked_out_only_by_its_checkout()
    {
        MainWindow window = Open();

        ContextMenu menu = OpenBranchWidget(window);
        MenuItem feature = Reach(window, menu.Items.OfType<MenuItem>(), "feature");
        Ui.Press(window, "Enter");
        Assert.True(window.Vcs.Branch == "main", $"Choosing feature in the branch menu checked out {window.Vcs.Branch}, where Rider's opens the branch's own menu");
        Assert.True(feature.IsSubMenuOpen, "Choosing feature in the branch menu opened no menu of feature's");
        Assert.Equal(
            [
                "Checkout", "New Branch from 'feature'…", "Checkout and Rebase onto 'main'", "Compare with 'main'", "Show Diff with Working Tree",
                "Rebase 'main' onto 'feature'", "Merge 'feature' into 'main'", "Update", "Push…", "Rename…", "Delete",
            ], feature.Items.OfType<MenuItem>().Select(i => i.Header as string));

        Reach(window, feature.Items.OfType<MenuItem>(), "Merge 'feature' into 'main'");
        Ui.Press(window, "Enter");
        Ui.Landed(() => window.Status == "Merged feature to main", "the merge");
        Assert.True(Ancestor("feature", "main"), "main lacks feature's commit after Merge 'feature' into 'main'");
        Assert.Equal("main", window.Vcs.Branch);

        menu = OpenBranchWidget(window);
        MenuItem login = Reach(window, menu.Items.OfType<MenuItem>(), "team/login");
        Ui.Press(window, "Enter");
        Reach(window, login.Items.OfType<MenuItem>(), "Checkout");
        Ui.Press(window, "Enter");
        Ui.Landed(() => window.Vcs.Branch == "team/login", "the checkout of team/login");
        window.Close();
    }

    /// <summary>The toolbar's branch widget clicked, as a person's pointer clicks it, and its menu waited for.</summary>
    private static ContextMenu OpenBranchWidget(MainWindow window)
    {
        Button button = window.FindControl<Button>("BranchButton")!;
        Ui.Click(button, settle: false);
        Ui.WaitFor(() => window.OpenContextMenu is { IsOpen: true }, "the branch widget's menu");
        Ui.Settle();
        return window.OpenContextMenu!;
    }

    /// <summary>The arrow keys taken down a menu to an item, as a person moves to it.</summary>
    private static MenuItem Reach(MainWindow window, IEnumerable<MenuItem> items, string header)
    {
        MenuItem item = items.FirstOrDefault(i => i.Header as string == header)
            ?? throw new InvalidOperationException($"The menu has no {header}: [{string.Join(", ", items.Select(i => i.Header))}]");
        for (int presses = 0; !item.IsSelected; presses++)
        {
            Assert.True(presses < 30, $"Down never reached {header} in the menu");
            Ui.Press(window, "Down");
        }
        return item;
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1300, height: 900);
        window.Perform(AppActions.ShowGit);
        Ui.Settle();
        Ui.Landed(() => window.GitBranches.Rows.Count > 0, "the branches panel");
        return window;
    }

    /// <summary>Chooses a branch or tag in the panel and an item of its menu, as the person would.</summary>
    private static void Choose(BranchesPanel panel, ListBox rows, string name, string item)
    {
        Assert.True(panel.Select(name), $"The branches panel has no {name}");
        Ui.ChooseFromMenu(rows, item);
        Ui.Settle();
    }

    private static ListBox List(BranchesPanel panel) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<ListBox>().Single();

    /// <summary>The rows as the panel shows them: indented by depth, the current and favourite marked, and a local branch's
    /// upstream after it.</summary>
    private static string[] Listed(BranchesPanel panel) => [.. panel.Rows.Select(r =>
    {
        string[] marks = [.. r.Branch?.IsCurrent == true ? ["current"] : Array.Empty<string>(), .. r.IsFavorite ? ["favourite"] : Array.Empty<string>()];
        string mark = marks.Length > 0 ? $" [{string.Join(", ", marks)}]" : string.Empty;
        string upstream = r.Branch is { Kind: RefKind.Local, Upstream: { } tracked } ? $" {tracked}" : string.Empty;
        return $"{new string(' ', r.Depth * 2)}{r.Title}{mark}{upstream}";
    })];

    private string[] LocalBranches() => Git("for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private bool Ancestor(string ancestor, string of)
    {
        Passenger.Core.ChildResult result = Task.Run(() => Passenger.Core.ChildProcesses.RunAsync(
            Passenger.Core.ChildProcesses.Command("git", ["merge-base", "--is-ancestor", ancestor, of], folder), limit: TimeSpan.FromMinutes(1))).GetAwaiter().GetResult();
        return result.Succeeded;
    }

    private void Commit(string name, string text, string message)
    {
        File.WriteAllText(Path.Combine(folder, name), text);
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    private string Git(params string[] arguments) => TestGit.Run(folder, arguments);
}

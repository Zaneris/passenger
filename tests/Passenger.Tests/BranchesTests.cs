using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
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

    /// <summary>The gate's Branches panel as one flow: head, local branches by directory, the remotes' branches and the
    /// tags listed in order, Enter filtering the log to a branch's commits, the keys renaming, making a branch from and
    /// deleting the chosen branch, and Checkout, New Branch From, Rename, Delete, Update and Push doing what they say,
    /// each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_branches_panel_lists_and_filters_the_branches_and_makes_renames_and_deletes_them()
    {
        Flow.StepApart<BranchesTests>("the panel lists head local branches by directory the remotes branches and the tags in riders order", t => t.The_panel_lists_head_local_branches_by_directory_the_remotes_branches_and_the_tags_in_riders_order());
        Flow.StepApart<BranchesTests>("enter on a branch filters the log to its commits and the filters x shows every branch again", t => t.Enter_on_a_branch_filters_the_log_to_its_commits_and_the_filters_x_shows_every_branch_again());
        Flow.StepApart<BranchesTests>("riders keys rename make a branch from and delete the chosen branch", t => t.Riders_keys_rename_make_a_branch_from_and_delete_the_chosen_branch());
        Flow.StepApart<BranchesTests>("checkout new branch from rename delete update and push do what they say", t => t.Checkout_new_branch_from_rename_delete_update_and_push_do_what_they_say());
    }

    /// <summary>The gate's merges, rebases and comparisons of branches as one flow: Merge Into, Rebase Onto and
    /// Checkout and Rebase moving the branches as git would, Compare with Current listing each branch's commits the
    /// other lacks, a merge from the panel that conflicts opening the conflicts dialog, and a branch in the toolbar's
    /// menu checked out only by its Checkout, each step the gate test it names in an instance of its own.</summary>
    [Gate]
    [AvaloniaFact]
    public void Branches_merge_rebase_and_compare_as_git_would_and_check_out_only_when_asked()
    {
        Flow.StepApart<BranchesTests>("merge into rebase onto and checkout and rebase onto current move the branches as git would", t => t.Merge_into_rebase_onto_and_checkout_and_rebase_onto_current_move_the_branches_as_git_would());
        Flow.StepApart<BranchesTests>("compare with current lists each branchs commits the other lacks and show diff with working tree its files", t => t.Compare_with_current_lists_each_branchs_commits_the_other_lacks_and_show_diff_with_working_tree_its_files());
        Flow.StepApart<BranchesTests>("a merge from the panel that conflicts opens the conflicts dialog", t => t.A_merge_from_the_panel_that_conflicts_opens_the_conflicts_dialog());
        Flow.StepApart<BranchesTests>("a branch in the toolbars branch menu opens its own menu and is checked out only by its checkout", t => t.A_branch_in_the_toolbars_branch_menu_opens_its_own_menu_and_is_checked_out_only_by_its_checkout());
    }

    /// <summary>The Branches panel beyond the gate's as one flow, each step the check one test made in an instance of
    /// its own: each side button greyed with why where the chosen row gives it nothing to act on, Show Diff with
    /// Working Tree listing its files and opening their diffs, an added row's diff showing the file beside the branch's
    /// empty side, the branch menu over 5,000 branches costing the window what 30 do, a checkout with uncommitted
    /// changes asking what becomes of them, and the branch menu listing and searching the remotes' branches.</summary>
    [AvaloniaFact]
    public void A_branchs_diff_with_the_working_tree_opens_its_files_and_the_panel_says_what_it_cannot_do()
    {
        Flow.StepApart<BranchesTests>("each side button is greyed with why where the chosen row gives it nothing to act on", t => t.Each_side_button_is_greyed_with_why_where_the_chosen_row_gives_it_nothing_to_act_on());
        Flow.StepApart<BranchesTests>("show diff with working tree lists the files in the Git window and opens their diffs in a tab or a window", t => t.Show_diff_with_working_tree_lists_the_files_in_the_Git_window_and_opens_their_diffs_in_a_tab_or_a_window());
        Flow.StepApart<BranchesTests>("show diff on an added row of the working tree comparison shows the file beside the branchs empty side", t => t.Show_diff_on_an_added_row_of_the_working_tree_comparison_shows_the_file_beside_the_branchs_empty_side());
        Flow.StepApart<BranchesTests>("the branch menu over 5000 branches costs the window what 30 do and more branches searches them all", t => t.The_branch_menu_over_5000_branches_costs_the_window_what_30_do_and_more_branches_searches_them_all());
        Flow.StepApart<BranchesTests>("get from a branch takes its version of a changed file one it lacks and one only it has each one undo and a closed files diff with it is edited in place", t => t.Get_from_a_branch_takes_its_version_of_a_changed_file_one_it_lacks_and_one_only_it_has_each_one_undo_and_a_closed_files_diff_with_it_is_edited_in_place());
        Flow.StepApart<BranchesTests>("a checkout with uncommitted changes asks to bring stash or discard them saying where git cannot bring them and unstashes on return", t => t.A_checkout_with_uncommitted_changes_asks_to_bring_stash_or_discard_them_saying_where_git_cannot_bring_them_and_unstashes_on_return());
        Flow.StepApart<BranchesTests>("the branch menu lists each remotes branches searches them with the locals and checks one out tracking it", t => t.The_branch_menu_lists_each_remotes_branches_searches_them_with_the_locals_and_checks_one_out_tracking_it());
    }

    /// <summary>The toolbar's branch menu lists a local branch with what it tracks, and each remote's branches in a menu of its
    /// own, one no local branch tracks among them; letters typed search the locals and the remotes' together, saying what
    /// they found at the top, Backspace taking one back and Escape all of them before the menu closes; and a remote's branch
    /// chosen from the search checks out as a new local branch tracking it.</summary>
    private void The_branch_menu_lists_each_remotes_branches_searches_them_with_the_locals_and_checks_one_out_tracking_it()
    {
        Git("push", "-q", "origin", "team/login:refs/heads/review");
        MainWindow window = Open();
        ContextMenu menu = OpenBranchWidget(window);
        string[] listed = Said(menu);
        string[] branches = [.. listed.SkipWhile(i => i != "Local")];
        Assert.True(branches.Length == 6 && branches[1] == "main [origin/main]" && branches[4..] is ["Remote", "origin"]
            && branches[2..4].Order(StringComparer.Ordinal).SequenceEqual(["feature [origin/feature]", "team/login"]),
            $"The branch menu lists [{string.Join(", ", listed)}]");

        MenuItem origin = Reach(window, menu.Items.OfType<MenuItem>(), "origin");
        Ui.Press(window, "Enter");
        Assert.True(origin.IsSubMenuOpen, "Choosing origin in the branch menu opened no menu of its branches");
        Assert.Equal(["feature", "main", "review"], origin.Items.OfType<MenuItem>().Select(MenuChoice.NameOf).Order(StringComparer.Ordinal));
        Ui.Press(window, "Left");

        window.KeyTextInput("rev");
        Ui.Settle();
        Assert.Equal(["“rev”: 1 match", "Remote", "origin/review"], Said(menu));
        Ui.Press(window, "BACK");
        Assert.True(Said(menu) is ["“re”: 3 matches", "Local", "feature [origin/feature]", "Remote", _, _] found && found[4..].Order(StringComparer.Ordinal).SequenceEqual(["origin/feature", "origin/review"]),
            $"With re typed the branch menu lists [{string.Join(", ", Said(menu))}]");
        Ui.Press(window, "ESCAPE");
        Assert.True(menu.IsOpen && Said(menu).SequenceEqual(listed), $"Escape over a search left the menu {(menu.IsOpen ? "listing " + string.Join(", ", Said(menu)) : "closed")}");

        window.KeyTextInput("rev");
        Ui.Settle();
        MenuItem review = Reach(window, menu.Items.OfType<MenuItem>(), "origin/review");
        Ui.Press(window, "Enter");
        Reach(window, review.Items.OfType<MenuItem>(), "Checkout");
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => window.Vcs.Branch == "review", () => $"origin/review checked out as review: {window.Status}");
        Assert.Equal("origin/review", Git("rev-parse", "--abbrev-ref", "review@{upstream}").Trim());
        Assert.Equal("Checked out new branch review from origin/review", window.Status);
        window.Close();

        // Each item as the menu shows it: its name, and after it in brackets what a branch tracks.
        static string[] Said(ContextMenu menu) => [.. menu.Items.OfType<MenuItem>().Select(i =>
            $"{MenuChoice.NameOf(i)}{(Avalonia.Automation.AutomationProperties.GetHelpText(i) is { Length: > 0 } tracks ? $" [{tracks}]" : string.Empty)}")];
    }

    /// <summary>Checkout with changes not yet committed asks what becomes of them, their files listed: Bring takes them
    /// along as git does; Stash puts them away, new files too, and checking that branch out again unstashes them, as
    /// its box asked; where the branch checked out changes a file they change too, Bring is greyed saying why and Stash is
    /// Enter's, and Discard… rolls them back once the person agrees; with Don't ask again ticked, the way chosen is
    /// Settings' from then on and nothing is asked; and a checkout with no change asks nothing.</summary>
    private void A_checkout_with_uncommitted_changes_asks_to_bring_stash_or_discard_them_saying_where_git_cannot_bring_them_and_unstashes_on_return()
    {
        string shop = Path.Combine(folder, "Shop.cs");
        string longer = Shop.Replace("int count", "long count", StringComparison.Ordinal);
        MainWindow window = Open(seen: true);
        List<string> confirmed = [];
        window.Confirm = (title, text, yes) =>
        {
            confirmed.Add($"{title}: {text}");
            return Task.FromResult(true);
        };
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);
        try
        {
            // Bring: Shop.cs's change goes along to feature, which leaves Shop.cs as main has it.
            File.WriteAllText(shop, longer);
            Ui.WaitFor(() => window.Vcs.Changes.Count == 1, "Shop.cs changed, Git listing it");
            ChoiceDialog question = Asked(window, panel, rows, "feature");
            Assert.Equal([MainWindow.BringChanges, MainWindow.StashChanges, MainWindow.DiscardUncommitted, "Cancel"], Answers(question).Select(b => (string)b.Content!));
            Assert.True(question.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Don't ask again").Bounds.Width > 0, "The question's Don't ask again has no room beside its answers");
            Assert.True(Answers(question)[0] is { IsEnabled: true, IsDefault: true }, "Bring, which git can do here, is not Enter's");
            Assert.Contains(question.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Your changes to Shop.cs are not committed. Bring them to feature, stash them, or discard them?");
            Ui.ClickButton(question, MainWindow.BringChanges);
            Ui.Landed(() => window.Vcs.Branch == "feature", "the checkout of feature, Shop.cs's change brought along");
            Assert.Equal(longer, File.ReadAllText(shop));

            // Stash, its box ticked: main checked out with Shop.cs as committed, and feature again brings the change back.
            question = Asked(window, panel, rows, "main");
            CheckBox back = question.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Unstash them as feature is checked out again");
            Assert.True(back.IsChecked, "The box unstashing a stash on returning to feature was not ticked to begin with");
            Ui.ClickButton(question, MainWindow.StashChanges);
            Ui.Landed(() => window.Vcs.Branch == "main", "the checkout of main, Shop.cs's change stashed");
            Assert.Equal(Shop, File.ReadAllText(shop));
            Assert.Equal([$"On feature: {VcsController.StashedFor("main")}"], StashList());
            Choose(panel, rows, "feature", "Checkout");
            Ui.WaitFor(() => window.Vcs.Branch == "feature" && File.ReadAllText(shop) == longer, () => $"feature checked out again and its stashed change unstashed: {window.Status}");
            Assert.Null(window.ChangesQuestion);
            Assert.Empty(StashList());
            Assert.Equal("Checked out feature, and the changes left on it were unstashed", window.Status);

            // Feature.cs changed too, which main has none of: git cannot bring the changes, so Bring is greyed and Stash takes Enter.
            File.WriteAllText(Path.Combine(folder, "Feature.cs"), "class Feature { int more; }\n");
            Ui.WaitFor(() => window.Vcs.Changes.Count == 2, "Feature.cs changed too, Git listing both");
            question = Asked(window, panel, rows, "main");
            Button bring = Answers(question)[0];
            Assert.False(bring.IsEnabled, "Bring is offered where git would refuse to bring Feature.cs's change to main");
            Assert.Equal("Git cannot bring them: main changes Feature.cs too, which checking it out would write over", ToolTip.GetTip(bring));
            Assert.True(Answers(question)[1].IsDefault, "Stash is not Enter's where Bring cannot be had");
            Ui.ClickButton(question, MainWindow.DiscardUncommitted);
            Ui.WaitFor(() => window.Vcs.Branch == "main", () => $"main checked out once the changes were discarded: {window.Status}");
            Assert.Equal(["Discard Changes: Discard the changes to these 2 files and check out main? This cannot be undone."], confirmed);
            Assert.True(File.ReadAllText(shop) == Shop && !File.Exists(Path.Combine(folder, "Feature.cs")) && StashList().Length == 0,
                "Discard… left a change, Feature.cs, or a stash behind");

            // Don't ask again with Bring: kept in Settings, the next checkout with changes brings them, asking nothing.
            File.WriteAllText(shop, longer);
            Ui.WaitFor(() => window.Vcs.Changes.Count == 1, "Shop.cs changed again");
            question = Asked(window, panel, rows, "team/login");
            question.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Don't ask again").IsChecked = true;
            Ui.ClickButton(question, MainWindow.BringChanges);
            Ui.Landed(() => window.Vcs.Branch == "team/login", "the checkout of team/login");
            Assert.Equal(Passenger.App.Settings.CheckoutChanges.Bring, window.Settings.ChangesAtCheckout);
            Choose(panel, rows, "main", "Checkout");
            Ui.Landed(() => window.Vcs.Branch == "main", "main checked out, Shop.cs's change brought along without a question");
            Assert.Null(window.ChangesQuestion);
            Assert.Equal(longer, File.ReadAllText(shop));
        }
        finally
        {
            window.ApplySettings(window.Settings with { ChangesAtCheckout = Passenger.App.Settings.CheckoutChanges.Ask });
            window.Close();
        }

        static ChoiceDialog Asked(MainWindow window, BranchesPanel panel, ListBox rows, string branch)
        {
            Assert.True(panel.Select(branch), $"The branches panel has no {branch}");
            Ui.ChooseFromMenu(rows, "Checkout");
            Ui.PumpUntil(() => window.ChangesQuestion is { IsVisible: true } || window.Vcs.Branch == branch,
                $"the question of the uncommitted changes at {branch}'s checkout");
            Assert.True(window.ChangesQuestion is not null, $"{branch} was checked out without a question of the uncommitted changes: {window.Status}");
            return DialogButtonsTests.Check(window.ChangesQuestion!);
        }

        static Button[] Answers(ChoiceDialog question) =>
            [.. question.GetVisualDescendants().OfType<Button>().Where(b => b is not Avalonia.Controls.Primitives.ToggleButton && b.Content is string && b.IsEffectivelyVisible)
                .OrderBy(b => (string)b.Content! == "Cancel" ? 1 : 0).ThenBy(b => Array.IndexOf<string>([MainWindow.BringChanges, MainWindow.StashChanges, MainWindow.DiscardUncommitted], (string)b.Content!))];
    }

    private string[] StashList() => Git("stash", "list", "--format=%gs").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private void The_panel_lists_head_local_branches_by_directory_the_remotes_branches_and_the_tags_in_riders_order()
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
                "Worktrees",
            ], Listed(panel));

        // The current branch's mark, HEAD's here, reads as a mark on the chosen row's ground, where a person looks for it.
        Assert.True(panel.Select("HEAD"));
        Ui.Settle();
        TreeList tree = panel.GetVisualDescendants().OfType<TreeList>().Single();
        RowIcon head = (tree.ContainerFromItem(panel.Selected!) ?? throw new Xunit.Sdk.XunitException("The panel shows no row for HEAD"))
            .GetVisualDescendants().OfType<RowIcon>().Single(i => i.DataKey == IconKeys.Branch);
        uint mark = Colour(head, head.ForegroundKey ?? TokenKeys.Icon);
        uint chosen = ThemeColours.Over(Colour(panel, TokenKeys.RowSelected), Colour(panel, TokenKeys.Panel));
        Assert.True(ThemeColours.Ratio(mark, chosen) >= 3,
            $"The current branch's mark ({head.ForegroundKey}, {mark:X8}) reads at {ThemeColours.Ratio(mark, chosen):0.00} to 1 on the chosen row's ground, under the 3 a mark needs: the checked-out branch is the one a person cannot find");

        panel.Toggle(panel.Rows.Single(r => r.Title == "Tags"));
        Assert.Equal(["v1.0", "v2.0"], panel.Rows.SkipWhile(r => r.Title != "Tags").Skip(1).TakeWhile(r => r.Title != "Worktrees").Select(r => r.Title));
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

    private void Enter_on_a_branch_filters_the_log_to_its_commits_and_the_filters_x_shows_every_branch_again()
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

    private void Riders_keys_rename_make_a_branch_from_and_delete_the_chosen_branch()
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

    private void Checkout_new_branch_from_rename_delete_update_and_push_do_what_they_say()
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
        TestGit.Run(clone, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        File.WriteAllText(Path.Combine(clone, "Feature.cs"), "class Feature { int more; }\n");
        TestGit.Run(clone, "commit", "-q", "-am", "More");
        TestGit.Run(clone, "push", "-q", "origin", "feature");
        Git("fetch", "-q", "origin");
        Choose(panel, rows, "feature", "Update");
        Assert.Equal(Git("rev-parse", "origin/feature").Trim(), Git("rev-parse", "feature").Trim());

        Choose(panel, rows, "hotfix", "Push…");
        Ui.PumpUntil(() => window.PushWindow is { IsVisible: true }, "the Push dialog for hotfix to show");
        Ui.ClickButton(window.PushWindow!, "Push");
        Ui.Landed(() => window.Status == "Pushed hotfix to origin/hotfix, which it tracks from now on", "the push of hotfix");
        Assert.Equal(Git("rev-parse", "hotfix").Trim(), TestGit.Run(origin, "rev-parse", "hotfix").Trim());
        Assert.Equal("origin/hotfix", Git("rev-parse", "--abbrev-ref", "hotfix@{upstream}").Trim());
        window.Close();
    }

    /// <summary>The panel's side buttons act only where they can, each greyed elsewhere with a tip saying why after its name:
    /// Update Selected on a local branch tracking a remote's (which it updates), never a remote's branch (Fetch's), a tag,
    /// HEAD or a group; Delete on any branch or tag but the one checked out; New Branch… and Navigate on any, Navigate in a
    /// log filtered to a branch lacking the head too; Mark/Unmark As Favorite on a branch. The menu and Delete key agree.</summary>
    private void Each_side_button_is_greyed_with_why_where_the_chosen_row_gives_it_nothing_to_act_on()
    {
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);
        string[] nothing = [$"New Branch…: {Refusals.NoRefChosen.Shown}", $"Update Selected: {Refusals.NoLocalBranchChosen.Shown}", $"Delete: {Refusals.NoRefChosen.Shown}",
            $"Mark/Unmark As Favorite: {Refusals.NoBranchChosen.Shown}", $"Navigate Log to Selected Branch Head: {Refusals.NoRefChosen.Shown}"];
        string[] all = ["New Branch…", "Update Selected", "Delete", "Mark/Unmark As Favorite", "Navigate Log to Selected Branch Head"];
        Assert.Null(panel.Selected);
        Assert.Equal(nothing, Tools(panel));
        panel.Toggle(panel.Rows.Single(r => r.Key == "tags"));
        Ui.Settle();
        foreach ((string key, string[] tools) in (ReadOnlySpan<(string, string[])>)[
            ("head", nothing),
            ("local", nothing),
            ("refs/heads/main", [all[0], all[1], $"Delete: {Refusals.CheckedOut("main").Shown}", all[3], all[4]]),
            ("local/team/", nothing),
            ("refs/heads/team/login", [all[0], $"Update Selected: {Refusals.Untracked("team/login").Shown}", all[2], all[3], all[4]]),
            ("refs/heads/feature", all),
            ("remote", nothing),
            ("remote/origin", nothing),
            ("refs/remotes/origin/main", [all[0], $"Update Selected: {Refusals.FetchedNotUpdated("origin/main").Shown}", all[2], all[3], all[4]]),
            ("tags", nothing),
            ("refs/tags/v1.0", [all[0], $"Update Selected: {Refusals.NoLocalBranchChosen.Shown}", all[2], $"Mark/Unmark As Favorite: {Refusals.NoBranchChosen.Shown}", all[4]])])
        {
            BranchNode row = panel.Rows.Single(r => r.Key == key);
            Ui.Click(Ui.Row(rows, row));
            Assert.Same(row, panel.Selected);
            Assert.Equal(tools, Tools(panel));
            if (row.Branch is not { } branch)
            {
                continue;
            }
            // The row's menu offers what the buttons do where they do it, its Update greyed with the same why.
            MenuItem[] menu = MenuOf(rows);
            Assert.Equal(SideButton(panel, "New Branch…").IsEnabled, menu.Any(i => i.Header as string == $"New Branch from '{branch.Name}'…"));
            Assert.Equal(SideButton(panel, "Delete").IsEnabled, menu.Any(i => i.Header as string == "Delete"));
            if (menu.FirstOrDefault(i => i.Header as string == "Update") is { } update)
            {
                Assert.Equal(SideButton(panel, "Update Selected").IsEnabled, update.IsEnabled);
                Assert.Equal(update.IsEnabled ? null : Refusals.Untracked(branch.Name).Shown, ToolTip.GetTip(update));
            }
            else
            {
                Assert.True(branch.Kind != RefKind.Local, $"{branch.Name}'s menu has no Update");
            }
        }

        // Navigate keeps a branch filter holding the head, and shows every branch again where the filter's history lacks it.
        GitLogView log = window.GitLog;
        Assert.True(panel.Select("feature"));
        panel.Activate(panel.Selected!);
        Assert.True(panel.Select("main"));
        Ui.Click(SideButton(panel, "Navigate Log to Selected Branch Head"));
        Ui.Landed(() => log.Selected?.Hash == Git("rev-parse", "main").Trim(), "main's head chosen in the log filtered to feature");
        Assert.Equal("refs/heads/feature", log.BranchFilter);
        panel.Activate(panel.Selected!);
        Assert.True(panel.Select("feature"));
        Ui.Click(SideButton(panel, "Navigate Log to Selected Branch Head"));
        Ui.Landed(() => log.Selected?.Hash == Git("rev-parse", "feature").Trim(), "feature's head chosen in the log once filtered to main");
        Assert.Null(log.BranchFilter);

        // The Delete key leaves the branch checked out, as the greyed Delete does.
        window.Confirm = (_, _, _) => throw new InvalidOperationException("Delete on the branch checked out asked to delete it");
        Assert.True(panel.Select("main"));
        panel.FocusRows();
        Ui.Press(window, "Delete");
        Assert.Contains("main", LocalBranches());

        // Update Selected on feature brings it up to the commit pushed to origin's since, fetched by the update itself.
        string clone = TestFolders.Create("branches-clone-");
        TestGit.Run(clone, "clone", "-q", "-b", "feature", origin, ".");
        TestGit.Run(clone, "config", GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        File.WriteAllText(Path.Combine(clone, "Feature.cs"), "class Feature { int more; }\n");
        TestGit.Run(clone, "commit", "-q", "-am", "More");
        TestGit.Run(clone, "push", "-q", "origin", "feature");
        string pushed = TestGit.Run(clone, "rev-parse", "HEAD").Trim();
        TestFolders.Delete(clone);
        Assert.True(panel.Select("feature"));
        Ui.Click(SideButton(panel, "Update Selected"));
        Ui.Landed(() => Git("rev-parse", "feature").Trim() == pushed, "feature updated to the commit pushed to origin");
        Assert.Equal("Updated feature", window.Status);
        window.Close();
    }

    private void Merge_into_rebase_onto_and_checkout_and_rebase_onto_current_move_the_branches_as_git_would()
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

    private void Compare_with_current_lists_each_branchs_commits_the_other_lacks_and_show_diff_with_working_tree_its_files()
    {
        Commit("Notes.txt", "notes\n", "Notes on main");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop.Replace("int count", "long count", StringComparison.Ordinal));
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);

        Choose(panel, rows, "feature", "Compare with 'main'");
        Assert.Equal(["Log", "Compare main and feature"], window.GitWindow.Titles);
        GitLogView feature = Assert.IsType<GitLogView>(window.GitWindow.Shown);
        Ui.Settle();
        Assert.Equal(["Feature"], feature.Rows.Select(r => r.Commit.Subject));
        GitLogView main = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.GitWindow).OfType<GitLogView>().Single(v => v.Range?.To == "main");
        Assert.Equal(["Notes on main"], main.Rows.Select(r => r.Commit.Subject));

        // The branches are the Log's, where the person goes back to them.
        window.GitWindow.ShowLog();
        Ui.Settle();

        Choose(panel, rows, "feature", "Show Diff with Working Tree");
        Assert.Equal(["Log", "Compare main and feature", "Compare feature and Working Tree"], window.GitWindow.Titles);
        WorkingTreeDiffView changes = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
        Assert.Equal(["Feature.cs Deleted", "Notes.txt Added", "Shop.cs Modified"], changes.Files.Select(f => $"{f.Name} {f.Kind}").Order());
        Ui.Click(Ui.Row(changes.List, changes.Files.Single(f => f.Name == "Shop.cs")));
        Ui.Press(window, "ENTER");
        IDiffHost diff = OpenDiffs.Landed(window, "Shop.cs as feature has it beside the file");
        Assert.Equal(Shop, OpenDiffs.ViewOf(diff).Left.Document.Snapshot.ToString());
        diff.Close();
        window.Close();
    }

    /// <summary>Show Diff with Working Tree lists a branch's files that differ from the working tree in a tab of the Git
    /// window, said above them, the first chosen with the keys; a file's diff opens beside the editors' files as a tab with
    /// diffs set to open as tabs, and over them in a window of its own with diffs set to open in windows, the list staying in
    /// the Git window either way. The list follows the working tree, and a branch nothing differs from says so; a file git
    /// does not track is listed as added, or as modified where the branch has it.</summary>
    private void Show_diff_with_working_tree_lists_the_files_in_the_Git_window_and_opens_their_diffs_in_a_tab_or_a_window()
    {
        Commit("Notes.txt", "notes\n", "Notes on main");
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop.Replace("int count", "long count", StringComparison.Ordinal));
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        ListBox rows = List(panel);

        Choose(panel, rows, "feature", "Show Diff with Working Tree");
        Assert.Equal(["Log", "Compare feature and Working Tree"], window.GitWindow.Titles);
        WorkingTreeDiffView changes = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
        Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.GitWindow).OfType<TextBlock>(),
            t => t.Text == "Files that differ between feature and the working tree");
        Assert.Equal(["Feature.cs Deleted", "Notes.txt Added", "Shop.cs Modified"], changes.Files.Select(f => $"{f.Name} {f.Kind}").Order());
        Assert.True(changes.List.SelectedIndex == 0 && changes.List.IsKeyboardFocusWithin,
            $"The list opened with row {changes.List.SelectedIndex} chosen and the keys {(changes.List.IsKeyboardFocusWithin ? "in" : "out of")} it, not on its first file");

        // Diffs set to open as tabs: Shop.cs's opens beside the files, the Git window still listing the branch's.
        window.ApplySettings(window.Settings with { OpenDiffsIn = Passenger.App.Settings.DiffPlace.Tab });
        Ui.Click(Ui.Row(changes.List, changes.Files.Single(f => f.Name == "Shop.cs")));
        Ui.Press(window, "ENTER");
        DiffTab tab = Assert.IsType<DiffTab>(OpenDiffs.Landed(window, "Shop.cs as feature has it, in a tab beside the file"));
        Assert.Equal("Shop.cs: feature and your version", tab.Document.Title);
        Assert.Same(tab.Document, window.EditorArea.Active);
        Assert.Equal(Shop, OpenDiffs.ViewOf(tab).Left.Document.Snapshot.ToString());
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Git) && window.GitWindow.Shown == changes && changes.IsVisible,
            "The diff's tab opened, the Git window no longer showed the branch's files");
        tab.Close();
        Ui.Settle();

        // Diffs set to open in windows: Shop.cs's opens in a window of its own over the IDE's, the list staying.
        window.ApplySettings(window.Settings with { OpenDiffsIn = Passenger.App.Settings.DiffPlace.Window });
        Ui.Click(Ui.Row(changes.List, changes.Files.Single(f => f.Name == "Shop.cs")));
        Ui.Press(window, "ENTER");
        DiffWindow shown = Assert.IsType<DiffWindow>(OpenDiffs.Landed(window, "Shop.cs as feature has it, in a window of its own"));
        Assert.Same(shown, window.Frontmost);
        Assert.Equal(Shop, shown.View.Left.Document.Snapshot.ToString());
        Assert.True(window.GitWindow.Shown == changes, "The diff's window opened, the Git window no longer showed the branch's files");
        shown.Close();
        Ui.Settle();

        // Shop.cs put back as committed: the list follows, and main, which the working tree now matches, has nothing listed.
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Shop);
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0, "Shop.cs put back as committed, Git listing nothing changed");
        Ui.Landed(() => changes.Files.Count == 2, $"feature's files following the working tree, where it lists [{string.Join(", ", changes.Files.Select(f => f.Name))}]");
        Assert.Equal(["Feature.cs Deleted", "Notes.txt Added"], changes.Files.Select(f => $"{f.Name} {f.Kind}").Order());
        window.GitWindow.ShowLog();
        Ui.Settle();
        Choose(panel, rows, "main", "Show Diff with Working Tree");
        WorkingTreeDiffView same = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
        Assert.Equal(["Log", "Compare feature and Working Tree", "Compare main and Working Tree"], window.GitWindow.Titles);
        Assert.True(same.Files.Count == 0 && same.Placeholder == "No differences",
            $"main, which the working tree matches, lists [{string.Join(", ", same.Files.Select(f => f.Name))}] and says \"{same.Placeholder}\"");

        // Files git does not track differ too: from main, which has neither, as added; from feature, which has Feature.cs, it as modified.
        File.WriteAllText(Path.Combine(folder, "Added.cs"), "class Added { }\n");
        File.WriteAllText(Path.Combine(folder, "Feature.cs"), "class Feature { int count; }\n");
        Ui.WaitFor(() => window.Vcs.Changes.Count == 2, "Added.cs and Feature.cs written, Git listing them untracked");
        Ui.Landed(() => same.Files.Count == 2, $"main's files following the working tree's untracked files, where it lists [{string.Join(", ", same.Files.Select(f => f.Name))}]");
        Assert.Equal(["Added.cs Added", "Feature.cs Added"], same.Files.Select(f => $"{f.Name} {f.Kind}"));
        window.GitWindow.ShowLog();
        Ui.Settle();
        Choose(panel, rows, "feature", "Show Diff with Working Tree");
        Assert.Same(changes, window.GitWindow.Shown);
        Ui.Landed(() => changes.Files.Count == 3, $"feature's files read again with the untracked files, where it lists [{string.Join(", ", changes.Files.Select(f => f.Name))}]");
        Assert.Equal(["Added.cs Added", "Feature.cs Modified", "Notes.txt Added"], changes.Files.Select(f => $"{f.Name} {f.Kind}"));
        window.Close();
    }

    /// <summary>Show Diff on an added row of the working tree's comparison with a branch, a file the branch has none of,
    /// shows the file beside an empty side named for the branch: one committed since the branch (Notes.txt) and one git
    /// does not track (Added.cs), each all added.</summary>
    private void Show_diff_on_an_added_row_of_the_working_tree_comparison_shows_the_file_beside_the_branchs_empty_side()
    {
        Commit("Notes.txt", "notes\n", "Notes on main");
        File.WriteAllText(Path.Combine(folder, "Added.cs"), "class Added { }\n");
        MainWindow window = Open();
        BranchesPanel panel = window.GitBranches;
        Choose(panel, List(panel), "feature", "Show Diff with Working Tree");
        WorkingTreeDiffView changes = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
        Ui.Landed(() => changes.Files.Any(f => f.Name == "Added.cs"), $"feature's comparison to list the untracked Added.cs, where it lists [{string.Join(", ", changes.Files.Select(f => f.Name))}]");
        foreach ((string name, string text) in (ReadOnlySpan<(string, string)>)[("Notes.txt", "notes\n"), ("Added.cs", "class Added { }\n")])
        {
            Assert.Equal(Passenger.Vcs.ChangeKind.Added, changes.Files.Single(f => f.Name == name).Kind);
            Ui.Click(Ui.Row(changes.List, changes.Files.Single(f => f.Name == name)));
            Ui.Press(window, "ENTER");
            IDiffHost diff = OpenDiffs.Landed(window, $"{name}'s diff with feature, which has none of it");
            DiffView view = OpenDiffs.ViewOf(diff);
            Assert.True(view.Left.Document.Snapshot.Length == 0 && view.Right.Document.Snapshot.ToString() == text,
                $"{name}'s diff with feature showed [{view.Left.Document.Snapshot}] beside [{view.Right.Document.Snapshot}], not nothing beside the file; the status bar says \"{window.Status}\"");
            Assert.Equal($"{name}: feature and your version", OpenDiffs.TitleOf(diff));
            diff.Close();
            Ui.Settle();
        }
        window.Close();
    }

    private void A_merge_from_the_panel_that_conflicts_opens_the_conflicts_dialog()
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

    private void A_branch_in_the_toolbars_branch_menu_opens_its_own_menu_and_is_checked_out_only_by_its_checkout()
    {
        MainWindow window = Open();

        ContextMenu menu = OpenBranchWidget(window);
        MenuItem feature = Reach(window, menu.Items.OfType<MenuItem>(), "feature");
        Ui.Press(window, "Enter");
        Assert.True(window.Vcs.Branch == "main", $"Choosing feature in the branch menu checked out {window.Vcs.Branch}, where Rider's opens the branch's own menu");
        Assert.True(feature.IsSubMenuOpen, "Choosing feature in the branch menu opened no menu of feature's");
        Assert.Equal(
            [
                "Checkout", "New Branch from 'feature'…", "New Tag at 'feature'…", "New Worktree from 'feature'…", "Checkout and Rebase onto 'main'", "Compare with 'main'", "Show Diff with Working Tree",
                "Rebase 'main' onto 'feature'", "Merge 'feature' into 'main'", "Update", "Push…", "Push to…", "Rename…", "Delete",
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

    /// <summary>The toolbar's branch menu lists the current branch, the favourites and those committed to last, ten at
    /// most, with More Branches… for the rest, and makes a branch's own menu only as it opens: over 5,000 local branches it
    /// costs the window's thread what 30 do, and More Branches… gives the keys to the Git window's branch search.</summary>
    private void The_branch_menu_over_5000_branches_costs_the_window_what_30_do_and_more_branches_searches_them_all()
    {
        string few = Repository(30);
        string many = Repository(5_000);
        try
        {
            MainWindow window = Ui.OpenFolder(few, width: 1300, height: 900, languageServer: false);
            (long fewCost, int fewMade) = Measured(window);
            window.Close();
            window = Ui.OpenFolder(many, width: 1300, height: 900, languageServer: false);
            (long manyCost, int manyMade) = Measured(window);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"The branch menu allocated {fewCost >> 10} KB on the window's thread and made {fewMade} menu items over 30 branches, {manyCost >> 10} KB and {manyMade} over 5,000");
            Assert.True(manyMade <= fewMade,
                $"The branch menu made {manyMade} menu items over 5,000 local branches, where over 30 it made {fewMade}: it makes items for branches it does not list");
            Assert.True(manyCost < fewCost * 3 / 2,
                $"Opening and closing the branch menu allocated {manyCost >> 10} KB on the window's thread over 5,000 local branches, {fewCost >> 10} KB over 30: the window pays for every branch");

            ContextMenu menu = OpenBranchWidget(window);
            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            MenuItem[] branches = [.. items.SkipWhile(i => i.Header as string != "Local").Skip(1).SkipLast(1)];
            Assert.Equal(
                ["main", "recent-12", "recent-11", "recent-10", "recent-09", "recent-08", "recent-07", "recent-06", "recent-05", "recent-04"],
                branches.Select(MenuChoice.NameOf));
            Assert.True(branches[0].IsChecked, "main, the current branch, is not checked in the branch menu");
            Assert.Equal("More Branches…", items[^1].Header);
            MenuItem[] made = [.. branches.Where(b => b.Items.OfType<MenuItem>().Any())];
            Assert.True(made.Length == 0, $"The branch menu made the menus of [{string.Join(", ", made.Select(b => b.Header))}] before any was opened");

            MenuItem recent = Reach(window, items, "recent-12");
            Ui.Press(window, "Enter");
            Assert.True(recent.IsSubMenuOpen, "Choosing recent-12 in the branch menu opened no menu of its own");
            MenuItem[] own = [.. recent.Items.OfType<MenuItem>()];
            Assert.True(own.Any(i => i.Header as string == "Checkout") && own.Any(i => i.Header as string == "Merge 'recent-12' into 'main'"),
                $"recent-12's menu, opened, holds [{string.Join(", ", own.Select(i => i.Header))}] rather than its Checkout and Merge");
            Ui.Press(window, "Left");
            Reach(window, items, "More Branches…");
            Ui.Press(window, "Enter");
            Ui.Settle();
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Git), "More Branches… did not show the Git window");
            window.KeyTextInput("b4321");
            Ui.Settle();
            Assert.True(window.GitBranches.SearchText == "b4321" && window.GitBranches.Rows.Any(r => r.Title == "b4321"),
                $"After More Branches… and b4321 typed the branch search holds '{window.GitBranches.SearchText}' and the panel lists [{string.Join(", ", Listed(window.GitBranches).Take(8))}]: the keys never reached the search");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(few);
            TestFolders.Delete(many);
        }

        // The menu opened and closed once to warm it, then again measured: what the window's thread allocated, and how
        // many menu items the menu made.
        static (long Cost, int Made) Measured(MainWindow window)
        {
            OpenBranchWidget(window);
            Ui.Press(window, "Escape");
            Ui.Settle();
            long before = GC.GetAllocatedBytesForCurrentThread();
            ContextMenu menu = OpenBranchWidget(window);
            long opened = GC.GetAllocatedBytesForCurrentThread();
            int made = menu.GetLogicalDescendants().OfType<MenuItem>().Count();
            long closing = GC.GetAllocatedBytesForCurrentThread();
            Ui.Press(window, "Escape");
            Ui.Settle();
            Assert.False(menu.IsOpen, "Escape left the branch menu open");
            return (opened - before + GC.GetAllocatedBytesForCurrentThread() - closing, made);
        }
    }

    /// <summary>A repository of so many local branches: main, checked out; recent-01 to recent-12, each committed to a
    /// minute after the one before and later than main; and the rest at main's commit, under bulk/.</summary>
    private static string Repository(int branches)
    {
        string made = TestFolders.Create("branches-many-");
        File.WriteAllText(Path.Combine(made, "Shop.cs"), Shop);
        TestGit.Init(made);
        string head = TestGit.Run(made, "rev-parse", "HEAD").Trim();
        long later = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        StringBuilder recent = new();
        for (int i = 1; i <= 12; i++)
        {
            recent.Append(CultureInfo.InvariantCulture,
                $"commit refs/heads/recent-{i:00}\ncommitter Passenger Tests <tests@passenger.invalid> {later + (i * 60)} +0000\ndata <<END\nRecent {i}\nEND\nfrom {head}\n\n");
        }
        TestGit.Feed(made, recent.ToString(), "fast-import", "--quiet");
        StringBuilder rest = new();
        for (int i = 0; i < branches - 13; i++)
        {
            rest.Append(CultureInfo.InvariantCulture, $"create refs/heads/bulk/b{i:0000} {head}\n");
        }
        TestGit.Feed(made, rest.ToString(), "update-ref", "--stdin");
        return made;
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
        MenuItem item = items.FirstOrDefault(i => MenuChoice.NameOf(i) == header)
            ?? throw new InvalidOperationException($"The menu has no {header}: [{string.Join(", ", items.Select(MenuChoice.NameOf))}]");
        for (int presses = 0; !item.IsSelected; presses++)
        {
            Assert.True(presses < 30, $"Down never reached {header} in the menu");
            Ui.Press(window, "Down");
        }
        return item;
    }

    /// <summary>The window on the repository with the Git window's branches shown; <paramref name="seen"/>, one the person
    /// sees, as only such a window asks them.</summary>
    private MainWindow Open(bool seen = false)
    {
        MainWindow window;
        if (seen)
        {
            window = new MainWindow { Width = 1300, Height = 900, Unseen = false };
            window.Show();
            window.OpenFolder(folder);
            Ui.Settle();
        }
        else
        {
            window = Ui.OpenFolder(folder, width: 1300, height: 900);
        }
        window.Perform(AppActions.ShowGit);
        Ui.Settle();
        Ui.Landed(() => window.GitBranches.Rows.Count > 0, "the branches panel");
        return window;
    }

    /// <summary>Chooses a branch or tag in the panel and an item of its menu, as the person would.</summary>
    /// <summary>Show Diff with Working Tree's Get from 'feature' takes feature's text of a file changed here, makes the file
    /// only feature has and moves to the Trash the one feature lacks, each one command Undo takes back, the open files showing
    /// it; and a diff of a file no editor has open, from that list, edits the file itself: its arrow takes feature's change
    /// into it, saved as the diff closes. Get from Branch or Tag… is in the Git menu beside Compare with Branch or Tag….</summary>
    private void Get_from_a_branch_takes_its_version_of_a_changed_file_one_it_lacks_and_one_only_it_has_each_one_undo_and_a_closed_files_diff_with_it_is_edited_in_place()
    {
        string trash = TestFolders.Create("branches-trash-");
        string shop = Path.Combine(folder, "Shop.cs");
        string added = Path.Combine(folder, "Added.cs");
        string feature = Path.Combine(folder, "Feature.cs");
        string longer = Shop.Replace("int count", "long count", StringComparison.Ordinal);
        Trash.Folder = trash;
        try
        {
            File.WriteAllText(shop, longer);
            File.WriteAllText(added, "class Added { }\n");
            MainWindow window = Open();
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Assert.Equal("Get from Branch or Tag…", window.Actions[AppActions.GetFromBranch]?.Title.English);
            BranchesPanel panel = window.GitBranches;
            Choose(panel, List(panel), "feature", "Show Diff with Working Tree");
            WorkingTreeDiffView changes = Assert.IsType<WorkingTreeDiffView>(window.GitWindow.Shown);
            Ui.Landed(() => changes.Files.Count == 3, "feature's three files");

            // The diff of Shop.cs, which no editor has open, edits the file: its arrow takes feature's int back.
            Ui.Click(Ui.Row(changes.List, changes.Files.Single(f => f.Name == "Shop.cs")));
            Ui.Press(window, "ENTER");
            IDiffHost diff = OpenDiffs.Landed(window, "Shop.cs beside feature's");
            DiffView view = OpenDiffs.ViewOf(diff);
            Assert.True(view.IsEditable, "The diff of a file no editor has open showed it read-only");
            view.Right.GoTo(new Passenger.Text.TextPosition(2, 4));
            Ui.Await(view.RollBackAtCaretAsync(), "feature's change taken at the caret");
            Assert.Equal(Shop, view.Right.Document.Snapshot.ToString());
            diff.Close();
            Ui.WaitFor(() => File.ReadAllText(shop) == Shop, "the diff's edit saved into Shop.cs as it closed");
            Ui.Landed(() => window.EditorArea.Documents.All(d => d.Name != "Shop.cs"), "Shop.cs let go once its diff closed");
            File.WriteAllText(shop, longer);
            Ui.WaitFor(() => changes.Files.Count == 3, "the folder's watcher to hear Shop.cs changed again");

            foreach ((string name, string said) in (ReadOnlySpan<(string, string)>)[
                ("Shop.cs", "Shop.cs now has feature's version: Undo puts yours back"),
                ("Feature.cs", "Feature.cs now has feature's version: Undo puts yours back"),
                ("Added.cs", "Added.cs, which feature has not, went to the Trash: Undo puts yours back")])
            {
                Ui.Click(Ui.Row(changes.List, changes.Files.Single(f => f.Name == name)));
                Ui.ChooseFromMenu(changes.List, "Get from 'feature'");
                Ui.Landed(() => window.Status == said, $"{name} taken from feature, where the status bar says \"{window.Status}\"");
            }
            Assert.Equal(Shop, File.ReadAllText(shop));
            Assert.Equal("class Feature { }\n", File.ReadAllText(feature));
            Assert.False(File.Exists(added), "Get from 'feature' left Added.cs, which feature has not, in place");
            Ui.WaitFor(() => changes.Files.Count == 0 && changes.Placeholder == "No differences", "the working tree now as feature has it");

            // Undo where the window's commands are taken back: the Explorer's rows.
            window.ShowToolWindow(ToolWindowIds.Explorer);
            window.ExplorerView.Reveal(PathNames.Canonical(shop));
            Ui.Settle();
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Path == PathNames.Canonical(shop)));
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.WaitFor(() => File.Exists(added), () => $"Undo to bring Added.cs back ({window.Status})");
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.WaitFor(() => !File.Exists(feature), () => $"Undo to take Feature.cs away again ({window.Status})");
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Ui.WaitFor(() => File.ReadAllText(shop) == longer, () => $"Undo to put Shop.cs's long back ({window.Status})");

            // A revision holding a symbolic link where the file is: its blob is the link's target, never the file's text.
            string target = TestGit.Feed(folder, "Feature.cs", "hash-object", "-w", "--stdin").Trim();
            string tree = TestGit.Feed(folder, $"120000 blob {target}\tShop.cs\n", "mktree").Trim();
            Git("tag", "linked", TestGit.Run(folder, "commit-tree", tree, "-m", "Linked").Trim());
            VcsOutcome linked = Ui.Await(window.GetFromAsync("linked", "linked", [(shop, shop, null)]), "Get from linked");
            Assert.Equal((false, "Shop.cs is a symbolic link in linked: Get from Branch takes a text file's version alone"), (linked.Succeeded, linked.Message));
            Assert.Equal(longer, File.ReadAllText(shop));
            window.Close();
        }
        finally
        {
            Trash.Folder = null;
            TestFolders.Delete(trash);
        }
    }

    private static void Choose(BranchesPanel panel, ListBox rows, string name, string item)
    {
        Assert.True(panel.Select(name), $"The branches panel has no {name}");
        Ui.ChooseFromMenu(rows, item);
        Ui.Settle();
    }

    private static ListBox List(BranchesPanel panel) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<ListBox>().Single();

    /// <summary>A side button of the panel, by the name a screen reader says for it.</summary>
    private static Button SideButton(BranchesPanel panel, string name) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    /// <summary>The side buttons' tips top to bottom, as the pointer finds them: a button's name, or its name and why it is
    /// greyed; a tip promising what a greyed button would do fails.</summary>
    private static string[] Tools(BranchesPanel panel) =>
    [
        .. ((string[])["New Branch…", "Update Selected", "Delete", "Mark/Unmark As Favorite", "Navigate Log to Selected Branch Head"]).Select(name =>
        {
            Button button = SideButton(panel, name);
            string tip = ToolTip.GetTip(button) as string ?? string.Empty;
            Assert.True(button.IsEnabled == (tip == name), $"{name} is {(button.IsEnabled ? "enabled" : "greyed")} with the tip '{tip}'");
            return tip;
        }),
    ];

    /// <summary>A theme colour as the control's look gives it, its alpha first.</summary>
    private static uint Colour(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out object? found) && found is ISolidColorBrush { Color: var colour }
            ? colour.ToUInt32() : throw new Xunit.Sdk.XunitException($"The look gives no colour {key}");

    /// <summary>The chosen row's menu as it opens under the pointer, closed again.</summary>
    private static MenuItem[] MenuOf(ListBox rows)
    {
        ContextMenu menu = rows.ContextMenu ?? throw new InvalidOperationException("The branches panel's rows have no menu");
        rows.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
        Ui.Settle();
        MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
        menu.Close();
        Ui.Settle();
        return items;
    }

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
        Passenger.Core.ChildResult result = Ui.Await(Waiting.Apart(() => Passenger.Core.ChildProcesses.RunAsync(
            Passenger.Core.ChildProcesses.Command("git", ["merge-base", "--is-ancestor", ancestor, of], folder), limit: TimeSpan.FromMinutes(1))), $"git merge-base {ancestor} {of}");
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

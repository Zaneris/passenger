using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The status bar's Git, read from what the window's Git keeps: a clone whose upstream is ahead of it and behind it
/// as last fetched says both counts and its changed files, each a click to the window that lists them; a save and the
/// window coming to the front count no commits again, a commit made in a terminal counts them once more; a branch that
/// tracks none says no commits, and a folder in no working tree says none of it; the branch checked out opens the branch
/// popup from the bar, going whole where the bar has no room for it.</summary>
public sealed class GitStatusBarTests : IDisposable
{
    private readonly string origin = TestFolders.Create("git-status-origin-");
    private readonly string clone = TestFolders.Create("git-status-clone-");

    public void Dispose()
    {
        TestFolders.Delete(origin);
        TestFolders.Delete(clone);
    }

    [AvaloniaFact]
    public void A_branch_behind_and_ahead_of_its_upstream_says_both_and_its_changed_files_each_a_click_to_their_window()
    {
        File.WriteAllText(Path.Combine(origin, "Shop.cs"), "class Shop\n{\n}\n");
        TestGit.Init(origin);
        TestGit.Run(origin, "clone", "-q", "-c", "core.autocrlf=false", origin, clone);
        TestGit.Run(clone, "config", Passenger.Vcs.GitConfigKeys.UserEmail, "tests@passenger.invalid");
        TestGit.Run(clone, "config", "user.name", "Passenger Tests");
        foreach (string added in new[] { "Price", "Stock" })
        {
            File.WriteAllText(Path.Combine(origin, $"{added}.cs"), $"class {added}\n{{\n}}\n");
            TestGit.Run(origin, "add", "-A");
            TestGit.Run(origin, "commit", "-q", "-m", added);
        }
        TestGit.Run(clone, "fetch", "-q");
        File.WriteAllText(Path.Combine(clone, "Notes.txt"), "notes\n");
        TestGit.Run(clone, "add", "-A");
        TestGit.Run(clone, "commit", "-q", "-m", "Notes");
        File.WriteAllText(Path.Combine(clone, "Shop.cs"), "sealed class Shop\n{\n}\n");
        File.WriteAllText(Path.Combine(clone, "Draft.txt"), "draft\n");

        MainWindow window = Ui.OpenFolder(clone, languageServer: false);
        Button sync = window.FindControl<Button>("GitSyncButton")!;
        Button changes = window.FindControl<Button>("GitChangesButton")!;
        Ui.WaitFor(() => sync.IsEffectivelyVisible && Words(sync) == "2 1", () => $"the status bar's 2 incoming and 1 outgoing commits, where it says [{Words(sync)}]");
        Assert.True(changes.IsEffectivelyVisible && Words(changes) == "2", $"The status bar says [{Words(changes)}] of a clone with 2 changed files");
        string syncTip = ToolTip.GetTip(sync) as string ?? string.Empty;
        Assert.True(syncTip.Contains("2 incoming commits from origin/main, as last fetched", StringComparison.Ordinal)
            && syncTip.Contains("1 outgoing commit of main to push", StringComparison.Ordinal), $"The status bar's commits are told as: {syncTip}");
        Assert.StartsWith("2 changed files to commit, 1 of them unversioned", ToolTip.GetTip(changes) as string ?? string.Empty, StringComparison.Ordinal);

        // A save reads the files again, and the window coming to the front the history, but neither counts the commits again.
        int counted = window.Vcs.DivergenceCounts;
        window.OpenFile(Path.Combine(clone, "Notes.txt"));
        Ui.Settle();
        window.KeyTextInput("more ");
        Ui.Run(window, AppActions.Save);
        Ui.WaitFor(() => Words(changes) == "3", () => $"the saved file among the changes, where the status bar says [{Words(changes)}]");
        Ui.Await(window.Vcs.RefreshAsync(), "the history read again");
        Assert.True(window.Vcs.DivergenceCounts == counted, $"A save and a refresh with no commit moved counted the commits {window.Vcs.DivergenceCounts - counted} more times");

        Ui.Click(sync);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Git), "A click on the status bar's commits left the Git window hidden");
        GitLogView incoming = Assert.IsType<GitLogView>(window.GitWindow.Shown);
        Assert.Equal("Compare main and origin/main", window.GitWindow.Titles[^1]);
        GitLogView outgoing = window.GitWindow.GetLogicalDescendants().OfType<GitLogView>().Single(l => l.Range?.To == "main");
        Ui.WaitFor(() => Subjects(incoming) == "Stock Price" && Subjects(outgoing) == "Notes",
            () => $"the incoming commits [{Subjects(incoming)}] and the outgoing [{Subjects(outgoing)}] listed in the Git window");

        Ui.Click(changes);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Commit), "A click on the status bar's changed files left the Commit window hidden");

        // A commit made in a terminal is heard, and the commits counted once more.
        TestGit.Run(clone, "commit", "-q", "-am", "More");
        Ui.WaitFor(() => Words(sync) == "2 2" && Words(changes) == "1", () => $"the terminal's commit outgoing, where the status bar says [{Words(sync)}] and [{Words(changes)}]");
        Assert.Equal(counted + 1, window.Vcs.DivergenceCounts);

        // A branch that tracks none has no commits to say, its changed files still said.
        TestGit.Run(clone, "switch", "-q", "-c", "loose");
        Ui.WaitFor(() => window.Vcs.Branch == "loose", "the new branch read");
        Assert.True(!sync.IsVisible && changes.IsVisible, $"On a branch that tracks none the status bar's commits show {sync.IsVisible} and its changed files {changes.IsVisible}");
        window.Close();
    }

    /// <summary>The status bar's branch says the branch checked out, and a click on it opens the branch popup the toolbar's
    /// Git widget opens, over the bar, whose New Branch… makes a branch and checks it out, which both then say; at the
    /// window's least width it goes whole, nothing in the bar cut or laid over another, and comes back as the window widens.</summary>
    [AvaloniaFact]
    public void The_status_bars_branch_opens_the_branch_popup_whose_New_Branch_checks_one_out_and_goes_whole_at_the_least_width()
    {
        File.WriteAllText(Path.Combine(origin, "Shop.cs"), "class Shop\n{\n}\n");
        TestGit.Init(origin);
        MainWindow window = Ui.OpenFolder(origin, languageServer: false);
        Button branch = window.FindControl<Button>("GitBranchButton")!;
        Ui.WaitFor(() => branch.IsEffectivelyVisible && Words(branch) == "main", () => $"the status bar's branch, where it says [{Words(branch)}]");
        Assert.StartsWith("Git branch: main", ToolTip.GetTip(branch) as string ?? string.Empty, StringComparison.Ordinal);

        string[] fromToolbar = Headers(OpenMenu(window, window.FindControl<Button>("BranchButton")!));
        Ui.Press(window, "Escape");
        Ui.Settle();
        ContextMenu menu = OpenMenu(window, branch);
        Assert.True(fromToolbar.SequenceEqual(Headers(menu)),
            $"The status bar's branch opens [{string.Join(", ", Headers(menu))}] where the toolbar's Git widget opens [{string.Join(", ", fromToolbar)}]");
        Border frame = menu.GetVisualDescendants().OfType<Border>().First(b => b.BoxShadow.Count > 0);
        Point top = branch.TranslatePoint(default, window)!.Value;
        double bottom = frame.TranslatePoint(new Point(0, frame.Bounds.Height), window)!.Value.Y;
        Assert.True(bottom <= top.Y + 0.5, $"The branch menu ends at {bottom:0.#}, below the status bar's branch's top at {top.Y:0.#}: it hangs past the window's foot");

        MenuItem item = menu.Items.OfType<MenuItem>().First(i => i.Header as string == "New Branch…");
        for (int presses = 0; !item.IsSelected; presses++)
        {
            Assert.True(presses < 30, "Down never reached New Branch… in the status bar branch's menu");
            Ui.Press(window, "Down");
        }
        Ui.Press(window, "Enter");
        Ui.WaitFor(() => window.Popup is NamePopup, "New Branch's name box");
        window.KeyTextInput("feature/stock");
        Ui.Press(window, "Enter");
        Ui.Landed(() => window.Vcs.Branch == "feature/stock" && Words(branch) == "feature/stock",
            "the new branch checked out, in Git and the status bar");
        Assert.Equal("feature/stock", window.FindControl<TextBlock>("BranchName")!.Text);

        window.OpenFile(Path.Combine(origin, "Shop.cs"));
        window.Width = MainWindow.SmallestWidth;
        Ui.Settle();
        Border bar = window.FindControl<Border>("StatusBarFrame")!;
        Assert.True(ShrinkingRow.HasGone(branch) && !branch.IsHitTestVisible, $"At {MainWindow.SmallestWidth} wide the status bar's branch keeps {branch.Bounds.Width:0.#} units of a bar the file's widgets fill");
        List<string> faults = LayoutFaults.Of(window, bar, $"At {MainWindow.SmallestWidth} wide the status bar");
        Assert.True(faults.Count == 0, string.Join("\n", faults));
        window.Width = 1200;
        Ui.Settle();
        Assert.True(!ShrinkingRow.HasGone(branch) && branch.IsHitTestVisible && Words(branch) == "feature/stock", "At 1200 wide the status bar's branch stays gone");
        window.Close();
    }

    [AvaloniaFact]
    public void A_folder_in_no_working_tree_says_no_Git_in_the_status_bar()
    {
        File.WriteAllText(Path.Combine(origin, "Shop.cs"), "class Shop\n{\n}\n");
        MainWindow window = Ui.OpenFolder(origin, languageServer: false);
        Ui.Await(window.Vcs.Opened, "the folder looked for in a working tree");
        Ui.Settle();
        Assert.Null(window.Vcs.Repository);
        Assert.False(window.FindControl<Button>("GitSyncButton")!.IsVisible, "A folder in no working tree says commits to pull and push");
        Assert.False(window.FindControl<Button>("GitChangesButton")!.IsVisible, "A folder in no working tree says changed files");
        Assert.False(window.FindControl<Button>("GitBranchButton")!.IsVisible, "A folder in no working tree says a branch");
        window.Close();
    }

    /// <summary>A widget clicked, as a person's pointer clicks it, and its menu waited for.</summary>
    private static ContextMenu OpenMenu(MainWindow window, Button widget)
    {
        Ui.Click(widget, settle: false);
        Ui.WaitFor(() => window.OpenContextMenu is { IsOpen: true }, () => $"{widget.Name}'s menu");
        Ui.Settle();
        return window.OpenContextMenu!;
    }

    private static string[] Headers(ContextMenu menu) => [.. menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? string.Empty)];

    private static string Words(Button item) => string.Join(' ', item.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static string Subjects(GitLogView log) => string.Join(' ', log.Rows.Select(r => r.Commit.Subject));
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Diffs as tabs of the editors, as Settings' Open diffs in chooses: Show Diff from the Commit window (Enter or a
/// double-click on a row) opens a tab titled as its window would be and no window, asked again it shows the same tab, and
/// the tab is no file's own; its file's side edits the file, which Save from the tab writes; it steps and puts changes
/// back; it goes with its file's tab; it splits and moves to a window of its own, and the folder opens again without it;
/// and a change of the setting moves the diffs open; the Commit window's tab follows the file chosen there until kept.</summary>
public sealed class DiffTabsTests : IDisposable
{
    private const string Original = "class Shop\n{\n    int count;\n\n    void Add() { count++; }\n}\n";

    private readonly string folder = TestFolders.Create("diff-tabs-");
    private readonly string state = TestFolders.Create("diff-tabs-state-");

    public DiffTabsTests() => TestFolders.Prepare(() =>
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original);
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        TestGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), Original.Replace("int count", "long count", StringComparison.Ordinal).Replace("void Add", "public void Add", StringComparison.Ordinal));
    }, folder);

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(state);
    }

    /// <summary>Diff tabs as one flow, each step one test's check in an instance of its own: a tab from the Commit
    /// window titled as its window, following the file chosen until kept, its edits saved, Rollback Lines at its caret,
    /// split, moved to a window and left out of the kept tabs, moved by Settings' Open Diffs In, searched by Find,
    /// brought back by Back and Reopen, and let go once closed.</summary>
    [AvaloniaFact]
    public void A_diff_opens_in_a_tab_that_follows_the_Commit_window_saves_its_edits_and_lets_go_once_closed()
    {
        Flow.StepApart<DiffTabsTests>("show Diff from the Commit window opens a tab of the editors titled as its window and no window", t => t.Show_Diff_from_the_Commit_window_opens_a_tab_of_the_editors_titled_as_its_window_and_no_window());
        Flow.StepApart<DiffTabsTests>("the Commit windows diff tab follows the file chosen there until it is kept", t => t.The_Commit_windows_diff_tab_follows_the_file_chosen_there_until_it_is_kept());
        Flow.StepApart<DiffTabsTests>("an edit in a diff tab is saved to the file from the tab and closing the files tab closes the diff", t => t.An_edit_in_a_diff_tab_is_saved_to_the_file_from_the_tab_and_closing_the_files_tab_closes_the_diff());
        Flow.StepApart<DiffTabsTests>("rollback lines in the commit windows diff puts back the change at the caret and the arrows tip names its keys", t => t.Rollback_lines_in_the_commit_windows_diff_puts_back_the_change_at_the_caret_and_the_arrows_tip_names_its_keys());
        Flow.StepApart<DiffTabsTests>("a diff tab splits moves to a window of its own jumps to its file and is left out of the folders kept tabs", t => t.A_diff_tab_splits_moves_to_a_window_of_its_own_jumps_to_its_file_and_is_left_out_of_the_folders_kept_tabs());
        Flow.StepApart<DiffTabsTests>("open diffs in on Settings Version Control page moves the diffs open between a window and a tab", t => t.Open_diffs_in_on_Settings_Version_Control_page_moves_the_diffs_open_between_a_window_and_a_tab());
        Flow.StepApart<DiffTabsTests>("find in a diff tab searches the side with the caret marking the other and find next steps through it", t => t.Find_in_a_diff_tab_searches_the_side_with_the_caret_marking_the_other_and_find_next_steps_through_it());
        Flow.StepApart<DiffTabsTests>("back returns to the diff tab a file was opened from and reopen closed tab brings a closed diff tab back", t => t.Back_returns_to_the_diff_tab_a_file_was_opened_from_and_reopen_closed_tab_brings_a_closed_diff_tab_back());
        Flow.StepApart<DiffTabsTests>("a closed diff tab kept to open again holds none of its texts nor its closed file", t => t.A_closed_diff_tab_kept_to_open_again_holds_none_of_its_texts_nor_its_closed_file());
    }

    private void Show_Diff_from_the_Commit_window_opens_a_tab_of_the_editors_titled_as_its_window_and_no_window()
    {
        MainWindow window = Open();
        string shop = window.EditorArea.Active!.Path;
        OpenDocument file = window.EditorArea.Active!;
        int windows = window.OwnedWindows.Count;

        IDiffHost host = DiffFromCommit(window, byEnter: true);
        Assert.True(window.OwnedWindows.Count == windows, $"Show Diff opened a window ({string.Join(", ", window.OwnedWindows.Select(w => w.Title))}) with diffs set to open as tabs");
        DiffTab tab = Assert.IsType<DiffTab>(host);
        EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.Same(tab.Document, group.Active);
        Assert.Equal("Shop.cs: HEAD and your version", tab.Document.Title);
        Assert.Contains(group.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains(StyleClasses.TabName) && t.Text == "Shop.cs: HEAD and your version");
        DiffView view = Assert.IsType<DiffView>(host.View);
        Assert.Same(view, group.Diff);
        Assert.Same(file.Text, view.Right.Document);
        Assert.Equal(Original, view.Left.Document.Snapshot.ToString());
        Assert.Equal([new DiffHunk(2, 1, 2, 1), new DiffHunk(4, 1, 4, 1)], view.Hunks);
        Ui.Landed(() => view.Right.IsKeyboardFocusWithin, "the diff tab's side of the file to have the keys");

        // The tab is no file's own: the file is found as itself, and the tab is listed among the diffs alone.
        Assert.Same(file, window.EditorArea.Find(shop));
        Assert.DoesNotContain(tab.Document, window.EditorArea.Documents);
        Assert.Equal([tab.Document], window.EditorArea.Diffs);
        Assert.False(tab.Document.HoldsText);

        // F7 goes to the next difference, Shift+F7 back; a band's arrow puts a change back in the file.
        view.Right.GoTo(new TextPosition(0, 0));
        Ui.Press(window, "F7");
        Assert.True(view.Right.CaretPosition.Line == 2, $"F7 in the diff tab left its caret on line {view.Right.CaretPosition.Line + 1}, not at the first difference");
        Ui.Press(window, "F7");
        Assert.True(view.Right.CaretPosition.Line == 4, $"F7 again left the caret on line {view.Right.CaretPosition.Line + 1}, not at the second difference");
        Ui.Press(window, "shift F7");
        Assert.True(view.Right.CaretPosition.Line == 2, $"Shift+F7 left the caret on line {view.Right.CaretPosition.Line + 1}, not back at the first difference");
        view.Revert(view.Hunks[1]);
        Ui.Settle();
        Assert.Contains("    void Add() { count++; }", file.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal([new DiffHunk(2, 1, 2, 1)], view.Hunks);

        // Asked again, from the editor's Show Diff on the file, it shows the tab open rather than a second.
        window.OpenFile(Path.Combine(folder, "Notes.txt"));
        window.OpenFile(shop);
        Ui.Settle();
        Assert.True(window.Perform(AppActions.ShowDiff));
        Ui.Landed(() => group.Active == tab.Document, "the diff's tab shown again");
        Assert.Single(window.EditorArea.Diffs);
        Assert.Equal(1, group.Tabs.Count(t => t.Document.Diff is not null));
        Assert.True(window.OwnedWindows.Count == windows, "Show Diff from the editor opened a window with diffs set to open as tabs");
        window.Close();
    }

    /// <summary>The diff tab the Commit window opens follows the file chosen there: Show Diff on another file's row shows
    /// that file's diff in the same tab, named for it and drawn aslant, and a row chosen by a click or the keys does too, the
    /// keys staying in the list. Pinned, or its file edited in it, the tab is kept, showing its own file, and the next file's
    /// diff the Commit window opens comes in a tab of its own (a new file's saying so), which follows from then on; one a kept
    /// tab shows brings it forward.</summary>
    private void The_Commit_windows_diff_tab_follows_the_file_chosen_there_until_it_is_kept()
    {
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes, edited\n");
        File.WriteAllText(Path.Combine(folder, "Draft.txt"), "draft\n");
        MainWindow window = Open();
        string shop = window.EditorArea.Active!.Path;
        DiffTab first = Assert.IsType<DiffTab>(DiffFromCommit(window, byEnter: true));
        Assert.True(first.Follows && Drawn(window, first).Classes.Contains(StyleClasses.Follows), "The Commit window's diff tab is not drawn as one that follows its choice");
        Assert.Contains("Follows the file chosen in the Commit window", ToolTip.GetTip(Drawn(window, first)) as string ?? string.Empty, StringComparison.Ordinal);

        Ui.DoubleClick(CommitRowOf(window, "Notes.txt"));
        Ui.Landed(() => first.Shown.Path.EndsWith("Notes.txt", StringComparison.Ordinal), "Notes.txt's diff taken by the tab that follows the Commit window");
        OpenDocument diff = Assert.Single(window.EditorArea.Diffs);
        Assert.Same(first.Document, diff);
        Assert.Equal(("Notes.txt: HEAD and your version", "Notes.txt"), (diff.Title, diff.Name));
        Assert.Equal("Notes.txt: HEAD and your version", Drawn(window, first).GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains(StyleClasses.TabName)).Text);
        Assert.Equal("notes, edited\n", Assert.IsType<DiffView>(window.EditorArea.ActiveGroup.Diff).Right.Document.Snapshot.ToString());

        // A row chosen by a click, then by the keys, is followed once the choice rests, the keys left in the list.
        ListBox changes = ChangesList(window);
        Ui.Click(CommitRowOf(window, "Shop.cs"));
        Ui.Landed(() => first.Shown.Path == shop, "Shop.cs's diff taken as its row is chosen");
        Assert.True(changes.IsKeyboardFocusWithin, "Following the choice took the keys from the Commit window's list");
        bool notesBelow = window.CommitView.Rows.ToList().IndexOf(window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "Notes.txt"))
            > window.CommitView.Rows.ToList().IndexOf(window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == "Shop.cs"));
        Ui.Press(window, notesBelow ? "DOWN" : "UP");
        Ui.Landed(() => first.Shown.Path.EndsWith("Notes.txt", StringComparison.Ordinal), "Notes.txt's diff taken as the keys choose its row");
        Assert.True(changes.IsKeyboardFocusWithin && window.EditorArea.Diffs.Count == 1, "Following the keys' choice took the keys or opened another tab");

        // Pinned, it is kept: the next file's diff comes in a tab of its own, which follows.
        window.EditorArea.ActiveGroup.Pin(first.Document, true);
        Ui.Settle();
        Assert.False(first.Follows || Drawn(window, first).Classes.Contains(StyleClasses.Follows), "A pinned diff tab still follows the Commit window's choice");
        Ui.DoubleClick(CommitRowOf(window, "Shop.cs"));
        Ui.Landed(() => window.EditorArea.Diffs.Count == 2, "Shop.cs's diff in a tab of its own beside the kept one");
        DiffTab second = window.EditorArea.Diffs.Select(d => d.Diff!).Single(d => d != first);
        Assert.True(second.Follows && second.Shown.Path == shop && first.Shown.Path.EndsWith("Notes.txt", StringComparison.Ordinal),
            $"The kept tab shows {first.Shown.Path} and the new one {second.Shown.Path}, following: {second.Follows}");

        // Edited in, it is kept too.
        DiffView view = second.View!;
        view.Right.Focus();
        view.Right.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        Ui.Settle();
        Assert.False(second.Follows, "A diff tab whose file was edited in it still follows the Commit window's choice");
        Ui.DoubleClick(CommitRowOf(window, "Draft.txt"));
        Ui.Landed(() => window.EditorArea.Diffs.Count == 3, "Draft.txt's diff in a third tab once the second was edited in");
        Assert.Equal(shop, second.Shown.Path);
        // Draft.txt is new: its tab shows no line of the last commit's, saying so, and all of the file as added.
        OpenDiffs.ShowsNoFile(Assert.IsType<DiffView>(window.EditorArea.ActiveGroup.Diff), MergeSide.Left);

        // Show Diff on a file a kept tab shows brings that tab forward, opening none.
        DiffTab third = window.EditorArea.Diffs.Select(d => d.Diff!).Single(d => d != first && d != second);
        Ui.DoubleClick(CommitRowOf(window, "Notes.txt"));
        Ui.WaitFor(() => window.EditorArea.Active == first.Document, () => $"the kept tab of Notes.txt's diff brought forward, where {window.EditorArea.Active?.Title} is shown, "
            + $"the diff tabs being [{string.Join("; ", window.EditorArea.Diffs.Select(d => $"{d.Title}{(d.Follows ? ", following" : string.Empty)}"))}] "
            + $"and the Commit window's chosen rows [{string.Join(", ", ChangesList(window).SelectedItems?.OfType<CommitRow>().Select(r => r.Change.Name) ?? [])}]");
        Assert.True(window.EditorArea.Diffs.Count == 3 && third.Follows, $"Show Diff on a file a kept tab shows left {window.EditorArea.Diffs.Count} diff tabs");
        window.Close();
    }

    private void An_edit_in_a_diff_tab_is_saved_to_the_file_from_the_tab_and_closing_the_files_tab_closes_the_diff()
    {
        MainWindow window = Open();
        OpenDocument file = window.EditorArea.Active!;
        string shop = file.Path;
        DiffTab tab = (DiffTab)DiffFromCommit(window, byEnter: false);
        DiffView view = tab.View!;
        Ui.Landed(() => view.Right.IsKeyboardFocusWithin, "the diff tab's side of the file to have the keys");

        view.Right.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("sealed ");
        Ui.Settle();
        Assert.StartsWith("sealed class Shop", file.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.True(file.IsModified, "Typed in the diff tab, the file shows no change");
        Assert.Null(window.Actions.WhyNot(AppActions.Save, window.Actions.Context()));
        Ui.Run(window, AppActions.Save);
        Assert.StartsWith("sealed class Shop", File.ReadAllText(shop), StringComparison.Ordinal);
        Assert.False(file.IsModified, "Save in the diff tab left the file it edits unsaved");
        Assert.Same(tab.Document, window.EditorArea.Active);

        window.OpenFile(shop);
        Ui.Settle();
        Assert.Same(file, window.EditorArea.Active);
        Ui.Run(window, AppActions.CloseTab);
        Assert.Empty(window.EditorArea.Diffs);
        Assert.DoesNotContain(window.EditorArea.ActiveGroup.Tabs, t => t.Document.Diff is not null);
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Path == shop);
        window.Close();
    }

    /// <summary>Rollback Lines' keys in the Commit window's diff put back the change at the caret, as its band's arrow does,
    /// whose tip names those keys: from the file's side, then from the last commit's, each an edit of the file Undo takes
    /// back. Where the file is not open, the diff's copy of it puts the change back on disk, one command its copy's Undo
    /// takes back, the copy following the file each way.</summary>
    private void Rollback_lines_in_the_commit_windows_diff_puts_back_the_change_at_the_caret_and_the_arrows_tip_names_its_keys()
    {
        MainWindow window = Open();
        OpenDocument file = window.EditorArea.Active!;
        DiffView view = Assert.IsType<DiffView>(DiffFromCommit(window, byEnter: true).View);
        Ui.Landed(() => view.Right.IsKeyboardFocusWithin, "the diff tab's side of the file to have the keys");
        string? keys = ActionTips.Keys(AppActions.RollbackLines);
        Assert.False(keys is null, "Rollback Lines has no keys in the keymap in force");
        Button arrow = view.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Roll back change 2 to HEAD");
        Assert.Equal($"Roll back change 2 to HEAD ({keys})", ToolTip.GetTip(arrow));

        // The file's side: the caret on the second change, which only its own line holds.
        view.Right.GoTo(new TextPosition(4, 3));
        Ui.Press(window, Ui.Shortcut(AppActions.RollbackLines));
        Ui.Landed(() => file.Text.Snapshot.ToString().Contains("    void Add() { count++; }", StringComparison.Ordinal) && view.Hunks.Count == 1,
            "the second change put back by Rollback Lines' keys in the diff");
        Assert.Equal([new DiffHunk(2, 1, 2, 1)], view.Hunks);
        Assert.Contains("long count", file.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal("Rolled back change 2 to HEAD", window.Status);

        // The last commit's side: its caret on the first change's line puts that one back.
        view.Left.Focus();
        view.Left.GoTo(new TextPosition(2, 0));
        Ui.Landed(() => view.Left.IsKeyboardFocusWithin, "the last commit's side to have the keys");
        Ui.Press(window, Ui.Shortcut(AppActions.RollbackLines));
        Ui.Landed(() => file.Text.Snapshot.ToString() == Original && view.Hunks.Count == 0, "the first change put back from the last commit's side");

        // Where no change is, it says so and changes nothing; the file's Undo takes the last one back.
        Ui.Press(window, Ui.Shortcut(AppActions.RollbackLines));
        Assert.Equal(DiffView.NoChangeHere, window.Status);
        view.Right.Focus();
        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Ui.Landed(() => file.Text.Snapshot.ToString().Contains("long count", StringComparison.Ordinal) && view.Hunks.Count == 1, "Undo to bring the first change back");

        // Saved and closed, the diff's side of it is a copy, whose rollback writes the file.
        Ui.Run(window, AppActions.Save);
        Assert.False(file.IsModified, "Save from the diff tab left Shop.cs unsaved");
        Ui.Run(window, AppActions.CloseTab);
        window.OpenFile(file.Path);
        Ui.Run(window, AppActions.CloseTab);
        Ui.Settle();
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Path == file.Path);
        DiffView copy = Assert.IsType<DiffView>(DiffFromCommit(window, byEnter: true).View);
        Ui.Landed(() => copy.Right.IsKeyboardFocusWithin && copy.Hunks.Count == 1, "the diff of the closed Shop.cs, its one change left, to have the keys");
        Assert.Contains(copy.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Roll back change 1 to HEAD");
        string saved = File.ReadAllText(file.Path);
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        copy.Right.GoTo(new TextPosition(2, 2));
        Ui.Press(window, Ui.Shortcut(AppActions.RollbackLines));
        Ui.WaitFor(() => File.ReadAllText(file.Path) == Original && copy.Hunks.Count == 0, () => $"the closed Shop.cs rolled back on disk and in its copy, where the disk has\n{File.ReadAllText(file.Path)}");
        Assert.Equal("Roll back change 1 of Shop.cs to HEAD", window.EditorArea.Commands.ToUndo(null)?.Name);

        // The copy's Undo takes the write back, and the copy follows the file.
        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Ui.WaitFor(() => File.ReadAllText(file.Path) == saved && copy.Right.Document.Snapshot.ToString() == saved && copy.Hunks.Count == 1,
            () => $"Undo in the copy to put Shop.cs back as saved, where the disk has\n{File.ReadAllText(file.Path)}\nand the copy\n{copy.Right.Document.Snapshot}");
        Assert.True(asked is [{ } question] && question.Contains("Roll back change 1 of Shop.cs", StringComparison.Ordinal), $"Undo asked [{string.Join(" | ", asked)}]");
        window.Close();
    }

    private void A_diff_tab_splits_moves_to_a_window_of_its_own_jumps_to_its_file_and_is_left_out_of_the_folders_kept_tabs()
    {
        MainWindow first = Keeping();
        EditorArea area = first.EditorArea;
        string shop = area.Active!.Path;
        DiffTab tab = (DiffTab)Ui.Landed(first.ShowDiffAsync(shop), "the diff tab")!;
        EditorGroup left = area.ActiveGroup;

        Assert.True(first.Perform(AppActions.SplitRight));
        Ui.Settle();
        Assert.Equal(2, area.Groups.Count);
        EditorGroup right = area.ActiveGroup;
        Assert.NotSame(left, right);
        Assert.Same(tab.Document, right.Active);
        Assert.NotNull(left.Diff);
        Assert.NotNull(right.Diff);
        Assert.NotSame(left.Diff, right.Diff);
        Assert.Same(left.Diff!.Right.Document, right.Diff!.Right.Document);

        // F4 shows the file in the group the diff is in, the diff's tab staying beside it.
        right.Diff.Right.Focus();
        right.Diff.Right.GoTo(new TextPosition(4, 4));
        Ui.Press(first, "F4");
        Assert.True(area.Active?.Path == shop && area.CodeEditor.CaretPosition == new TextPosition(4, 4),
            $"Jump to Source from the diff tab showed {area.Active?.Title} at {area.CodeEditor.CaretPosition}, not Shop.cs at line 5");
        Assert.True(right.Has(tab.Document), "Jump to Source closed the diff's tab");

        area.OpenInNewWindow(right, tab.Document);
        Ui.Settle();
        EditorWindow editors = Assert.Single(area.Windows);
        Assert.Contains(editors.Groups, g => g.Active == tab.Document && g.Diff is not null);
        Assert.Same(tab.Document, editors.Groups.Single().Active);

        first.Close();
        Ui.Settle();
        MainWindow second = Keeping(open: false);
        Assert.Empty(second.EditorArea.Diffs);
        Assert.Empty(second.EditorArea.Windows);
        Assert.Contains(second.EditorArea.Documents, d => d.Path == shop);
        Assert.All(second.EditorArea.Groups, g => Assert.NotEmpty(g.Tabs));
        second.Close();
    }

    private void Open_diffs_in_on_Settings_Version_Control_page_moves_the_diffs_open_between_a_window_and_a_tab()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { OpenDiffsIn = DiffPlace.Window });
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        string shop = window.EditorArea.Active!.Path;
        DiffWindow shown = Assert.IsType<DiffWindow>(Ui.Landed(window.ShowDiffAsync(shop), "the diff in its window"));
        Assert.Same(shown, window.Frontmost);
        Assert.Empty(window.EditorArea.Diffs);

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Page = SettingsDialog.VersionControlPage;
        Assert.Equal(["Open diffs in", "Highlighting differences", "Uncommitted changes at a checkout"], dialog.Shown);
        dialog.Change(s => s with { OpenDiffsIn = DiffPlace.Tab });
        dialog.Ok();
        Ui.Settle();
        Assert.False(shown.IsVisible, "The diff's window stayed open once diffs open as tabs");
        OpenDocument tab = Assert.Single(window.EditorArea.Diffs);
        Assert.Equal("Shop.cs: HEAD and your version", tab.Title);
        Assert.Same(tab, window.EditorArea.Active);

        window.ApplySettings(window.Settings with { OpenDiffsIn = DiffPlace.Window });
        Ui.Settle();
        Assert.Empty(window.EditorArea.Diffs);
        DiffWindow back = Assert.Single(window.OwnedWindows.OfType<DiffWindow>());
        Assert.Equal("Shop.cs: HEAD and your version", back.Title);
        Assert.Same(window.EditorArea.Active!.Text, back.View.Right.Document);
        back.Close();
        window.Close();
    }

    /// <summary>Find in a diff's tab searches the side with the caret, its matches marked there and the other side's matches
    /// of the same words marked too, Find Next stepping through those of the side searched; with the last commit's side
    /// chosen, Find searches that side.</summary>
    private void Find_in_a_diff_tab_searches_the_side_with_the_caret_marking_the_other_and_find_next_steps_through_it()
    {
        MainWindow window = Open();
        DiffView view = Assert.IsType<DiffView>(DiffFromCommit(window, byEnter: true).View);
        Ui.Landed(() => view.Right.IsKeyboardFocusWithin, "the diff tab's side of the file to have the keys");
        FindIn(window, view, view.Right, view.Left);
        window.Close();
    }

    /// <summary>Back from a file goes to the diff's tab Jump to Source came from, at its caret, and Forward to the file again;
    /// a closed diff's tab opens again by Reopen Closed Tab, at the versions as they are now: the file's edit since in it.</summary>
    private void Back_returns_to_the_diff_tab_a_file_was_opened_from_and_reopen_closed_tab_brings_a_closed_diff_tab_back()
    {
        MainWindow window = Open();
        DiffTab tab = Assert.IsType<DiffTab>(DiffFromCommit(window, byEnter: true));
        DiffView view = Assert.IsType<DiffView>(tab.View);
        view.Right.GoTo(new TextPosition(4, 4));
        Ui.Press(window, "F4");
        Ui.Settle();
        Assert.True(window.EditorArea.Active is { Diff: null } file && file.Path.EndsWith("Shop.cs", StringComparison.Ordinal),
            $"Jump to Source from the diff's tab showed {window.EditorArea.Active?.Title}, not Shop.cs");
        Ui.Run(window, AppActions.Back);
        Assert.True(window.EditorArea.Active == tab.Document, $"Back from the file went to {window.EditorArea.Active?.Title}, not the diff's tab it came from");
        Assert.Equal(4, tab.View!.Right.CaretPosition.Line);
        Ui.Run(window, AppActions.Forward);
        Assert.True(window.EditorArea.Active is { Diff: null }, $"Forward from the diff's tab went to {window.EditorArea.Active?.Title}, not the file");

        window.EditorArea.Activate(tab.Document);
        Ui.Run(window, AppActions.CloseTab);
        Ui.Settle();
        Assert.Empty(window.EditorArea.Diffs);
        Assert.True(window.EditorArea.Active is { Diff: null }, $"Closing the diff's tab showed {window.EditorArea.Active?.Title}, not Shop.cs");
        // The file changed since the diff closed: the diff opened again shows it as it is now.
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Shop");
        window.KeyTextInput(" // edited");
        Assert.True(window.EditorArea.Active!.Text.Snapshot.ToString().Contains("class Shop // edited", StringComparison.Ordinal),
            "Typed once the diff's tab had closed, the words went nowhere: Shop.cs, shown in its place, never took the keys the diff's side had");
        Assert.True(window.Perform(AppActions.ReopenClosedTab), "Reopen Closed Tab could not run with a diff's tab closed");
        Ui.Landed(() => window.EditorArea.Diffs.Count == 1 && window.EditorArea.Active?.Diff is not null, "the closed diff's tab opened again by Reopen Closed Tab");
        Assert.Equal("Shop.cs: HEAD and your version", window.EditorArea.Active!.Title);
        DiffView again = Assert.IsType<DiffView>(window.EditorArea.ActiveGroup.Diff);
        Ui.WaitFor(() => again.Hunks.Count == 3, () => $"the reopened diff to have the file's new change as its first, where it has {string.Join(", ", again.Hunks)}");
        Assert.Equal([new DiffHunk(0, 1, 0, 1), new DiffHunk(2, 1, 2, 1), new DiffHunk(4, 1, 4, 1)], again.Hunks);
        window.Close();
    }

    /// <summary>A diff's tab closed, then its file's: what Reopen Closed Tab and Back keep of the diff is how it is asked for
    /// afresh, so the closed file goes, its text and colours, the last commit's text and the differences with it, and the diff
    /// still opens again.</summary>
    private void A_closed_diff_tab_kept_to_open_again_holds_none_of_its_texts_nor_its_closed_file()
    {
        MainWindow window = Open();
        WeakReference<OpenDocument> closed = DiffThenFileClosed(window);
        Ui.Collected(closed, "Shop.cs's document, closed after its diff's tab, to be let go: kept to open the diff again, each diff tab closed "
            + "would hold its file, the last commit's text and the differences, up to fifty of them");

        Assert.True(window.Perform(AppActions.ReopenClosedTab), "Reopen Closed Tab could not bring Shop.cs back");
        Ui.Settle();
        Assert.True(window.Perform(AppActions.ReopenClosedTab), "Reopen Closed Tab could not run with the diff's tab closed before Shop.cs's");
        Ui.Landed(() => window.EditorArea.Diffs.Count == 1 && window.EditorArea.Active?.Diff is not null, "the closed diff's tab opened again by Reopen Closed Tab");
        DiffView again = Assert.IsType<DiffView>(window.EditorArea.ActiveGroup.Diff);
        Ui.WaitFor(() => again.Hunks.Count == 2, () => $"the reopened diff to show Shop.cs's two changes, where it has {string.Join(", ", again.Hunks)}");
        window.Close();
    }

    /// <summary>Shop.cs's diff shown from the Commit window as a tab, closed, then Shop.cs's own tab: the file held only weakly here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<OpenDocument> DiffThenFileClosed(MainWindow window)
    {
        OpenDocument file = window.EditorArea.Active!;
        Assert.IsType<DiffTab>(DiffFromCommit(window, byEnter: true));
        Ui.Run(window, AppActions.CloseTab);
        Ui.Settle();
        Assert.True(window.EditorArea.Diffs.Count == 0 && window.EditorArea.Active == file, $"Closing the diff's tab showed {window.EditorArea.Active?.Title}, not Shop.cs");
        Ui.Run(window, AppActions.CloseTab);
        Ui.Settle();
        Assert.DoesNotContain(file, window.EditorArea.Documents);
        return new WeakReference<OpenDocument>(file);
    }

    /// <summary>Find over a diff's side with the keys, typed into where the keys are: "count" twice on each side.</summary>
    private static void FindIn(Avalonia.Controls.Window keys, DiffView view, CodeEditor searched, CodeEditor beside)
    {
        Ui.Press(keys, Ui.Shortcut(AppActions.Find));
        Assert.True(view.Find.IsVisible, "Find opened no find bar over the diff");
        keys.KeyTextInput("count");
        Ui.Settle();
        string text = searched.Document.Snapshot.ToString();
        int first = text.IndexOf("count", StringComparison.Ordinal);
        int second = text.IndexOf("count", first + 1, StringComparison.Ordinal);
        Assert.Equal(new Selection(first, first + 5), searched.Selections.Primary with { PreferredX = double.NaN });
        Assert.Equal("1/2", view.Find.CountText);
        Assert.True(beside.Highlights.Count == 2, $"The other side marked {beside.Highlights.Count} matches of \"count\", not its two");
        Ui.Press(keys, Ui.Shortcut(AppActions.FindNext));
        Assert.Equal(new Selection(second, second + 5), searched.Selections.Primary with { PreferredX = double.NaN });
        Assert.Equal("2/2", view.Find.CountText);
        Ui.Press(keys, "ESCAPE");
        Assert.False(view.Find.IsVisible, "Escape left Find open over the diff");
        Assert.True(searched.Highlights.Count == 0 && beside.Highlights.Count == 0, "Find closed left its matches marked");

        // The last commit's side chosen, Find searches it.
        beside.Focus();
        Ui.Press(keys, Ui.Shortcut(AppActions.Find));
        keys.KeyTextInput("int");
        Ui.Settle();
        int at = beside.Document.Snapshot.ToString().IndexOf("int", StringComparison.Ordinal);
        Assert.Equal(new Selection(at, at + 3), beside.Selections.Primary with { PreferredX = double.NaN });
        Assert.Equal("1/1", view.Find.CountText);
        Ui.Press(keys, "ESCAPE");
    }

    /// <summary>The folder open on Shop.cs, diffs set to open as tabs.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { OpenDiffsIn = DiffPlace.Tab });
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        return window;
    }

    /// <summary>The window a person uses, keeping its state in the test's own, on the folder (Shop.cs opened in it unless
    /// <paramref name="open"/> is false), diffs set to open as tabs.</summary>
    private MainWindow Keeping(bool open = true)
    {
        MainWindow window = new() { Width = 1400, Height = 800, StateFolder = state };
        window.Show();
        window.KeepLayout();
        window.ApplySettings(window.Settings with { OpenDiffsIn = DiffPlace.Tab });
        window.OpenFolder(folder);
        Ui.Settle();
        if (open)
        {
            window.OpenFile(Path.Combine(folder, "Shop.cs"));
            Ui.Settle();
        }
        return window;
    }

    /// <summary>The Commit window's list of changed files.</summary>
    private static ListBox ChangesList(MainWindow window) =>
        window.CommitView.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, window.CommitView.Rows));

    /// <summary>A changed file's row in the Commit window.</summary>
    private static Control CommitRowOf(MainWindow window, string name) =>
        Ui.Row(ChangesList(window), window.CommitView.Rows.OfType<CommitRow>().First(r => r.Change.Name == name));

    /// <summary>The tab drawn for a diff on the active group's strip.</summary>
    private static Border Drawn(MainWindow window, DiffTab diff) => window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<Border>()
        .First(b => b.Classes.Contains(StyleClasses.Tab) && b.DataContext is EditorTab { } tab && tab.Document == diff.Document);

    /// <summary>Show Diff on Shop.cs's row of the Commit window, by Enter on it or a double-click.</summary>
    private static IDiffHost DiffFromCommit(MainWindow window, bool byEnter)
    {
        window.Perform(AppActions.ShowCommit);
        CommitView commit = window.CommitView;
        Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Shop.cs"), "Shop.cs listed as changed");
        ListBox changes = commit.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, commit.Rows));
        Control row = Ui.Row(changes, commit.Rows.OfType<CommitRow>().First(r => r.Change.Name == "Shop.cs"));
        if (byEnter)
        {
            Ui.Click(row);
            Ui.Press(window, "ENTER");
        }
        else
        {
            Ui.DoubleClick(row);
        }
        return OpenDiffs.Landed(window, $"Shop.cs's diff, from {(byEnter ? "Enter on its row" : "a double-click on its row")} in the Commit window");
    }
}

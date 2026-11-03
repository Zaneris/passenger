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
/// and a change of the setting moves the diffs open.</summary>
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

    [AvaloniaFact]
    public void Show_Diff_from_the_Commit_window_opens_a_tab_of_the_editors_titled_as_its_window_and_no_window()
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

    [AvaloniaFact]
    public void An_edit_in_a_diff_tab_is_saved_to_the_file_from_the_tab_and_closing_the_files_tab_closes_the_diff()
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

    [AvaloniaFact]
    public void A_diff_tab_splits_moves_to_a_window_of_its_own_jumps_to_its_file_and_is_left_out_of_the_folders_kept_tabs()
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

    [AvaloniaFact]
    public void Open_diffs_in_on_Settings_Version_Control_page_moves_the_diffs_open_between_a_window_and_a_tab()
    {
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        Ui.Settle();
        string shop = window.EditorArea.Active!.Path;
        DiffWindow shown = (DiffWindow)Ui.Landed(window.ShowDiffAsync(shop), "the diff in its window")!;
        Assert.Same(shown, window.Frontmost);
        Assert.Empty(window.EditorArea.Diffs);

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Page = SettingsDialog.VersionControlPage;
        Assert.Equal(["Open diffs in", "Highlighting differences"], dialog.Shown);
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
        Ui.Landed(() => window.EditorArea.Diffs.Count > 0 || window.OwnedWindows.OfType<DiffWindow>().Any(),
            $"Shop.cs's diff, from {(byEnter ? "Enter on its row" : "a double-click on its row")} in the Commit window");
        return window.EditorArea.Diffs.FirstOrDefault()?.Diff ?? (IDiffHost)window.OwnedWindows.OfType<DiffWindow>().First();
    }
}

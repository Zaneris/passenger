using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Split editors: Split Right shows a file twice over one text, each split with its own caret; a tab dragged
/// to a split's edge splits there and onto another's strip moves into it; Close takes the tab of the split worked in
/// alone, and a split whose last tab goes gives its room back.</summary>
[SameOnEverySystem]
public sealed class EditorSplitTests : IDisposable
{
    private readonly string folder = TestFolders.Create("splits-");

    public EditorSplitTests()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n}\n");
        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two\n{\n}\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A file split beside itself shows one text with a caret in each and the same decorations and folds,
    /// Close takes the tab from the split worked in and the split goes with its last tab and is let go, a split
    /// scrolled to the end draws what another split left, a tab dragged to an edge splits off and back joins again, the
    /// splits come back with the folder, and Find Action finds an action with no shortcut by name.</summary>
    [AvaloniaFact]
    public void A_file_split_beside_itself_or_into_its_own_window_shows_one_text_and_lets_go_as_it_closes()
    {
        Flow.StepApart<EditorSplitTests>("split right shows the file in both splits over one text each with its own caret", t => t.Split_right_shows_the_file_in_both_splits_over_one_text_each_with_its_own_caret());
        Flow.StepApart<EditorSplitTests>("close takes the tab from the split worked in alone and the split goes with its last tab", t => t.Close_takes_the_tab_from_the_split_worked_in_alone_and_the_split_goes_with_its_last_tab());
        Flow.StepApart<EditorSplitTests>("a closed split is let go by the file it showed and the split left still follows the file", t => t.A_closed_split_is_let_go_by_the_file_it_showed_and_the_split_left_still_follows_the_file());
        Flow.StepApart<EditorSplitTests>("a split scrolled to the file's end draws the lines left once another split deletes most of them", t => t.A_split_scrolled_to_the_files_end_draws_the_lines_left_once_another_split_deletes_most_of_them());
        Flow.StepApart<EditorSplitTests>("a tab dragged to its splits right edge splits it off and back onto the first strip joins it again", t => t.A_tab_dragged_to_its_splits_right_edge_splits_it_off_and_back_onto_the_first_strip_joins_it_again());
        Flow.StepApart<EditorSplitTests>("the splits their tabs and caret's come back when the folder opens again and go when another opens", t => t.The_splits_their_tabs_and_carets_come_back_when_the_folder_opens_again_and_go_when_another_opens());
        Flow.StepApart<EditorSplitTests>("a file in two splits shows the same decorations after an edit in one and folds in both", t => t.A_file_in_two_splits_shows_the_same_decorations_after_an_edit_in_one_and_folds_in_both());
        Flow.StepApart<EditorSplitTests>("find action finds an action with no shortcut by name and runs it", t => t.Find_action_finds_an_action_with_no_shortcut_by_name_and_runs_it());
    }

    private void Split_right_shows_the_file_in_both_splits_over_one_text_each_with_its_own_caret()
    {
        MainWindow window = Open("One.cs");
        EditorArea area = window.EditorArea;
        EditorGroup left = area.ActiveGroup;
        left.Editor.GoTo(new TextPosition(2, 0));
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        Assert.Equal(2, area.Groups.Count);
        EditorGroup right = area.ActiveGroup;
        Assert.NotSame(left, right);
        Assert.Same(left.Active, right.Active);
        Assert.Single(area.Documents);

        right.Editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("// ");
        Ui.Settle();
        Assert.StartsWith("// class One", left.Editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, left.Editor.CaretPosition.Line);
        Assert.Equal(0, right.Editor.CaretPosition.Line);

        area.CloseTab(right, right.Active!);
        Ui.Settle();
        Assert.Single(area.Groups);
        Assert.Single(area.Documents);
        window.Close();
    }

    private void Close_takes_the_tab_from_the_split_worked_in_alone_and_the_split_goes_with_its_last_tab()
    {
        MainWindow window = Open("One.cs", "Two.cs");
        EditorArea area = window.EditorArea;
        EditorGroup left = area.ActiveGroup;
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup right = area.ActiveGroup;
        Assert.Equal(["Two.cs"], right.Tabs.Select(t => t.Document.Name));

        Ui.Run(window, AppActions.CloseTab);
        Assert.True(left.Tabs.Count == 2, $"Close in the right split took Two.cs from the left one too, which has only [{string.Join(", ", left.Tabs.Select(t => t.Document.Name))}]");
        Assert.Same(left, Assert.Single(area.Groups));
        Assert.Same(left, area.ActiveGroup);
        Assert.Equal(["One.cs", "Two.cs"], area.Documents.Select(d => d.Name));

        Ui.Run(window, AppActions.CloseTab);
        Assert.Equal(["One.cs"], left.Tabs.Select(t => t.Document.Name));
        Assert.Equal(["One.cs"], area.Documents.Select(d => d.Name));
        window.Close();
    }

    private void A_closed_split_is_let_go_by_the_file_it_showed_and_the_split_left_still_follows_the_file()
    {
        MainWindow window = Open("One.cs", "Two.cs");
        WeakReference<EditorGroup> closed = SplitFindAndClose(window);

        window.KeyTextInput("// ");
        Ui.Settle();
        EditorGroup left = Assert.Single(window.EditorArea.Groups);
        Assert.StartsWith("// class Two", left.Editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.True(Tab(left, "Two.cs").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Classes.Contains(StyleClasses.Modified)).IsVisible,
            "The tab of the file typed in shows no mark that it changed");

        // Avalonia's hold gesture keeps the pressed close button, and so the split, until a frame fires its timer.
        Ui.Collected(closed, "the closed split (its editor, find bar and tabs) to be let go: still alive, it is held by the file it showed or the window");
        window.Close();
    }

    /// <summary>A split scrolled to its file's end while another split deletes all but its first lines: the first draws the
    /// lines left, where its view stood past the last of them, blank, gutter and all, until scrolled.</summary>
    private void A_split_scrolled_to_the_files_end_draws_the_lines_left_once_another_split_deletes_most_of_them()
    {
        File.WriteAllText(Path.Combine(folder, "Long.cs"), string.Concat(Enumerable.Range(1, 300).Select(i => $"// line {i}\n")));
        MainWindow window = Open("Long.cs");
        EditorArea area = window.EditorArea;
        EditorGroup left = area.ActiveGroup;
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup right = area.ActiveGroup;
        left.Editor.GoTo(new TextPosition(299, 0));
        Ui.Settle();
        Assert.True(left.Editor.ScrollOffset.Y > 200 * left.Editor.LineHeight, "Going to the last line left the view at the file's top");

        TextSnapshot text = right.Editor.Document.Snapshot;
        right.Editor.Selections = SelectionSet.Single(new Selection(text.GetLineStart(10), text.Length));
        right.Editor.Focus();
        Ui.Press(window, "BACK");
        Assert.Equal(11, left.Editor.Document.Snapshot.LineCount);
        Ui.DrawsText(window, left.Editor, "another split deleted all but ten lines");
        window.Close();
    }

    private void A_tab_dragged_to_its_splits_right_edge_splits_it_off_and_back_onto_the_first_strip_joins_it_again()
    {
        MainWindow window = Open("One.cs", "Two.cs");
        EditorArea area = window.EditorArea;
        EditorGroup first = area.ActiveGroup;
        Control two = Tab(first, "Two.cs");
        Point from = Ui.Centre(two, window);
        Point edge = first.Editor.TranslatePoint(new Point(first.Editor.Bounds.Width - 20, first.Editor.Bounds.Height / 2), window)!.Value;
        Ui.Drag(window, from, edge);
        Assert.Equal(2, area.Groups.Count);
        EditorGroup second = area.Groups[1];
        Assert.Equal(["Two.cs"], second.Tabs.Select(t => t.Document.Name));
        Assert.Equal(["One.cs"], first.Tabs.Select(t => t.Document.Name));
        Assert.True(second.TranslatePoint(default, window)!.Value.X > first.TranslatePoint(default, window)!.Value.X + 100);

        Point back = Ui.Centre(Tab(second, "Two.cs"), window);
        Point strip = first.TabStrip.TranslatePoint(new Point(first.TabStrip.Bounds.Width - 30, first.TabStrip.Bounds.Height / 2), window)!.Value;
        Ui.Drag(window, back, strip);
        Assert.Single(area.Groups);
        Assert.Equal(["One.cs", "Two.cs"], area.Groups[0].Tabs.Select(t => t.Document.Name));
        window.Close();
    }

    private void Find_action_finds_an_action_with_no_shortcut_by_name_and_runs_it()
    {
        MainWindow window = Open("One.cs");
        Ui.Run(window, AppActions.FindAction);
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        popup.Query = "split right";
        Ui.Settle();
        Assert.Equal(AppActions.SplitRight, popup.Rows[0].Id);
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen);
        Assert.Equal(2, window.EditorArea.Groups.Count);

        Ui.Run(window, AppActions.FindAction);
        Assert.Equal(AppActions.SplitRight, ((Passenger.App.Navigation.ActionPopup)window.Popup!).Rows[0].Id);
        Ui.Press(window, "ESCAPE");
        Assert.False(window.IsPopupOpen);
        window.Close();
    }

    private void The_splits_their_tabs_and_carets_come_back_when_the_folder_opens_again_and_go_when_another_opens()
    {
        File.WriteAllText(Path.Combine(folder, "Three.cs"), "class Three\n{\n    int x;\n}\n");
        string settings = TestFolders.Create("settings-");
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = settings };
        first.Show();
        first.KeepLayout();
        first.OpenFolder(folder);
        Ui.Settle();
        first.OpenFile(Path.Combine(folder, "One.cs"));
        first.OpenFile(Path.Combine(folder, "Two.cs"));
        Ui.Settle();
        first.Perform(AppActions.SplitDown);
        first.OpenFile(Path.Combine(folder, "Three.cs"));
        first.EditorArea.CodeEditor.GoTo(new TextPosition(2, 8));
        Ui.Settle();
        first.Close();

        MainWindow second = new() { Width = 1200, Height = 800, StateFolder = settings };
        second.Show();
        second.KeepLayout();
        second.OpenFolder(folder);
        Ui.Settle();
        EditorArea area = second.EditorArea;
        Assert.Equal(2, area.Groups.Count);
        Assert.Equal(["One.cs", "Two.cs"], area.Groups[0].Tabs.Select(t => t.Document.Name));
        Assert.Equal("Two.cs", area.Groups[0].Active!.Name);
        Assert.Equal(["Two.cs", "Three.cs"], area.Groups[1].Tabs.Select(t => t.Document.Name));
        Assert.Same(area.Groups[1], area.ActiveGroup);
        Assert.Equal("Three.cs", area.Active!.Name);
        Assert.Equal(new TextPosition(2, 8), area.CodeEditor.CaretPosition);
        Assert.True(area.Groups[1].TranslatePoint(default, area)!.Value.Y > area.Groups[0].TranslatePoint(default, area)!.Value.Y,
            "Split Down came back as a split beside, not below");

        string other = TestFolders.Create("other-");
        second.OpenFolder(other);
        Assert.Empty(area.Documents);
        second.OpenFolder(folder);
        Ui.Settle();
        Assert.Equal(3, area.Documents.Count);
        second.Close();
    }

    /// <summary>A file in two splits is drawn from one set of decorations: its problems, hints, folds, annotations, change
    /// bars, bookmarks and breakpoints show the same in the split not typed in, carried where the edit moved them, and a
    /// fold toggled in one split folds the other.</summary>
    private void A_file_in_two_splits_shows_the_same_decorations_after_an_edit_in_one_and_folds_in_both()
    {
        File.WriteAllText(Path.Combine(folder, "Marked.cs"), SameDecorations.Text);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, "Marked.cs"));
        Ui.Settle();
        EditorArea area = window.EditorArea;
        EditorGroup left = area.ActiveGroup;
        SameDecorations.Decorate(window, left);
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup right = area.ActiveGroup;
        Assert.NotSame(left, right);

        right.Editor.GoTo(new TextPosition(0, 0));
        window.KeyTextInput("// note");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        SameDecorations.Check(right.Editor, left.Editor, "after a line typed in the right split");

        SameDecorations.FoldMethod(left.Editor, right.Editor);
        window.Close();
    }

    private MainWindow Open(params string[] files)
    {
        MainWindow window = Ui.OpenFolder(folder);
        foreach (string file in files)
        {
            window.OpenFile(Path.Combine(folder, file));
        }
        Ui.Settle();
        return window;
    }

    /// <summary>Splits the file shown off to the right, opens Find there and closes the split's tab by its close button, the
    /// file still open on the left: the split gone, held by nothing here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<EditorGroup> SplitFindAndClose(MainWindow window)
    {
        EditorArea area = window.EditorArea;
        Assert.True(window.Perform(AppActions.SplitRight));
        Ui.Settle();
        EditorGroup right = area.ActiveGroup;
        Assert.True(window.Perform(AppActions.Find));
        Ui.Settle();
        Assert.True(right.Find.IsVisible);
        Button close = Tab(right, "Two.cs").GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains(StyleClasses.TabClose));
        Ui.Click(close);
        // The pointer moves on, as a person's does: until it does, the pointer still rests on the closed tab's button.
        window.MouseMove(new Point(1, 1));
        Ui.Settle();
        Assert.Single(area.Groups);
        Assert.NotSame(right, area.ActiveGroup);
        return new WeakReference<EditorGroup>(right);
    }

    private static Control Tab(EditorGroup group, string name) =>
        group.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains(StyleClasses.TabName)).FindAncestorOfType<Border>()!;
}

/// <summary>What two editors of one file must both show, and the file that shows it.</summary>
internal static class SameDecorations
{
    public const string Text = "class Marked\n{\n    void Run(int count)\n    {\n        Run(1);\n        int unused = 2;\n    }\n}\n";

    /// <summary>Gives the file shown in a group what the IDE's services give one: a warning, a hint, an annotation and a
    /// change bar set on it as theirs are, and a bookmark and a breakpoint toggled by their actions.</summary>
    public static void Decorate(MainWindow window, EditorGroup group)
    {
        OpenDocument document = group.Active!;
        CodeEditor editor = group.Editor;
        Ui.WaitFor(() => editor.FoldRegions.Count == 2, "the class's and the method's regions");
        int unused = Text.IndexOf("unused", StringComparison.Ordinal);
        document.Markers = [new Marker(new TextRange(unused, "unused".Length), MarkerKind.Warning, "CS0219: The variable is assigned but its value is never used")];
        document.InlayHints = [new InlayHint(Text.IndexOf("1);", StringComparison.Ordinal), "count:", PadRight: true)];
        document.LineAnnotations = [new LineAnnotation(4, "count = 1", LineAnnotationLook.Muted)];
        document.ChangeMarks = [new ChangeMark(5, 1, ChangeMarkKind.Added)];
        editor.GoTo(new TextPosition(4, 8));
        Assert.True(window.Perform(AppActions.ToggleBookmark));
        editor.GoTo(new TextPosition(5, 8));
        Assert.True(window.Perform(AppActions.ToggleBreakpoint));
        Ui.Settle();
    }

    /// <summary>The editor typed in and the other show the same of each, what moves with the text a line down from where it
    /// was set.</summary>
    public static void Check(CodeEditor typed, CodeEditor other, string where)
    {
        // The problems stay where they were set, as their service carries them, and not without one.
        Assert.True(typed.Markers.Count == 1 && typed.Markers.SequenceEqual(other.Markers) && typed.ProblemCount == other.ProblemCount,
            $"The problems {where} are [{string.Join(", ", typed.Markers)}] in one editor and [{string.Join(", ", other.Markers)}] in the other");
        Same(typed.InlayHints.Select(h => h.Offset), other.InlayHints.Select(h => h.Offset), "inlay hints", where, Text.IndexOf("1);", StringComparison.Ordinal) + 8);
        Same(typed.LineAnnotations.Select(a => a.Line), other.LineAnnotations.Select(a => a.Line), "annotations", where, 5);
        Same(typed.Bookmarks, other.Bookmarks, "bookmarks", where, 5);
        Same(typed.LineMarks.Select(m => m.Line), other.LineMarks.Select(m => m.Line), "breakpoints", where, 6);
        Assert.True(typed.ChangeMarks.Count > 0 && typed.ChangeMarks.SequenceEqual(other.ChangeMarks),
            $"The change bars {where} are [{string.Join(", ", typed.ChangeMarks)}] in one editor and [{string.Join(", ", other.ChangeMarks)}] in the other");
        Assert.True(typed.FoldRegions.Count == 2 && typed.FoldRegions.SequenceEqual(other.FoldRegions),
            $"The folds {where} are [{string.Join(", ", typed.FoldRegions)}] in one editor and [{string.Join(", ", other.FoldRegions)}] in the other");
    }

    /// <summary>The method folded in one editor is folded in the other, which shows as few rows, and unfolded there opens
    /// in the first.</summary>
    public static void FoldMethod(CodeEditor one, CodeEditor other)
    {
        int rows = other.RowsShown;
        one.GoTo(new TextPosition(4, 0));
        Assert.True(one.TryExecute(EditorActions.ToggleFold));
        Ui.Settle();
        Assert.True(other.FoldRegions.Count(r => r.Folded) == 1 && other.RowsShown == one.RowsShown && other.RowsShown < rows,
            $"The method folded in one editor shows {other.RowsShown} rows of {rows} in the other, folded [{string.Join(", ", other.FoldRegions.Where(r => r.Folded))}]");
        Assert.True(other.TryExecute(EditorActions.UnfoldAll));
        Ui.Settle();
        Assert.True(!one.FoldRegions.Any(r => r.Folded) && one.RowsShown == rows, $"The method unfolded in the other editor stays folded in the first, which shows {one.RowsShown} rows of {rows}");
    }

    private static void Same<T>(IEnumerable<T> typed, IEnumerable<T> other, string what, string where, T moved)
    {
        T[] shown = [.. typed];
        T[] there = [.. other];
        Assert.True(shown.SequenceEqual([moved]) && there.SequenceEqual([moved]),
            $"The {what} {where} are at [{string.Join(", ", shown)}] in one editor and [{string.Join(", ", there)}] in the other, where each should be at {moved}");
    }
}

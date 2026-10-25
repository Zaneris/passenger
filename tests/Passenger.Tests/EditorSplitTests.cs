using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Split editors: Split Right shows a file twice over one text, each split with its own caret; a tab dragged
/// to a split's edge splits there and onto another's strip moves into it; Close takes the tab of the split worked in
/// alone, and a split whose last tab goes gives its room back.</summary>
public sealed class EditorSplitTests : IDisposable
{
    private readonly string folder = TestFolders.Create("splits-");

    public EditorSplitTests()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n}\n");
        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two\n{\n}\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Split_right_shows_the_file_in_both_splits_over_one_text_each_with_its_own_caret()
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

    [AvaloniaFact]
    public void Close_takes_the_tab_from_the_split_worked_in_alone_and_the_split_goes_with_its_last_tab()
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

    [AvaloniaFact]
    public void A_closed_split_is_let_go_by_the_file_it_showed_and_the_split_left_still_follows_the_file()
    {
        MainWindow window = Open("One.cs", "Two.cs");
        WeakReference<EditorGroup> closed = SplitFindAndClose(window);

        window.KeyTextInput("// ");
        Ui.Settle();
        EditorGroup left = Assert.Single(window.EditorArea.Groups);
        Assert.StartsWith("// class Two", left.Editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.True(Tab(left, "Two.cs").GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Classes.Contains("modified")).IsVisible,
            "The tab of the file typed in shows no mark that it changed");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(closed.TryGetTarget(out _), "The closed split (its editor, find bar and tabs) is still alive, held by the file it showed");
        window.Close();
    }

    /// <summary>A split scrolled to its file's end while another split deletes all but its first lines: the first draws the
    /// lines left, where its view stood past the last of them, blank, gutter and all, until scrolled.</summary>
    [AvaloniaFact]
    public void A_split_scrolled_to_the_files_end_draws_the_lines_left_once_another_split_deletes_most_of_them()
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

    [AvaloniaFact]
    public void A_tab_dragged_to_its_splits_right_edge_splits_it_off_and_back_onto_the_first_strip_joins_it_again()
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

    [AvaloniaFact]
    public void The_divider_between_two_splits_grabbed_either_side_of_its_line_gives_one_the_room_the_other_gives_up_beside_and_below()
    {
        MainWindow window = Open("One.cs");
        EditorArea area = window.EditorArea;
        Assert.True(window.Perform(AppActions.SplitRight));
        Assert.True(window.Perform(AppActions.SplitDown));
        Ui.Settle();
        Assert.Equal(3, area.Groups.Count);
        EditorGroup left = area.Groups[0];
        EditorGroup top = area.Groups[1];
        EditorGroup bottom = area.Groups[2];
        (double leftWidth, double rightWidth) = (left.Bounds.Width, top.Bounds.Width);
        (double topHeight, double bottomHeight) = (top.Bounds.Height, bottom.Bounds.Height);

        // Grabbed 3 px right of its line, over the split after it.
        Point across = Ui.Centre(Divider(area, GridResizeDirection.Columns), window) + new Point(3, 0);
        Ui.Drag(window, across, across - new Point(120, 0));
        Assert.True(Math.Abs(left.Bounds.Width - (leftWidth - 120)) < 2 && Math.Abs(top.Bounds.Width - (rightWidth + 120)) < 2,
            $"The splits beside each other were {leftWidth} and {rightWidth} wide, and {left.Bounds.Width} and {top.Bounds.Width} once their divider, grabbed right of its line, was dragged 120 px left");

        // Grabbed 3 px below its line, and 2 px above.
        Point down = Ui.Centre(Divider(area, GridResizeDirection.Rows), window) + new Point(0, 3);
        Ui.Drag(window, down, down + new Point(0, 80));
        Assert.True(Math.Abs(top.Bounds.Height - (topHeight + 80)) < 2 && Math.Abs(bottom.Bounds.Height - (bottomHeight - 80)) < 2,
            $"The splits one over the other were {topHeight} and {bottomHeight} tall, and {top.Bounds.Height} and {bottom.Bounds.Height} once their divider, grabbed below its line, was dragged 80 px down");
        Point up = Ui.Centre(Divider(area, GridResizeDirection.Rows), window) - new Point(0, 2);
        Ui.Drag(window, up, up - new Point(0, 40));
        Assert.True(Math.Abs(top.Bounds.Height - (topHeight + 40)) < 2 && Math.Abs(bottom.Bounds.Height - (bottomHeight - 40)) < 2,
            $"The splits one over the other were {topHeight + 80} and {bottomHeight - 80} tall, and {top.Bounds.Height} and {bottom.Bounds.Height} once their divider, grabbed above its line, was dragged 40 px up");
        window.Close();
    }

    [AvaloniaFact]
    public void Find_action_finds_an_action_with_no_shortcut_by_name_and_runs_it()
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

    [AvaloniaFact]
    public void Find_action_finds_split_right_and_down_by_riders_older_names_split_vertically_and_horizontally()
    {
        MainWindow window = Open("One.cs");
        foreach ((string typed, string action, int groups) in (ReadOnlySpan<(string, string, int)>)[("Split Vertically", AppActions.SplitRight, 2), ("split horiz", AppActions.SplitDown, 3)])
        {
            Ui.Run(window, AppActions.FindAction);
            Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
            popup.Query = typed;
            Ui.Settle();
            Assert.True(popup.Rows.Count > 0 && popup.Rows[0].Id == action,
                $"Find Action offered [{string.Join(", ", popup.Rows.Take(3).Select(r => r.Title))}] for '{typed}', Rider's older name for {action}");
            Ui.Press(window, "ENTER");
            Assert.Equal(groups, window.EditorArea.Groups.Count);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_splits_their_tabs_and_carets_come_back_when_the_folder_opens_again_and_go_when_another_opens()
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
        Assert.Equal(3, area.Documents.Count);
        second.Close();
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
        Button close = Tab(right, "Two.cs").GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose"));
        Ui.Click(close);
        // The pointer moves on, as a person's does: until it does, the pointer still rests on the closed tab's button.
        window.MouseMove(new Point(1, 1));
        Ui.Settle();
        Assert.Single(area.Groups);
        Assert.NotSame(right, area.ActiveGroup);
        return new WeakReference<EditorGroup>(right);
    }

    private static Control Tab(EditorGroup group, string name) =>
        group.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains("tabName")).FindAncestorOfType<Border>()!;

    private static GridSplitter Divider(EditorArea area, GridResizeDirection direction) =>
        area.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.ResizeDirection == direction);
}

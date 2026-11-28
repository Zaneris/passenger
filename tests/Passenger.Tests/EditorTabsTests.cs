using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Window > Editor Tabs and the tab's own menu: a pinned tab stands left of the others with a pin, stays
/// through Close Other Tabs and Close All Tabs and comes back pinned with the folder; Reopen Closed Tab brings tabs
/// back in the reverse of the order they closed, each where it stood with its caret; closing the tab shown shows the one
/// shown before it, its neighbour only where none was; Close Tabs to the Left and to the Right leave the pinned ones;
/// Unsplit and Unsplit All join the splits, keeping every tab; the open tab of a changed file shows its dot as the
/// others do.</summary>
[SameOnEverySystem]
public sealed class EditorTabsTests : IDisposable
{
    private static readonly string[] Five = ["One.cs", "Two.cs", "Three.cs", "Four.cs", "Five.cs"];

    private readonly string folder = TestFolders.Create("tabs-");
    private readonly string settings = TestFolders.Create("settings-");

    public EditorTabsTests()
    {
        foreach (string file in Five)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            File.WriteAllText(Path.Combine(folder, file), $"class {name}\n{{\n    int first;\n    int second;\n    int third;\n}}\n");
        }
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>Editor tabs: a pinned tab stands left through Close Others and Close All and comes back pinned, Reopen
    /// Closed Tab brings tabs back in reverse order each with its caret, a tab scrolled to a file's end draws the lines
    /// left once it is shorter on disk, closing the tab shown shows the one shown before it, Close Tabs to the Right
    /// and Close Other Tabs end on the tab chosen, Unsplit joins splits keeping each tab and caret, and the strip's end
    /// lists the tabs out of sight, which the wheel scrolls into view.</summary>
    [AvaloniaFact]
    public void Editor_tabs_open_close_pin_and_come_back_as_a_person_left_them()
    {
        Flow.StepApart<EditorTabsTests>("a pinned tab stands left with a pin stays through Close Others and Close All and comes back pinned with the folder", t => t.A_pinned_tab_stands_left_with_a_pin_stays_through_Close_Others_and_Close_All_and_comes_back_pinned_with_the_folder());
        Flow.StepApart<EditorTabsTests>("reopen Closed Tab brings tabs back in the reverse of the order they closed each where it stood with its caret", t => t.Reopen_Closed_Tab_brings_tabs_back_in_the_reverse_of_the_order_they_closed_each_where_it_stood_with_its_caret());
        Flow.StepApart<EditorTabsTests>("a tab left scrolled to its file's end draws the lines left once the file is far shorter on disk", t => t.A_tab_left_scrolled_to_its_files_end_draws_the_lines_left_once_the_file_is_far_shorter_on_disk());
        Flow.StepApart<EditorTabsTests>("closing the tab shown shows the one shown before it with its caret rather than its neighbour", t => t.Closing_the_tab_shown_shows_the_one_shown_before_it_with_its_caret_rather_than_its_neighbour());
        Flow.StepApart<EditorTabsTests>("close Tabs to the Right and Close Other Tabs end showing the tab they were chosen on", t => t.Close_Tabs_to_the_Right_and_Close_Other_Tabs_end_showing_the_tab_they_were_chosen_on());
        Flow.StepApart<EditorTabsTests>("unsplit joins a split into the group worked in and Unsplit All joins every split keeping each tab and its caret", t => t.Unsplit_joins_a_split_into_the_group_worked_in_and_Unsplit_All_joins_every_split_keeping_each_tab_and_its_caret());
        Flow.StepApart<EditorTabsTests>("the button at the strip's end lists the tabs out of its sight and goes to the one chosen", t => t.The_button_at_the_strips_end_lists_the_tabs_out_of_its_sight_and_goes_to_the_one_chosen());
    }

    private void A_pinned_tab_stands_left_with_a_pin_stays_through_Close_Others_and_Close_All_and_comes_back_pinned_with_the_folder()
    {
        MainWindow first = Keeping("One.cs", "Two.cs", "Three.cs", "Four.cs");
        EditorGroup group = first.EditorArea.ActiveGroup;
        Assert.Equal(["One.cs", "Two.cs", "Three.cs", "Four.cs"], Names(group));

        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Pin Tab");
        Assert.Equal(["Three.cs", "One.cs", "Two.cs", "Four.cs"], Names(group));
        Assert.True(PinShown(group, "Three.cs"), "The pinned tab shows no pin");
        Assert.False(PinShown(group, "One.cs"), "A tab never pinned shows a pin");
        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Unpin Tab");
        Assert.Equal(["Three.cs", "One.cs", "Two.cs", "Four.cs"], Names(group));
        Assert.False(PinShown(group, "Three.cs"), "The tab unpinned still shows its pin");
        Ui.ChooseFromMenu(Tab(group, "Three.cs"), "Pin Tab");

        first.OpenFile("Three.cs");
        first.OpenFile("Five.cs");
        Ui.Settle();
        Assert.Equal(["Three.cs", "Five.cs", "One.cs", "Two.cs", "Four.cs"], Names(group));

        first.OpenFile("Two.cs");
        FromEditorTabs(first, "Close Other Tabs");
        Assert.Equal(["Three.cs", "Two.cs"], Names(group));
        FromEditorTabs(first, "Close All Tabs");
        Assert.Equal(["Three.cs"], Names(group));
        Assert.Equal(["Three.cs"], first.EditorArea.Documents.Select(d => d.Name));
        Assert.False(MenuItem(first, "Close All Tabs").IsEnabled, "Close All Tabs can still run with only a pinned tab open");
        first.OpenFile("One.cs");
        Ui.Settle();
        first.Close();

        MainWindow second = Keeping();
        EditorGroup reopened = second.EditorArea.ActiveGroup;
        Assert.Equal(["Three.cs", "One.cs"], Names(reopened));
        Assert.True(reopened.IsPinned(reopened.Tabs[0].Document), "Three.cs came back with the folder but no longer pinned");
        Assert.False(reopened.IsPinned(reopened.Tabs[1].Document));
        Assert.True(PinShown(reopened, "Three.cs"), "Three.cs came back pinned but shows no pin");
        second.Close();
    }

    private void Reopen_Closed_Tab_brings_tabs_back_in_the_reverse_of_the_order_they_closed_each_where_it_stood_with_its_caret()
    {
        MainWindow window = Keeping(Five);
        EditorArea area = window.EditorArea;
        EditorGroup group = area.ActiveGroup;
        Assert.False(MenuItem(window, "Reopen Closed Tab").IsEnabled, "Reopen Closed Tab can run with no tab closed");

        CloseAt(window, "Two.cs", new TextPosition(2, 4));
        CloseAt(window, "Four.cs", new TextPosition(3, 8));
        CloseAt(window, "One.cs", new TextPosition(4, 2));
        Assert.Equal(["Three.cs", "Five.cs"], Names(group));

        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(["One.cs", "Three.cs", "Five.cs"], Names(group));
        Assert.Equal(("One.cs", new TextPosition(4, 2)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(["One.cs", "Three.cs", "Four.cs", "Five.cs"], Names(group));
        Assert.Equal(("Four.cs", new TextPosition(3, 8)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(Five, Names(group));
        Assert.Equal(("Two.cs", new TextPosition(2, 4)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        Assert.False(MenuItem(window, "Reopen Closed Tab").IsEnabled, "Reopen Closed Tab can still run with every closed tab back");

        EditorGroup left = group;
        window.OpenFile("Four.cs");
        FromEditorTabs(window, "Split Right");
        EditorGroup right = area.ActiveGroup;
        Ui.ChooseFromMenu(Tab(left, "Five.cs"), "Move to Opposite Group");
        Assert.Equal(["Four.cs", "Five.cs"], Names(right));
        CloseAt(window, "Five.cs", new TextPosition(1, 0));
        Assert.Equal(["Four.cs"], Names(right));
        left.Editor.Focus();
        Ui.Settle();
        Assert.Same(left, area.ActiveGroup);
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(["Four.cs", "Five.cs"], Names(right));
        Assert.Same(right, area.ActiveGroup);
        Assert.DoesNotContain("Five.cs", Names(left));
        window.Close();
    }

    /// <summary>A file left scrolled to its end, then made far shorter on disk (a checkout, an agent writing it): its tab
    /// draws the lines left whether it is shown as the file changes, shown again from behind another tab, or closed and
    /// reopened, where its view stood past the last of them, blank, gutter and all, until scrolled.</summary>
    private void A_tab_left_scrolled_to_its_files_end_draws_the_lines_left_once_the_file_is_far_shorter_on_disk()
    {
        string path = Path.Combine(folder, "Long.cs");
        string full = string.Concat(Enumerable.Range(1, 300).Select(i => $"// line {i}\n"));
        const string Short = "// line 1\n// line 2\n// line 3\n";
        File.WriteAllText(path, full);
        MainWindow window = Keeping("Long.cs");
        EditorArea area = window.EditorArea;
        string Long() => area.Documents.Single(d => d.Name == "Long.cs").Text.Snapshot.ToString();
        void AtTheEnd()
        {
            File.WriteAllText(path, full);
            Ui.WaitFor(() => Long() == full, "the file's every line from disk");
            area.CodeEditor.GoTo(new TextPosition(299, 0));
            Ui.Settle();
            Assert.True(area.CodeEditor.ScrollOffset.Y > 200 * area.CodeEditor.LineHeight, "Going to the last line left the view at the file's top");
        }

        AtTheEnd();
        File.WriteAllText(path, Short);
        Ui.WaitFor(() => Long() == Short, "the shorter text from disk");
        Ui.DrawsText(window, area.CodeEditor, "the file shown was made shorter on disk");

        AtTheEnd();
        window.OpenFile("One.cs");
        Ui.Settle();
        File.WriteAllText(path, Short);
        Ui.WaitFor(() => Long() == Short, "the shorter text from disk");
        window.OpenFile("Long.cs");
        Ui.DrawsText(window, area.CodeEditor, "the file behind another tab was made shorter on disk and shown again");

        AtTheEnd();
        Ui.Run(window, AppActions.CloseTab);
        Assert.DoesNotContain(area.Documents, d => d.Name == "Long.cs");
        File.WriteAllText(path, Short);
        FromEditorTabs(window, "Reopen Closed Tab");
        Assert.Equal(Short, Long());
        Ui.DrawsText(window, area.CodeEditor, "the file was closed, made shorter on disk and reopened");
        window.Close();
    }

    /// <summary>A file opened from another (by Go to File, as Go to Declaration opens one) comes in a tab just after it;
    /// Close Tab's keys there show the file it was opened from, its caret where it was, never the tab that took the closed
    /// one's place, and a close after that shows the tab shown before it again.</summary>
    private void Closing_the_tab_shown_shows_the_one_shown_before_it_with_its_caret_rather_than_its_neighbour()
    {
        MainWindow window = Keeping("One.cs", "Two.cs", "Three.cs");
        EditorArea area = window.EditorArea;
        EditorGroup group = area.ActiveGroup;
        Ui.Click(TabName(group, "One.cs"));
        Assert.Equal("One.cs", area.Active?.Name);
        area.CodeEditor.GoTo(new TextPosition(3, 8));
        Ui.Settle();

        Ui.Press(window, Ui.Shortcut(AppActions.GoToFile));
        window.KeyTextInput("Four");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.Equal(["One.cs", "Four.cs", "Two.cs", "Three.cs"], Names(group));
        Assert.Equal("Four.cs", area.Active?.Name);

        Ui.Press(window, Ui.Shortcut(AppActions.CloseTab));
        Assert.True(area.Active?.Name == "One.cs", $"Closing Four.cs, opened from One.cs, showed {area.Active?.Name}, not the file it came from");
        Assert.Equal(new TextPosition(3, 8), area.CodeEditor.CaretPosition);

        // Three.cs was the tab shown last before One.cs; Two.cs is only its neighbour.
        Ui.Press(window, Ui.Shortcut(AppActions.CloseTab));
        Assert.True(area.Active?.Name == "Three.cs", $"Closing One.cs showed {area.Active?.Name}, not Three.cs, the tab shown before it");
        Assert.Equal(["Two.cs", "Three.cs"], Names(group));
        window.Close();
    }

    /// <summary>Close Tabs to the Right and Close Other Tabs, chosen on a tab not shown, end showing that tab, the one
    /// they keep, rather than the tab shown before them (left open) or a pinned tab shown more lately.</summary>
    private void Close_Tabs_to_the_Right_and_Close_Other_Tabs_end_showing_the_tab_they_were_chosen_on()
    {
        MainWindow window = Keeping(Five);
        EditorArea area = window.EditorArea;
        EditorGroup group = area.ActiveGroup;
        Ui.ChooseFromMenu(Tab(group, "One.cs"), "Pin Tab");
        Ui.Click(TabName(group, "Three.cs"));
        Ui.Click(TabName(group, "One.cs"));
        Assert.Equal("One.cs", area.Active?.Name);

        Ui.ChooseFromMenu(Tab(group, "Four.cs"), "Close Tabs to the Right");
        Assert.Equal(["One.cs", "Two.cs", "Three.cs", "Four.cs"], Names(group));
        Assert.True(area.Active?.Name == "Four.cs", $"Close Tabs to the Right of Four.cs showed {area.Active?.Name}, not Four.cs, the tab it kept");

        // Closing Three.cs alone would show One.cs, pinned and shown just before it.
        Ui.Click(TabName(group, "One.cs"));
        Ui.Click(TabName(group, "Three.cs"));
        Ui.ChooseFromMenu(Tab(group, "Two.cs"), "Close Other Tabs");
        Assert.Equal(["One.cs", "Two.cs"], Names(group));
        Assert.True(area.Active?.Name == "Two.cs", $"Close Other Tabs on Two.cs showed {area.Active?.Name}, not Two.cs, the tab it kept");
        window.Close();
    }

    private void Unsplit_joins_a_split_into_the_group_worked_in_and_Unsplit_All_joins_every_split_keeping_each_tab_and_its_caret()
    {
        MainWindow window = Keeping("One.cs", "Two.cs");
        EditorArea area = window.EditorArea;
        Assert.False(MenuItem(window, "Unsplit All").IsEnabled, "Unsplit All can run on an editor that is not split");
        FromEditorTabs(window, "Split Right");
        window.OpenFile("Three.cs");
        FromEditorTabs(window, "Split Down");
        window.OpenFile("Four.cs");
        area.CodeEditor.GoTo(new TextPosition(3, 6));
        Ui.Settle();
        Assert.Equal([["One.cs", "Two.cs"], ["Two.cs", "Three.cs"], ["Three.cs", "Four.cs"]], area.Groups.Select(Names));
        EditorGroup bottom = area.ActiveGroup;

        FromEditorTabs(window, "Unsplit");
        Assert.Equal([["One.cs", "Two.cs"], ["Three.cs", "Four.cs", "Two.cs"]], area.Groups.Select(Names));
        Assert.Same(bottom, area.ActiveGroup);

        FromEditorTabs(window, "Unsplit All");
        EditorGroup joined = Assert.Single(area.Groups);
        Assert.Same(bottom, joined);
        Assert.Equal(["Three.cs", "Four.cs", "Two.cs", "One.cs"], Names(joined));
        Assert.Equal(4, area.Documents.Count);
        Assert.Equal(("Four.cs", new TextPosition(3, 6)), (area.Active?.Name, area.CodeEditor.CaretPosition));
        window.Close();
    }

    /// <summary>More tabs than the strip shows: the button at its end appears, lists the tabs out of its sight with their
    /// files' icons, narrows them as a name is typed and goes to the one chosen by Enter or by Down and Enter, which comes
    /// into the strip's sight; the wheel scrolls the strip across; with the tabs fitting again it is gone.</summary>
    private void The_button_at_the_strips_end_lists_the_tabs_out_of_its_sight_and_goes_to_the_one_chosen()
    {
        string[] many = [.. Enumerable.Range(1, 16).Select(i => $"Component{i:00}.cs")];
        foreach (string file in many)
        {
            File.WriteAllText(Path.Combine(folder, file), $"class {Path.GetFileNameWithoutExtension(file)}\n{{\n}}\n");
        }
        MainWindow window = Keeping("One.cs");
        window.Width = 900;
        EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Settle();
        Assert.False(group.HiddenTabsButton.IsVisible, "The list of hidden tabs shows with every tab in sight");
        foreach (string file in many)
        {
            window.OpenFile(file);
        }
        Ui.Settle();
        string[] hidden = [.. group.HiddenTabs.Select(t => t.Document.Name)];
        Assert.True(hidden.Length > 2 && !hidden.Contains("Component16.cs") && group.HiddenTabsButton.IsEffectivelyVisible,
            $"With {group.Tabs.Count} tabs the strip hides [{string.Join(", ", hidden)}] and the button {(group.HiddenTabsButton.IsEffectivelyVisible ? "shows" : "is hidden")}");

        Ui.Click(group.HiddenTabsButton);
        HiddenTabsPopup popup = Assert.IsType<HiddenTabsPopup>(window.Popup);
        Assert.Equal(hidden, popup.Rows.Select(r => r.Name));
        Assert.True(popup.GetVisualDescendants().OfType<RowIcon>().Any(i => i.IsEffectivelyVisible && (i.Data is not null || i.DataKey is { } key && i.FindResource(key) is Geometry)), "The hidden tabs' rows show no file icon");
        window.KeyTextInput("Component03");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.False(window.IsPopupOpen, "Enter on a hidden tab left the list open");
        Assert.Equal("Component03.cs", window.EditorArea.Active?.Name);
        Assert.DoesNotContain("Component03.cs", group.HiddenTabs.Select(t => t.Document.Name));

        Ui.Click(group.HiddenTabsButton);
        string second = Assert.IsType<HiddenTabsPopup>(window.Popup).Rows[1].Name;
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.Equal(second, window.EditorArea.Active?.Name);

        // The wheel over the strip scrolls it across, the tab shown staying the one shown.
        Point over = group.TabStrip.TranslatePoint(new Point(group.TabStrip.Bounds.Width / 2, group.TabStrip.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(over, new Vector(0, -100));
        Ui.Settle();
        string[] right = [.. group.HiddenTabs.Select(t => t.Document.Name)];
        window.MouseWheel(over, new Vector(0, 100));
        Ui.Settle();
        string[] left = [.. group.HiddenTabs.Select(t => t.Document.Name)];
        Assert.True(right.Contains("One.cs") && !right.Contains("Component16.cs") && left.Contains("Component16.cs") && !left.Contains("One.cs")
            && window.EditorArea.Active?.Name == second,
            $"The wheel turned down left [{string.Join(", ", right)}] out of sight and turned up [{string.Join(", ", left)}], showing {window.EditorArea.Active?.Name}, "
            + $"where the strip goes to its end and back to its start, {second} shown throughout");

        foreach (string file in many)
        {
            window.EditorArea.CloseTab(group, window.EditorArea.Documents.First(d => d.Name == file));
        }
        Ui.Settle();
        Assert.False(group.HiddenTabsButton.IsVisible, "The list of hidden tabs still shows once every tab is in sight");
        window.Close();
    }

    /// <summary>The window a person uses, keeping its state in the test's own folder, on the folder with these files open.</summary>
    private MainWindow Keeping(params string[] files)
    {
        MainWindow window = new() { Width = 1400, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        foreach (string file in files)
        {
            window.OpenFile(file);
        }
        Ui.Settle();
        return window;
    }

    /// <summary>Shows a file's tab, puts its caret somewhere, and closes it by its keys.</summary>
    private static void CloseAt(MainWindow window, string file, TextPosition caret)
    {
        window.OpenFile(file);
        window.EditorArea.CodeEditor.GoTo(caret);
        Ui.Settle();
        Ui.Run(window, AppActions.CloseTab);
    }

    /// <summary>An item of Window > Editor Tabs as the menu bar shows it once opened.</summary>
    private static NativeMenuItem MenuItem(MainWindow window, string title) =>
        Ui.Submenu(Ui.Submenu(window.MenuBar, "Window"), "Editor Tabs").Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == title)
            ?? throw new Xunit.Sdk.XunitException($"Window > Editor Tabs has no {title}");

    /// <summary>Chooses an item of Window > Editor Tabs, failing if it cannot run.</summary>
    private static void FromEditorTabs(MainWindow window, string title)
    {
        NativeMenuItem item = MenuItem(window, title);
        Assert.True(item.IsEnabled, $"Window > Editor Tabs > {title} cannot run here");
        item.Command!.Execute(null);
        Ui.Settle();
    }

    private static string[] Names(EditorGroup group) => [.. group.Tabs.Select(t => t.Document.Name)];

    private static Control Tab(EditorGroup group, string name) => TabName(group, name).FindAncestorOfType<Border>()!;

    /// <summary>The name a tab shows, where a click on it shows its file.</summary>
    private static TextBlock TabName(EditorGroup group, string name) =>
        group.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == name && t.Classes.Contains(StyleClasses.TabName));

    private static Button TabClose(EditorGroup group, string name) =>
        Tab(group, name).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains(StyleClasses.TabClose));

    /// <summary>Whether a control is drawn: shown, and not faded out as a tab's × and dot are while the other shows.</summary>
    private static bool Shows(Control control) => control.IsEffectivelyVisible && control.Opacity == 1;

    /// <summary>Whether a tab shows its pin, the small button that unpins it.</summary>
    private static bool PinShown(EditorGroup group, string name) =>
        Tab(group, name).GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Unpin Tab").IsEffectivelyVisible;
}

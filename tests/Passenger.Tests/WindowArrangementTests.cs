using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>The window's arrangement as a person meets it: each a setting whose default a theme may give and the person's
/// own choice wins over. The editor's tabs down a side, gone between by a click, a drag and the keys.</summary>
public sealed class WindowArrangementTests : IDisposable
{
    private readonly string folder = TestFolders.Create("arrangement-");

    public WindowArrangementTests()
    {
        foreach (string name in (string[])["One.cs", "Two.cs", "Three.cs"])
        {
            File.WriteAllText(Path.Combine(folder, name), $"class {Path.GetFileNameWithoutExtension(name)} {{ }}\n");
        }
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Editor_tabs_set_down_a_side_stand_in_a_column_there_where_a_click_a_drag_and_the_next_tabs_keys_go_between_them()
    {
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { EditorTabs = EditorTabsPlace.Left });
            Ui.Settle();
            EditorGroup group = window.EditorArea.ActiveGroup;
            Rect strip = Placed(window, group.TabStrip);
            Rect editor = Placed(window, group.Editor);
            Assert.True(strip.Right <= editor.X + 1 && strip.Height >= editor.Height - 1,
                $"Set to the left, the tabs stand at {strip}, beside the editor at {editor} rather than down its left side");
            Assert.True(Tabs(group).Select(t => Placed(window, t).Y).Order().SequenceEqual(Tabs(group).Select(t => Placed(window, t).Y)) && Tabs(group).Select(t => Placed(window, t).X).Distinct().Count() == 1,
                $"The tabs down the side are not one column: {string.Join(", ", Tabs(group).Select(t => Placed(window, t)))}");

            Ui.Click(Tab(group, "One.cs"));
            Assert.Equal("One.cs", group.Active?.Name);
            Ui.Press(window, window.Actions.Keymap.ShortcutsFor(AppActions.NextTab)[0].Notation());
            Assert.Equal("Two.cs", group.Active?.Name);

            Rect one = Placed(window, Tab(group, "One.cs"));
            Rect three = Placed(window, Tab(group, "Three.cs"));
            Ui.Drag(window, one.Center, new Point(three.Center.X, three.Bottom - 2));
            Assert.True(group.Tabs.Select(t => t.Document.Name).SequenceEqual(["Two.cs", "Three.cs", "One.cs"]),
                $"One.cs dragged down past Three.cs left the column as {string.Join(", ", group.Tabs.Select(t => t.Document.Name))}");

            window.ApplySettings(window.Settings with { EditorTabs = EditorTabsPlace.Right });
            Ui.Settle();
            strip = Placed(window, group.TabStrip);
            editor = Placed(window, group.Editor);
            Assert.True(strip.X >= editor.Right - 1, $"Set to the right, the tabs stand at {strip}, not right of the editor at {editor}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_themes_editor_tabs_place_is_the_default_the_Settings_row_overrides_and_Theme_default_follows_again()
    {
        string name = Kept("""{"editorTabs": "right"}""");
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            EditorGroup group = window.EditorArea.ActiveGroup;
            Assert.True(group.TabsPlace == EditorTabsPlace.Right && Placed(window, group.TabStrip).X >= Placed(window, group.Editor).Right - 1,
                $"The theme gives the tabs the right, where they stand {group.TabsPlace} at {Placed(window, group.TabStrip)}");

            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ComboBox row = SettingsTests.Editor<ComboBox>(dialog, "Editor tabs");
            Assert.Equal("Theme default (Right)", row.SelectedItem as string);
            row.SelectedItem = "Top";
            dialog.Ok();
            Ui.Settle();
            Assert.True(window.Settings.EditorTabs == EditorTabsPlace.Top && group.TabsPlace == EditorTabsPlace.Top,
                $"The person chose Top in Settings over the theme's right, and the tabs stand {group.TabsPlace}");

            dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            SettingsTests.Editor<ComboBox>(dialog, "Editor tabs").SelectedIndex = 0;
            dialog.Ok();
            Ui.Settle();
            Assert.True(window.Settings.EditorTabs is null && group.TabsPlace == EditorTabsPlace.Right,
                $"Theme default chosen again, the tabs stand {group.TabsPlace} where the theme gives the right");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Titled_tabs_name_the_windows_docked_in_a_place_where_a_click_opens_one_and_a_drag_takes_it_to_another_side()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            dock.Show(ToolWindowIds.Build);
            Ui.Settle();
            ToolAnchor place = dock[ToolWindowIds.Build].Anchor;
            Assert.Empty(ToolTabs(window));

            window.ApplySettings(window.Settings with { ToolWindowLook = ToolWindowLook.TitledTabs });
            Ui.Settle();
            string[] docked = [.. dock.Windows.Where(w => w.Anchor == place && w.Mode == ToolMode.Docked).OrderBy(w => w.Order).Select(w => w.Id)];
            string[] named = [.. ToolTabs(window).Select(t => (string)t.Tag!).Where(id => dock[id].Anchor == place)];
            Assert.True(named.SequenceEqual(docked), $"The tabs along the place's foot name {string.Join(", ", named)}, where {string.Join(", ", docked)} dock there");
            Assert.True(ToolTab(window, ToolWindowIds.Build).Classes.Contains(StyleClasses.Active), "Build, open, has no open tab");

            Ui.Click(ToolTab(window, ToolWindowIds.Run));
            Assert.True(dock.IsShown(ToolWindowIds.Run) && !dock.IsShown(ToolWindowIds.Build), "A click on Run's tab did not open Run in Build's place");

            Ui.Drag(window, Ui.Centre(ToolTab(window, ToolWindowIds.Run), window), new Point(window.Bounds.Width - 20, 120));
            Assert.True(dock[ToolWindowIds.Run] is { Anchor: ToolAnchor.RightTop, IsShown: true },
                $"Run's tab dragged to the right side left it {dock[ToolWindowIds.Run].Anchor}");
            Assert.True(Ui.Centre(ToolTab(window, ToolWindowIds.Run), window).X > window.Bounds.Width / 2,
                "Run's tab did not go with it to the place it was dragged to, at the right");

            window.ApplySettings(window.Settings with { ToolWindowLook = ToolWindowLook.StripeIcons });
            Ui.Settle();
            Assert.Empty(ToolTabs(window));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_tool_window_closed_by_its_x_leaves_its_stripe_through_a_restart_until_its_action_opens_it_and_keeping_icons_changes_only_the_x()
    {
        string state = TestFolders.Create("arrangement-state-");
        try
        {
            // A key no setting reads (stripeIcons) is passed over, the rest read as written.
            File.WriteAllText(Path.Combine(state, Passenger.App.State.StateStore.SettingsFile), """{ "version": 5, "closedToolWindows": "LeaveStripe", "stripeIcons": "Untabbed" }""");
            MainWindow window = Keeping(state);
            try
            {
                window.DockHost.Show(ToolWindowIds.Run);
                Ui.Settle();
                Assert.True(StripeButton(window, ToolWindowIds.Run) is not null, "Run has no stripe icon before it was closed");
                Ui.Click(window.DockHost.GetVisualDescendants().OfType<Button>().Single(b => b.Name == AppActions.CloseToolWindow && b.IsEffectivelyVisible
                    && b.FindAncestorOfType<Border>() is { } header && header.Classes.Contains(StyleClasses.ToolHeader)
                    && header.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains(StyleClasses.ToolTitle) && t.Text == "Run")));
                Assert.True(!window.DockHost.IsShown(ToolWindowIds.Run) && StripeButton(window, ToolWindowIds.Run) is null,
                    "Run closed by its × kept its stripe icon, where closed windows leave the stripe");
                Assert.True(StripeButton(window, ToolWindowIds.Debug) is not null, "Debug, hidden but never closed, lost its stripe icon");
            }
            finally
            {
                window.Close();
                Ui.Settle();
            }

            MainWindow again = Keeping(state);
            try
            {
                Assert.True(StripeButton(again, ToolWindowIds.Run) is null, "Run, closed, came back to its stripe at the next start");
                Ui.Run(again, AppActions.ShowRun);
                Assert.True(again.DockHost.IsShown(ToolWindowIds.Run) && StripeButton(again, ToolWindowIds.Run) is not null,
                    "Run opened by its action did not come back to its stripe, open");

                again.DockHost.Show(ToolWindowIds.Debug);
                Ui.Settle();
                Assert.True(again.Perform(AppActions.CloseToolWindow), "Close Active Tool Window was refused with Debug open");
                Ui.Settle();
                Assert.True(StripeButton(again, ToolWindowIds.Debug) is null, "Close Active Tool Window left Debug's stripe icon");
                again.ApplySettings(again.Settings with { ClosedToolWindows = ClosedToolWindows.KeepIcons });
                Ui.Settle();
                // The setting says what × does: a window off its stripe stays off until opened or checked on the stripe again.
                Assert.True(StripeButton(again, ToolWindowIds.Debug) is null && again.Actions.WhyNot(AppActions.CloseToolWindow, again.Actions.Context()) == Refusals.ClosedKeepIcons,
                    "Keeping icons chosen, the closed Debug window came back to its stripe unasked, or Close was not greyed for it");
                Assert.DoesNotContain(again.DockHost.GetVisualDescendants().OfType<Button>(), b => b.Name == AppActions.CloseToolWindow && b.IsEffectivelyVisible);
            }
            finally
            {
                again.ApplySettings(new AppSettings());
                again.Close();
            }
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    [AvaloniaFact]
    public void Find_in_Files_matches_are_kept_in_the_Find_window_once_one_is_gone_to_and_Open_in_Find_Windows_keys_put_them_there()
    {
        MainWindow window = Open();
        try
        {
            FindInFilesPopup popup = window.ShowFindInFiles(replace: false)!;
            Ui.Search(popup, "class");
            Assert.Equal(3, popup.Matches.Count);
            Ui.Press(window, "ENTER");
            Assert.False(window.DockHost.IsShown(ToolWindowIds.Find), "A match gone to put the matches in the Find window, where they stay in the popup");

            window.ApplySettings(window.Settings with { FindResults = FindResults.FindWindow });
            Ui.Settle();
            popup = window.ShowFindInFiles(replace: false)!;
            Ui.Search(popup, "class");
            string second = popup.Matches[1].Name;
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Assert.Equal(second, window.EditorArea.Active?.Name);
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Find) && window.Find.Shown == "Occurrences of 'class'"
                && window.Find.Rows.Count(r => r.Usage is not null) == 3,
                $"Kept in the Find window, the matches show there as {window.Find.Shown} with {window.Find.Rows.Count(r => r.Usage is not null)} rows");
            Assert.True(window.EditorArea.CodeEditor.IsKeyboardFocusWithin, "The match gone to does not have the keys, the Find window having taken them");

            popup = window.ShowFindInFiles(replace: false)!;
            Ui.Search(popup, "Two");
            Ui.Press(window, OperatingSystem.IsMacOS() ? "meta ENTER" : "control ENTER");
            Assert.True(window.Find.Shown == "Occurrences of 'Two'" && window.Find.IsKeyboardFocusWithin,
                $"Open in Find Window's keys left {window.Find.Shown} in the Find window, its keys {(window.Find.IsKeyboardFocusWithin ? "there" : "elsewhere")}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Cubicle_arranges_the_window_as_an_enterprise_IDE_does()
    {
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { Look = SourcedTheme.BuiltIn("Cubicle") });
            Ui.Settle();
            DockHost dock = window.DockHost;
            Assert.True(window.EditorArea.ActiveGroup.TabsPlace == EditorTabsPlace.Top && dock.TitledTabs && dock.ClosesOffStripe
                && window.Settings.FindResultsIn() == FindResults.FindWindow && ToolTabs(window).Length > 0,
                $"Cubicle arranges the tabs {window.EditorArea.ActiveGroup.TabsPlace}, titled tabs {dock.TitledTabs}, closed windows leaving {dock.ClosesOffStripe}, "
                + $"Find in Files' results {window.Settings.FindResultsIn()}, {ToolTabs(window).Length} tool tabs shown");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Unit_Tests_dragged_onto_the_editors_is_a_tab_among_the_files_through_a_restart_and_docks_again_by_its_menu_and_by_a_drag()
    {
        string state = TestFolders.Create("arrangement-state-");
        try
        {
            MainWindow window = Keeping(state);
            try
            {
                window.OpenFile(Path.Combine(folder, "One.cs"));
                Ui.Settle();
                DockHost dock = window.DockHost;
                Ui.Drag(window, Ui.Centre(StripeButton(window, ToolWindowIds.Tests)!, window), Ui.Centre(window.EditorArea.CodeEditor, window));
                Assert.True(dock[ToolWindowIds.Tests] is { Mode: ToolMode.Editor, IsShown: true } && window.EditorArea.ActiveGroup.Active?.Tool?.Window == dock[ToolWindowIds.Tests],
                    $"Unit Tests dropped on the editors stands {dock[ToolWindowIds.Tests].Mode}, the editors showing {window.EditorArea.ActiveGroup.Active?.Title}");
                Assert.True(dock[ToolWindowIds.Tests].Content.IsEffectivelyVisible && EditorTab(window, "Unit Tests") is not null,
                    "Unit Tests' view is not shown among the editors under a tab of its name");
                Assert.Contains(window.EditorArea.ActiveGroup.Tabs, t => t.Document.Name == "One.cs");
            }
            finally
            {
                window.Close();
                Ui.Settle();
            }

            MainWindow again = Keeping(state);
            try
            {
                DockHost dock = again.DockHost;
                Assert.True(dock[ToolWindowIds.Tests].Mode == ToolMode.Editor && EditorTab(again, "Unit Tests") is not null,
                    $"Unit Tests, an editor tab when the window closed, stands {dock[ToolWindowIds.Tests].Mode} at the next start, its tab {(EditorTab(again, "Unit Tests") is null ? "gone" : "there")}");

                Ui.Click(EditorTab(again, "Unit Tests")!);
                Assert.True(again.Perform(AppActions.DockToolWindow), "Dock Tool Window was refused on Unit Tests' tab");
                Ui.Settle();
                Assert.True(dock[ToolWindowIds.Tests] is { Mode: ToolMode.Docked, IsShown: true } && EditorTab(again, "Unit Tests") is null,
                    $"Dock Tool Window left Unit Tests {dock[ToolWindowIds.Tests].Mode}, its editor tab {(EditorTab(again, "Unit Tests") is null ? "gone" : "still there")}");

                dock.SetMode(dock[ToolWindowIds.Tests], ToolMode.Editor);
                Ui.Settle();
                Ui.Drag(again, Ui.Centre(EditorTab(again, "Unit Tests")!, again), new Point(again.Bounds.Width - 20, 120));
                Assert.True(dock[ToolWindowIds.Tests] is { Mode: ToolMode.Docked, Anchor: ToolAnchor.RightTop, IsShown: true } && EditorTab(again, "Unit Tests") is null,
                    $"Unit Tests' tab dragged to the right stripe left it {dock[ToolWindowIds.Tests].Mode} in {dock[ToolWindowIds.Tests].Anchor}");

                Ui.Click(StripeButton(again, ToolWindowIds.Tests)!);
                dock.SetMode(dock[ToolWindowIds.Tests], ToolMode.Editor);
                Ui.Settle();
                Assert.Null(EditorTab(again, "Unit Tests"));
                Ui.Click(StripeButton(again, ToolWindowIds.Tests)!);
                Assert.True(EditorTab(again, "Unit Tests") is not null && dock.IsShown(ToolWindowIds.Tests), "Unit Tests' icon did not open its editor tab");
                Ui.Click(EditorTab(again, "Unit Tests")!.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains(StyleClasses.TabClose)));
                Assert.True(!dock.IsShown(ToolWindowIds.Tests) && StripeButton(again, ToolWindowIds.Tests) is { IsChecked: false },
                    "Unit Tests' editor tab closed by its × left the window shown");
            }
            finally
            {
                again.Close();
            }
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    [AvaloniaFact]
    public void A_themes_layout_places_a_tool_window_among_the_editors()
    {
        string name = Kept("""{"places": {"editor": ["tests"]}, "open": ["explorer", "tests"]}""");
        string state = TestFolders.Create("arrangement-state-");
        try
        {
            File.WriteAllText(Path.Combine(state, Passenger.App.State.StateStore.SettingsFile),
                $$"""{ "version": 5, "look": { "source": "{{ThemeSource.Added.Name}}", "name": "{{name}}" } }""");
            MainWindow window = Keeping(state);
            try
            {
                Assert.True(window.DockHost[ToolWindowIds.Tests] is { Mode: ToolMode.Editor, IsShown: true } && EditorTab(window, "Unit Tests") is not null,
                    $"The theme puts Unit Tests among the editors, where it stands {window.DockHost[ToolWindowIds.Tests].Mode}");
            }
            finally
            {
                window.ApplySettings(new AppSettings());
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>An editor tab by its title, among every group's; null for none.</summary>
    private static Border? EditorTab(MainWindow window, string title) =>
        window.EditorArea.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(StyleClasses.Tab) && b.IsEffectivelyVisible
            && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == title));

    /// <summary>A window that keeps its state, settings and layout in a folder, over the test's project.</summary>
    private MainWindow Keeping(string state)
    {
        MainWindow window = new() { Width = 1400, Height = 900, StateFolder = state };
        window.Show();
        window.KeepSettings();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>A window's stripe icon, shown or behind More for want of room; null for none.</summary>
    private static ToggleButton? StripeButton(MainWindow window, string id) =>
        window.DockHost.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(b => Equals(b.Tag, id));

    [AvaloniaFact]
    public void Under_stripe_icons_a_window_docked_beside_another_or_opened_again_takes_no_icon_off_the_stripes_and_no_tab_names_it()
    {
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { ClosedToolWindows = ClosedToolWindows.LeaveStripe });
            Ui.Settle();
            DockHost dock = window.DockHost;
            Ui.Drag(window, Ui.Centre(StripeButton(window, ToolWindowIds.Bookmarks)!, window), new Point(Placed(window, dock[ToolWindowIds.Explorer].Content).X + 60, 80));
            Assert.True(dock[ToolWindowIds.Bookmarks].Anchor == ToolAnchor.LeftTop && dock.IsShown(ToolWindowIds.Explorer),
                $"Bookmarks dragged into the Explorer's place stands in {dock[ToolWindowIds.Bookmarks].Anchor}, the Explorer shown {dock.IsShown(ToolWindowIds.Explorer)}");
            Assert.True(Iconless(window).Length == 0 && ToolTabs(window).Length == 0,
                $"With Bookmarks docked beside the Explorer, {string.Join(", ", Iconless(window))} lost their stripe icons, {ToolTabs(window).Length} tabs name windows");

            dock.Close(ToolWindowIds.Explorer);
            Ui.Settle();
            Assert.Null(StripeButton(window, ToolWindowIds.Explorer));
            Assert.True(window.Perform(AppActions.FocusExplorer), "View | Tool Windows | Explorer was refused");
            Ui.Settle();
            Assert.True(dock.IsShown(ToolWindowIds.Explorer) && Iconless(window).Length == 0 && ToolTabs(window).Length == 0,
                $"The Explorer opened again into the place Bookmarks holds left {string.Join(", ", Iconless(window))} without stripe icons, {ToolTabs(window).Length} tabs naming windows");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Under_titled_tabs_no_stripe_stands_and_a_window_docked_beside_another_or_opened_again_is_named_by_a_tab_or_its_header()
    {
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { ToolWindowLook = ToolWindowLook.TitledTabs, ClosedToolWindows = ClosedToolWindows.LeaveStripe });
            Ui.Settle();
            DockHost dock = window.DockHost;
            Assert.True(Unnamed(window).Length == 0 && StripeButtons(window).Length == 0,
                $"Under titled tabs {string.Join(", ", Unnamed(window))} are named by no tab or header, {StripeButtons(window).Length} stripe icons standing");
            Assert.True(EdgeTab(window, ToolWindowIds.Bookmarks) is not null && Header(window, "Explorer") is not null,
                "Bookmarks, hidden, has no tab along the left edge, or the Explorer, alone in its place, no header naming it");

            Ui.Drag(window, Ui.Centre(EdgeTab(window, ToolWindowIds.Bookmarks)!, window), new Point(Placed(window, dock[ToolWindowIds.Explorer].Content).X + 60, 80));
            Assert.True(dock[ToolWindowIds.Bookmarks].Anchor == ToolAnchor.LeftTop && FootTab(window, ToolWindowIds.Explorer) is not null && FootTabs(window).Any(t => Equals(t.Tag, ToolWindowIds.Bookmarks)),
                $"Bookmarks' edge tab dragged into the Explorer's place left it in {dock[ToolWindowIds.Bookmarks].Anchor}, the place's foot naming {string.Join(", ", FootTabs(window).Select(t => t.Tag))}");
            Assert.True(Unnamed(window).Length == 0 && StripeButtons(window).Length == 0,
                $"With Bookmarks docked beside the Explorer, {string.Join(", ", Unnamed(window))} are named by nothing, {StripeButtons(window).Length} stripe icons standing");

            dock.Close(ToolWindowIds.Explorer);
            Ui.Settle();
            Assert.True(window.Perform(AppActions.FocusExplorer), "View | Tool Windows | Explorer was refused");
            Ui.Settle();
            Assert.True(dock.IsShown(ToolWindowIds.Explorer) && FootTab(window, ToolWindowIds.Explorer) is not null && Unnamed(window).Length == 0 && StripeButtons(window).Length == 0,
                $"The Explorer opened again beside Bookmarks: shown {dock.IsShown(ToolWindowIds.Explorer)}, {string.Join(", ", Unnamed(window))} named by nothing, {StripeButtons(window).Length} stripe icons");

            // The bottom's many windows: each tab whole or behind the strip's last button, none cut.
            dock.Show(ToolWindowIds.Build);
            window.Width = 900;
            Ui.Settle();
            Border[] cut = [.. FootTabs(window).Where(t => Seen(t) is { } part && part != Placed(window, t))];
            Assert.Empty(cut);
            Button more = window.DockHost.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is "Hidden Tool Windows" && b.FindAncestorOfType<Border>() is { } strip
                && strip.Classes.Contains(StyleClasses.ToolTabStrip) && strip.GetVisualDescendants().OfType<Border>().Any(t => Equals(t.Tag, ToolWindowIds.Build)));
            string[] gone = [.. FootTabs(window).Where(t => dock[(string)t.Tag!].Anchor == ToolAnchor.BottomLeft && Seen(t) is null).Select(t => dock[(string)t.Tag!].Title)];
            Assert.True(gone.Length > 0 && Seen(more) is not null, $"At 900 wide the bottom's tabs all fit, or no button lists those gone ({string.Join(", ", gone)})");
            Ui.Click(more);
            string[] listed = [.. (dock.HiddenTabsMenu?.Items.OfType<MenuItem>() ?? []).Select(i => i.Header as string ?? "")];
            Assert.True(listed.SequenceEqual(gone), $"The strip's last button lists {string.Join(", ", listed)}, where {string.Join(", ", gone)} have no room");
            dock.HiddenTabsMenu?.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Every_window_of_a_place_hidden_is_a_click_away_by_its_icon_or_by_its_tab_along_the_edge_which_opens_it_over_the_editors()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            dock.Show(ToolWindowIds.Build);
            Ui.Settle();
            dock.Hide(ToolWindowIds.Build);
            Ui.Settle();
            string[] bottom = [.. dock.Windows.Where(w => w.Anchor == ToolAnchor.BottomLeft && !w.IsClosed).Select(w => w.Id)];
            Assert.True(bottom.All(id => StripeButton(window, id) is not null), $"Hidden, {string.Join(", ", bottom.Where(id => StripeButton(window, id) is null))} have no stripe icon");
            Ui.Click(StripeButton(window, ToolWindowIds.Run)!);
            Assert.True(dock.IsShown(ToolWindowIds.Run), "Run's icon did not open it");
            dock.Hide(ToolWindowIds.Run);

            window.ApplySettings(window.Settings with { ToolWindowLook = ToolWindowLook.TitledTabs });
            Ui.Settle();
            Assert.True(bottom.All(id => EdgeTab(window, id) is not null), $"Hidden, {string.Join(", ", bottom.Where(id => EdgeTab(window, id) is null))} have no tab along the edge");
            double editors = Placed(window, window.EditorArea).Height;
            Ui.Click(EdgeTab(window, ToolWindowIds.Run)!);
            Assert.True(dock.Peeked == dock[ToolWindowIds.Run] && dock[ToolWindowIds.Run].Content.IsEffectivelyVisible && dock[ToolWindowIds.Run].Content.IsKeyboardFocusWithin
                && Placed(window, window.EditorArea).Height == editors && EdgeTab(window, ToolWindowIds.Run)!.Classes.Contains(StyleClasses.Active),
                $"Run's edge tab opened it over the editors {dock.Peeked?.Id}, shown {dock[ToolWindowIds.Run].Content.IsEffectivelyVisible}, with the keys {dock[ToolWindowIds.Run].Content.IsKeyboardFocusWithin}, "
                + $"the editors {Placed(window, window.EditorArea).Height} high where they were {editors}");
            Rect over = Placed(window, dock[ToolWindowIds.Run].Content);
            Assert.True(over.Intersects(Placed(window, window.EditorArea)), $"Run, open from its edge tab at {over}, is not over the editors at {Placed(window, window.EditorArea)}");

            Ui.Click(window.EditorArea.CodeEditor);
            Assert.True(dock.Peeked is null && !dock.IsShown(ToolWindowIds.Run) && EdgeTab(window, ToolWindowIds.Run) is not null,
                "A click in the editor left Run over it, or took its edge tab away");

            Ui.Click(EdgeTab(window, ToolWindowIds.Run)!);
            Ui.Click(window.DockHost.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) is "Dock" && b.IsEffectivelyVisible));
            Assert.True(dock.Peeked is null && dock.IsShown(ToolWindowIds.Run) && FootTab(window, ToolWindowIds.Run) is not null && EdgeTab(window, ToolWindowIds.Run) is null
                && Placed(window, window.EditorArea).Height < editors,
                $"Run's Dock left it over the editors {dock.Peeked?.Id}, shown {dock.IsShown(ToolWindowIds.Run)}, the editors {Placed(window, window.EditorArea).Height} high");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_theme_choosing_either_look_gets_that_look_whole()
    {
        string icons = Kept("""{"toolWindows": "icons"}""");
        MainWindow window = Open();
        try
        {
            window.DockHost.Show(ToolWindowIds.Build);
            window.ApplySettings(window.Settings with { Look = SourcedTheme.BuiltIn("Cubicle") });
            Ui.Settle();
            Assert.True(window.DockHost.TitledTabs && StripeButtons(window).Length == 0 && FootTabs(window).Length > 0 && EdgeTabs(window).Length > 0 && Unnamed(window).Length == 0,
                $"Cubicle, choosing titled tabs, shows {StripeButtons(window).Length} stripe icons, {FootTabs(window).Length} tabs at places' feet, {EdgeTabs(window).Length} along the edges, "
                + $"{string.Join(", ", Unnamed(window))} named by nothing");

            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, icons) });
            Ui.Settle();
            Assert.True(!window.DockHost.TitledTabs && Iconless(window).Length == 0 && ToolTabs(window).Length == 0,
                $"A theme choosing icons leaves {string.Join(", ", Iconless(window))} without stripe icons and {ToolTabs(window).Length} tabs naming windows");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_theme_chosen_moves_each_window_still_where_the_theme_before_put_it_and_keeps_the_one_the_person_moved()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            dock.Move(dock[ToolWindowIds.Terminal], ToolAnchor.RightBottom);
            dock.Show(ToolWindowIds.Run);
            Ui.Settle();
            DockLayout passengers = dock.Default();

            window.ApplySettings(window.Settings with { Look = SourcedTheme.BuiltIn("Cubicle") });
            Ui.Settle();
            DockLayout cubicles = dock.Default();
            string[] astray = [.. Astray(dock, cubicles, ToolWindowIds.Terminal)];
            Assert.True(astray.Length == 0 && dock[ToolWindowIds.Explorer] is { Anchor: ToolAnchor.RightTop, IsShown: true },
                $"Cubicle chosen left {string.Join(", ", astray)} where Cubicle does not put them, the Explorer in {dock[ToolWindowIds.Explorer].Anchor}");
            Assert.True(dock[ToolWindowIds.Terminal].Anchor == ToolAnchor.RightBottom && dock.IsShown(ToolWindowIds.Run) && !dock.IsShown(ToolWindowIds.Build) && dock.IsShown(ToolWindowIds.Problems),
                $"Cubicle took the Terminal the person moved to {dock[ToolWindowIds.Terminal].Anchor}, or Run they opened shown {dock.IsShown(ToolWindowIds.Run)} lost its place to Build, "
                + $"or Problems, unopened, did not open as Cubicle opens it ({dock.IsShown(ToolWindowIds.Problems)})");
            DockLayout kept = dock.Capture();
            Assert.True((kept.RightWidth, kept.BottomHeight) == (cubicles.RightWidth, cubicles.BottomHeight),
                $"The areas nobody resized stand {kept.RightWidth} wide and {kept.BottomHeight} high, where Cubicle gives {cubicles.RightWidth} and {cubicles.BottomHeight}");

            window.ApplySettings(window.Settings with { Look = null });
            Ui.Settle();
            astray = [.. Astray(dock, passengers, ToolWindowIds.Terminal)];
            Assert.True(astray.Length == 0 && dock[ToolWindowIds.Explorer].Anchor == ToolAnchor.LeftTop && dock[ToolWindowIds.Terminal].Anchor == ToolAnchor.RightBottom && dock.IsShown(ToolWindowIds.Run),
                $"Passenger's own theme chosen again left {string.Join(", ", astray)} where Cubicle put them, the Terminal the person moved in {dock[ToolWindowIds.Terminal].Anchor}, Run shown {dock.IsShown(ToolWindowIds.Run)}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>The windows but those named that do not stand docked in the place a default layout gives them.</summary>
    private static IEnumerable<string> Astray(DockHost dock, DockLayout layout, params string[] but) =>
        dock.Windows.Where(w => !but.Contains(w.Id) && (w.Anchor, w.Mode) != (layout.Windows[w.Id].Anchor, layout.Windows[w.Id].Mode)).Select(w => $"{w.Id} in {w.Anchor}");

    /// <summary>The windows on the stripes (closed off them by none) with no stripe icon, shown or behind More.</summary>
    private static string[] Iconless(MainWindow window) =>
        [.. window.DockHost.Windows.Where(w => !w.IsClosed && StripeButton(window, w.Id) is not { IsEffectivelyVisible: true } && !(StripeButton(window, w.Id) is { } hidden && hidden.FindAncestorOfType<Border>() is { IsEffectivelyVisible: true }))
            .Select(w => w.Id)];

    /// <summary>The stripe icons a person sees.</summary>
    private static ToggleButton[] StripeButtons(MainWindow window) =>
        [.. window.DockHost.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Tag is string && b.IsEffectivelyVisible)];

    /// <summary>The docked windows named by nothing a person sees: no tab along an edge or at a place's foot (whole, or
    /// listed by its strip's last button for want of room), no header; those closed are left out.</summary>
    private static string[] Unnamed(MainWindow window) =>
        [.. window.DockHost.Windows.Where(w => !w.IsClosed && w.Mode == ToolMode.Docked && EdgeTab(window, w.Id) is null && !FootTabs(window).Any(t => Equals(t.Tag, w.Id))
            && !(w.IsShown && Header(window, w.Title) is not null)).Select(w => w.Id)];

    /// <summary>A shown pane's header naming a window by its title.</summary>
    private static TextBlock? Header(MainWindow window, string title) =>
        window.DockHost.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains(StyleClasses.ToolTitle) && t.Text == title && t.IsEffectivelyVisible);

    /// <summary>The tabs along the frame's edges.</summary>
    private static Border[] EdgeTabs(MainWindow window) =>
        [.. ToolTabs(window).Where(t => t.FindAncestorOfType<Border>() is { } edge && edge.GetVisualAncestors().Prepend(edge).OfType<Border>().Any(b => b.Classes.Contains(StyleClasses.EdgeTabs)))];

    private static Border? EdgeTab(MainWindow window, string id) => EdgeTabs(window).FirstOrDefault(t => Equals(t.Tag, id));

    /// <summary>The tabs at the places' feet a person sees, each whole: one with no room is in none.</summary>
    private static Border[] FootTabs(MainWindow window) =>
        [.. ToolTabs(window).Where(t => t.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains(StyleClasses.ToolTabStrip)))];

    private static Border? FootTab(MainWindow window, string id) => FootTabs(window).FirstOrDefault(t => Equals(t.Tag, id) && Seen(t) is not null);

    /// <summary>The part of a control a person sees inside what clips it, in the window's coordinates; null for none.</summary>
    private static Rect? Seen(Control control)
    {
        if (TopLevel.GetTopLevel(control) is not { } top)
        {
            return null;
        }
        Rect part = Placed(top, control);
        foreach (Visual clip in control.GetVisualAncestors().Where(a => a.ClipToBounds))
        {
            part = part.Intersect(Placed(top, clip));
        }
        return part.Width >= 1 && part.Height >= 1 ? part : null;
    }

    private static Border[] ToolTabs(MainWindow window) =>
        [.. window.DockHost.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Tab) && b.Tag is string && b.IsEffectivelyVisible)];

    private static Border ToolTab(MainWindow window, string id) => ToolTabs(window).Single(t => Equals(t.Tag, id));

    /// <summary>A theme kept as one a person added, its layout as given; its name.</summary>
    private static string Kept(string layout)
    {
        string name = $"Arranged {Guid.NewGuid():N}";
        string json = $$$"""{"format": 1, "name": "{{{name}}}", "layout": {{{layout}}} }""";
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, Encoding.UTF8.GetBytes(json)));
        return name;
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        foreach (string name in (string[])["One.cs", "Two.cs", "Three.cs"])
        {
            window.OpenFile(Path.Combine(folder, name));
        }
        Ui.Settle();
        return window;
    }

    private static Border[] Tabs(EditorGroup group) =>
        [.. group.TabStrip.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Tab) && b.IsEffectivelyVisible)];

    private static Border Tab(EditorGroup group, string name) =>
        Tabs(group).Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == name));

    private static Rect Placed(Visual within, Visual control) => new(control.TranslatePoint(default, within) ?? default, control.Bounds.Size);
}

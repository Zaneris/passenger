using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Settings: a search narrows the pages to those with an option whose words match and shows only those options;
/// a change is in force the moment it is applied, in every editor (one a split makes later too), a window that keeps its
/// settings opens on them again, and another Passenger's change to them is followed even where the watcher lost it.</summary>
[AboutMacOS]
public sealed class SettingsTests : IDisposable
{
    /// <summary>Settings previews a change in every window and editor and applies it to the next window too, the font the
    /// size actions and the zoom wheel it turns on change as well, and a close unapplied or Cancel puts back the last
    /// apply, as one flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void Settings_previews_applies_and_cancels_each_change_where_a_person_sees_it()
    {
        Flow.StepApart<SettingsTests>("visual edits preview without saving and cancel restores the last apply", t => t.Visual_edits_preview_without_saving_and_cancel_restores_the_last_apply());
        Flow.StepApart<SettingsTests>("preview overlays other windows and external changes without undoing them on close", t => t.Preview_overlays_other_windows_and_external_changes_without_undoing_them_on_close());
        Flow.StepApart<SettingsTests>("soft wrap and the terminals size and cursor show as changed in settings and go back as it closes unapplied", t => t.Soft_wrap_and_the_terminals_size_and_cursor_show_as_changed_in_settings_and_go_back_as_it_closes_unapplied());
        Flow.StepApart<SettingsTests>("a change reaches every editor and the next window opens on it", t => t.A_change_reaches_every_editor_and_the_next_window_opens_on_it());
        Flow.StepApart<SettingsTests>("the font size actions and the zoom wheel change the editor's font and the next window opens on it", t => t.The_font_size_actions_and_the_zoom_wheel_change_the_editors_font_and_the_next_window_opens_on_it());
        Flow.StepApart<SettingsTests>("terminal preferences apply to the view and are kept for the next window", t => t.Terminal_preferences_apply_to_the_view_and_are_kept_for_the_next_window());
        Flow.StepApart<SettingsTests>("the language chosen is in force at once and the words shown follow it in place", t => t.The_language_chosen_is_in_force_at_once_and_the_words_shown_follow_it_in_place());
    }

    /// <summary>A monospaced font installed beside the carried one is the code font everywhere once chosen, a
    /// proportional one refused, and the terminal's shows as chosen until Settings closes unapplied, as one flow of the
    /// steps that need such a font, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_monospaced_font_installed_is_chosen_as_the_code_and_terminal_font()
    {
        Flow.StepApart<SettingsTests>("a terminal font shows as chosen until settings closes unapplied and same as the editor follows the editors font", t => t.A_terminal_font_shows_as_chosen_until_settings_closes_unapplied_and_same_as_the_editor_follows_the_editors_font());
        Flow.StepApart<SettingsTests>("the editor font family chosen in settings is the code font everywhere and a proportional one is refused", t => t.The_editor_font_family_chosen_in_settings_is_the_code_font_everywhere_and_a_proportional_one_is_refused());
    }

    /// <summary>The display scale reaches every window and popup and stays usable however it was kept, and old terminal
    /// settings or a font no longer there keep a usable default, as one flow, each step in an instance of its
    /// own.</summary>
    [AvaloniaFact]
    public void The_display_scale_chosen_in_Settings_holds_in_every_window()
    {
        Flow.StepApart<SettingsTests>("display scale applies to open windows and popups and is kept", t => t.Display_scale_applies_to_open_windows_and_popups_and_is_kept());
        Flow.StepApart<SettingsTests>("a display scale is chosen from a list that closes and every window holds its layout and its screen at each", t => t.A_display_scale_is_chosen_from_a_list_that_closes_and_every_window_holds_its_layout_and_its_screen_at_each());
        Flow.StepApart<SettingsTests>("display scale outside its range still opens usable settings (0, 1)", t => t.Display_scale_outside_its_range_still_opens_usable_settings(0, 1));
        Flow.StepApart<SettingsTests>("display scale outside its range still opens usable settings (10, 3)", t => t.Display_scale_outside_its_range_still_opens_usable_settings(10, 3));
        Flow.StepApart<SettingsTests>("old terminal settings and an unavailable font keep a usable default", t => t.Old_terminal_settings_and_an_unavailable_font_keep_a_usable_default());
    }

    /// <summary>settings.json changed outside the window is followed however the watcher heard it, the window's own
    /// change is not read back, and the keymap, the browser and the shell kept are the ones run, as one flow, each step
    /// in an instance of its own.</summary>
    [AvaloniaFact]
    public void Settings_json_and_the_keymap_follow_whatever_changed_them()
    {
        Flow.StepApart<SettingsTests>("a change to settings json the watcher lost is followed as it overflows and as the window comes in front", t => t.A_change_to_settings_json_the_watcher_lost_is_followed_as_it_overflows_and_as_the_window_comes_in_front());
        Flow.StepApart<SettingsTests>("the windows own change to settings json is not read back as the window comes in front", t => t.The_windows_own_change_to_settings_json_is_not_read_back_as_the_window_comes_in_front());
        Flow.StepApart<SettingsTests>("a change to settings json kept as a link is followed each time the window comes in front", t => t.A_change_to_settings_json_kept_as_a_link_is_followed_each_time_the_window_comes_in_front());
        Flow.StepApart<SettingsTests>("a shortcut set in the keymap runs its action and one taken from another action leaves it", t => t.A_shortcut_set_in_the_keymap_runs_its_action_and_one_taken_from_another_action_leaves_it());
        Flow.StepApart<SettingsTests>("a shortcut kept under an actions former id stays reset once reset and read again", t => t.A_shortcut_kept_under_an_actions_former_id_stays_reset_once_reset_and_read_again());
        Flow.StepApart<SettingsTests>("a shell path naming no program is refused in settings and a terminal tab met with it says why", t => t.A_shell_path_naming_no_program_is_refused_in_settings_and_a_terminal_tab_met_with_it_says_why());
        Flow.StepApart<SettingsTests>("the browser kept for debugging is the browser settings shows and keeps under its name and no program row looks on the windows thread", t => t.The_browser_kept_for_debugging_is_the_browser_settings_shows_and_keeps_under_its_name_and_no_program_row_looks_on_the_windows_thread());
    }

    /// <summary>Every page of Settings at its least width and smallest size cuts no word and runs no row under its
    /// scroll bar, its font lists draw every family they offer, and a search keeps what it matches, as one flow, each step
    /// in an instance of its own.</summary>
    [AvaloniaFact]
    public void Every_Settings_page_is_whole_at_its_smallest_and_found_by_a_search()
    {
        Flow.StepApart<SettingsTests>("every page at the least width cuts no word and lays nothing over anything", t => t.Every_page_at_the_least_width_cuts_no_word_and_lays_nothing_over_anything());
        Flow.StepApart<SettingsTests>("no row of any page runs under its scroll bar at the dialogs smallest size or its own", t => t.No_row_of_any_page_runs_under_its_scroll_bar_at_the_dialogs_smallest_size_or_its_own());
        Flow.StepApart<SettingsTests>("every family the font lists offer draws as its row comes into view", t => t.Every_family_the_font_lists_offer_draws_as_its_row_comes_into_view());
        Flow.StepApart<SettingsTests>("a search keeps the pages and options it matches, each row made as its page first shows it", t => t.A_search_keeps_the_pages_and_options_it_matches());
    }

    /// <summary>Every family the code's and the window's font lists offer draws as its row comes into view, each list opened
    /// and scrolled from end to end: a face the renderer cannot read (a Type1 font's, as Linux systems install) is never
    /// offered, where its row threw as it was drawn and the list ignored every click (#2291).</summary>
    private void Every_family_the_font_lists_offer_draws_as_its_row_comes_into_view()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            foreach ((string page, Func<Task<IReadOnlyList<string>>> installed) in new (string, Func<Task<IReadOnlyList<string>>>)[]
                { (SettingsDialog.EditorPage, CodeFonts.InstalledAsync), (SettingsDialog.Appearance, WindowFonts.InstalledAsync) })
            {
                dialog.Page = page;
                Ui.Settle();
                IReadOnlyList<string> offered = Ui.Await(installed(), "the installed fonts");
                ComboBox box = Editor<ComboBox>(dialog, "Font family");
                Ui.WaitFor(() => offered.All(name => box.Items.Cast<object?>().Any(i => i?.ToString() == name)), $"the installed fonts to fill {page}'s list");
                Ui.Click(box);
                Ui.WaitFor(() => box.IsDropDownOpen, $"{page}'s font list to open");
                for (int at = 0; at < box.ItemCount; at += 8)
                {
                    box.ScrollIntoView(at);
                    Ui.Settle();
                }
                box.ScrollIntoView(box.ItemCount - 1);
                Ui.Settle();
                Assert.True(box.ContainerFromIndex(box.ItemCount - 1) is not null, $"{page}'s font list never showed its last family, {box.Items.Cast<object?>().Last()}");
                box.IsDropDownOpen = false;
                Ui.Settle();
            }
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
    }

    /// <summary>Settings' Language row offers the system's language, named in itself, English and each carried language; a
    /// language put in force (the pseudo-locale, as --language puts it) says again in place every word the open dialog shows
    /// through ShownWords, and English chosen, or the system's languages followed (the test run's en-US), each in force as
    /// it is applied.</summary>
    private void The_language_chosen_is_in_force_at_once_and_the_words_shown_follow_it_in_place()
    {
        MainWindow window = Open();
        window.KeepSettings();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            string[] offered = [.. Editor<ComboBox>(dialog, "Language").Items.Cast<object?>().Select(i => i?.ToString() ?? string.Empty)];
            string[] expected = ["System (English)", "English", .. LanguageSetting.Carried.Select(c => LanguageSetting.NameOf(LanguageSetting.InForce(c.Name)).Shown)];
            Assert.True(offered.SequenceEqual(expected), $"The Language row offers [{string.Join(", ", offered)}], not [{string.Join(", ", expected)}]");
            TextBlock label = dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Language");

            UiLanguage.Put(UiLanguage.Of(UiLanguage.PseudoTag, LanguageSetting.Carried)!);
            Ui.Settle();
            Assert.True(label.Text?.StartsWith("[Ļåñĝûåĝé", StringComparison.Ordinal) == true && label.Text.EndsWith(']'),
                $"The Language row's label reads \"{label.Text}\" under the pseudo-locale: words given through ShownWords are not said again as the language changes");
            UiLanguage.Put(UiLanguage.English);
            Ui.Settle();
            Assert.True(label.Text == "Language", $"The Language row's label reads \"{label.Text}\" once English is in force again");

            dialog.Change(s => s with { DisplayLanguage = LanguageSetting.English });
            dialog.Apply();
            Assert.True(UiLanguage.InForce is { IsEnglish: true, Tag: LanguageSetting.English } && window.Settings.DisplayLanguage == LanguageSetting.English,
                $"English chosen puts {UiLanguage.InForce.Tag} in force and keeps {window.Settings.DisplayLanguage ?? "System"}");
            dialog.Change(s => s with { DisplayLanguage = null });
            dialog.Apply();
            Assert.True(UiLanguage.InForce is { IsEnglish: true, Tag: "en-US" },
                $"System puts {UiLanguage.InForce.Tag} in force, where the system's languages (the test run's stand-in) are en-US alone");
        }
        finally
        {
            dialog.Close();
            window.Close();
            UiLanguage.Put(LanguageSetting.InForce(null));
        }
    }

    private void Visual_edits_preview_without_saving_and_cancel_restores_the_last_apply()
    {
        MainWindow window = Open();
        window.KeepSettings();
        StateStore store = StateStore.Open(settings);
        try
        {
            window.ApplySettings(window.Settings with { EditorFontSize = 15 });
            store.Settings.Flush();
            string file = Path.Combine(settings, StateStore.SettingsFile);
            string saved = File.ReadAllText(file);
            File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
            window.OpenFile(Path.Combine(folder, "One.cs"));
            window.Perform(AppActions.SplitRight);
            Ui.Settle();
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            Editor<NumericUpDown>(dialog, "Font size").Value = 19;
            Ui.Click(Editor<CheckBox>(dialog, "Show line numbers"));
            Assert.True(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("takes more processor time and memory", StringComparison.Ordinal) == true && t.IsEffectivelyVisible),
                "Solution-wide analysis's row does not say what it costs");
            bool analysed = window.Settings.SolutionWideAnalysis;
            dialog.Change(s => s with { CloseTags = false, TerminalFontSize = 21, SolutionWideAnalysis = !analysed });
            Assert.All(window.EditorArea.Groups, g => Assert.Equal(19, g.Editor.FontSize));
            Assert.False(window.EditorArea.CodeEditor.ShowLineNumbers);
            Assert.True(window.EditorArea.CodeEditor.ClosesTags);
            Assert.Equal(analysed, window.Settings.SolutionWideAnalysis);
            Assert.Equal(15, window.Settings.EditorFontSize);
            store.Settings.Flush();
            Assert.Equal(saved, File.ReadAllText(file));
            dialog.Close();
            Assert.All(window.EditorArea.Groups, g => Assert.Equal(15, g.Editor.FontSize));
            Assert.True(window.EditorArea.CodeEditor.ShowLineNumbers);

            dialog = window.ShowSettings();
            dialog.Change(s => s with { EditorFontSize = 17 });
            dialog.Apply();
            dialog.Change(s => s with { EditorFontSize = 22 });
            Assert.Equal(22, window.EditorArea.CodeEditor.FontSize);
            Ui.Click(dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Cancel"));
            Assert.Equal(17, window.EditorArea.CodeEditor.FontSize);
            Assert.Equal(17, window.Settings.EditorFontSize);

            dialog = window.ShowSettings();
            dialog.Change(s => s with { EditorFontSize = 20 });
            dialog.Change(s => s with { EditorFontFamily = "Passenger Missing Font" });
            Assert.NotNull(dialog.Refused);
            Assert.Equal(CodeEditor.CodeFont, window.EditorArea.CodeEditor.FontFamily);
            Ui.Press(dialog, "ESCAPE");
            Assert.False(dialog.IsVisible);
            Assert.Equal(17, window.EditorArea.CodeEditor.FontSize);
        }
        finally
        {
            window.Close();
            store.Release();
        }
    }

    private void Preview_overlays_other_windows_and_external_changes_without_undoing_them_on_close()
    {
        MainWindow window = Open();
        MainWindow other = Open();
        window.KeepSettings();
        other.KeepSettings();
        StateStore store = StateStore.Open(settings);
        try
        {
            window.ApplySettings(window.Settings with { EditorFontSize = 13 });
            store.Settings.Flush();
            SettingsDialog dialog = window.ShowSettings();
            dialog.Change(s => s with { EditorFontSize = 19 });
            Assert.Equal(19, other.EditorArea.CodeEditor.FontSize);
            other.ApplySettings(other.Settings with { EditorLineSpacing = 1.5 });
            Assert.Equal(19, window.EditorArea.CodeEditor.FontSize);
            Assert.Equal(1.5, window.EditorArea.CodeEditor.LineSpacing);
            store.Settings.Flush();
            WrittenElsewhere(Path.Combine(settings, StateStore.SettingsFile), 21);
            store.Settings.LookAgain();
            Ui.WaitFor(() => window.Settings.EditorFontSize == 21, "the external setting to be read during preview");
            Assert.Equal(19, window.EditorArea.CodeEditor.FontSize);
            dialog.Close();
            Assert.Equal(21, window.EditorArea.CodeEditor.FontSize);
            Assert.Equal(21, other.EditorArea.CodeEditor.FontSize);
            Assert.Equal(1.5, window.EditorArea.CodeEditor.LineSpacing);
        }
        finally
        {
            other.Close();
            window.Close();
            store.Release();
        }
    }

    /// <summary>Settings shows the soft wrap of the files it names and the terminal's size and cursor as they are changed,
    /// before Apply, and the file and the terminal go back as it closes unapplied.</summary>
    private void Soft_wrap_and_the_terminals_size_and_cursor_show_as_changed_in_settings_and_go_back_as_it_closes_unapplied()
    {
        TestFolders.Write(folder, "Long.cs", "class Long { }\n");
        MainWindow window = Open();
        try
        {
            window.OpenFile(Path.Combine(folder, "Long.cs"));
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument document = window.EditorArea.Active!;
            Assert.False(document.SoftWrap);
            Passenger.Terminal.TerminalView view = window.TerminalPanel.View;
            Assert.Equal((13, Passenger.Terminal.CursorShape.Block), (view.FontSize, view.CursorStyle));
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            Editor<TextBox>(dialog, "Soft-wrap these files").Text = AppSettings.DefaultSoftWrapFiles + "; *.cs";
            Ui.Settle();
            Assert.True(document.SoftWrap, "Long.cs is among the files Settings now soft-wraps, but it is not wrapped before Apply");
            dialog.Page = SettingsDialog.TerminalPage;
            Ui.Settle();
            Editor<NumericUpDown>(dialog, "Font size").Value = 21;
            Editor<ComboBox>(dialog, "Cursor style").SelectedIndex = 1;
            Ui.Settle();
            Assert.Equal((21, Passenger.Terminal.CursorShape.Bar), (view.FontSize, view.CursorStyle));
            Assert.Equal((AppSettings.DefaultSoftWrapFiles, 13), (window.Settings.SoftWrapFiles, window.Settings.TerminalFontSize));

            dialog.Close();
            Ui.Settle();
            Assert.False(document.SoftWrap, "Settings closed unapplied, but Long.cs is still soft-wrapped");
            Assert.Equal((13, Passenger.Terminal.CursorShape.Block), (view.FontSize, view.CursorStyle));
        }
        finally
        {
            window.Close();
        }
    }

    private void Display_scale_applies_to_open_windows_and_popups_and_is_kept()
    {
        string file = Path.Combine(folder, "Scale.cs");
        File.WriteAllText(file, "class Scale { }\n");
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            window.OpenFile(file);
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ComboBox factor = Editor<ComboBox>(dialog, "Display scale");
            Assert.Equal("100%", factor.SelectedItem);
            factor.SelectedItem = "150%";
            Ui.Settle();
            Assert.Equal(1, window.Settings.DisplayScale);
            Assert.True(Scale(window.EditorArea.CodeEditor) == 1.5, "Settings did not show the display scale chosen before Apply");
            dialog.Apply();
            Ui.Settle();
            Assert.Equal(1.5, window.Settings.DisplayScale);
            Assert.Equal(1.5, Scale(factor));
            Assert.Equal(1.5, Scale(window.EditorArea.CodeEditor));
            Assert.Equal(13, window.EditorArea.CodeEditor.FontSize);
            dialog.Close();

            Button target = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible);
            Panel host = window.GetVisualDescendants().OfType<Panel>().First();
            foreach (bool overlay in new[] { false, true })
            {
                int clicked = 0;
                Button choice = new() { Content = "Scaled popup choice" };
                choice.Click += (_, _) => clicked++;
                Panel choices = new() { Children = { choice } };
                Popup popup = new() { PlacementTarget = target, Child = choices, ShouldUseOverlayLayer = overlay };
                host.Children.Add(popup);
                try
                {
                    popup.Open();
                    Ui.Settle();
                    Assert.Equal(1.5, Scale(choice));
                    Ui.Click(choice);
                    Assert.Equal(1, clicked);
                    Button nestedChoice = new() { Content = "Nested choice" };
                    Popup nested = new() { PlacementTarget = choice, Child = nestedChoice, ShouldUseOverlayLayer = overlay };
                    choices.Children.Add(nested);
                    try
                    {
                        nested.Open();
                        Ui.Settle();
                        Assert.Equal(1.5, Scale(nestedChoice));
                    }
                    finally
                    {
                        nested.Close();
                        choices.Children.Remove(nested);
                    }
                }
                finally
                {
                    popup.Close();
                    host.Children.Remove(popup);
                }
            }

            window.Close();
            window = Open();
            window.KeepSettings();
            window.OpenFile(file);
            Ui.Settle();
            Assert.Equal(1.5, window.Settings.DisplayScale);
            dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            factor = Editor<ComboBox>(dialog, "Display scale");
            Assert.Equal("150%", factor.SelectedItem);
            Assert.Equal(1.5, Scale(factor));
            factor.SelectedItem = "200%";
            Ui.Settle();
            Assert.Equal(2, Scale(window.EditorArea.CodeEditor));
            dialog.Close();
            Ui.Settle();
            Assert.True(Scale(window.EditorArea.CodeEditor) == 1.5, "Settings closed unapplied, but the window keeps the display scale it showed");
            dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            factor = Editor<ComboBox>(dialog, "Display scale");
            factor.SelectedItem = "125%";
            dialog.Apply();
            Ui.Settle();
            Assert.Equal(1.25, Scale(factor));
            Assert.Equal(1.25, Scale(window.EditorArea.CodeEditor));
            factor.SelectedItem = "100%";
            dialog.Ok();
            Ui.Settle();
            Assert.Equal(1, Scale(window.EditorArea.CodeEditor));
        }
        finally
        {
            window.ApplySettings(window.Settings with { DisplayScale = 1 });
            window.Close();
        }

        static double Scale(Control control) => control.TransformToVisual(TopLevel.GetTopLevel(control)!)!.Value.M11;
    }

    /// <summary>A display scale is chosen from a list of sizes that closes as the pointer chooses one, so the size the
    /// window then takes moves nothing the pointer is on, each choice landing where a spin box's arrows moved out from under
    /// it; at each the main window holds its layout's smallest width and height (640 by 500 units of content however large
    /// they are drawn), and the Settings dialog, grown with its content, stays on its screen by the windows' one rule.</summary>
    private void A_display_scale_is_chosen_from_a_list_that_closes_and_every_window_holds_its_layout_and_its_screen_at_each()
    {
        string file = Path.Combine(folder, "Scaled.cs");
        File.WriteAllText(file, "class Scaled { }\n");
        MainWindow window = Open();
        try
        {
            window.OpenFile(file);
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            Avalonia.Platform.Screen screen = dialog.Screens.ScreenFromWindow(dialog) ?? dialog.Screens.Primary!;
            Size area = screen.WorkingArea.Size.ToSize(screen.Scaling);
            foreach ((double size, string chosen) in new[] { (1.25, "125%"), (1.5, "150%"), (2.0, "200%"), (3.0, "300%") })
            {
                ComboBox box = Editor<ComboBox>(dialog, "Display scale");
                Ui.Click(box);
                Assert.True(box.IsDropDownOpen, "A click on the display scale's box did not open its list");
                ComboBoxItem item = Assert.IsType<ComboBoxItem>(box.ContainerFromIndex(Array.IndexOf((string[])box.ItemsSource!, chosen)));
                Ui.Click(item);
                Assert.False(box.IsDropDownOpen, $"The list stayed open under the pointer after {chosen} was chosen");
                Assert.Equal(size, Scale(window.EditorArea.CodeEditor));
                Assert.Equal(chosen, Editor<ComboBox>(dialog, "Display scale").SelectedItem);

                Assert.True(window.MinWidth == Math.Min(MainWindow.SmallestWidth * size, area.Width) && window.MinHeight == Math.Min(MainWindow.SmallestHeight * size, area.Height),
                    $"At {chosen} the main window's least size was {window.MinWidth}x{window.MinHeight} on a {area} screen");
                window.Width = MainWindow.SmallestWidth;
                Ui.Settle();
                Control content = window.GetVisualDescendants().OfType<LayoutTransformControl>().First().Child!;
                Assert.True(content.Bounds.Width >= Math.Min(MainWindow.SmallestWidth, Math.Floor(area.Width / size)),
                    $"At {chosen} the main window made {MainWindow.SmallestWidth} wide laid its content out {content.Bounds.Width} units wide");
                PixelRect frame = new(dialog.Position, PixelSize.FromSize(dialog.FrameSize ?? dialog.ClientSize, dialog.DesktopScaling));
                Assert.True(screen.WorkingArea.Contains(frame), $"At {chosen} the Settings dialog's frame {frame} left the screen's working area {screen.WorkingArea}");
            }
            dialog.Close();
            Ui.Settle();
            Assert.True(window.MinWidth == MainWindow.SmallestWidth && Scale(window.EditorArea.CodeEditor) == 1,
                $"Settings closed unapplied left the window's least width {window.MinWidth} and its scale {Scale(window.EditorArea.CodeEditor)}");
        }
        finally
        {
            window.Close();
        }

        static double Scale(Control control) => control.TransformToVisual(TopLevel.GetTopLevel(control)!)!.Value.M11;
    }

    private void Display_scale_outside_its_range_still_opens_usable_settings(double given, double expected)
    {
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { DisplayScale = given });
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            Assert.Equal(expected, window.Settings.DisplayScale);
            Assert.Equal(SizeSetting.Percent(expected), Editor<ComboBox>(dialog, "Display scale").SelectedItem);
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(window.Settings with { DisplayScale = 1 });
            window.Close();
        }
    }

    private void Terminal_preferences_apply_to_the_view_and_are_kept_for_the_next_window()
    {
        string font = OtherTerminalFont();
        MainWindow window = Open();
        window.KeepSettings();
        SettingsDialog dialog = TerminalDialog(window);
        Assert.Contains("Font family", dialog.Shown);
        Assert.False(Editor<CheckBox>(dialog, "Copy on selection").IsChecked);
        Assert.False(Editor<CheckBox>(dialog, "Cursor blinking").IsChecked);
        Assert.Equal("Block", Editor<ComboBox>(dialog, "Cursor style").SelectedItem);
        Assert.Equal(TerminalFollows, FontList(dialog).SelectedItem?.ToString());
        ChooseRow(FontList(dialog), font);
        Ui.Click(Editor<CheckBox>(dialog, "Copy on selection"));
        Ui.Click(Editor<CheckBox>(dialog, "Cursor blinking"));
        Editor<ComboBox>(dialog, "Cursor style").SelectedIndex = 1;
        dialog.Ok();
        Ui.Settle();
        Assert.Equal(font, window.TerminalPanel.View.FontFamily.Name);
        window.Close();

        MainWindow again = Open();
        again.KeepSettings();
        Assert.Equal(font, again.TerminalPanel.View.FontFamily.Name);
        SettingsDialog kept = TerminalDialog(again);
        Assert.Equal(font, FontList(kept).SelectedItem?.ToString());
        Assert.True(Editor<CheckBox>(kept, "Copy on selection").IsChecked);
        Assert.True(Editor<CheckBox>(kept, "Cursor blinking").IsChecked);
        Assert.Equal("Line", Editor<ComboBox>(kept, "Cursor style").SelectedItem);
        kept.Close();
        again.Close();
    }

    /// <summary>The terminal's font chosen in Settings › Terminal shows at once and goes back as Settings closes unapplied;
    /// JetBrains Mono applied there stays the terminal's while the editor's font changes, and Same as the editor has it
    /// follow the editor's again.</summary>
    private void A_terminal_font_shows_as_chosen_until_settings_closes_unapplied_and_same_as_the_editor_follows_the_editors_font()
    {
        string font = OtherTerminalFont();
        MainWindow window = Open();
        try
        {
            window.TerminalPanel.Shell = (OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", []);
            Ui.Run(window, AppActions.ShowTerminal);
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the terminal's shell to start");
            Passenger.Terminal.TerminalView view = window.TerminalPanel.View;
            SettingsDialog dialog = TerminalDialog(window);
            ChooseRow(FontList(dialog), font);
            Ui.Settle();
            Assert.Equal(font, view.FontFamily.Name);
            Assert.Null(window.Settings.TerminalFontFamily);
            dialog.Close();
            Assert.Equal(CodeFonts.Carried, view.FontFamily.Name);

            dialog = TerminalDialog(window);
            ChooseRow(FontList(dialog), font);
            dialog.Apply();
            Ui.Settle();
            Assert.Equal(font, view.FontFamily.Name);
            ChooseRow(FontList(dialog), CodeFonts.Carried);
            dialog.Change(s => s with { EditorFontFamily = font });
            dialog.Apply();
            Ui.Settle();
            Assert.Equal(CodeFonts.Carried, view.FontFamily.Name);
            ChooseRow(FontList(dialog), TerminalFollows);
            dialog.Apply();
            Ui.Settle();
            Assert.Null(window.Settings.TerminalFontFamily);
            Assert.Equal(font, view.FontFamily.Name);
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(window.Settings with { EditorFontFamily = null });
            window.Close();
        }
    }

    private void Old_terminal_settings_and_an_unavailable_font_keep_a_usable_default()
    {
        File.WriteAllText(Path.Combine(settings, "settings.json"), """
            { "version": 1, "terminalFontSize": 15, "terminalFontFamily": "Passenger Missing Font", "terminalCursorStyle": 99 }
            """);
        MainWindow window = Open();
        window.KeepSettings();
        Assert.Equal(15, window.TerminalPanel.View.FontSize);
        Assert.Equal(CodeEditor.CodeFont, window.TerminalPanel.View.FontFamily);
        Assert.Equal("Passenger Missing Font", window.Settings.TerminalFontFamily);
        SettingsDialog dialog = TerminalDialog(window);
        Assert.Contains("Cursor style", dialog.Shown);
        Assert.Equal("Passenger Missing Font (not installed)", FontList(dialog).SelectedItem?.ToString());
        Assert.Null(dialog.Refused);
        Assert.Equal("Block", Editor<ComboBox>(dialog, "Cursor style").SelectedItem);
        Assert.False(Editor<CheckBox>(dialog, "Copy on selection").IsChecked);
        Assert.False(Editor<CheckBox>(dialog, "Cursor blinking").IsChecked);
        dialog.Close();
        window.Close();
    }

    /// <summary>A proportional family named for the terminal by settings.json leaves its cells in the code font, and
    /// Settings refuses one chosen there, saying why.</summary>
    [AvaloniaFact]
    public void A_proportional_terminal_font_keeps_the_cell_grid_in_the_code_font_and_settings_refuses_it()
    {
        string? proportional = FontManager.Current.SystemFonts.Select(f => f.Name).FirstOrDefault(name => !name.StartsWith('.') && CodeFonts.Named(name) is null);
        Assert.SkipWhen(proportional is null, "No proportional system font is installed");
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { TerminalFontFamily = proportional });
        Ui.Settle();
        Assert.Equal(CodeEditor.CodeFont, window.TerminalPanel.View.FontFamily);
        Assert.Equal(proportional, window.Settings.TerminalFontFamily);
        window.ApplySettings(window.Settings with { TerminalFontFamily = null });
        SettingsDialog dialog = TerminalDialog(window);
        dialog.Change(s => s with { TerminalFontFamily = proportional });
        Assert.Equal($"Font family {proportional} ({SettingsDialog.PlaceOfPage(SettingsDialog.TerminalPage)}) is not monospaced: code's columns need every character one width", dialog.Refused);
        dialog.Close();
        window.Close();
    }

    private readonly string folder = TestFolders.Create("folder-");
    private readonly string settings = TestFolders.Create("settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>Every page of Settings at the dialog's least width cuts no word and lays nothing over anything
    /// (LayoutFaults): a field's placeholder too long for it, as the Run page's browser where one is found at a long path,
    /// ends in an ellipsis rather than being cut mid-letter at the field's edge.</summary>
    private void Every_page_at_the_least_width_cuts_no_word_and_lays_nothing_over_anything()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        List<string> found = [];
        try
        {
            Ui.Settle();
            dialog.Width = dialog.MinWidth;
            foreach (string page in dialog.Pages.ToList())
            {
                dialog.Page = page;
                Ui.Settle();
                found.AddRange(LayoutFaults.Of(dialog, dialog, $"Settings › {page}, at the dialog's least width,"));
            }
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
        Assert.True(found.Count == 0, string.Join("\n", found.Distinct()));
    }

    private void A_search_keeps_the_pages_and_options_it_matches()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        Assert.True(dialog.RowsMade == dialog.Shown.Count, $"Opening Settings made {dialog.RowsMade} rows where its first page shows {dialog.Shown.Count}: "
            + "every page's rows, the Keymap page's hundreds among them, are made as it opens");
        dialog.Query = "font";
        Ui.Settle();
        // The Keymap page has Increase, Decrease and Reset Font Size.
        Assert.Equal([SettingsDialog.Appearance, SettingsDialog.EditorPage, SettingsDialog.TerminalPage, SettingsDialog.KeymapPage], dialog.Pages);
        Assert.Equal(SettingsDialog.Appearance, dialog.Page);
        Assert.Equal(["Font family", "Display scale"], dialog.Shown);
        dialog.Page = SettingsDialog.EditorPage;
        Ui.Settle();
        Assert.Contains("Font size", dialog.Shown);
        Assert.DoesNotContain("Show line numbers", dialog.Shown);

        int made = dialog.RowsMade;
        dialog.Query = "split right";
        Ui.Settle();
        Assert.Equal([SettingsDialog.KeymapPage], dialog.Pages);
        Assert.Equal(["Split Right"], dialog.Shown);
        Assert.True(dialog.RowsMade == made + 1, $"A search finding one action on the Keymap page, never shown, made {dialog.RowsMade - made} rows: a search makes every row of the page it shows");
        dialog.Close();
        window.Close();
    }

    /// <summary>The editor's font family chosen in Settings › Editor from the installed monospaced fonts, each row in its
    /// own face, is the code font everywhere: every editor, the terminal, which has none of its own, and code the window
    /// shows elsewhere (the find bar's and Find in Files' regex toggles); a proportional or missing family is refused,
    /// saying why, and the carried font comes back when it is chosen again.</summary>
    private void The_editor_font_family_chosen_in_settings_is_the_code_font_everywhere_and_a_proportional_one_is_refused()
    {
        IReadOnlyList<string> installed = Ui.Await(CodeFonts.InstalledAsync(), "the installed monospaced fonts");
        Assert.SkipWhen(installed.Count == 0, "No monospaced font is installed beside the one Passenger carries");
        string font = installed[0];
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            ComboBox family = Editor<ComboBox>(dialog, "Font family");
            Ui.WaitFor(() => family.ItemCount == installed.Count + 1, "the installed monospaced fonts to fill Settings' list");
            Assert.Equal(CodeFonts.Carried, family.SelectedItem?.ToString());
            TextBlock row = ChooseRow(family, font);
            Assert.Equal(font, row.FontFamily.Name);
            Ui.Settle();
            Assert.Equal(font, window.EditorArea.CodeEditor.FontFamily.Name);
            Assert.Null(window.Settings.EditorFontFamily);
            dialog.Ok();
            Ui.Settle();
            Assert.Equal(font, window.Settings.EditorFontFamily);
            Assert.Equal(font, window.EditorArea.CodeEditor.FontFamily.Name);
            window.TerminalPanel.Shell = (OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", []);
            Ui.Run(window, AppActions.ShowTerminal);
            Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the terminal's shell to start");
            Assert.Equal(font, window.TerminalPanel.View.FontFamily.Name);
            window.EditorArea.CodeEditor.Focus();
            Ui.Press(window, Ui.Shortcut(AppActions.Find));
            Assert.Equal(font, RegexToggle(window.EditorArea.FindBar).FontFamily.Name);
            Ui.Press(window, "ESCAPE");
            Ui.Press(window, Ui.Shortcut(AppActions.FindInFiles));
            Assert.Equal(font, RegexToggle(Assert.IsType<Passenger.App.Navigation.FindInFilesPopup>(window.Popup)).FontFamily.Name);
            Ui.Press(window, "ESCAPE");

            SettingsDialog again = window.ShowSettings();
            Ui.Settle();
            string? proportional = FontManager.Current.SystemFonts.Select(f => f.Name)
                .FirstOrDefault(name => !name.StartsWith('.') && CodeFonts.Named(name) is null);
            if (proportional is not null)
            {
                again.Change(s => s with { EditorFontFamily = proportional });
                Assert.Equal($"Font family {proportional} ({SettingsDialog.PlaceOfPage(SettingsDialog.EditorPage)}) is not monospaced: code's columns need every character one width", again.Refused);
            }
            again.Change(s => s with { EditorFontFamily = "Passenger Missing Font" });
            Assert.Equal($"Font family Passenger Missing Font ({SettingsDialog.PlaceOfPage(SettingsDialog.EditorPage)}) is not a font installed on this computer", again.Refused);
            again.Change(s => s with { EditorFontFamily = null });
            Assert.Null(again.Refused);
            again.Ok();
            Ui.Settle();
            Assert.Equal(CodeEditor.CodeFont, window.EditorArea.CodeEditor.FontFamily);
            Assert.Equal(CodeEditor.CodeFont, window.TerminalPanel.View.FontFamily);
        }
        finally
        {
            window.ApplySettings(window.Settings with { EditorFontFamily = null });
            window.Close();
        }
    }

    private void A_change_reaches_every_editor_and_the_next_window_opens_on_it()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Open();
        window.KeepSettings();
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();

        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Change(s => s with { EditorFontSize = 17, ShowLineNumbers = false, TerminalFontSize = 15 });
        dialog.Ok();
        Ui.Settle();
        Assert.Equal(17, window.EditorArea.CodeEditor.FontSize);
        Assert.False(window.EditorArea.CodeEditor.ShowLineNumbers);
        Assert.Equal(15, window.TerminalPanel.View.FontSize);
        window.Perform(AppActions.SplitRight);
        Ui.Settle();
        Assert.All(window.EditorArea.Groups, g => Assert.Equal(17, g.Editor.FontSize));
        window.Close();

        MainWindow again = Open();
        again.KeepSettings();
        Assert.Equal(17, again.Settings.EditorFontSize);
        Assert.Equal(17, again.EditorArea.CodeEditor.FontSize);
        again.Close();
    }

    private void The_font_size_actions_and_the_zoom_wheel_change_the_editors_font_and_the_next_window_opens_on_it()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepSettings();
        window.OpenFolder(folder);
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(13, editor.FontSize);

        MenuEntry view = MainMenu.Build(window.Actions.All).Single(m => m.Title == "View");
        MenuEntry active = view.Items!.Single(m => m.Title == "Active Editor");
        Assert.Equal([AppActions.SoftWrap, null, AppActions.ShowWhitespaces, null, AppActions.IncreaseFontSize, AppActions.DecreaseFontSize, AppActions.ResetFontSize],
            active.Items!.Select(i => i.Action));

        Assert.True(window.RunFromMenu(AppActions.IncreaseFontSize));
        Assert.Equal(14, editor.FontSize);
        Assert.True(window.RunFromMenu(AppActions.DecreaseFontSize));
        Assert.True(window.RunFromMenu(AppActions.DecreaseFontSize));
        Assert.Equal(12, editor.FontSize);
        Assert.True(window.RunFromMenu(AppActions.ResetFontSize));
        Assert.Equal(13, editor.FontSize);

        // Until Settings turns it on, the wheel with Cmd (Ctrl elsewhere) leaves the font as it is.
        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
        RawInputModifiers zoom = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.MouseWheel(over, new Vector(0, 1), zoom);
        Ui.Settle();
        Assert.Equal(13, editor.FontSize);
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "mouse wheel";
        Ui.Settle();
        Assert.Equal([OperatingSystem.IsMacOS() ? "Change font size with ⌘Mouse Wheel" : "Change font size with Ctrl+Mouse Wheel"], dialog.Shown);
        dialog.Change(s => s with { WheelZoom = true });
        dialog.Ok();
        Ui.Settle();
        for (int notch = 0; notch < 3; notch++)
        {
            window.MouseWheel(over, new Vector(0, 1), zoom);
        }
        window.MouseWheel(over, new Vector(0, -1), zoom);
        Ui.Settle();
        Assert.Equal(15, editor.FontSize);
        Assert.Equal(15, window.Settings.EditorFontSize);
        window.Close();

        MainWindow again = new() { Width = 1200, Height = 800, StateFolder = settings };
        again.Show();
        again.KeepSettings();
        Ui.Settle();
        Assert.Equal(15, again.Settings.EditorFontSize);
        Assert.Equal(15, again.EditorArea.CodeEditor.FontSize);
        again.Close();
    }

    private void A_shortcut_set_in_the_keymap_runs_its_action_and_one_taken_from_another_action_leaves_it()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = Open();
        window.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        string mine = OperatingSystem.IsMacOS() ? "meta alt shift J" : "control alt shift J";
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Rebind(AppActions.SplitRight, mine);
            string saveAll = Ui.Shortcut(AppActions.SaveAll);
            dialog.Rebind(AppActions.SplitDown, saveAll);
            Assert.Empty(AppActions.Keymap(dialog.Working.Shortcuts).ShortcutsFor(AppActions.SaveAll));
            dialog.Ok();
            Ui.Settle();

            Ui.Press(window, mine);
            Assert.Equal(2, window.EditorArea.Groups.Count);
            Ui.Press(window, saveAll);
            Assert.True(window.EditorArea.Groups.Count == 3, "Save All's old keys, given to Split Down, split nothing");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>The browser debugging was given, kept as debugBrowser, is Settings' Browser, the one pages open and are
    /// debugged in: Settings › Run shows it there, the system's default browser said where it is empty, and a change to it is
    /// kept under browser alone. The program a row runs when empty is looked for off the window's thread: Settings opens
    /// without a search for Godot there, and shows the Godot found once looked for.</summary>
    private void The_browser_kept_for_debugging_is_the_browser_settings_shows_and_keeps_under_its_name_and_no_program_row_looks_on_the_windows_thread()
    {
        string file = Path.Combine(settings, StateStore.SettingsFile);
        string line = $"\"{Path.Combine(folder, "Chrome Dev", "chrome")}\" --lang=fr";
        File.WriteAllText(file, new JsonObject { ["version"] = AppSettings.Upgrades.Count, ["debugBrowser"] = line }.ToJsonString());
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            Assert.Equal(line, window.Settings.Browser);
            int searched = Passenger.Core.GodotExecutables.SearchedOnThisThread;
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Page = SettingsDialog.GodotPage;
            Ui.Settle();
            Assert.True(Passenger.Core.GodotExecutables.SearchedOnThisThread == searched, "Settings looked for Godot on the window's thread as it opened, "
                + "which walks every PATH folder and the Applications folders while the window draws nothing");
            string godot = GodotLaunch.Found(null) is { Exists: true } found ? found.Executable : "Not found";
            Assert.Equal(godot, Editor<TextBox>(dialog, Declared.GodotExecutable.Row!.Label.English).PlaceholderText);
            dialog.Page = SettingsDialog.RunPage;
            Ui.Settle();
            TextBox browser = Editor<TextBox>(dialog, PageBrowsers.Row);
            Assert.True(browser.Text == line && browser.PlaceholderText == PageBrowsers.SystemDefault,
                $"Settings › Run's Browser shows \"{browser.Text}\" over \"{browser.PlaceholderText}\", where the browser kept for debugging is {line}");
            browser.Text = $"{line} --incognito";
            Ui.Settle();
            dialog.Ok();
            Ui.Settle();
            Assert.Equal($"{line} --incognito", window.Settings.Browser);
            StateStore.Open(settings).Settings.Flush();
            JsonObject written = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.True(written["browser"]?.GetValue<string>() == $"{line} --incognito" && !written.ContainsKey("debugBrowser"),
                $"settings.json keeps the browser so: {written.ToJsonString()}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A shortcut kept under an action's former id, reset in the keymap, stays reset: settings.json is written with
    /// the former id gone, so the next window, reading it again, gives the action nothing.</summary>
    private void A_shortcut_kept_under_an_actions_former_id_stays_reset_once_reset_and_read_again()
    {
        string file = Path.Combine(settings, StateStore.SettingsFile);
        string keys = OperatingSystem.IsMacOS() ? "meta alt shift K" : "control alt shift K";
        File.WriteAllText(file, new JsonObject
        {
            ["version"] = AppSettings.Upgrades.Count,
            ["shortcuts"] = new JsonObject { ["app.aspire.debug"] = new JsonArray(keys) },
        }.ToJsonString());
        try
        {
            MainWindow window = Open();
            window.KeepSettings();
            Assert.True(Binds(window, keys), "The shortcut kept under app.aspire.debug did not bind Debug Service, so the test showed nothing");
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "debug service";
            Ui.Settle();
            dialog.Page = SettingsDialog.KeymapPage;
            Ui.Settle();
            Ui.ChooseFromMenu(dialog.KeymapRow(AppActions.ServiceDebug), "Reset Shortcuts");
            dialog.Ok();
            Ui.Settle();
            Assert.False(Binds(window, keys), "Reset Shortcuts left Debug Service the shortcut kept under app.aspire.debug");
            window.Close();
            string written = File.ReadAllText(file);
            Assert.True(!written.Contains("app.aspire.debug", StringComparison.Ordinal), $"settings.json still keeps the shortcut under the former id once reset:\n{written}");

            MainWindow again = Open();
            again.KeepSettings();
            Assert.False(Binds(again, keys), $"Read again, settings.json gives Debug Service back the shortcut reset:\n{written}");
            again.Close();
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            AppActions.Choose(new AppSettings().Keymap, new Dictionary<string, string[]>());
        }
    }

    /// <summary>A shell path naming no program ("" alone) is refused where it is set: Settings says why under its box and
    /// greys OK and Apply, putting nothing in force. Met in settings.json, a new terminal tab says why no shell started
    /// (where its start threw, unsaid), Settings shows why under the box, and a shell named there starts.</summary>
    private void A_shell_path_naming_no_program_is_refused_in_settings_and_a_terminal_tab_met_with_it_says_why()
    {
        string plain = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = TerminalPage(window);
            Ui.Click(ShellPath(dialog));
            dialog.KeyTextInput("\"\"");
            Ui.Settle();
            Assert.True(ShellProblem(dialog) == "This names no program to start: the program's path comes first, its arguments after it",
                $"Settings said nothing under a shell path naming no program: {ShellProblem(dialog)}");
            Assert.False(Footer(dialog, "OK").IsEffectivelyEnabled || Footer(dialog, "Apply").IsEffectivelyEnabled,
                "OK and Apply could put in force a shell path naming no program");
            DialogButtonsTests.Check(dialog);
            dialog.Ok();
            Ui.Settle();
            Assert.True(dialog.IsVisible && window.Settings.TerminalShell is null, $"OK took a shell path naming no program: {window.Settings.TerminalShell}");
            dialog.Close();
        }
        finally
        {
            window.Close();
        }

        File.WriteAllText(Path.Combine(settings, StateStore.SettingsFile),
            new JsonObject { ["version"] = AppSettings.Upgrades.Count, ["terminalShell"] = "\"\"" }.ToJsonString());
        MainWindow again = Open();
        try
        {
            again.KeepSettings();
            again.Perform(AppActions.NewTerminalSession);
            Ui.WaitFor(() => again.TerminalPanel.Active is { } shell && TerminalTests.Screen(shell).Contains("could not start", StringComparison.Ordinal),
                () => $"the new terminal tab to say why no shell started: {string.Join(", ", again.TerminalPanel.Tabs.Select(t => t.Name))}");
            string said = TerminalTests.Screen(again.TerminalPanel.Active!);
            Assert.True(said.Contains("Passenger could not start a shell: the Shell path in Settings | Terminal names no program to start", StringComparison.Ordinal),
                $"The terminal tab met with a shell path naming no program said:\n{said}");

            SettingsDialog dialog = TerminalPage(again);
            TextBox path = ShellPath(dialog);
            Assert.True(path.Text == "\"\"" && ShellProblem(dialog) is not null, $"Settings showed the kept shell path as {path.Text}, saying {ShellProblem(dialog)}");
            Ui.Click(path);
            path.SelectAll();
            dialog.KeyTextInput(plain);
            Ui.Settle();
            Assert.True(ShellProblem(dialog) is null && Footer(dialog, "OK").IsEffectivelyEnabled, $"Settings refused the shell path {path.Text}: {ShellProblem(dialog)}");
            dialog.Ok();
            Ui.Settle();
            Assert.Equal(plain, again.Settings.TerminalShell);
            again.Perform(AppActions.NewTerminalSession);
            Ui.WaitFor(() => again.TerminalPanel.Active is { IsRunning: true }, () => $"{plain} to start in a new terminal tab");
        }
        finally
        {
            again.Close();
        }

        static SettingsDialog TerminalPage(MainWindow window)
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Page = SettingsDialog.TerminalPage;
            Ui.Settle();
            return dialog;
        }

        static TextBox ShellPath(SettingsDialog dialog) =>
            ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Shell path").Parent!).GetVisualDescendants().OfType<TextBox>().First();

        static string? ShellProblem(SettingsDialog dialog) =>
            dialog.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text?.Contains("no program to start", StringComparison.Ordinal) == true)?.Text;

        static Button Footer(SettingsDialog dialog, string words) => dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == words);
    }

    /// <summary>Whether a window's keymap gives Debug Service the keys.</summary>
    private static bool Binds(MainWindow window, string keys) =>
        window.Actions.Keymap.ShortcutsFor(AppActions.ServiceDebug).Any(s => s.First.Notation() == Passenger.Editor.Shortcut.Parse(keys).First.Notation());

    /// <summary>Another Passenger's change to settings.json that the watcher never heard is followed all the same: read again
    /// as the watcher says it overflowed, and, for a change dropped without a word, as the window comes in front. The
    /// watcher stops hearing while the file changes, so each change is lost as a dropped event loses it.</summary>
    private void A_change_to_settings_json_the_watcher_lost_is_followed_as_it_overflows_and_as_the_window_comes_in_front()
    {
        MainWindow window = Open();
        window.KeepSettings();
        StateStore store = StateStore.Open(settings);
        string file = Path.Combine(settings, StateStore.SettingsFile);
        WatchedFile watched = store.Settings.Watched ?? throw new InvalidOperationException($"{file} is not watched");
        IDisposable deaf = watched.Deaf();
        try
        {
            window.ApplySettings(window.Settings with { EditorFontSize = 15 });
            store.Settings.Flush();

            WrittenElsewhere(file, 21);
            watched.Fail();
            Ui.WaitFor(() => window.EditorArea.CodeEditor.FontSize == 21,
                $"the window to follow another Passenger's change to settings.json, lost in the watcher's overflow (its font is {window.EditorArea.CodeEditor.FontSize})");

            WrittenElsewhere(file, 19);
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.FontSize == 21, "settings.json was read again with its watcher deaf and no word of a change");
            window.Activate();
            Ui.WaitFor(() => window.EditorArea.CodeEditor.FontSize == 19,
                $"the window come in front to follow another Passenger's change to settings.json, dropped without a word (its font is {window.EditorArea.CodeEditor.FontSize})");
        }
        finally
        {
            deaf.Dispose();
            store.Release();
            window.Close();
        }
    }

    /// <summary>The window's own change to the settings is never read back from settings.json: neither as the window comes in
    /// front nor as the watcher says it overflowed. The file is changed after the window's write with its last write and
    /// length kept, which only a read would find; the window's next change, which reads the file first, finds it.</summary>
    private void The_windows_own_change_to_settings_json_is_not_read_back_as_the_window_comes_in_front()
    {
        MainWindow window = Open();
        window.KeepSettings();
        StateStore store = StateStore.Open(settings);
        string file = Path.Combine(settings, StateStore.SettingsFile);
        WatchedFile watched = store.Settings.Watched ?? throw new InvalidOperationException($"{file} is not watched");
        // Deaf, so its word of the window's own write, which comes when the system sends it, is no part of the test.
        IDisposable deaf = watched.Deaf();
        try
        {
            window.ApplySettings(window.Settings with { EditorFontSize = 15 });
            store.Settings.Flush();
            ChangedUnstamped(file, 16);

            window.Activate();
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.FontSize == 15, "settings.json was read again as the window came in front, though only the window's own change had moved it");
            watched.Fail();
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.FontSize == 15, "settings.json was read again as its watcher overflowed, though only the window's own change had moved it");

            window.ApplySettings(window.Settings with { ShowLineNumbers = false });
            Assert.True(window.EditorArea.CodeEditor.FontSize == 16,
                $"The window's next change did not find the font settings.json was given under its stamp (its font is {window.EditorArea.CodeEditor.FontSize}), so the test showed nothing of what is read");
        }
        finally
        {
            deaf.Dispose();
            store.Release();
            window.Close();
        }
    }

    /// <summary>settings.json kept as a link into a dotfiles folder: another Passenger's change, written to the file the link
    /// names, is followed each time the window comes in front, though no watcher of the state folder hears a write there
    /// and the link itself stays as it was made.</summary>
    private void A_change_to_settings_json_kept_as_a_link_is_followed_each_time_the_window_comes_in_front()
    {
        string dotfiles = TestFolders.Create("dotfiles-");
        MainWindow window = Open();
        window.KeepSettings();
        StateStore store = StateStore.Open(settings);
        string file = Path.Combine(settings, StateStore.SettingsFile);
        string kept = Path.Combine(dotfiles, StateStore.SettingsFile);
        try
        {
            window.ApplySettings(window.Settings with { EditorFontSize = 15 });
            store.Settings.Flush();
            File.Move(file, kept);
            TestFolders.Link(file, kept);
            window.Activate();
            Ui.Settle();

            foreach (int size in (int[])[21, 19])
            {
                WrittenElsewhere(kept, size);
                window.Activate();
                Ui.WaitFor(() => window.EditorArea.CodeEditor.FontSize == size,
                    () => $"the window come in front to follow another Passenger's change to the settings.json a link names, to {size} (its font is {window.EditorArea.CodeEditor.FontSize})");
            }
        }
        finally
        {
            store.Release();
            window.Close();
            TestFolders.Delete(dotfiles);
        }
    }

    /// <summary>settings.json with the editor's font changed in place, its last write and length as they were: a change only a
    /// read of the file finds.</summary>
    private static void ChangedUnstamped(string file, int fontSize)
    {
        FileInfo was = new(file);
        (DateTime written, long length) = (was.LastWriteTimeUtc, was.Length);
        string text = Ui.Shared(file);
        string changed = System.Text.RegularExpressions.Regex.Replace(text, @"(""editorFontSize"":\s*)\d\d\b", $"${{1}}{fontSize}");
        Assert.True(changed != text && changed.Length == text.Length, $"settings.json holds no two-digit editorFontSize to change in place:\n{text}");
        File.WriteAllText(file, changed);
        File.SetLastWriteTimeUtc(file, written);
        FileInfo now = new(file);
        Assert.True((now.LastWriteTimeUtc, now.Length) == (written, length), $"settings.json's last write and length moved as it was changed in place: {now.LastWriteTimeUtc:o} {now.Length}, not {written:o} {length}");
    }

    /// <summary>settings.json with the editor's font changed, as another Passenger writes it: a temporary file moved over it.</summary>
    private static void WrittenElsewhere(string file, int fontSize)
    {
        JsonObject changed = (JsonObject)JsonNode.Parse(Ui.Shared(file))!;
        changed["editorFontSize"] = fontSize;
        string written = file + ".other.tmp";
        File.WriteAllText(written, changed.ToJsonString());
        File.Move(written, file, overwrite: true);
    }

    /// <summary>What edits an option, beside its label in the page shown.</summary>
    /// <summary>The first row of the terminal's font list, which follows the editor's.</summary>
    private const string TerminalFollows = "Same as the editor";

    private static string OtherTerminalFont()
    {
        IReadOnlyList<string> installed = Ui.Await(CodeFonts.InstalledAsync(), "the installed monospaced fonts");
        Assert.SkipWhen(installed.Count == 0, "No monospaced font is installed beside the one Passenger carries");
        return installed[0];
    }

    /// <summary>The page's Font family drop-down, once the installed fonts fill it.</summary>
    private static ComboBox FontList(SettingsDialog dialog)
    {
        ComboBox box = Editor<ComboBox>(dialog, "Font family");
        IReadOnlyList<string> installed = Ui.Await(CodeFonts.InstalledAsync(), "the installed monospaced fonts");
        Ui.WaitFor(() => installed.All(name => box.Items.Cast<object?>().Any(i => i?.ToString() == name)), "the installed monospaced fonts to fill Settings' list");
        return box;
    }

    /// <summary>Chooses a drop-down's row by the words it shows, as a person does: the list opened, the row scrolled to and
    /// clicked; the row's words, as shown.</summary>
    internal static TextBlock ChooseRow(ComboBox box, string shown)
    {
        Ui.Click(box);
        Ui.WaitFor(() => box.IsDropDownOpen, "the drop-down to open");
        int at = box.Items.Cast<object?>().Select(i => i?.ToString()).ToList().IndexOf(shown);
        Assert.True(at >= 0, $"The drop-down has no row {shown}: {string.Join(", ", box.Items.Cast<object?>())}");
        box.ScrollIntoView(at);
        Ui.Settle();
        Control row = Assert.IsAssignableFrom<Control>(box.ContainerFromIndex(at));
        TextBlock words = row.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(shown, words.Text);
        Ui.Click(row);
        Assert.False(box.IsDropDownOpen, $"Choosing {shown} left the drop-down open");
        return words;
    }

    private static TextBlock RegexToggle(Visual within) => within.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == ".*");

    /// <summary>At its smallest size, and at its own, no row of any page runs under the page's scroll bar, which shows over
    /// the page's right: every label, editor and note keeps to the left of where the bar stands, so a note's last words are
    /// never drawn under its thumb.</summary>
    private void No_row_of_any_page_runs_under_its_scroll_bar_at_the_dialogs_smallest_size_or_its_own()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            List<string> under = [];
            foreach ((double width, double height) in new[] { (dialog.Width, dialog.Height), (dialog.MinWidth, dialog.MinHeight) })
            {
                dialog.Width = width;
                dialog.Height = height;
                Ui.Settle();
                bool scrolled = false;
                foreach (string page in dialog.Pages.ToList())
                {
                    dialog.Page = page;
                    Ui.Settle();
                    ScrollViewer scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel && s.IsEffectivelyVisible);
                    ScrollBar bar = scroll.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
                    scrolled |= bar.IsEffectivelyVisible;
                    // Where the bar stands, shown or not: the scroller's right, its own width in.
                    double barLeft = scroll.Bounds.Width - bar.Width;
                    foreach (Control row in ((StackPanel)scroll.Content!).Children.Where(r => r.IsEffectivelyVisible))
                    {
                        Rect at = new Rect(row.Bounds.Size).TransformToAABB(row.TransformToVisual(scroll)!.Value);
                        if (at.Right > barLeft + 0.5)
                        {
                            string words = row.GetSelfAndVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text))?.Text ?? string.Empty;
                            under.Add($"{width:0} wide, {page}: '{words[..Math.Min(words.Length, 40)]}' to {at.Right:0}, the bar from {barLeft:0}");
                        }
                    }
                }
                Assert.True(scrolled, $"At {width:0} by {height:0} no page of Settings scrolled, so none showed its scroll bar to keep clear of");
            }
            Assert.True(under.Count == 0, $"These rows of Settings run under the page's scroll bar, their right drawn under its thumb:\n{string.Join("\n", under.Take(40))}");
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
    }

    internal static SettingsDialog TerminalDialog(MainWindow window)
    {
        SettingsDialog dialog = window.ShowSettings();
        dialog.Page = SettingsDialog.TerminalPage;
        Ui.Settle();
        return dialog;
    }

    internal static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
        ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label).Parent!).GetVisualDescendants().OfType<T>().First();

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }
}

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
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Settings: a search narrows the pages to those with an option whose words match and shows only those options;
/// a change is in force the moment it is applied, in every editor (one a split makes later too), a window that keeps its
/// settings opens on them again, and another Passenger's change to them is followed even where the watcher lost it.</summary>
public sealed class SettingsTests : IDisposable
{
    [AvaloniaFact]
    public void Visual_edits_preview_without_saving_and_cancel_restores_the_last_apply()
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
            dialog.Change(s => s with { CloseTags = false, TerminalFontSize = 21, SolutionWideAnalysis = true });
            Assert.All(window.EditorArea.Groups, g => Assert.Equal(19, g.Editor.FontSize));
            Assert.False(window.EditorArea.CodeEditor.ShowLineNumbers);
            Assert.True(window.EditorArea.CodeEditor.ClosesTags);
            Assert.False(window.Settings.SolutionWideAnalysis);
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

    [AvaloniaFact]
    public void Preview_overlays_other_windows_and_external_changes_without_undoing_them_on_close()
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
    [AvaloniaFact]
    public void Soft_wrap_and_the_terminals_size_and_cursor_show_as_changed_in_settings_and_go_back_as_it_closes_unapplied()
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

    [AvaloniaFact]
    public void Display_scale_applies_to_open_windows_and_popups_and_is_kept()
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
    [AvaloniaFact]
    public void A_display_scale_is_chosen_from_a_list_that_closes_and_every_window_holds_its_layout_and_its_screen_at_each()
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

    /// <summary>A dialog opened while a display scale is in force is sized for its content at that scale, as far as its
    /// screen holds it, by the windows' one rule: Settings, of a set size, and About, as tall as its content, stand 1.5 and
    /// 2 times as large as at 100% where the screen has room, on its working area, every footer button inside.</summary>
    [AvaloniaFact]
    public void A_dialog_opened_at_a_display_scale_is_sized_for_its_content_at_it_with_its_buttons_inside()
    {
        MainWindow window = Open();
        try
        {
            Size settings = Opened(window.ShowSettings(), "Settings", 1, default);
            Assert.True(window.Perform(AppActions.About));
            Ui.Settle();
            Size about = Opened(Assert.IsType<AboutDialog>(window.Frontmost), "About", 1, default);
            foreach (double scale in new[] { 1.5, 2.0 })
            {
                window.ApplySettings(window.Settings with { DisplayScale = scale });
                Ui.Settle();
                Opened(window.ShowSettings(), "Settings", scale, settings);
                Assert.True(window.Perform(AppActions.About));
                Ui.Settle();
                Opened(Assert.IsType<AboutDialog>(window.Frontmost), "About", scale, about);
            }
        }
        finally
        {
            window.ApplySettings(window.Settings with { DisplayScale = 1 });
            window.Close();
        }

        // The dialog's client size, its frame on the screen's working area, its footer buttons inside it, and at a scale the
        // size it had at 100% grown by as much, as far as the area holds it round the frame the window fit keeps free.
        static Size Opened(Window dialog, string name, double scale, Size atOne)
        {
            Ui.Settle();
            try
            {
                Avalonia.Platform.Screen screen = dialog.Screens.ScreenFromWindow(dialog) ?? dialog.Screens.Primary!;
                Size area = screen.WorkingArea.Size.ToSize(screen.Scaling);
                Size client = dialog.ClientSize;
                PixelRect frame = new(dialog.Position, PixelSize.FromSize(dialog.FrameSize ?? client, dialog.DesktopScaling));
                Assert.True(screen.WorkingArea.Contains(frame), $"At {scale:P0} {name}'s frame {frame} left the screen's working area {screen.WorkingArea}");
                Button[] footer = [.. dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is "OK" or "Apply" or "Cancel" or "Close" or "Copy and Close")];
                Assert.True(footer.Length >= 2, $"At {scale:P0} {name} shows {footer.Length} footer buttons");
                foreach (Button button in footer)
                {
                    Rect shown = new(button.TranslatePoint(default, dialog)!.Value, button.Bounds.Size * scale);
                    Assert.True(new Rect(client).Contains(shown), $"At {scale:P0} {name}'s {button.Content} stands at {shown}, outside its {client} client area");
                }
                if (scale != 1)
                {
                    Size wanted = new(Math.Min(atOne.Width * scale, Math.Floor(area.Width - 16)), Math.Min(atOne.Height * scale, Math.Floor(area.Height - 40)));
                    Assert.True(Math.Abs(client.Width - wanted.Width) <= 2 && Math.Abs(client.Height - wanted.Height) <= 2,
                        $"At {scale:P0} {name} opened {client}, where {atOne} at 100% asks for {wanted} on a {area} screen");
                }
                return client;
            }
            finally
            {
                dialog.Close();
                Ui.Settle();
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(0, 1)]
    [InlineData(10, 3)]
    [InlineData(1, 1)]
    [InlineData(1.2, 1.2)]
    [InlineData(1.5, 1.5)]
    public void Display_scale_outside_its_range_still_opens_usable_settings(double given, double expected)
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

    [AvaloniaFact]
    public void Terminal_preferences_apply_to_the_view_and_are_kept_for_the_next_window()
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
    [AvaloniaFact]
    public void A_terminal_font_shows_as_chosen_until_settings_closes_unapplied_and_same_as_the_editor_follows_the_editors_font()
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

    [AvaloniaFact]
    public void Old_terminal_settings_and_an_unavailable_font_keep_a_usable_default()
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
    [AvaloniaFact]
    public void Every_page_at_the_least_width_cuts_no_word_and_lays_nothing_over_anything()
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

    /// <summary>Every row of every Settings page, at the dialog's usual width and at its least, stands its editor level with
    /// its label's first line where the two stand side by side, and lays its note a gap below what stands over it in its
    /// column, so a label running on below its editor (Remote Stylesheets') leaves no empty room beside it under the editor.</summary>
    [AvaloniaFact]
    public void Every_rows_editor_stands_by_its_labels_first_line_with_its_note_right_under_it()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        List<string> found = [];
        int rows = 0;
        int longBeside = 0;
        try
        {
            Ui.Settle();
            foreach (double width in (double[])[dialog.Width, dialog.MinWidth])
            {
                dialog.Width = width;
                foreach (string page in dialog.Pages.ToList())
                {
                    dialog.Page = page;
                    Ui.Settle();
                    foreach (OptionRow row in dialog.GetVisualDescendants().OfType<OptionRow>().Where(r => r.IsEffectivelyVisible))
                    {
                        rows++;
                        string what = $"Settings › {page}, at {width:0} wide, '{row.Label.Text}'";
                        found.AddRange(RoomBesideEditor(row, what));
                        if (!row.IsStacked && row.Label.TextLayout.TextLines.Count > 1 && row.Note is { IsVisible: true })
                        {
                            longBeside++;
                        }
                    }
                }
            }
        }
        finally
        {
            dialog.Close();
            window.Close();
        }
        Assert.True(rows >= 200, $"Only {rows} rows of Settings were found at the two widths: the search no longer reads them");
        Assert.True(longBeside > 0, "No row with a label of several lines and a note stood beside its editor: nothing here reads the room beside an editor under a long label");
        Assert.True(found.Count == 0, $"These rows leave empty room beside their editors, a person's eye crossing a gap from the label to what edits it "
            + $"or from the editor to its note:\n{string.Join("\n", found.Distinct())}");
    }

    /// <summary>Where a row's label and editor stand side by side and not level (the editor's box and the label's first
    /// line, neither within the other), and each line under them begun more than the row's gap below what stands over it
    /// in its column.</summary>
    private static IEnumerable<string> RoomBesideEditor(OptionRow row, string what)
    {
        const double Slack = 0.5;
        Rect label = row.Label.Bounds;
        Rect editor = row.Editor.Bounds;
        bool across = label.Right <= editor.Left + Slack || editor.Right <= label.Left + Slack;
        if (across && row.Label.TextLayout.TextLines is [{ } first, ..])
        {
            double top = label.Top + row.Label.Padding.Top;
            double bottom = top + first.Height;
            bool level = (top >= editor.Top - Slack && bottom <= editor.Bottom + Slack) || (editor.Top >= top - Slack && editor.Bottom <= bottom + Slack);
            if (!level)
            {
                yield return $"{what} stands its editor at {editor.Top:0.#}–{editor.Bottom:0.#} px beside its label's first line at {top:0.#}–{bottom:0.#} px";
            }
        }
        List<Rect> over = [label, editor];
        foreach (TextBlock under in ((TextBlock?[])[row.Note, row.SaidOver]).OfType<TextBlock>().Where(t => t.IsVisible))
        {
            Rect line = under.Bounds;
            double[] above = [.. over.Where(r => r.Right > line.Left + Slack && r.Left < line.Right - Slack && r.Bottom <= line.Top + Slack).Select(r => r.Bottom)];
            if (above.Length > 0 && line.Top - above.Max() > Gaps.Tight + Slack)
            {
                yield return $"{what} leaves {line.Top - above.Max():0.#} px empty over '{under.Text}', which stands a gap of {Gaps.Tight:0} below what is over it in its column";
            }
            over.Add(line);
        }
    }

    [AvaloniaFact]
    public void A_search_keeps_the_pages_and_options_it_matches()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
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

        dialog.Query = "split right";
        Ui.Settle();
        Assert.Equal([SettingsDialog.KeymapPage], dialog.Pages);
        Assert.Equal(["Split Right"], dialog.Shown);
        dialog.Close();
        window.Close();
    }

    /// <summary>The editor's font family chosen in Settings › Editor from the installed monospaced fonts, each row in its
    /// own face, is the code font everywhere: every editor, the terminal, which has none of its own, and code the window
    /// shows elsewhere (the find bar's and Find in Files' regex toggles); a proportional or missing family is refused,
    /// saying why, and the carried font comes back when it is chosen again.</summary>
    [AvaloniaFact]
    public void The_editor_font_family_chosen_in_settings_is_the_code_font_everywhere_and_a_proportional_one_is_refused()
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

    [AvaloniaFact]
    public void A_change_reaches_every_editor_and_the_next_window_opens_on_it()
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

    [AvaloniaFact]
    public void A_shortcut_set_in_the_keymap_runs_its_action_and_one_taken_from_another_action_leaves_it()
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
    /// kept under browser alone.</summary>
    [AvaloniaFact]
    public void The_browser_kept_for_debugging_is_the_browser_settings_shows_and_keeps_under_its_name()
    {
        string file = Path.Combine(settings, StateStore.SettingsFile);
        string line = $"\"{Path.Combine(folder, "Chrome Dev", "chrome")}\" --lang=fr";
        File.WriteAllText(file, new JsonObject { ["version"] = AppSettings.Upgrades.Count, ["debugBrowser"] = line }.ToJsonString());
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            Assert.Equal(line, window.Settings.Browser);
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
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
    [AvaloniaFact]
    public void A_shortcut_kept_under_an_actions_former_id_stays_reset_once_reset_and_read_again()
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
    [AvaloniaFact]
    public void A_shell_path_naming_no_program_is_refused_in_settings_and_a_terminal_tab_met_with_it_says_why()
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

    /// <summary>Toolset and Build, Presentation, with Rider's names and defaults: the Build window opens as an explicit build
    /// starts, not an implicit one, and comes forward only for errors; the implicit build's option is for changing only while
    /// the explicit build's is on. What is chosen is kept, by name.</summary>
    [AvaloniaFact]
    public void The_build_windows_options_are_riders_with_its_defaults_and_are_kept()
    {
        const string Explicitly = "Tool Window: when build is explicitly invoked, open the build progress view in a tool window";
        const string Implicitly = "Tool Window: open the build progress view even when the build is invoked implicitly (i.e. Run/Debug)";
        const string Results = "Showing build results after build completed";
        MainWindow window = Open();
        window.KeepSettings();
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "build progress";
        Ui.Settle();
        Assert.Equal([SettingsDialog.BuildPage], dialog.Pages);
        dialog.Query = string.Empty;
        dialog.Page = SettingsDialog.BuildPage;
        Ui.Settle();
        Assert.Equal([Explicitly, Implicitly, Results], dialog.Shown);
        CheckBox explicitly = Editor<CheckBox>(dialog, Explicitly);
        CheckBox implicitly = Editor<CheckBox>(dialog, Implicitly);
        ComboBox results = Editor<ComboBox>(dialog, Results);
        Assert.True(explicitly.IsChecked == true && implicitly.IsChecked == false && implicitly.IsEnabled, "The Build window's options are not at Rider's defaults");
        Assert.Equal(["Always", "Only if errors or warnings received", "Only if errors received", "Never"], results.Items.Cast<string>());
        Assert.Equal("Only if errors received", results.SelectedItem);

        Ui.Click(explicitly);
        Assert.False(explicitly.IsChecked, "A click did not clear the explicit build's option");
        Assert.False(implicitly.IsEnabled, "The implicit build's option could still be changed with the explicit build's off, which Rider greys");
        results.SelectedIndex = 3;
        dialog.Ok();
        Ui.Settle();
        Assert.False(window.Settings.OpenBuildWindowOnBuild);
        Assert.Equal(BuildResultsMode.Never, window.Settings.ShowBuildResults);
        window.Close();

        MainWindow again = Open();
        again.KeepSettings();
        Assert.False(again.Settings.OpenBuildWindowOnBuild);
        Assert.Equal(BuildResultsMode.Never, again.Settings.ShowBuildResults);
        again.Close();
    }

    /// <summary>Appearance's Theme: Passenger's own or one of a source's (Harbour of Passenger's themes repository here), each
    /// in the dark or light the next row says, listed by name and source once the drop-down opens, the window dressed in the
    /// one chosen the moment it is chosen, before Apply; the Colour scheme beside it: the theme's own or the repository's.</summary>
    [AvaloniaFact]
    public void Harbour_chosen_as_the_theme_dresses_the_window_at_once_beside_passengers_own()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Equal(["Theme", "Dark or light", "Colour scheme"], dialog.Shown.Take(3));
            ThemePicker theme = dialog.ThemePicker;
            Assert.Equal("Passenger", theme.Listed[0]);
            Assert.Equal(0, theme.Box.SelectedIndex);
            Assert.Equal(["System", "Dark", "Light"], Editor<ComboBox>(dialog, "Dark or light").Items.Cast<string>());
            theme.Box.IsDropDownOpen = true;
            // Passenger's own, then those it carries, then the repository's; themes another test added from a file come after.
            string[] repository = ["Passenger", .. ThemeSource.Carried, .. ThemeSource.FormerlyCarried.Select(n => $"{n} ({ThemeSource.RepositoryName})")];
            Ui.WaitFor(() => theme.Listed.Take(repository.Length).SequenceEqual(repository), () => $"every theme of the repository listed in its order, where it lists {string.Join(", ", theme.Listed)}");
            theme.Box.IsDropDownOpen = false;
            Border stripe = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.StripeBar) && b.IsEffectivelyVisible);
            Point bare = Bare(window, stripe);
            Color passenger = Ui.Pixel(window, bare);

            theme.Choose("Harbour");
            Ui.Settle();
            Color harbour = Ui.Pixel(window, bare);
            Assert.True(window.TryFindResource(TokenKeys.Frame, window.ActualThemeVariant, out object? frame) && frame is ISolidColorBrush desk && desk.Color == harbour && harbour != passenger,
                $"With Harbour applied the window's frame is {harbour}, where it was {passenger} under Passenger's own: Harbour's desk did not take its place");
            Assert.True(KeptThemes.Holds(ThemeSource.Repository, false, "Harbour"), "Harbour chosen is not kept, so it would not draw without its source");

            Assert.Null(window.Settings.Look);
            theme.Choose("Passenger");
            Ui.Settle();
            Assert.Equal(passenger, Ui.Pixel(window, bare));
            theme.Choose("Harbour");
            Ui.Settle();
            dialog.Close();
            Ui.Settle();
            Assert.Equal(passenger, Ui.Pixel(window, bare));
            dialog = window.ShowSettings();
            dialog.Change(s => s with { Look = SourcedTheme.Repository("Harbour") });
            dialog.Apply();
            Assert.Equal(SourcedTheme.Repository("Harbour"), window.Settings.Look);
            dialog.SchemePicker.Box.IsDropDownOpen = true;
            Ui.WaitFor(() => dialog.SchemePicker.Listed.Take(2).SequenceEqual(["Same as theme", $"{ThemeSource.FormerScheme} ({ThemeSource.RepositoryName})"]),
                () => $"the repository's code colour scheme listed after the theme's own, where it lists {string.Join(", ", dialog.SchemePicker.Listed)}");
            dialog.SchemePicker.Box.IsDropDownOpen = false;
            dialog.Close();
        }
        finally
        {
            // The theme in force is the process's; the next test starts on Passenger's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A point of a stripe where its own background alone is drawn: the middle of the first five points in a row
    /// down its middle where a hit lands on the stripe itself, so no button, icon or rule on it is read for the frame.</summary>
    private static Point Bare(MainWindow window, Border stripe)
    {
        const int Clear = 5;
        int run = 0;
        for (double y = 0; y < stripe.Bounds.Height; y++)
        {
            run = ReferenceEquals(window.InputHitTest(stripe.TranslatePoint(new Point(stripe.Bounds.Width / 2, y), window)!.Value), stripe) ? run + 1 : 0;
            if (run == Clear)
            {
                return stripe.TranslatePoint(new Point(stripe.Bounds.Width / 2, y - (Clear / 2)), window)!.Value;
            }
        }
        throw new InvalidOperationException($"No {Clear} points down the stripe's middle are clear of what stands on it, so the frame's colour has nowhere to be read");
    }

    /// <summary>Another Passenger's change to settings.json that the watcher never heard is followed all the same: read again
    /// as the watcher says it overflowed, and, for a change dropped without a word, as the window comes in front. The
    /// watcher stops hearing while the file changes, so each change is lost as a dropped event loses it.</summary>
    [AvaloniaFact]
    public void A_change_to_settings_json_the_watcher_lost_is_followed_as_it_overflows_and_as_the_window_comes_in_front()
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
    [AvaloniaFact]
    public void The_windows_own_change_to_settings_json_is_not_read_back_as_the_window_comes_in_front()
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
    [AvaloniaFact]
    public void A_change_to_settings_json_kept_as_a_link_is_followed_each_time_the_window_comes_in_front()
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

    /// <summary>A row another setting leaves no effect greys, saying why, as that setting goes off and comes back as it goes
    /// on again: GDScript's Line length with its wrapping, XAML's Right margin with both its wraps.</summary>
    [AvaloniaFact]
    public void A_line_length_greys_while_its_pages_wrapping_is_off_on_both_code_style_pages()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        dialog.Page = SettingsDialog.GDScriptCodeStylePage;
        Ui.Settle();
        Assert.Null(dialog.WhyGreyed("Line length (columns)"));
        Ui.Click(Editor<CheckBox>(dialog, "Wrap long lines inside brackets"));
        Assert.Equal("Wrap long lines inside brackets is off, so no line is wrapped", dialog.WhyGreyed("Line length (columns)"));
        Assert.False(Editor<NumericUpDown>(dialog, "Line length (columns)").IsEnabled, "Line length stayed editable with wrapping off");
        Ui.Click(Editor<CheckBox>(dialog, "Wrap long lines inside brackets"));
        Assert.Null(dialog.WhyGreyed("Line length (columns)"));

        dialog.Page = SettingsDialog.XamlCodeStylePage;
        Ui.Settle();
        Ui.Click(Editor<CheckBox>(dialog, "Wrap tags and processing instructions"));
        Assert.Null(dialog.WhyGreyed("Right margin (columns)"));
        Ui.Click(Editor<CheckBox>(dialog, "Wrap before and after elements"));
        Assert.NotNull(dialog.WhyGreyed("Right margin (columns)"));
        Assert.False(Editor<NumericUpDown>(dialog, "Right margin (columns)").IsEnabled, "Right margin stayed editable with both wraps off");
        dialog.Close();
        window.Close();
    }

    /// <summary>At its smallest size, Settings' every page keeps what it shows within its width, so each row's label,
    /// editor and note is read whole and reached by scrolling down alone: the rows stand their labels above their
    /// editors, which narrow to the page, and the list pages their lists above what they show. At its own size the
    /// labels stand beside the editors as ever.</summary>
    [AvaloniaFact]
    public void Every_page_keeps_its_rows_within_its_width_at_the_dialogs_smallest_size()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            Ui.Settle();
            OptionRow theme = dialog.GetVisualDescendants().OfType<OptionRow>().First(r => r.Label.Text == "Theme");
            Assert.False(theme.IsStacked, "At Settings' own size, Appearance's Theme label stands above its editor rather than beside it");
            dialog.Width = dialog.MinWidth;
            dialog.Height = dialog.MinHeight;
            Ui.Settle();
            Assert.True(theme.IsStacked, $"At Settings' smallest size ({dialog.Bounds.Size}), Appearance's Theme label stands beside its editor, which has no room there");
            List<string> cut = [];
            foreach (string page in dialog.Pages.ToList())
            {
                dialog.Page = page;
                Ui.Settle();
                ScrollViewer scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is StackPanel && s.IsEffectivelyVisible);
                Control column = (Control)scroll.GetVisualParent()!;
                foreach (Visual part in column.GetVisualChildren().Where(v => v != scroll).Append((Visual)scroll.Content!))
                {
                    Cut(part, column, column.Bounds.Width, page, cut);
                }
            }
            Assert.True(cut.Count == 0, $"At Settings' smallest size ({dialog.Bounds.Size}), these run past the page's right edge, cut off "
                + $"and reached by no scrolling:\n{string.Join("\n", cut.Distinct().Take(40))}");
        }
        finally
        {
            dialog.Close();
            window.Close();
        }

        // What is drawn of a part and inside it, but for what a control clips to itself (a list's rows, a box's text).
        static void Cut(Visual part, Visual column, double width, string page, List<string> cut)
        {
            if (!part.IsEffectivelyVisible || part.Bounds.Width <= 0)
            {
                return;
            }
            Rect at = new Rect(part.Bounds.Size).TransformToAABB(part.TransformToVisual(column)!.Value);
            if (at.Right > width + 0.5 || at.X < -0.5)
            {
                string words = part.GetSelfAndVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text))?.Text ?? string.Empty;
                cut.Add($"{page}: a {part.GetType().Name} '{words[..Math.Min(words.Length, 40)]}' from {at.X:0} to {at.Right:0} of {width:0}");
                return;
            }
            if (part.ClipToBounds)
            {
                return;
            }
            foreach (Visual inside in part.GetVisualChildren())
            {
                Cut(inside, column, width, page, cut);
            }
        }
    }

    /// <summary>At its smallest size, and at its own, no row of any page runs under the page's scroll bar, which shows over
    /// the page's right: every label, editor and note keeps to the left of where the bar stands, so a note's last words are
    /// never drawn under its thumb.</summary>
    [AvaloniaFact]
    public void No_row_of_any_page_runs_under_its_scroll_bar_at_the_dialogs_smallest_size_or_its_own()
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

    /// <summary>At Settings' own size, between, and at its smallest, every row's editor that takes a line of text or
    /// lists several (a path, a glob, the theme sources) and every note under an editor reach one right edge: the widest
    /// editor's where the page has room, the page's where it has not, so none stands narrower than its neighbours nor
    /// wider than a narrow page; and every number box stands at a number's width.</summary>
    [AvaloniaFact]
    public void Every_rows_text_box_list_and_note_reach_one_edge_and_narrow_with_the_page()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        try
        {
            List<string> off = [];
            int fields = 0;
            int notes = 0;
            foreach ((double width, double height) in new[] { (dialog.Width, dialog.Height), (800.0, 600.0), (dialog.MinWidth, dialog.MinHeight) })
            {
                dialog.Width = width;
                dialog.Height = height;
                Ui.Settle();
                foreach (string page in dialog.Pages.ToList())
                {
                    dialog.Page = page;
                    Ui.Settle();
                    foreach (OptionRow row in dialog.GetVisualDescendants().OfType<OptionRow>().Where(r => r.IsEffectivelyVisible))
                    {
                        // Where the row's editors start, and the edge none of them passes.
                        double left = row.IsStacked ? 0 : OptionRow.LabelWidth;
                        double edge = Math.Min(row.Bounds.Width, left + OptionRow.EditorWidth);
                        string where = $"{width:0} wide, {page}, {row.Label.Text}";
                        // The theme explorer lays itself under the theme picker's row, as wide as that row.
                        if (row.Editor is not ThemeExplorer && row.Editor.GetSelfAndLogicalDescendants().Any(c => c is TextBox or ListBox))
                        {
                            fields++;
                            if (Math.Abs(row.Editor.Bounds.Right - edge) > 0.5)
                            {
                                off.Add($"{where}: its editor ends at {row.Editor.Bounds.Right:0}, where the editors' edge is {edge:0}");
                            }
                        }
                        foreach (NumericUpDown number in row.Editor.GetSelfAndLogicalDescendants().OfType<NumericUpDown>())
                        {
                            if (Math.Abs(number.Bounds.Width - Math.Min(OptionRow.NumberWidth, row.Bounds.Width)) > 0.5)
                            {
                                off.Add($"{where}: its number box is {number.Bounds.Width:0} wide, where a number's is {OptionRow.NumberWidth:0}");
                            }
                        }
                        if (row.Note is { IsEffectivelyVisible: true } note)
                        {
                            notes++;
                            // The width the note's lines wrap at: what the row gave it, held by any most of its own.
                            double wraps = Math.Min(LayoutInformation.GetPreviousMeasureConstraint(note)!.Value.Width, note.MaxWidth);
                            if (Math.Abs(note.Bounds.X + wraps - edge) > 0.5)
                            {
                                off.Add($"{where}: its note wraps at {note.Bounds.X + wraps:0}, where the editors' edge is {edge:0}");
                            }
                        }
                    }
                }
            }
            Assert.True(fields >= 10 && notes >= 50, $"Only {fields} rows of a text box or list and {notes} notes were found: the search no longer reads them");
            Assert.True(off.Count == 0, $"These rows' editors and notes stand at widths of their own rather than reaching the edge every editor reaches, "
                + $"so the page's right edges are ragged where there is room and may run past a narrow page:\n{string.Join("\n", off.Take(60))}");
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

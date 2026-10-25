using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Settings: a search narrows the pages to those with an option whose words match and shows only those options;
/// a change is in force the moment it is applied, in every editor (one a split makes later too), and a window that keeps
/// its settings opens on them again.</summary>
public sealed class SettingsTests : IDisposable
{
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
        Editor<TextBox>(dialog, "Font family").Text = font;
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
        Assert.True(Editor<CheckBox>(kept, "Copy on selection").IsChecked);
        Assert.True(Editor<CheckBox>(kept, "Cursor blinking").IsChecked);
        Assert.Equal("Line", Editor<ComboBox>(kept, "Cursor style").SelectedItem);
        kept.Close();
        again.Close();
    }

    [AvaloniaFact]
    public void Terminal_font_changes_wait_for_apply_and_a_blank_name_restores_the_bundled_font()
    {
        string font = OtherTerminalFont();
        MainWindow window = Open();
        string bundled = window.TerminalPanel.View.FontFamily.Name;
        SettingsDialog dialog = TerminalDialog(window);
        Assert.Contains("Font family", dialog.Shown);
        Editor<TextBox>(dialog, "Font family").Text = font;
        dialog.Close();
        Assert.Equal(bundled, window.TerminalPanel.View.FontFamily.Name);

        dialog = TerminalDialog(window);
        Editor<TextBox>(dialog, "Font family").Text = "  " + font + "  ";
        Ui.Settle();
        dialog.Apply();
        Ui.Settle();
        Assert.Equal(font, window.TerminalPanel.View.FontFamily.Name);
        Editor<TextBox>(dialog, "Font family").Text = "   ";
        Ui.Settle();
        dialog.Apply();
        Ui.Settle();
        Assert.Equal(bundled, window.TerminalPanel.View.FontFamily.Name);
        dialog.Close();
        window.Close();
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
        Assert.Equal(Passenger.Terminal.TerminalView.DefaultFontFamily, window.TerminalPanel.View.FontFamily);
        Assert.Equal("Passenger Missing Font", window.Settings.TerminalFontFamily);
        SettingsDialog dialog = TerminalDialog(window);
        Assert.Contains("Cursor style", dialog.Shown);
        Assert.Equal("Passenger Missing Font", Editor<TextBox>(dialog, "Font family").Text);
        Assert.Equal("Block", Editor<ComboBox>(dialog, "Cursor style").SelectedItem);
        Assert.False(Editor<CheckBox>(dialog, "Copy on selection").IsChecked);
        Assert.False(Editor<CheckBox>(dialog, "Cursor blinking").IsChecked);
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void A_proportional_terminal_font_keeps_the_cell_grid_in_the_bundled_font()
    {
        FontFamily? proportional = FontManager.Current.SystemFonts.FirstOrDefault(f => !new Typeface(f).GlyphTypeface.Metrics.IsFixedPitch);
        Assert.SkipWhen(proportional is null, "No proportional system font is installed");
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { TerminalFontFamily = proportional!.Name });
        Ui.Settle();
        Assert.Equal(Passenger.Terminal.TerminalView.DefaultFontFamily, window.TerminalPanel.View.FontFamily);
        Assert.Equal(proportional.Name, window.Settings.TerminalFontFamily);
        window.Close();
    }

    private readonly string folder = TestFolders.Create("folder-");
    private readonly string settings = TestFolders.Create("settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
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
        Assert.Equal([SettingsDialog.EditorPage, SettingsDialog.TerminalPage, SettingsDialog.KeymapPage], dialog.Pages);
        Assert.Equal(SettingsDialog.EditorPage, dialog.Page);
        Assert.Contains("Font size", dialog.Shown);
        Assert.DoesNotContain("Show line numbers", dialog.Shown);

        dialog.Query = "split right";
        Ui.Settle();
        Assert.Equal([SettingsDialog.KeymapPage], dialog.Pages);
        Assert.Equal(["Split Right"], dialog.Shown);
        dialog.Close();
        window.Close();
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

    /// <summary>Appearance's Theme: Passenger's own or another (Harbour here), each in the dark or light the next row says,
    /// the window dressed in the one chosen the moment it is applied; Editor's Colour scheme: the theme's own or Rider's.</summary>
    [AvaloniaFact]
    public void Harbour_chosen_as_the_theme_dresses_the_window_at_once_beside_passengers_own()
    {
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Equal(["Theme", "Dark or light"], dialog.Shown.Take(2));
            ComboBox theme = Editor<ComboBox>(dialog, "Theme");
            Assert.Equal("Passenger", theme.SelectedItem);
            Assert.Equal(["System", "Dark", "Light"], Editor<ComboBox>(dialog, "Dark or light").Items.Cast<string>());
            Border stripe = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("stripeBar") && b.IsEffectivelyVisible);
            Point bare = stripe.TranslatePoint(new Point(stripe.Bounds.Width / 2, stripe.Bounds.Height * 0.45), window)!.Value;
            Color passenger = Ui.Pixel(window, bare);

            theme.SelectedItem = "Harbour";
            dialog.Apply();
            Ui.Settle();
            Color harbour = Ui.Pixel(window, bare);
            Assert.True(window.TryFindResource("Passenger.Frame", window.ActualThemeVariant, out object? frame) && frame is ISolidColorBrush desk && desk.Color == harbour && harbour != passenger,
                $"With Harbour applied the window's frame is {harbour}, where it was {passenger} under Passenger's own: Harbour's desk did not take its place");

            dialog.Page = SettingsDialog.EditorPage;
            Ui.Settle();
            Assert.Equal(["Same as theme", "Rider"], Editor<ComboBox>(dialog, "Colour scheme").Items.Cast<string>());
            dialog.Close();
        }
        finally
        {
            // The theme in force is the process's; the next test starts on Passenger's own.
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>What edits an option, beside its label in the page shown.</summary>
    private static string OtherTerminalFont()
    {
        FontFamily? font = FontManager.Current.SystemFonts.FirstOrDefault(f => f.Name != Passenger.Terminal.TerminalView.DefaultFontFamily.Name
            && new Typeface(f).GlyphTypeface.Metrics.IsFixedPitch);
        Assert.SkipWhen(font is null, "No other monospace system font is installed");
        return font!.Name;
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

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Settings: a search narrows the pages to those with an option whose words match and shows only those options;
/// a change is in force the moment it is applied, in every editor (one a split makes later too), a window that keeps its
/// settings opens on them again, and another Passenger's change to them is followed even where the watcher lost it.</summary>
public sealed class SettingsTests : IDisposable
{
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
            NumericUpDown factor = Editor<NumericUpDown>(dialog, "Display scale");
            Assert.Equal(1m, factor.Value);
            factor.Value = 1.5m;
            Assert.Equal(1, window.Settings.DisplayScale);
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
            factor = Editor<NumericUpDown>(dialog, "Display scale");
            Assert.Equal(1.5m, factor.Value);
            Assert.Equal(1.5, Scale(factor));
            factor.Value = 1.2m;
            dialog.Apply();
            Ui.Settle();
            Assert.Equal(1.2, Scale(factor));
            Assert.Equal(1.2, Scale(window.EditorArea.CodeEditor));
            factor.Value = 1m;
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
            Assert.Equal((decimal)expected, Editor<NumericUpDown>(dialog, "Display scale").Value);
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

    /// <summary>The terminal's font chosen in Settings › Terminal is put in force only by Apply or OK; JetBrains Mono chosen
    /// there stays the terminal's while the editor's font changes, and Same as the editor has it follow the editor's
    /// again.</summary>
    [AvaloniaFact]
    public void A_terminal_font_waits_for_apply_and_same_as_the_editor_follows_the_editors_font()
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
            Border stripe = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.StripeBar) && b.IsEffectivelyVisible);
            Point bare = Bare(window, stripe);
            Color passenger = Ui.Pixel(window, bare);

            theme.SelectedItem = "Harbour";
            dialog.Apply();
            Ui.Settle();
            Color harbour = Ui.Pixel(window, bare);
            Assert.True(window.TryFindResource(TokenKeys.Frame, window.ActualThemeVariant, out object? frame) && frame is ISolidColorBrush desk && desk.Color == harbour && harbour != passenger,
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
        FileSystemWatcher watcher = store.Settings.Watcher ?? throw new InvalidOperationException($"{file} is not watched");
        try
        {
            watcher.EnableRaisingEvents = false;
            window.ApplySettings(window.Settings with { EditorFontSize = 15 });
            store.Settings.Flush();

            WrittenElsewhere(file, 21);
            FolderWatchTests.RaiseError(watcher, new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
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
            watcher.EnableRaisingEvents = true;
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
        FileSystemWatcher watcher = store.Settings.Watcher ?? throw new InvalidOperationException($"{file} is not watched");
        try
        {
            // Deaf, so its word of the window's own write, which comes when the system sends it, is no part of the test.
            watcher.EnableRaisingEvents = false;
            window.ApplySettings(window.Settings with { EditorFontSize = 15 });
            store.Settings.Flush();
            ChangedUnstamped(file, 16);

            window.Activate();
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.FontSize == 15, "settings.json was read again as the window came in front, though only the window's own change had moved it");
            FolderWatchTests.RaiseError(watcher, new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
            Ui.Settle();
            Assert.True(window.EditorArea.CodeEditor.FontSize == 15, "settings.json was read again as its watcher overflowed, though only the window's own change had moved it");

            window.ApplySettings(window.Settings with { ShowLineNumbers = false });
            Assert.True(window.EditorArea.CodeEditor.FontSize == 16,
                $"The window's next change did not find the font settings.json was given under its stamp (its font is {window.EditorArea.CodeEditor.FontSize}), so the test showed nothing of what is read");
        }
        finally
        {
            watcher.EnableRaisingEvents = true;
            store.Release();
            window.Close();
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

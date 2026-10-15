using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Settings as Rider's: a search narrows the pages to those with an option whose words match and shows only
/// those options; a change is in force the moment it is applied, in every editor (one a split makes later too), and a
/// window that keeps its settings opens on them again.</summary>
public sealed class SettingsTests : IDisposable
{
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

        // An action's name finds its shortcut on the Keymap page.
        dialog.Query = "split right";
        Ui.Settle();
        Assert.Equal([SettingsDialog.KeymapPage], dialog.Pages);
        Assert.Equal(["Split Right"], dialog.Shown);
        dialog.Close();
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_settings_page_keeps_its_controls_inside_the_frame_at_normal_and_minimum_widths(bool light)
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        dialog.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        dialog.SetRenderScaling(2);
        Ui.Settle();
        string[] pages = [.. dialog.Pages];
        List<string> overflow = [];
        ScrollViewer viewport = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "SettingsPageScroll");
        foreach (double width in new double[] { 980, 640, 683, 980 })
        {
            dialog.Width = width;
            foreach (string page in pages)
            {
                dialog.Page = page;
                viewport.Offset = default;
                Ui.Settle();
                string surface = $"settings-{string.Concat(page.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'))}-{width:0}-{(light ? "light" : "dark")}";
                overflow.AddRange(UiAudit.HorizontalOverflow(dialog, surface, viewport));
                if (width != 683)
                {
                    UiAudit.Capture(dialog, surface);
                    if (viewport.Extent.Height > viewport.Viewport.Height + 1)
                    {
                        viewport.Offset = new Vector(0, viewport.Extent.Height - viewport.Viewport.Height);
                        Ui.Settle();
                        UiAudit.Capture(dialog, surface + "-end");
                    }
                }
            }
        }
        dialog.Close();
        window.Close();
        Assert.True(overflow.Count == 0, string.Join(Environment.NewLine, overflow.Distinct()));
    }

    [AvaloniaFact]
    public void Resizing_settings_keeps_typed_values_the_chosen_template_and_the_field_with_the_keys()
    {
        MainWindow window = Open();
        SettingsDialog dialog = window.ShowSettings();
        dialog.Page = SettingsDialog.NodePage;
        Ui.Settle();
        TextBox node = Editor<TextBox>(dialog, "Node interpreter");
        node.Text = "C:/my tools/node.exe";
        node.Focus();
        node.SelectionStart = 3;
        node.SelectionEnd = 11;
        foreach (double width in new double[] { 640, 683, 980 })
        {
            dialog.Width = width;
            Ui.Settle();
            Assert.Equal(SettingsDialog.NodePage, dialog.Page);
            Assert.Equal("C:/my tools/node.exe", dialog.Working.NodeInterpreter);
            Assert.True(node.IsFocused, "The responsive layout stole focus from the edited field");
            Assert.Equal((3, 11), (node.SelectionStart, node.SelectionEnd));
        }
        dialog.Page = SettingsDialog.LiveTemplatesPage;
        Ui.Settle();
        Assert.True(dialog.Templates.Choose("cw"));
        Ui.Settle();
        LiveTemplate chosen = Assert.IsType<LiveTemplate>(dialog.Templates.Chosen);
        string? text = dialog.Templates.TextBox.Text;
        foreach (double width in new double[] { 640, 683, 980 })
        {
            dialog.Width = width;
            Ui.Settle();
            Assert.Equal(chosen.Key, dialog.Templates.Chosen?.Key);
            Assert.True(chosen.SameAs(dialog.Templates.Chosen!));
            Assert.Equal(text, dialog.Templates.TextBox.Text);
        }
        dialog.Page = SettingsDialog.EditorPage;
        Ui.Settle();
        Control number = Editor<NumericUpDown>(dialog, "Font size");
        Control check = Editor<CheckBox>(dialog, "Enable ligatures");
        Assert.Equal(number.TranslatePoint(default, dialog)!.Value.X, check.TranslatePoint(default, dialog)!.Value.X);
        dialog.Page = SettingsDialog.NodePage;
        Ui.Settle();
        TextBlock note = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("The Node.js", StringComparison.Ordinal) == true);
        Assert.Equal(node.TranslatePoint(default, dialog)!.Value.X, note.TranslatePoint(default, dialog)!.Value.X);
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
            // Save All's shortcut given to Split Down: Save All has none left.
            string saveAll = Ui.Shortcut(AppActions.SaveAll);
            dialog.Rebind(AppActions.SplitDown, saveAll);
            Assert.Empty(AppActions.Keymap(dialog.Working.Shortcuts).ShortcutsFor(AppActions.SaveAll));
            dialog.Ok();
            Ui.Settle();

            Ui.Press(window, mine);
            Assert.Equal(2, window.EditorArea.Groups.Count);
            // Save All's old keys split down now.
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

    /// <summary>Rider's Toolset and Build, Presentation, by its names and with its defaults: the Build window opens as an
    /// explicit build starts, not an implicit one, and comes forward only for errors; the implicit build's option is for
    /// changing only while the explicit build's is on, as in Rider. What is chosen is kept, by name.</summary>
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

        Click(dialog, explicitly);
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

    /// <summary>What edits an option, beside its label in the page shown.</summary>
    private static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
        ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label).Parent!).GetVisualDescendants().OfType<T>().First();

    private static void Click(Window window, Control control)
    {
        Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }
}

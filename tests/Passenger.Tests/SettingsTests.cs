using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.State;

namespace Passenger.Tests;

/// <summary>Settings: a search narrows the pages to those with an option whose words match and shows only those options;
/// a change is in force the moment it is applied, in every editor (one a split makes later too), a window that keeps its
/// settings opens on them again, and another Passenger's change to them is followed even where the watcher lost it.</summary>
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
            Border stripe = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("stripeBar") && b.IsEffectivelyVisible);
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
    private static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
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

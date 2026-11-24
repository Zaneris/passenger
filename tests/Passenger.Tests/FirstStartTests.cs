using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.App.Welcome;

namespace Passenger.Tests;

/// <summary>The first start's question as a person meets it: in their window over a new state, before anything else, the
/// theme from large pictures of Passenger's own and Cubicle, then dark or light from pictures of the theme just chosen, or
/// the system's, then the keymap from each Settings offers, Vim mode over it; by keys and by clicks, each choice in force at
/// once and kept as Settings keeps it, which Settings shows afterwards; Escape keeping the defaults; and nothing asked at a
/// later start, after an update, or in a window nobody sees or that keeps no settings. Each window keeps its state in the
/// test's own folder.</summary>
public sealed class FirstStartTests : IDisposable
{
    private static readonly SourcedTheme Cubicle = SourcedTheme.BuiltIn("Cubicle");

    private readonly string state = TestFolders.Create("first-start-state-");

    public void Dispose() => TestFolders.Delete(state);

    /// <summary>A new state's first start asks the theme, dark or light and the keymap by keys or clicks, Escape keeping the
    /// defaults, and never asks again, after an update, in a window nobody sees, and fits the smallest window, as one
    /// flow, each step the check one test made in an instance of its own.</summary>
    [AvaloniaFact]
    public void A_new_states_first_start_asks_the_theme_and_its_look_once()
    {
        Flow.StepApart<FirstStartTests>("a new states first start asks the theme dark or light and the keymap by keys and Settings shows what was chosen", t => t.A_new_states_first_start_asks_the_theme_dark_or_light_and_the_keymap_by_keys_and_Settings_shows_what_was_chosen());
        Flow.StepApart<FirstStartTests>("clicks choose a theme go back choose another follow the system and choose a keymap", t => t.Clicks_choose_a_theme_go_back_choose_another_follow_the_system_and_choose_a_keymap());
        Flow.StepApart<FirstStartTests>("Escape keeps the defaults and a later start asks nothing", t => t.Escape_keeps_the_defaults_and_a_later_start_asks_nothing());
        Flow.StepApart<FirstStartTests>("an update from a version whose state is there asks nothing (StateStore.SettingsFile)", t => t.An_update_from_a_version_whose_state_is_there_asks_nothing(StateStore.SettingsFile));
        Flow.StepApart<FirstStartTests>("an update from a version whose state is there asks nothing (\"recentSolutions.json\")", t => t.An_update_from_a_version_whose_state_is_there_asks_nothing("recentSolutions.json"));
        Flow.StepApart<FirstStartTests>("a window nobody sees or that keeps no settings never asks", t => t.A_window_nobody_sees_or_that_keeps_no_settings_never_asks());
        Flow.StepApart<FirstStartTests>("at the windows smallest size every question fits its width and scrolls to its end (3.0)", t => t.At_the_windows_smallest_size_every_question_fits_its_width_and_scrolls_to_its_end(3.0));
    }

    private void A_new_states_first_start_asks_the_theme_dark_or_light_and_the_keymap_by_keys_and_Settings_shows_what_was_chosen()
    {
        // As the person's window starts: asked before it shows, then started, which gives the Welcome screen the keys.
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false, Unseen = false };
        window.KeepLayout();
        window.KeepSettings();
        Assert.True(window.ShowFirstStartOnce(), "A new state's first start asked nothing");
        window.Show();
        window.Start(LaunchOptions.Empty);
        Ui.Settle();
        try
        {
            FirstStartView page = Shown(window);
            Assert.Equal(FirstStartView.Screen.Theme, page.Shown);
            Assert.Equal(["Passenger", "Cubicle", ThemeSource.HighContrast], Names(page));
            Assert.True(Chosen(page) == "Passenger", $"The theme in force, Passenger's own, is not the one marked, which is {Chosen(page)}");
            Assert.True(page.IsKeyboardFocusWithin, "The question does not have the keys, so the person's arrows and Enter go elsewhere");
            Color passengers = Ui.Pixel(window, Bare);

            Ui.Press(window, "RIGHT");
            Assert.True(page.Tiles[1].IsKeyboardFocusWithin, "Right did not move the keys from Passenger's tile to Cubicle's");
            Ui.Press(window, "ENTER");
            Assert.Equal(Cubicle, window.Settings.Look);
            Assert.Equal(Cubicle, Themes.Look);
            Assert.Equal(FirstStartView.Screen.DarkOrLight, page.Shown);
            Assert.Equal(["Dark", "Light"], Names(page));
            Color cubicles = Ui.Pixel(window, Bare);
            Assert.True(cubicles != passengers && cubicles == Token(window, TokenKeys.Editor_Background),
                $"Dark or light is not asked in Cubicle, just chosen: its ground is {cubicles}, where Passenger's was {passengers}");
            Assert.True(page.SystemChoice.IsKeyboardFocusWithin && page.SystemChoice.Classes.Contains(StyleClasses.Chosen),
                "Following the system, in force, is not the choice marked and with the keys");

            Ui.Press(window, "UP");
            Assert.True(page.Tiles[0].IsKeyboardFocusWithin, "Up from the system's choice did not reach the Dark tile");
            Ui.Press(window, "RIGHT");
            Ui.Press(window, "ENTER");
            Assert.True(window.Settings is { Theme: ThemeChoice.Light } && Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                $"Light chosen left the settings at {window.Settings.Theme} and the window {Application.Current!.RequestedThemeVariant}");

            Assert.Equal(FirstStartView.Screen.Keymap, page.Shown);
            Assert.Equal(["Visual Studio", "VS Code", "IntelliJ IDEA", "Emacs"], Names(page));
            Assert.True(Chosen(page) == "Visual Studio" && page.Tiles[0].IsKeyboardFocusWithin,
                $"The keymap in force, Visual Studio's, is not the one marked and with the keys: {Chosen(page)} is");
            Assert.True(page.Tiles[3].GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Passenger.Editor.Shortcut.Parse("control X , control F").Display()),
                "Emacs's tile does not show its own key for Go to File");
            Ui.Press(window, "DOWN");
            Assert.True(page.VimChoice.IsKeyboardFocusWithin, "Down from the keymaps did not reach Vim mode's choice");
            Ui.Press(window, "ENTER");
            Assert.True(window.Settings.VimMode && page.VimChoice.Classes.Contains(StyleClasses.Chosen), "Vim mode's choice turned no Vim mode on");
            Ui.Press(window, "UP");
            Ui.Press(window, "RIGHT");
            Ui.Press(window, "RIGHT");
            Ui.Press(window, "RIGHT");
            Ui.Press(window, "ENTER");
            Assert.Null(window.FirstStart);
            Assert.True(window.Settings is { Keymap: Passenger.Editor.KeymapPreset.Emacs, VimMode: true } && AppActions.Preset == Passenger.Editor.KeymapPreset.Emacs,
                $"Emacs chosen left the keymap at {window.Settings.Keymap}, {AppActions.Preset} in force, Vim mode {window.Settings.VimMode}");
            Assert.True(window.Welcome.IsKeyboardFocusWithin, "The Welcome screen did not take the keys once the question ended");
            Ui.Press(window, "alt X");
            Assert.True(window.Popup is Passenger.App.Navigation.ActionPopup, "Emacs's Alt+X, pressed as the window opens on it, opened no Find Action");
            Ui.Press(window, "ESCAPE");

            JsonObject kept = Kept();
            Assert.True((string?)kept["theme"] == "Light" && (string?)kept["look"]?["name"] == "Cubicle" && (string?)kept["keymap"] == "Emacs" && (bool?)kept["vimMode"] == true,
                $"settings.json keeps {kept.ToJsonString()}, not Cubicle in Light with Emacs's keys and Vim mode");
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Equal("Cubicle", dialog.ThemePicker.Box.SelectedItem?.ToString());
            Assert.Equal("Light", SettingsTests.Editor<ComboBox>(dialog, "Dark or light").SelectedItem);
            Assert.Equal("Emacs", dialog.KeymapChoice.SelectedItem);
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void Clicks_choose_a_theme_go_back_choose_another_follow_the_system_and_choose_a_keymap()
    {
        MainWindow window = Started();
        try
        {
            window.ShowFirstStartOnce();
            Ui.Settle();
            FirstStartView page = Shown(window);
            Ui.Click(page.Tiles[1]);
            Assert.Equal(Cubicle, window.Settings.Look);
            Assert.Equal(FirstStartView.Screen.DarkOrLight, page.Shown);

            Ui.Click(page.Back);
            Assert.Equal(FirstStartView.Screen.Theme, page.Shown);
            Assert.True(Chosen(page) == "Cubicle", $"Back to the themes did not mark Cubicle, chosen, but {Chosen(page)}");
            Ui.Click(page.Tiles[0]);
            Assert.Null(window.Settings.Look);
            Assert.Null(Themes.Look);

            window.ApplySettings(window.Settings with { Theme = ThemeChoice.Dark });
            Ui.Settle();
            Ui.Click(page.SystemChoice);
            Assert.True(window.Settings is { Theme: ThemeChoice.System, Look: null } && Application.Current!.RequestedThemeVariant == ThemeVariant.Default,
                $"Following the system left the settings at {window.Settings.Look?.Name ?? "Passenger"} in {window.Settings.Theme}");
            Assert.Equal(FirstStartView.Screen.Keymap, page.Shown);
            Ui.Click(page.Back);
            Assert.True(page.Shown == FirstStartView.Screen.DarkOrLight && page.SystemChoice.Classes.Contains(StyleClasses.Chosen),
                "Back from the keymaps did not return to dark or light with the system's marked");
            Ui.Click(page.SystemChoice);
            Ui.Click(page.Tiles[2]);
            Assert.Null(window.FirstStart);
            Assert.True(window.Settings is { Keymap: Passenger.Editor.KeymapPreset.IntelliJ, VimMode: false } && AppActions.Preset == Passenger.Editor.KeymapPreset.IntelliJ,
                $"IntelliJ IDEA clicked left the keymap at {window.Settings.Keymap}, {AppActions.Preset} in force");
            JsonObject kept = Kept();
            Assert.True((string?)kept["theme"] is null or "System" && (string?)kept["keymap"] == "IntelliJ",
                $"settings.json keeps {kept.ToJsonString()}, not the system's dark or light with IntelliJ IDEA's keys");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private void Escape_keeps_the_defaults_and_a_later_start_asks_nothing()
    {
        MainWindow window = Started();
        Assert.True(window.ShowFirstStartOnce(), "A new state's first start asked nothing");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Assert.Null(window.FirstStart);
        Assert.True(window.Settings is { Look: null, Theme: ThemeChoice.System, Keymap: Passenger.Editor.KeymapPreset.VisualStudio } && Themes.Look is null,
            $"Escape changed the settings to {window.Settings.Look?.Name ?? "Passenger"} in {window.Settings.Theme}, {window.Settings.Keymap}'s keys");
        window.Close();
        Ui.Settle();

        window = Started();
        Assert.False(window.ShowFirstStartOnce(), "A second start asked the theme again");
        Assert.Null(window.FirstStart);
        window.Close();
    }

    /// <summary>What an earlier Passenger left behind, any one of its files: its settings, layout, recent solutions or
    /// record of updates.</summary>
    private void An_update_from_a_version_whose_state_is_there_asks_nothing(string file)
    {
        File.WriteAllText(Path.Combine(state, file), "{}\n");
        MainWindow window = Started();
        Assert.False(window.ShowFirstStartOnce(), $"An update over an earlier Passenger's {file} asked the theme as a first start would");
        Assert.Null(window.FirstStart);
        window.Close();
    }

    private void A_window_nobody_sees_or_that_keeps_no_settings_never_asks()
    {
        MainWindow unseen = Started(unseen: true);
        Assert.False(unseen.ShowFirstStartOnce(), "A window nobody sees asked the theme over a new state");
        unseen.Close();
        Ui.Settle();

        MainWindow own = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false, Unseen = false };
        own.Show();
        Ui.Settle();
        Assert.False(own.ShowFirstStartOnce(), "A window keeping no settings (an agent's, a screenshot's) asked the theme");
        Assert.Null(own.FirstStart);
        own.Close();
    }

    /// <summary>At the window's smallest size, 640 by 500 at a display scale of 100% and that times the largest scale
    /// Settings offers, every question is laid out at the smallest width with nothing past it: every tile, the small choice,
    /// Back and Keep the Defaults whole across it, the tiles' words untrimmed, and the page scrolls down to its end.</summary>
    private void At_the_windows_smallest_size_every_question_fits_its_width_and_scrolls_to_its_end(double scale)
    {
        MainWindow window = new()
        {
            Width = MainWindow.SmallestWidth * scale, Height = MainWindow.SmallestHeight * scale, StateFolder = state, StartsLanguageServer = false, Unseen = false,
        };
        window.Show();
        window.ApplySettings(window.Settings with { DisplayScale = scale });
        Ui.Settle();
        try
        {
            FirstStartView page = window.ShowFirstStart();
            Ui.Settle();
            Assert.True(Math.Abs(page.Bounds.Width - MainWindow.SmallestWidth) < 1,
                $"At a display scale of {scale:P0} the question is laid out {page.Bounds.Width} wide, not at the window's smallest width, {MainWindow.SmallestWidth}");
            Fits(page, $"The theme's question at {scale:P0}");
            Ui.Click(page.Tiles[0]);
            Assert.Equal(FirstStartView.Screen.DarkOrLight, page.Shown);
            Ui.Settle();
            Fits(page, $"The question of dark or light at {scale:P0}");
            Ui.Click(page.Tiles[0]);
            Assert.Equal(FirstStartView.Screen.Keymap, page.Shown);
            Ui.Settle();
            Fits(page, $"The question of the keymap at {scale:P0}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>Holds that a question shows nothing past its width (every tile, the small choice, Back and Keep the Defaults
    /// whole across it, no words of a tile trimmed, nothing in a tile past its edge) and that scrolled to its end, Keep the
    /// Defaults shows whole.</summary>
    private static void Fits(FirstStartView page, string question)
    {
        ScrollViewer scroller = page.GetVisualDescendants().OfType<ScrollViewer>().First();
        Button keep = page.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Keep the Defaults");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, $"{question} is {scroller.Extent.Width} wide, past the {scroller.Viewport.Width} it is given");
        foreach (Control shown in ((Control[])[.. page.Tiles, page.SystemChoice, page.VimChoice, page.Back, keep]).Where(c => c.IsEffectivelyVisible))
        {
            Rect place = Place(shown, scroller);
            Assert.True(place.X >= -0.5 && place.Right <= scroller.Viewport.Width + 0.5,
                $"{question} cuts {Avalonia.Automation.AutomationProperties.GetName(shown) ?? Ui.Named(shown)}, from {place.X} to {place.Right} across the {scroller.Viewport.Width} it shows");
        }
        foreach (TextBlock words in page.Tiles.Append(page.SystemChoice).Append(page.VimChoice).SelectMany(t => t.GetVisualDescendants().OfType<TextBlock>()).Where(t => t.IsEffectivelyVisible))
        {
            Assert.True(words.TextLayout.TextLines.All(l => !l.HasCollapsed), $"{question} trims \"{words.Text}\" to fit");
        }
        foreach (Button tile in page.Tiles)
        {
            foreach (TextBlock words in tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
            {
                Rect inTile = Place(words, tile);
                double right = inTile.X + Math.Max(words.TextLayout.Width, words.Bounds.Width);
                Assert.True(inTile.X >= -0.5 && right <= tile.Bounds.Width + 0.5,
                    $"{question} draws \"{words.Text}\" from {inTile.X} to {right} across a tile {tile.Bounds.Width} wide");
            }
        }
        scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
        Ui.Settle();
        Rect last = Place(keep, scroller);
        Assert.True(last.Y >= -0.5 && last.Bottom <= scroller.Viewport.Height + 0.5,
            $"{question} scrolled to its end shows Keep the Defaults from {last.Y} to {last.Bottom} of the {scroller.Viewport.Height} it shows");
        scroller.Offset = default;
        Ui.Settle();

        static Rect Place(Control control, Visual within) =>
            new(control.TranslatePoint(default, within) ?? new Point(double.NaN, double.NaN), control.Bounds.Size);
    }

    /// <summary>A bare point of the page, under the title bar and left of everything on it.</summary>
    private static readonly Point Bare = new(12, 60);

    /// <summary>A person's window over the test's state, keeping its layout and settings there; one nobody sees where asked.</summary>
    private MainWindow Started(bool unseen = false)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false, Unseen = unseen };
        window.KeepLayout();
        window.KeepSettings();
        window.Show();
        Ui.Settle();
        return window;
    }

    private static FirstStartView Shown(MainWindow window)
    {
        FirstStartView? page = window.FirstStart;
        Assert.True(page is { IsEffectivelyVisible: true }, "The window shows no first start's question");
        return page!;
    }

    private static string[] Names(FirstStartView page) => [.. page.Tiles.Select(t => Avalonia.Automation.AutomationProperties.GetName(t) ?? string.Empty)];

    private static string? Chosen(FirstStartView page) =>
        page.Tiles.Where(t => t.Classes.Contains(StyleClasses.Chosen)).Select(t => Avalonia.Automation.AutomationProperties.GetName(t)).SingleOrDefault();

    private static Color Token(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;

    /// <summary>settings.json as written, once what waits is flushed.</summary>
    private JsonObject Kept()
    {
        StateStore store = StateStore.Open(state);
        store.Flush();
        store.Release();
        return JsonNode.Parse(File.ReadAllText(Path.Combine(state, StateStore.SettingsFile)))!.AsObject();
    }
}

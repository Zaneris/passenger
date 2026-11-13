using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.App.Welcome;

namespace Passenger.Tests;

/// <summary>The first start's question as a person meets it: in their window over a new state, before anything else, the
/// theme from large pictures of Passenger's own and Cubicle, then dark or light from pictures of the theme just chosen, or
/// the system's; by keys and by clicks, each choice in force at once and kept as Settings keeps it, which Settings shows
/// afterwards; Escape keeping the defaults; and nothing asked at a later start, after an update from a version whose state
/// is there, or in a window nobody sees or that keeps no settings. Each window keeps its state in the test's own folder.</summary>
public sealed class FirstStartTests : IDisposable
{
    private static readonly SourcedTheme Cubicle = SourcedTheme.BuiltIn("Cubicle");

    private readonly string state = TestFolders.Create("first-start-state-");

    public void Dispose() => TestFolders.Delete(state);

    [AvaloniaFact]
    public void A_new_states_first_start_asks_the_theme_then_dark_or_light_by_keys_and_Settings_shows_what_was_chosen()
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
            Assert.Equal(["Passenger", "Cubicle"], Names(page));
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
            Assert.Null(window.FirstStart);
            Assert.True(window.Settings is { Theme: ThemeChoice.Light } && Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                $"Light chosen left the settings at {window.Settings.Theme} and the window {Application.Current!.RequestedThemeVariant}");
            Assert.True(window.Welcome.IsKeyboardFocusWithin, "The Welcome screen did not take the keys once the question ended");

            JsonObject kept = Kept();
            Assert.True((string?)kept["theme"] == "Light" && (string?)kept["look"]?["name"] == "Cubicle",
                $"settings.json keeps {kept.ToJsonString()}, not Cubicle in Light");
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Equal("Cubicle", dialog.ThemePicker.Box.SelectedItem?.ToString());
            Assert.Equal("Light", SettingsTests.Editor<ComboBox>(dialog, "Dark or light").SelectedItem);
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Clicks_choose_a_theme_go_back_choose_another_and_follow_the_system()
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
            Assert.Null(window.FirstStart);
            Assert.True(window.Settings is { Theme: ThemeChoice.System, Look: null } && Application.Current!.RequestedThemeVariant == ThemeVariant.Default,
                $"Following the system left the settings at {window.Settings.Look?.Name ?? "Passenger"} in {window.Settings.Theme}");
            Assert.True((string?)Kept()["theme"] is null or "System", $"settings.json keeps {Kept().ToJsonString()}, not the system's dark or light");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Escape_keeps_the_defaults_and_a_later_start_asks_nothing()
    {
        MainWindow window = Started();
        Assert.True(window.ShowFirstStartOnce(), "A new state's first start asked nothing");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Assert.Null(window.FirstStart);
        Assert.True(window.Settings is { Look: null, Theme: ThemeChoice.System } && Themes.Look is null,
            $"Escape changed the settings to {window.Settings.Look?.Name ?? "Passenger"} in {window.Settings.Theme}");
        window.Close();
        Ui.Settle();

        window = Started();
        Assert.False(window.ShowFirstStartOnce(), "A second start asked the theme again");
        Assert.Null(window.FirstStart);
        window.Close();
    }

    /// <summary>What an earlier Passenger left behind, any one of its files: its settings, layout, recent solutions or
    /// record of updates.</summary>
    [AvaloniaTheory]
    [InlineData(StateStore.SettingsFile)]
    [InlineData("layout.json")]
    [InlineData("recentSolutions.json")]
    [InlineData("updates.json")]
    public void An_update_from_a_version_whose_state_is_there_asks_nothing(string file)
    {
        File.WriteAllText(Path.Combine(state, file), "{}\n");
        MainWindow window = Started();
        Assert.False(window.ShowFirstStartOnce(), $"An update over an earlier Passenger's {file} asked the theme as a first start would");
        Assert.Null(window.FirstStart);
        window.Close();
    }

    [AvaloniaFact]
    public void A_window_nobody_sees_or_that_keeps_no_settings_never_asks()
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

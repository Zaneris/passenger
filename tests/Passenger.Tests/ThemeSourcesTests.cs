using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Themes from sources, as a person meets them in Settings | Appearance: a folder and an address added and read
/// through their index, their themes listed by name and source, one chosen kept and drawn at once, and again from its kept
/// copy with the source gone; a source, a file it lists or a file added that cannot be read said why, changing nothing; and
/// a theme chosen in 0.3, when Passenger carried it, taken from Passenger's themes repository, said while it is fetched.</summary>
public sealed class ThemeSourcesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("themes-folder-");
    private readonly string settings = TestFolders.Create("themes-settings-");
    private readonly string sources = TestFolders.Create("themes-sources-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
        TestFolders.Delete(sources);
    }

    [AvaloniaFact]
    public void A_folder_source_added_in_Settings_lists_its_themes_by_name_and_source_and_one_chosen_is_kept_and_drawn_before_Apply()
    {
        string source = Path.Combine(sources, "mine");
        CarriedThemes.CopyTo(source, "Moss", "Phosphor");
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.ThemeSourceBox.Text = source;
            Ui.Click(dialog.AddThemeSourceButton);
            Ui.Settle();
            string added = PathNames.Canonical(source);
            Assert.True(dialog.Working.ThemeSources.SequenceEqual([added]), $"The folder is not the source added: {dialog.ThemeSourceWords.Text}");
            Assert.Equal([ThemeSource.RepositoryName, added], dialog.ThemeSourceList.Items.Cast<string>());

            ThemePicker picker = dialog.ThemePicker;
            picker.Box.IsDropDownOpen = true;
            Ui.WaitFor(() => picker.Listed.Contains($"Phosphor ({added})"), () => $"the folder's themes listed by name and source, where it lists {string.Join(", ", picker.Listed)}");
            Assert.Contains($"Moss ({added})", picker.Listed);
            picker.Box.IsDropDownOpen = false;
            picker.Choose("Phosphor", added);
            Ui.Settle();
            Assert.Equal(Frame("Phosphor", window.ActualThemeVariant), Drawn(window));
            Assert.True(KeptThemes.Holds(new ThemeSource(ThemeSourceKind.Folder, added), false, "Phosphor"), "Phosphor chosen from the folder is not kept");
            dialog.Apply();
            Assert.Equal(new SourcedTheme(added, "Phosphor"), window.Settings.Look);
            dialog.Close();

            // Its source gone, the theme draws from the copy kept.
            TestFolders.Delete(source);
            Themes.Reload();
            Ui.Settle();
            Assert.Equal(Frame("Phosphor", window.ActualThemeVariant), Drawn(window));
            Assert.Null(Themes.Said);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_address_source_is_read_through_its_index_and_a_theme_chosen_from_it_draws_at_the_next_start_with_the_address_gone()
    {
        string served = Path.Combine(sources, "served");
        CarriedThemes.CopyTo(served, "Sorbet");
        List<string> asked = [];
        StandInHttp? web = new(exchange =>
        {
            lock (asked)
            {
                asked.Add(exchange.Path);
            }
            string file = Path.Combine(served, exchange.Path.Replace("/themes/", string.Empty, StringComparison.Ordinal));
            return exchange.Path.StartsWith("/themes/", StringComparison.Ordinal) && File.Exists(file)
                ? exchange.ReplyAsync(200, "application/json", File.ReadAllBytes(file))
                : exchange.ReplyAsync(404, "text/plain", "none");
        });
        string address = $"{web.Root}/themes/";
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.ThemeSourceBox.Text = $"{address}passenger-themes.json";
            Ui.Click(dialog.AddThemeSourceButton);
            Ui.Settle();
            Assert.True(dialog.Working.ThemeSources.SequenceEqual([address]), $"The address is not the source added: {dialog.ThemeSourceWords.Text}");
            Assert.Equal(["/themes/passenger-themes.json", "/themes/Sorbet.json"], Asked());
            ThemePicker picker = dialog.ThemePicker;
            Ui.WaitFor(() => picker.Listed.Contains($"Sorbet ({address})"), () => $"the address's theme listed by name and source, where it lists {string.Join(", ", picker.Listed)}");
            picker.Choose("Sorbet", address);
            Ui.Settle();
            dialog.Ok();
            Ui.Settle();
            Assert.Equal(new SourcedTheme(address, "Sorbet"), window.Settings.Look);
            Assert.Equal(Frame("Sorbet", window.ActualThemeVariant), Drawn(window));
            Assert.Equal(2, Asked().Length);
            window.Close();
            web.Dispose();
            web = null;

            // The next start draws it from the copy kept, asking no source: the address answers no one now.
            Themes.Apply(Application.Current!, null, null);
            Themes.Reload();
            window = Open();
            window.KeepSettings();
            Ui.Settle();
            Assert.Equal(new SourcedTheme(address, "Sorbet"), window.Settings.Look);
            Assert.Equal(Frame("Sorbet", window.ActualThemeVariant), Drawn(window));
            Assert.Null(Themes.Said);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            web?.Dispose();
        }

        string[] Asked()
        {
            lock (asked)
            {
                return [.. asked];
            }
        }
    }

    [AvaloniaFact]
    public void A_source_or_a_file_that_cannot_be_read_says_why_and_changes_nothing()
    {
        string later = Path.Combine(sources, "later");
        Directory.CreateDirectory(later);
        File.WriteAllText(Path.Combine(later, ThemeFiles.IndexFile), """{"format": 2, "themes": []}""");
        string mixed = Path.Combine(sources, "mixed");
        CarriedThemes.CopyTo(mixed, "Ledger");
        File.WriteAllText(Path.Combine(mixed, "Broken.json"), """{"format": 1, "name": "Broken", "dark": {"Frame": "teal"}}""");
        File.WriteAllText(Path.Combine(mixed, ThemeFiles.IndexFile), """{"format": 1, "themes": ["Ledger.json", "Broken.json"]}""");
        string notJson = Path.Combine(folder, "Not a theme.json");
        File.WriteAllText(notJson, "frame: teal\n");
        string mine = Path.Combine(folder, "Mine.json");
        File.WriteAllText(mine, """{"format": 1, "name": "Mine of the test", "tokens": {"ControlRadius": 2}, "dark": {"Frame": "#203040"}, "light": {"Frame": "#E0E8F0"}, "fram": 1}""");
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.ThemeSourceBox.Text = later;
            Ui.Click(dialog.AddThemeSourceButton);
            Ui.Settle();
            Assert.Empty(dialog.Working.ThemeSources);
            Assert.True(dialog.ThemeSourceWords.Text?.Contains("is format 2, which this Passenger does not read", StringComparison.Ordinal) == true,
                $"A source whose index is a later format is refused without saying so: {dialog.ThemeSourceWords.Text}");
            dialog.ThemeSourceBox.Text = "not a source";
            Ui.Click(dialog.AddThemeSourceButton);
            Assert.Empty(dialog.Working.ThemeSources);
            Assert.Contains("no full path to a folder, no GitHub repository (owner/name) and no https address", dialog.ThemeSourceWords.Text, StringComparison.Ordinal);

            // An index that reads is added; a file it lists that does not is said, and only the others listed.
            dialog.ThemeSourceBox.Text = mixed;
            Ui.Click(dialog.AddThemeSourceButton);
            Ui.Settle();
            string added = PathNames.Canonical(mixed);
            Assert.Equal([added], dialog.Working.ThemeSources);
            ThemePicker picker = dialog.ThemePicker;
            Ui.WaitFor(() => picker.Listed.Contains($"Ledger ({added})"), () => $"the source's readable theme listed, where it lists {string.Join(", ", picker.Listed)}");
            Assert.DoesNotContain(picker.Listed, l => l.StartsWith("Broken", StringComparison.Ordinal));
            Assert.True(picker.Problems?.Contains("Frame in dark as \"teal\", where it takes a colour", StringComparison.Ordinal) == true,
                $"The file the source lists that does not read is not said: {picker.Problems}");

            SourcedTheme? before = dialog.Working.Look;
            string? why = Ui.Await(picker.AddFileAsync(notJson), "a file that is no JSON added");
            Assert.True(why?.Contains("is not JSON as format 1 reads it", StringComparison.Ordinal) == true, $"A file that is no JSON is not refused for it: {why}");
            Assert.StartsWith(why!, picker.Problems, StringComparison.Ordinal);
            Assert.Equal(before, dialog.Working.Look);

            Assert.Null(Ui.Await(picker.AddFileAsync(mine), "a theme file added"));
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, "Mine of the test"), dialog.Working.Look);
            Assert.Contains($"Mine of the test ({ThemeSource.Added.Shown})", picker.Listed);
            Assert.True(picker.Problems?.Contains("fram is no member of a theme this Passenger knows", StringComparison.Ordinal) == true, $"A member the file gives that no theme has is not named: {picker.Problems}");
            Ui.Settle();
            Assert.Equal(window.ActualThemeVariant == ThemeVariant.Dark ? Color.Parse("#203040") : Color.Parse("#E0E8F0"), Drawn(window));
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_theme_chosen_in_0_3_is_the_same_from_the_themes_repository_said_while_it_is_fetched_and_one_out_of_reach_is_said_never_dropped()
    {
        foreach ((bool code, string name) in ((bool, string)[])[(false, "Nightjar"), (true, ThemeSource.FormerScheme)])
        {
            TestFolders.Delete(Path.GetDirectoryName(KeptThemes.PathOf(ThemeSource.Repository, code, name))!);
        }
        Themes.Apply(Application.Current!, null, null);
        File.WriteAllText(Path.Combine(settings, StateStore.SettingsFile), """{ "version": 3, "look": "Nightjar", "colourScheme": "Rider", "editorFontSize": 14 }""");
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            Assert.Equal(SourcedTheme.Repository("Nightjar"), window.Settings.Look);
            Assert.Equal(SourcedTheme.Repository(ThemeSource.FormerScheme), window.Settings.ColourScheme);
            Assert.Equal(14, window.Settings.EditorFontSize);
            string fetching = $"Nightjar is being fetched from {ThemeSource.RepositoryName}";
            Assert.True(Themes.Said?.StartsWith(fetching, StringComparison.Ordinal) == true, $"The window does not say Nightjar is fetched as it comes from its new home: {Themes.Said}");
            Assert.StartsWith(fetching, window.Status);
            Ui.Settle();
            Assert.Equal(Frame("Nightjar", window.ActualThemeVariant), Drawn(window));
            Assert.Null(Themes.Said);
            Assert.True(KeptThemes.Holds(ThemeSource.Repository, false, "Nightjar") && KeptThemes.Holds(ThemeSource.Repository, true, ThemeSource.FormerScheme),
                "Nightjar and the code colour scheme fetched are not kept, so the next start would fetch them again");
            window.Close();

            // A theme whose source answers no one is said, the settings keeping it for when it can be had.
            File.WriteAllText(Path.Combine(settings, StateStore.SettingsFile), """{ "version": 4, "look": { "source": "http://127.0.0.1:9/gone/", "name": "Gone" } }""");
            window = Open();
            window.KeepSettings();
            Ui.Settle();
            Assert.Equal(new SourcedTheme("http://127.0.0.1:9/gone/", "Gone"), window.Settings.Look);
            Assert.True(Themes.SaidProblem && Themes.Said?.StartsWith("Gone could not be fetched from http://127.0.0.1:9/gone/", StringComparison.Ordinal) == true,
                $"A theme out of reach is not said as one: {Themes.Said}");
            Assert.StartsWith("Gone could not be fetched", window.Status);
            Assert.Equal(Token(TokenKeys.Frame, window), Drawn(window));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>The frame's colour a theme of the repository's copy gives in a variant.</summary>
    private static Color Frame(string theme, ThemeVariant variant)
    {
        ThemeFile file = CarriedThemes.Theme(theme)!;
        TokenValue value = (variant == ThemeVariant.Dark ? file.Dark : file.Light).TryGetValue("Frame", out TokenValue? own) ? own : file.Both["Frame"];
        return Color.FromUInt32(((ColourValue)value).Argb);
    }

    /// <summary>The frame's colour as the window has it now.</summary>
    private static Color Drawn(MainWindow window) =>
        window.TryFindResource(TokenKeys.Frame, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;

    /// <summary>Passenger's own frame colour in the window's variant, read from Tokens.axaml under every theme.</summary>
    private static Color Token(string key, MainWindow window)
    {
        Avalonia.Controls.ResourceDictionary tokens = (Avalonia.Controls.ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri("avares://Passenger/Theme/Tokens.axaml"));
        ((Avalonia.Controls.ResourceDictionary)tokens.ThemeDictionaries[window.ActualThemeVariant]).TryGetValue(key, out object? value);
        return ((ISolidColorBrush)value!).Color;
    }
}

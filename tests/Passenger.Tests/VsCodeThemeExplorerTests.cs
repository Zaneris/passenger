using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>VS Code colour themes from Open VSX, as a person meets them in Settings | Appearance, against a stand-in of Open
/// VSX's API on a loopback port (no test reaches Open VSX): the explorer asks nothing until opened, then lists each colour
/// theme its extensions contribute, most downloaded first, by rating or by words on request; one chosen shows at once,
/// in its own variant whatever the setting, is kept with its base and draws with Open VSX gone; a <c>.vsix</c> that cannot
/// be read is refused, saying why; and a Passenger theme naming a VS Code theme by its id draws that base under its own
/// tokens, kept so it draws offline, and one naming a VS Code theme beside it kept apart from the theme's own file whatever
/// it is named; and Add from File taking a VS Code theme's JSON or .vsix as a theme, and one as a code colour scheme under
/// any theme.</summary>
public sealed class VsCodeThemeExplorerTests : IDisposable
{
    private readonly string folder = TestFolders.Create("vscode-themes-folder-");
    private readonly string settings = TestFolders.Create("vscode-themes-settings-");
    private readonly string sources = TestFolders.Create("vscode-themes-sources-");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OpenVsx.Variable, "http://127.0.0.1:9/");
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
        TestFolders.Delete(sources);
    }

    [AvaloniaFact]
    public void The_explorer_asks_Open_VSX_only_once_opened_lists_colour_themes_most_downloaded_first_and_one_chosen_shows_before_Apply_and_draws_offline()
    {
        StandInOpenVsx registry = new();
        registry.Add("lantern", "lantern-theme", "Lantern Themes", 5000, 4.5, LanternVsix());
        registry.Add("someone", "icons", "Only Icons", 9000, 4.9, Vsix("""{"publisher": "someone", "name": "icons", "version": "1.0.0", "contributes": {"iconThemes": [{"id": "i", "path": "./i.json"}]}}"""));
        registry.Add("other", "plain-dark", "Plain Dark", 100, 5.0, Vsix(
            """{"publisher": "other", "name": "plain-dark", "version": "1.0.0", "contributes": {"themes": [{"label": "Plain", "uiTheme": "vs-dark", "path": "./plain.json"}]}}""",
            ("extension/plain.json", """{"colors": {"editor.background": "#101010"}}""")));
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            window.ApplySettings(new AppSettings { Theme = ThemeChoice.Light });
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ThemeExplorer explorer = dialog.ThemeExplorer;
            Assert.True(registry.Asked.Count == 0, $"Open VSX was asked before the explorer was opened: {string.Join(", ", registry.Asked)}");

            Ui.Click(explorer.OpenButton);
            Ui.WaitFor(() => explorer.Listed.Count == 3, () => $"the colour themes listed, where it lists {string.Join(", ", explorer.Listed)} and says {explorer.Said.Text}");
            Assert.Equal(["Lantern (Lantern Themes, lantern)", "Paper (Lantern Themes, lantern)", "Plain (Plain Dark, other)"], explorer.Listed);
            Assert.Contains(registry.Asked, a => a.StartsWith("/api/-/search?", StringComparison.Ordinal) && a.Contains("category=Themes&sortBy=downloadCount&sortOrder=desc", StringComparison.Ordinal));
            Assert.Equal("3 colour themes from 2 extensions on Open VSX", explorer.Said.Text);

            explorer.Order = OpenVsxOrder.Rating;
            Ui.WaitFor(() => explorer.Listed.FirstOrDefault() == "Plain (Plain Dark, other)", () => $"the best rated first, where it lists {string.Join(", ", explorer.Listed)}");
            Assert.Contains(registry.Asked, a => a.Contains("sortBy=rating", StringComparison.Ordinal));

            explorer.SearchBox.Text = "lantern";
            explorer.SearchBox.Focus();
            Ui.Press(dialog, "enter");
            Ui.WaitFor(() => explorer.Listed.Count == 2, () => $"the themes found by words, where it lists {string.Join(", ", explorer.Listed)}");
            Assert.Contains(registry.Asked, a => a.Contains("query=lantern", StringComparison.Ordinal));

            explorer.Choose("Lantern");
            Ui.WaitFor(() => dialog.Working.Look == new SourcedTheme(ThemeSource.OpenVsx.Name, "Lantern"), () => $"Lantern chosen, where the explorer says {explorer.Said.Text}");
            Ui.Settle();
            ThemeFile lantern = CarriedThemes.Theme(CarriedThemes.VsCodePrefix + "Lantern")!;
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal(Token(lantern, "Frame"), Drawn(window, TokenKeys.Frame));
            Assert.Equal(Token(lantern, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));
            Assert.True(KeptThemes.Holds(ThemeSource.OpenVsx, false, "Lantern") && KeptThemes.HoldsBase(ThemeSource.OpenVsx, false, "Lantern"),
                "Lantern chosen is not kept with its VS Code theme beside it");
            dialog.Apply();
            Assert.Equal(new SourcedTheme(ThemeSource.OpenVsx.Name, "Lantern"), window.Settings.Look);
            Assert.Contains("Lantern (Open VSX)", dialog.ThemePicker.Listed);
            dialog.Close();
            window.Close();

            // With Open VSX out of reach, the theme kept draws at the next start.
            registry.Dispose();
            Environment.SetEnvironmentVariable(OpenVsx.Variable, "http://127.0.0.1:9/");
            Themes.Apply(Avalonia.Application.Current!, null, null);
            Themes.Reload();
            window = Open();
            window.KeepSettings();
            Ui.Settle();
            Assert.Null(Themes.Said);
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal(Token(lantern, "Frame"), Drawn(window, TokenKeys.Frame));
        }
        finally
        {
            registry.Dispose();
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_vsix_that_cannot_be_read_is_refused_saying_why_and_changes_nothing()
    {
        using StandInOpenVsx registry = new();
        registry.Add("broken", "theme", "Broken Theme", 10, null, Encoding.UTF8.GetBytes("no zip at all"), """
            {"publisher": "broken", "name": "theme", "version": "1.0.0", "contributes": {"themes": [{"label": "Broken", "uiTheme": "vs-dark", "path": "./b.json"}]}}
            """);
        MainWindow window = Open();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ThemeExplorer explorer = dialog.ThemeExplorer;
            Ui.Click(explorer.OpenButton);
            Ui.WaitFor(() => explorer.Listed.Count == 1, () => $"the broken theme listed, where it says {explorer.Said.Text}");
            explorer.Choose("Broken");
            Ui.WaitFor(() => explorer.Said.Text?.Contains("is not used", StringComparison.Ordinal) == true, () => $"the refusal said, where it says {explorer.Said.Text}");
            Assert.Contains("no zip Passenger reads", explorer.Said.Text, StringComparison.Ordinal);
            Assert.Contains(StyleClasses.Error, explorer.Said.Classes);
            Assert.Null(dialog.Working.Look);
            Assert.False(KeptThemes.Holds(ThemeSource.OpenVsx, false, "Broken"), "A theme refused was kept");
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_VS_Code_theme_from_the_explorer_or_a_vsix_is_kept_as_a_theme_file_reads_it_its_long_label_cut_short_and_draws()
    {
        string label = "Midnight Harbour Lights " + new string('x', 70);
        string Plain(string colour) => $$$"""{"colors": {"editor.background": "{{{colour}}}"}}""";
        using StandInOpenVsx registry = new();
        registry.Add("harbour", "lights", "Harbour", 50, null, Vsix(
            $$$"""{"publisher": "harbour", "name": "lights", "version": "1.0.0", "contributes": {"themes": [{"label": "{{{label}}}", "uiTheme": "vs-dark", "path": "./a.json"}]}}""",
            ("extension/a.json", Plain("#102030"))));
        registry.Add("quay", "lights", "Quay", 40, null, Vsix(
            $$$"""{"publisher": "quay", "name": "lights", "version": "1.0.0", "contributes": {"themes": [{"label": "{{{label}}}", "uiTheme": "vs-dark", "path": "./b.json"}]}}""",
            ("extension/b.json", Plain("#304050"))));
        string vsix = Path.Combine(sources, "long.vsix");
        File.WriteAllBytes(vsix, Vsix(
            $$$"""{"publisher": "pier", "name": "lights", "version": "2.0.0 local", "contributes": {"themes": [{"label": "{{{label}}}", "uiTheme": "vs-dark", "path": "./c.json"}]}}""",
            ("extension/c.json", Plain("#506070"))));
        MainWindow window = Open();
        try
        {
            window.ApplySettings(new AppSettings { Theme = ThemeChoice.Dark });
            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ThemeExplorer explorer = dialog.ThemeExplorer;
            Ui.Click(explorer.OpenButton);
            Ui.WaitFor(() => explorer.Listed.Count == 2, () => $"both themes listed, where it lists {string.Join(", ", explorer.Listed)} and says {explorer.Said.Text}");

            // Beside another extension's theme of its label, kept under its label cut short before its extension's name.
            string cut = label[..63] + "…";
            foreach ((string extension, string kept, string colour) in ((string, string, string)[])[("harbour.lights", cut, "#102030"), ("quay.lights", label[..56] + "… (Quay)", "#304050")])
            {
                explorer.Choose(label, extension);
                Ui.WaitFor(() => explorer.Working.IsCompleted, () => $"{extension}'s theme chosen, where the explorer says {explorer.Said.Text}");
                Ui.Settle();
                Assert.True(Themes.Said is null, $"{extension}'s theme chosen does not read as kept: {Themes.Said}");
                Assert.Equal(new SourcedTheme(ThemeSource.OpenVsx.Name, kept), dialog.Working.Look);
                Assert.Equal(Color.Parse(colour), Drawn(window, TokenKeys.Editor_Background));
            }
            Assert.Equal("harbour.lights", ThemeFiles.ReadTheme(cut, KeptThemes.Bytes(ThemeSource.OpenVsx, false, cut)!, ThemeResources.Kinds).Read?.Base?.Id);

            Assert.Null(Ui.Await(dialog.ThemePicker.AddFileAsync(vsix), "the .vsix of a long label added"));
            Ui.Settle();
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, cut), dialog.Working.Look);
            Assert.True(Themes.Said is null, $"The theme added from a .vsix does not read: {Themes.Said}");
            Assert.Equal(Color.Parse("#506070"), Drawn(window, TokenKeys.Editor_Background));
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_theme_naming_a_VS_Code_theme_by_its_id_draws_it_under_its_own_tokens_and_is_kept_with_it_so_it_draws_offline()
    {
        StandInOpenVsx registry = new();
        registry.Add("lantern", "lantern-theme", "Lantern Themes", 5000, 4.5, LanternVsix());
        string source = Path.Combine(sources, "based");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, ThemeFiles.IndexFile), """{"format": 1, "themes": ["Lit.json"]}""");
        File.WriteAllText(Path.Combine(source, "Lit.json"), """
            {"format": 1, "name": "Lit", "vscode": {"id": "lantern.lantern-theme", "theme": "Lantern"},
             "tokens": {"Frame": "#0A0A12", "ControlRadius": 0}}
            """);
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.ThemeSourceBox.Text = source;
            Ui.Click(dialog.AddThemeSourceButton);
            Ui.Settle();
            string added = PathNames.Canonical(source);
            ThemePicker picker = dialog.ThemePicker;
            picker.Box.IsDropDownOpen = true;
            Ui.WaitFor(() => picker.Listed.Contains($"Lit ({added})"), () => $"Lit listed, where it lists {string.Join(", ", picker.Listed)}");
            picker.Box.IsDropDownOpen = false;
            picker.Choose("Lit", added);
            Ui.Settle();
            Assert.True(picker.Problems is null, $"Lit was refused: {picker.Problems}");
            ThemeFile lantern = CarriedThemes.Theme(CarriedThemes.VsCodePrefix + "Lantern")!;
            Assert.Equal(Color.Parse("#0A0A12"), Drawn(window, TokenKeys.Frame));
            Assert.Equal(Token(lantern, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Contains(registry.Asked, a => a == "/api/lantern/lantern-theme");
            ThemeSource from = new(ThemeSourceKind.Folder, added);
            Assert.True(KeptThemes.Holds(from, false, "Lit") && KeptThemes.HoldsBase(from, false, "Lit"), "Lit is not kept with its base");
            dialog.Apply();
            dialog.Close();
            window.Close();

            // Its source and Open VSX gone, the theme and its base draw from what was kept.
            registry.Dispose();
            Environment.SetEnvironmentVariable(OpenVsx.Variable, "http://127.0.0.1:9/");
            TestFolders.Delete(source);
            Themes.Apply(Avalonia.Application.Current!, null, null);
            Themes.Reload();
            window = Open();
            window.KeepSettings();
            Ui.Settle();
            Assert.Null(Themes.Said);
            Assert.Equal(Color.Parse("#0A0A12"), Drawn(window, TokenKeys.Frame));
            Assert.Equal(Token(lantern, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));
        }
        finally
        {
            registry.Dispose();
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_theme_s_VS_Code_theme_beside_it_named_Theme_json_or_including_theme_json_is_kept_apart_from_the_theme_s_own_file_and_draws()
    {
        // On a file system that ignores letter case, Theme.json is the name the theme's own file is kept under.
        string source = Path.Combine(sources, "beside");
        string lit = Path.Combine(source, "lit");
        string dim = Path.Combine(source, "dim");
        Directory.CreateDirectory(lit);
        Directory.CreateDirectory(dim);
        byte[] lanternJson = CarriedThemes.VsCodeBytes("Lantern.json");
        byte[] lanternBase = CarriedThemes.VsCodeBytes("lantern-base.json");
        File.WriteAllText(Path.Combine(source, ThemeFiles.IndexFile), """{"format": 1, "themes": ["lit/Lit.json", "dim/Dim.json"]}""");
        string litFile = Path.Combine(lit, "Lit.json");
        File.WriteAllText(litFile, """{"format": 1, "name": "Lit", "vscode": {"file": "Theme.json"}, "tokens": {"Frame": "#0A0A12"}}""");
        File.WriteAllBytes(Path.Combine(lit, "Theme.json"), lanternJson);
        File.WriteAllBytes(Path.Combine(lit, "lantern-base.json"), lanternBase);
        File.WriteAllText(Path.Combine(dim, "Dim.json"), """{"format": 1, "name": "Dim", "vscode": {"file": "dusk.json"}, "tokens": {"Frame": "#0B0B13"}}""");
        File.WriteAllText(Path.Combine(dim, "dusk.json"), """{"name": "Dusk", "type": "dark", "include": "./theme.json", "colors": {"editor.foreground": "#ECE7F5"}}""");
        File.WriteAllBytes(Path.Combine(dim, "theme.json"), lanternJson);
        File.WriteAllBytes(Path.Combine(dim, "lantern-base.json"), lanternBase);
        ThemeFile lantern = CarriedThemes.Theme(CarriedThemes.VsCodePrefix + "Lantern")!;
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.ThemeSourceBox.Text = source;
            Ui.Click(dialog.AddThemeSourceButton);
            Ui.Settle();
            string added = PathNames.Canonical(source);
            ThemeSource from = new(ThemeSourceKind.Folder, added);
            ThemePicker picker = dialog.ThemePicker;
            picker.Box.IsDropDownOpen = true;
            Ui.WaitFor(() => picker.Listed.Contains($"Lit ({added})") && picker.Listed.Contains($"Dim ({added})"),
                () => $"Lit and Dim listed, where it lists {string.Join(", ", picker.Listed)}");
            picker.Box.IsDropDownOpen = false;
            foreach ((string name, string frame) in ((string, string)[])[("Lit", "#0A0A12"), ("Dim", "#0B0B13")])
            {
                picker.Choose(name, added);
                Ui.Settle();
                Assert.True(picker.Problems is null && Themes.Said is null, $"{name} was refused: {picker.Problems ?? Themes.Said}");
                Assert.Equal(Color.Parse(frame), Drawn(window, TokenKeys.Frame));
                Assert.True(Token(lantern, "Editor.Background") == Drawn(window, TokenKeys.Editor_Background)
                    && Token(lantern, "Syntax.Keyword") == Drawn(window, TokenKeys.Syntax_Keyword),
                    $"{name} chosen from its source does not draw Lantern's colours, so its VS Code theme was lost to its own kept file");
                Assert.Equal(File.ReadAllBytes(Path.Combine(source, name.ToLowerInvariant(), $"{name}.json")), KeptThemes.Bytes(from, false, name));
            }

            // Added from its file, the theme and its VS Code theme are kept apart too.
            Assert.Null(Ui.Await(picker.AddFileAsync(litFile), "Lit added from its file"));
            Ui.Settle();
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, "Lit"), dialog.Working.Look);
            Assert.True(Themes.Said is null, $"Lit added was refused: {Themes.Said}");
            Assert.Equal(Token(lantern, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));
            dialog.Close();
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Add_from_File_takes_a_VS_Code_theme_s_JSON_or_vsix_as_a_theme_and_one_as_a_code_colour_scheme_under_any_theme()
    {
        string files = Path.Combine(sources, "files");
        Directory.CreateDirectory(files);
        string json = Path.Combine(files, "Lantern.json");
        File.WriteAllBytes(json, CarriedThemes.VsCodeBytes("Lantern.json"));
        File.WriteAllBytes(Path.Combine(files, "lantern-base.json"), CarriedThemes.VsCodeBytes("lantern-base.json"));
        string vsix = Path.Combine(files, "lantern.vsix");
        File.WriteAllBytes(vsix, LanternVsix());
        string broken = Path.Combine(files, "broken.vsix");
        File.WriteAllText(broken, "no zip at all");
        string huge = Path.Combine(files, "huge.vsix");
        using (FileStream sparse = File.Create(huge))
        {
            sparse.SetLength(VsCodeThemes.MostVsixBytes + 1024 * 1024);
        }
        ThemeFile lantern = CarriedThemes.Theme(CarriedThemes.VsCodePrefix + "Lantern")!;
        MainWindow window = Open();
        try
        {
            window.KeepSettings();
            window.ApplySettings(new AppSettings { Theme = ThemeChoice.Dark });
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            Assert.Null(Ui.Await(dialog.ThemePicker.AddFileAsync(json), "Lantern's JSON added"));
            Ui.Settle();
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, "Lantern"), dialog.Working.Look);
            Assert.Equal(Token(lantern, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));
            Assert.True(KeptThemes.Holds(ThemeSource.Added, false, "Lantern"), "Lantern added from its JSON is not kept");

            Assert.Null(Ui.Await(dialog.ThemePicker.AddFileAsync(vsix), "Lantern's .vsix added"));
            Ui.Settle();
            Assert.Contains("Paper (added from a file)", dialog.ThemePicker.Listed);
            dialog.ThemePicker.Choose("Paper", "added from a file");
            Ui.Settle();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal(Token(CarriedThemes.Theme(CarriedThemes.VsCodePrefix + "Paper")!, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));

            string? refused = Ui.Await(dialog.ThemePicker.AddFileAsync(broken), "a broken .vsix refused");
            Assert.Contains("no zip Passenger reads", refused, StringComparison.Ordinal);
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, "Paper"), dialog.Working.Look);
            Assert.EndsWith("huge.vsix is 33 MB, more than the 32 MB it may hold", Ui.Await(dialog.ThemePicker.AddFileAsync(huge), "a .vsix past the most refused"));
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, "Paper"), dialog.Working.Look);

            // As a code colour scheme under Passenger's own theme: Lantern's code colours in Dark, moved to read in Light.
            dialog.ThemePicker.Choose("Passenger");
            Assert.Null(Ui.Await(dialog.SchemePicker.AddFileAsync(json), "Lantern's JSON added as a code colour scheme"));
            Ui.Settle();
            Assert.Equal(new SourcedTheme(ThemeSource.Added.Name, "Lantern"), dialog.Working.ColourScheme);
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal(Token(lantern, "Syntax.Keyword"), Drawn(window, TokenKeys.Syntax_Keyword));
            Assert.NotEqual(Token(lantern, "Editor.Background"), Drawn(window, TokenKeys.Editor_Background));
            window.RequestedThemeVariant = ThemeVariant.Light;
            Ui.Settle();
            Color light = Drawn(window, TokenKeys.Syntax_Keyword);
            Assert.NotEqual(Token(lantern, "Syntax.Keyword"), light);
            Assert.True(ThemeColours.Ratio(light.ToUInt32(), Drawn(window, TokenKeys.Editor_Background).ToUInt32()) >= 4.5,
                $"Lantern's keyword under a light theme, {light}, does not read on its ground");
            dialog.Close();
        }
        finally
        {
            window.RequestedThemeVariant = null;
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

    private static Color Token(ThemeFile theme, string token) => Color.FromUInt32(((ColourValue)theme.Both[token]).Argb);

    private static Color Drawn(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;

    /// <summary>Lantern's extension: Lantern (dark, with the file it includes) and Paper (light), the tests' own themes.</summary>
    private static byte[] LanternVsix() => Vsix("""
        {"publisher": "lantern", "name": "lantern-theme", "version": "1.2.0", "displayName": "Lantern Themes",
         "contributes": {"themes": [
           {"label": "Lantern", "uiTheme": "vs-dark", "path": "./themes/Lantern.json"},
           {"label": "Paper", "uiTheme": "vs", "path": "./themes/Paper.json"}]}}
        """,
        ("extension/themes/Lantern.json", Encoding.UTF8.GetString(CarriedThemes.VsCodeBytes("Lantern.json"))),
        ("extension/themes/lantern-base.json", Encoding.UTF8.GetString(CarriedThemes.VsCodeBytes("lantern-base.json"))),
        ("extension/themes/Paper.json", Encoding.UTF8.GetString(CarriedThemes.VsCodeBytes("Paper.json"))));

    /// <summary>A .vsix of a manifest and files by their paths in the zip.</summary>
    private static byte[] Vsix(string manifest, params (string Path, string Text)[] files)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string text) in files.Prepend(("extension/package.json", manifest)))
            {
                using Stream entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return bytes.ToArray();
    }

    /// <summary>Open VSX's API as the explorer and a theme's base ask it, on a loopback port named by <see cref="OpenVsx.Variable"/>
    /// while it lives: its themes search (by words, most downloaded or best rated first), each extension's metadata and
    /// manifest, and its .vsix; every request kept.</summary>
    private sealed class StandInOpenVsx : IDisposable
    {
        private readonly StandInHttp http;
        private readonly List<(string Namespace, string Name, string Display, long Downloads, double? Rating, byte[] Vsix, string Manifest)> extensions = [];
        private readonly List<string> asked = [];
        private bool ended;

        public StandInOpenVsx()
        {
            http = new StandInHttp(AnswerAsync);
            Environment.SetEnvironmentVariable(OpenVsx.Variable, http.Root + "/");
        }

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (asked)
                {
                    return [.. asked];
                }
            }
        }

        public void Add(string space, string name, string display, long downloads, double? rating, byte[] vsix, string? manifest = null)
        {
            manifest ??= Encoding.UTF8.GetString(VsCodeThemes.Vsix(vsix, out _)!(VsCodeThemes.Manifest).Bytes!);
            lock (extensions)
            {
                extensions.Add((space, name, display, downloads, rating, vsix, manifest));
            }
        }

        /// <summary>Its end, once however often asked, a test ending it partway as well as at its end.</summary>
        public void Dispose()
        {
            if (!ended)
            {
                ended = true;
                http.Dispose();
            }
        }

        private Task AnswerAsync(StandInHttp.Exchange exchange)
        {
            lock (asked)
            {
                asked.Add(exchange.Path);
            }
            string path = exchange.Path;
            string query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : string.Empty;
            string[] parts = (path.Contains('?') ? path[..path.IndexOf('?')] : path).Trim('/').Split('/');
            lock (extensions)
            {
                if (path.StartsWith("/api/-/search", StringComparison.Ordinal))
                {
                    string words = Uri.UnescapeDataString(Value(query, "query") ?? string.Empty);
                    IEnumerable<(string Namespace, string Name, string Display, long Downloads, double? Rating, byte[] Vsix, string Manifest)> found = extensions.Where(e => words.Length == 0 || $"{e.Namespace} {e.Name} {e.Display}".Contains(words, StringComparison.OrdinalIgnoreCase));
                    found = Value(query, "sortBy") == "rating" ? found.OrderByDescending(e => e.Rating ?? 0) : found.OrderByDescending(e => e.Downloads);
                    string body = JsonSerializer.Serialize(new { offset = 0, totalSize = found.Count(), extensions = found.Select(Entry) });
                    return exchange.ReplyAsync(200, "application/json", body);
                }
                if (parts is ["api", string space, string name, ..] && extensions.FirstOrDefault(e => e.Namespace == space && e.Name == name) is { Name: not null } extension)
                {
                    return parts.Length == 6 && parts[4] == "file"
                        ? exchange.ReplyAsync(200, "application/json", extension.Manifest)
                        : exchange.ReplyAsync(200, "application/json", JsonSerializer.Serialize(Entry(extension)));
                }
                if (parts is ["files", string file] && extensions.FirstOrDefault(e => file == $"{e.Namespace}.{e.Name}.vsix") is { Name: not null } download)
                {
                    return exchange.ReplyAsync(200, "application/octet-stream", download.Vsix);
                }
            }
            return exchange.ReplyAsync(404, "application/json", """{"error": "not found"}""");
        }

        private object Entry((string Namespace, string Name, string Display, long Downloads, double? Rating, byte[] Vsix, string Manifest) e) => new Dictionary<string, object?>
        {
            ["namespace"] = e.Namespace,
            ["name"] = e.Name,
            ["version"] = "1.2.0",
            ["displayName"] = e.Display,
            ["downloadCount"] = e.Downloads,
            ["averageRating"] = e.Rating,
            ["files"] = new Dictionary<string, string> { ["download"] = $"{http.Root}/files/{e.Namespace}.{e.Name}.vsix" },
        };

        private static string? Value(string query, string name) =>
            query.Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name) is [_, string value] ? value : null;
    }
}

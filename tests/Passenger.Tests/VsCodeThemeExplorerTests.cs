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
/// tokens, kept so it draws offline.</summary>
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

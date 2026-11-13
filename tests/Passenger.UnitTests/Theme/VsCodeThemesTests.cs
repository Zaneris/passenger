using System.IO.Compression;
using System.Text;
using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>VS Code colour themes read as data: a theme's JSON with comments and trailing commas, the files it includes, a
/// <c>.vsix</c>'s manifest and theme files; each refusal (no JSON, no theme, past a limit, a path leaving the extension, a
/// TextMate theme file), each saying why; TextMate scope selectors matched as VS Code matches them; and Open VSX's answers.</summary>
public class VsCodeThemesTests
{
    private const string Dark = """
        // A theme's comment, as VS Code allows.
        {
          "name": "Lantern",
          "type": "dark",
          "semanticHighlighting": true,
          "colors": {
            "editor.background": "#202020",
            "editor.foreground": "#ddd",
            "editor.lineHighlightBackground": "#FFFFFF10",
            "sideBar.background": "not a colour",
          },
          "tokenColors": [
            { "settings": { "foreground": "#CCCCCC" } },
            { "scope": "comment", "settings": { "foreground": "#505050", "fontStyle": "italic" } },
            { "scope": ["keyword", "storage.modifier"], "settings": { "foreground": "#FF79C6" } },
            { "scope": "string, constant.numeric", "settings": { "foreground": "#F1FA8C" } },
          ],
          "semanticTokenColors": { "class": "#8BE9FD", "method": { "foreground": "#50FA7B", "italic": true } },
        }
        """;

    private static VsCodeThemes.ExtensionFile Files(params (string Path, string Text)[] files) => path =>
        files.FirstOrDefault(f => f.Path == path) is { Text: { } text } ? (Encoding.UTF8.GetBytes(text), null) : (null, $"no {path}");

    private static VsCodeTheme Read(string json, string path = "theme.json")
    {
        ThemeRead<VsCodeTheme> read = VsCodeThemes.ReadTheme(path, null, null, Files((path, json)));
        Assert.True(read.Read is not null, read.Refused);
        return read.Read;
    }

    [Fact]
    public void A_theme_reads_with_comments_and_trailing_commas_its_colours_rules_and_semantic_colours_and_names_what_it_passed_over()
    {
        ThemeRead<VsCodeTheme> read = VsCodeThemes.ReadTheme("theme.json", null, null, Files(("theme.json", Dark)));
        VsCodeTheme theme = read.Read!;
        Assert.Equal("Lantern", theme.Name);
        Assert.True(theme.Dark);
        Assert.True(theme.SemanticHighlighting);
        Assert.Equal(0xFF202020u, theme.Colors["editor.background"]);
        Assert.Equal(0xFFDDDDDDu, theme.Colors["editor.foreground"]);
        Assert.Equal(0x10FFFFFFu, theme.Colors["editor.lineHighlightBackground"]);
        Assert.False(theme.Colors.ContainsKey("sideBar.background"));
        Assert.Contains(read.Problems, p => p.Contains("sideBar.background", StringComparison.Ordinal));
        Assert.Equal(["string", "constant.numeric"], theme.TokenColors[3].Selectors);
        Assert.Equal(["keyword", "storage.modifier"], theme.TokenColors[2].Selectors);
        Assert.Empty(theme.TokenColors[0].Selectors);
        Assert.Equal(new VsCodeStyle(0xFF50FA7B, null, true), theme.SemanticColors["method"]);
    }

    [Fact]
    public void A_light_theme_is_light_and_a_label_and_a_kind_given_win_over_its_own()
    {
        Assert.False(Read("""{"type": "light", "colors": {"editor.background": "#FFFFFF"}}""").Dark);
        Assert.False(Read("""{"type": "hcLight", "colors": {"editor.background": "#FFFFFF"}}""").Dark);
        Assert.Equal("light-theme", Read("""{"colors": {"a": "#FFF"}}""", "themes/light-theme.json").Name);
        VsCodeTheme named = VsCodeThemes.ReadTheme("t.json", "Given", false, Files(("t.json", """{"name": "Own", "type": "dark", "colors": {"a": "#FFF"}}"""))).Read!;
        Assert.Equal(("Given", false), (named.Name, named.Dark));
    }

    [Fact]
    public void An_included_file_is_laid_first_so_the_theme_s_own_colours_and_rules_win()
    {
        VsCodeTheme theme = VsCodeThemes.ReadTheme("themes/soft.json", null, null, Files(
            ("themes/soft.json", """{"include": "./base/dark.json", "colors": {"editor.background": "#111111"}, "tokenColors": [{"scope": "comment", "settings": {"foreground": "#999999"}}]}"""),
            ("themes/base/dark.json", """{"colors": {"editor.background": "#000000", "editor.foreground": "#EEEEEE"}, "tokenColors": [{"scope": "comment", "settings": {"foreground": "#555555"}}]}"""))).Read!;
        Assert.Equal(0xFF111111u, theme.Colors["editor.background"]);
        Assert.Equal(0xFFEEEEEEu, theme.Colors["editor.foreground"]);
        Assert.Equal([0xFF555555u, 0xFF999999u], theme.TokenColors.Select(r => r.Foreground!.Value));
    }

    [Theory]
    [InlineData("""{"include": "../../outside.json", "colors": {"a": "#FFF"}}""", "not a file inside the extension")]
    [InlineData("""{"include": "theme.json", "colors": {"a": "#FFF"}}""", "includes more than the 8 files")]
    [InlineData("""{"tokenColors": "./dark.tmTheme"}""", "a TextMate theme file, which Passenger does not read")]
    [InlineData("""{"name": "Empty"}""", "gives no colors and no tokenColors")]
    [InlineData("""[1, 2]""", "is no JSON object")]
    [InlineData("""{"colors": {""", "is not JSON as VS Code reads it")]
    public void A_theme_that_cannot_be_read_is_refused_saying_why(string json, string why)
    {
        ThemeRead<VsCodeTheme> read = VsCodeThemes.ReadTheme("theme.json", null, null, Files(("theme.json", json)));
        Assert.Null(read.Read);
        Assert.Contains(why, read.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_theme_file_past_the_most_a_theme_file_holds_is_refused_unread()
    {
        string big = $$"""{"colors": {"a": "#FFF"}, "name": "{{new string('x', ThemeFiles.MostBytes)}}"}""";
        ThemeRead<VsCodeTheme> read = VsCodeThemes.ReadTheme("theme.json", null, null, Files(("theme.json", big)));
        Assert.Null(read.Read);
        Assert.Contains($"more than the {ThemeFiles.MostBytes} a theme file may hold", read.Refused, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("./theme/dark.json", "", "theme/dark.json")]
    [InlineData("theme/../themes/dark.json", "", "themes/dark.json")]
    [InlineData("../base.json", "themes", "base.json")]
    [InlineData("../base.json", "", null)]
    [InlineData("/etc/dark.json", "", null)]
    [InlineData("C:/dark.json", "", null)]
    [InlineData("theme/dark.tmTheme", "", null)]
    public void A_path_a_file_names_is_taken_inside_the_extension_or_refused(string path, string folder, string? inside) =>
        Assert.Equal(inside, VsCodeThemes.Inside(path, folder));

    [Theory]
    [InlineData("#abc", 0xFFAABBCCu)]
    [InlineData("#abc8", 0x88AABBCCu)]
    [InlineData("#A1B2C3", 0xFFA1B2C3u)]
    [InlineData("#A1B2C380", 0x80A1B2C3u)]
    public void A_vs_code_colour_reads_with_its_alpha_last(string text, uint argb) => Assert.Equal(argb, VsCodeThemes.Colour(text));

    [Fact]
    public void A_vsix_names_its_extension_and_colour_themes_and_reads_them_from_its_zip()
    {
        byte[] vsix = Vsix(("extension/package.json", """
            {"publisher": "lantern", "name": "lantern-theme", "version": "1.2.3", "displayName": "Lantern Themes",
             "contributes": {"themes": [
               {"label": "Lantern", "uiTheme": "vs-dark", "path": "./themes/lantern.json"},
               {"label": "Lantern Day", "uiTheme": "vs", "path": "./themes/day.json"},
               {"label": "Broken", "uiTheme": "vs-dark"}]}}
            """), ("extension/themes/lantern.json", Dark), ("extension/themes/day.json", """{"colors": {"editor.background": "#FFFFFF"}}"""));
        VsCodeThemes.ExtensionFile files = VsCodeThemes.Vsix(vsix, out string? why)!;
        Assert.Null(why);
        ThemeRead<VsCodeExtension> manifest = VsCodeThemes.ReadManifest("package.json", files(VsCodeThemes.Manifest).Bytes);
        VsCodeExtension extension = manifest.Read!;
        Assert.Equal(("lantern.lantern-theme", "1.2.3", "Lantern Themes"), (extension.Id, extension.Version, extension.DisplayName));
        Assert.Equal(["Lantern", "Lantern Day"], extension.Themes.Select(t => t.Label));
        Assert.Contains(manifest.Problems, p => p.Contains("passed over", StringComparison.Ordinal));
        VsCodeTheme day = VsCodeThemes.ReadContributed(extension.Themes[1], files).Read!;
        Assert.Equal(("Lantern Day", false), (day.Name, day.Dark));

        (VsCodeTaken? taken, string? refused) = VsCodeThemes.FromVsix(vsix, "Lantern");
        Assert.Null(refused);
        Assert.Equal(["package.json", "themes/lantern.json"], taken!.Files.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_vsix_that_is_no_zip_holds_no_manifest_or_is_past_a_limit_is_refused_saying_why()
    {
        Assert.Null(VsCodeThemes.Vsix(Encoding.UTF8.GetBytes("no zip"), out string? notZip));
        Assert.Contains("no zip Passenger reads", notZip, StringComparison.Ordinal);
        Assert.Null(VsCodeThemes.Vsix(Vsix(("extension/readme.md", "hi")), out string? noManifest));
        Assert.Contains("holds no extension/package.json", noManifest, StringComparison.Ordinal);
        Assert.Null(VsCodeThemes.Vsix(Vsix([.. Enumerable.Range(0, VsCodeThemes.MostEntries + 1).Select(i => ($"extension/{i}.txt", string.Empty))]), out string? many));
        Assert.Contains($"more than the {VsCodeThemes.MostEntries} Passenger reads", many, StringComparison.Ordinal);
        VsCodeThemes.ExtensionFile files = VsCodeThemes.Vsix(Vsix(("extension/package.json", "{}"), ("extension/big.json", new string(' ', ThemeFiles.MostBytes + 1))), out _)!;
        Assert.Contains("more than the", files("big.json").Why, StringComparison.Ordinal);
        Assert.Contains("holds no gone.json", files("gone.json").Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_naming_no_publisher_and_name_is_refused()
    {
        ThemeRead<VsCodeExtension> read = VsCodeThemes.ReadManifest("package.json", Encoding.UTF8.GetBytes("""{"name": "x", "contributes": {}}"""));
        Assert.Null(read.Read);
        Assert.Contains("names no publisher and name", read.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void The_innermost_scope_a_rule_colours_wins_then_the_longer_selector_then_more_parents_then_the_later_rule()
    {
        VsCodeRule[] rules =
        [
            new(["string"], 0xFF000001, null),
            new(["string.quoted"], 0xFF000002, null),
            new(["source.json string.quoted"], 0xFF000003, null),
            new(["meta.structure"], 0xFF000004, null),
            new(["string.quoted"], 0xFF000005, "italic"),
            new(["comment - comment.block"], 0xFF000006, null),
        ];
        Assert.Equal(0xFF000003u, VsCodeScopes.Resolve(rules, ["source.json", "meta.structure", "string.quoted.double.json"]).Foreground);
        Assert.Equal(0xFF000005u, VsCodeScopes.Resolve(rules, ["source.cs", "string.quoted.double.cs"]).Foreground);
        Assert.Equal("italic", VsCodeScopes.Resolve(rules, ["source.cs", "string.quoted.double.cs"]).FontStyle);
        Assert.Equal(0xFF000001u, VsCodeScopes.Resolve(rules, ["source.cs", "string.interpolated.cs"]).Foreground);
        Assert.Equal(0xFF000004u, VsCodeScopes.Resolve(rules, ["source.cs", "meta.structure.cs", "punctuation.cs"]).Foreground);
        Assert.Equal(0xFF000006u, VsCodeScopes.Resolve(rules, ["source.cs", "comment.line.cs"]).Foreground);
        Assert.Null(VsCodeScopes.Resolve(rules, ["source.cs", "stringy"]).Foreground);
    }

    [Fact]
    public void Open_vsx_s_search_answer_lists_each_extension_with_its_downloads_rating_and_vsix()
    {
        OpenVsxPage page = OpenVsx.ReadPage(Encoding.UTF8.GetBytes("""
            {"offset": 0, "totalSize": 2083, "extensions": [
              {"namespace": "dracula-theme", "name": "theme-dracula", "version": "2.25.1", "displayName": "Dracula Theme Official",
               "downloadCount": 463364, "averageRating": 5.0, "reviewCount": 3,
               "files": {"download": "https://open-vsx.org/api/dracula-theme/theme-dracula/2.25.1/file/x.vsix"}},
              {"namespace": "someone", "name": "unrated", "version": "0.1.0", "downloadCount": 12, "files": {"download": "http://example.com/x.vsix"}},
              {"name": "no-namespace"}]}
            """), out string? why)!;
        Assert.Null(why);
        Assert.Equal(2083, page.Total);
        Assert.Equal(["dracula-theme.theme-dracula", "someone.unrated"], page.Extensions.Select(e => e.Id));
        OpenVsxEntry dracula = page.Extensions[0];
        Assert.Equal(("Dracula Theme Official", 463364L, 5.0, 3), (dracula.DisplayName, dracula.Downloads, dracula.Rating, dracula.Reviews));
        Assert.Equal("https://open-vsx.org/api/dracula-theme/theme-dracula/2.25.1/file/x.vsix", dracula.Download!.AbsoluteUri);
        Assert.Equal(("unrated", (double?)null), (page.Extensions[1].DisplayName, page.Extensions[1].Rating));
        Assert.Null(page.Extensions[1].Download);
        Assert.Null(OpenVsx.ReadPage(Encoding.UTF8.GetBytes("""{"error": "down"}"""), out string? none));
        Assert.Contains("no list of extensions", none, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", OpenVsxOrder.Downloads, 0, "api/-/search?category=Themes&sortBy=downloadCount&sortOrder=desc&size=24&offset=0")]
    [InlineData("one dark", OpenVsxOrder.Rating, 24, "api/-/search?query=one%20dark&category=Themes&sortBy=rating&sortOrder=desc&size=24&offset=24")]
    [InlineData(" nord ", OpenVsxOrder.Newest, 0, "api/-/search?query=nord&category=Themes&sortBy=timestamp&sortOrder=desc&size=24&offset=0")]
    public void A_search_asks_open_vsx_s_themes_category_in_its_order(string words, OpenVsxOrder order, int offset, string asked) =>
        Assert.Equal(asked, OpenVsx.SearchAddress(words, order, offset).PathAndQuery.TrimStart('/'));

    /// <summary>A zip of files by their paths and texts, as a .vsix is.</summary>
    private static byte[] Vsix(params (string Path, string Text)[] files)
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string text) in files)
            {
                using Stream entry = zip.CreateEntry(path, CompressionLevel.Fastest).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return bytes.ToArray();
    }
}

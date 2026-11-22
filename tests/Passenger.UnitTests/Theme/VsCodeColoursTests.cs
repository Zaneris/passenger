using System.Text;
using Passenger.App;
using static Passenger.App.ThemeColours;

namespace Passenger.UnitTests.Theme;

/// <summary>A VS Code colour theme made a Passenger theme of its one variant: its workbench colours on the tokens they
/// correspond to, its token and semantic token colours on the code's exactly where they read at WCAG AA and moved in
/// lightness just far enough where they do not, every colour token given, and a Passenger theme naming a base laid over it.</summary>
public class VsCodeColoursTests
{
    private static VsCodeTheme Theme(string json, bool dark = true) =>
        VsCodeThemes.ReadTheme("t.json", "Lantern", dark, _ => (Encoding.UTF8.GetBytes(json), null)).Read!;

    private static uint Colour(ThemeFile theme, string token) => ((ColourValue)theme.Both[token]).Argb;

    private static readonly string Lantern = """
        {
          "semanticHighlighting": true,
          "colors": {
            "editor.background": "#282A36", "editor.foreground": "#F8F8F2", "sideBar.background": "#21222C",
            "editorGroupHeader.tabsBackground": "#191A21", "editor.lineHighlightBackground": "#44475A75",
            "terminal.ansiRed": "#FF5555", "terminal.ansiGreen": "#50FA7B", "button.background": "#44475A",
            "titleBar.activeBackground": "#21222C", "list.activeSelectionBackground": "#44475A"
          },
          "tokenColors": [
            { "scope": "comment", "settings": { "foreground": "#6272A4", "fontStyle": "italic" } },
            { "scope": "keyword", "settings": { "foreground": "#FF79C6" } },
            { "scope": "string", "settings": { "foreground": "#F1FA8C" } },
            { "scope": "entity.name.function", "settings": { "foreground": "#50FA7B" } },
            { "scope": "entity.name.type", "settings": { "foreground": "#8BE9FD", "fontStyle": "italic" } }
          ],
          "semanticTokenColors": { "interface": "#BD93F9" }
        }
        """;

    [Fact]
    public void A_vs_code_themes_colours_become_Passengers_each_readable_where_it_is_drawn() => Cases.All(
        ("a dark theme shows dark alone its grounds and code colours taken as it gives them", A_dark_theme_shows_dark_alone_its_grounds_and_code_colours_taken_as_it_gives_them),
        ("a code colour too faint to read is moved in lightness to AA on the editor and its current line keeping its hue", A_code_colour_too_faint_to_read_is_moved_in_lightness_to_AA_on_the_editor_and_its_current_line_keeping_its_hue),
        ("every word and mark meets WCAG AA on what it is drawn on whatever the theme gives", () => Cases.Each<bool>(
        [
            true,
            false,
        ], Every_word_and_mark_meets_WCAG_AA_on_what_it_is_drawn_on_whatever_the_theme_gives)),
        ("as a code colour scheme a vs code theme gives only the code s colours its own variant s as drawn and the other s moved to read", As_a_code_colour_scheme_a_vs_code_theme_gives_only_the_code_s_colours_its_own_variant_s_as_drawn_and_the_other_s_moved_to_read));

    private void A_dark_theme_shows_dark_alone_its_grounds_and_code_colours_taken_as_it_gives_them()
    {
        ThemeFile theme = VsCodeColours.ToTheme(Theme(Lantern));
        Assert.Equal(("Lantern", ThemeShows.Dark), (theme.Name, theme.Shows));
        Assert.Empty(theme.Dark);
        Assert.Empty(theme.Light);
        Assert.Equal(0xFF282A36u, Colour(theme, "Editor.Background"));
        Assert.Equal(0xFF21222Cu, Colour(theme, "Panel"));
        Assert.Equal(0xFF191A21u, Colour(theme, "Frame"));
        Assert.Equal(0xFFF8F8F2u, Colour(theme, "Editor.Text"));
        Assert.Equal(Over(0x7544475A, 0xFF282A36), Colour(theme, "Editor.CurrentLine"));
        Assert.Equal(0xFFFF79C6u, Colour(theme, "Syntax.Keyword"));
        Assert.Equal(0xFFF1FA8Cu, Colour(theme, "Syntax.String"));
        Assert.Equal(0xFF50FA7Bu, Colour(theme, "Syntax.Method"));
        Assert.Equal(0xFF8BE9FDu, Colour(theme, "Syntax.Class"));
        Assert.Equal(0xFFBD93F9u, Colour(theme, "Syntax.Interface"));
        Assert.Equal(0xFF8BE9FDu, Colour(theme, "Syntax.Struct"));
        Assert.Equal(new TextStyleValue("Italic"), theme.Both["SyntaxStyle.Comment"]);
    }

    private void A_code_colour_too_faint_to_read_is_moved_in_lightness_to_AA_on_the_editor_and_its_current_line_keeping_its_hue()
    {
        ThemeFile theme = VsCodeColours.ToTheme(Theme(Lantern));
        uint comment = Colour(theme, "Syntax.Comment");
        Assert.True(Ratio(0xFF6272A4, 0xFF282A36) < 4.5, "The test's comment colour reads already");
        Assert.True(Ratio(comment, Colour(theme, "Editor.Background")) >= 4.5 && Ratio(comment, Colour(theme, "Editor.CurrentLine")) >= 4.5,
            $"The comment {comment:X8} does not read at AA");
        // Still blue-grey: blue above red and green.
        Assert.True((comment & 0xFF) > ((comment >> 16) & 0xFF) && (comment & 0xFF) > ((comment >> 8) & 0xFF), $"The comment {comment:X8} lost its hue");
    }

    private void Every_word_and_mark_meets_WCAG_AA_on_what_it_is_drawn_on_whatever_the_theme_gives(bool dark)
    {
        // A theme giving faint words and grounds too close to tell apart.
        string faint = dark
            ? """{"colors": {"editor.background": "#1E1E1E", "editor.foreground": "#444444", "sideBar.background": "#1E1E1E", "foreground": "#333333", "descriptionForeground": "#2A2A2A", "button.background": "#202020", "button.foreground": "#222222", "textLink.foreground": "#101060"}, "tokenColors": [{"scope": "comment", "settings": {"foreground": "#252525"}}]}"""
            : """{"colors": {"editor.background": "#FFFFFF", "editor.foreground": "#DDDDDD", "sideBar.background": "#FFFFFF", "foreground": "#EEEEEE", "descriptionForeground": "#F0F0F0", "button.background": "#FAFAFA", "button.foreground": "#FFFFFF", "textLink.foreground": "#E0E0FF"}, "tokenColors": [{"scope": "comment", "settings": {"foreground": "#F5F5F5"}}]}""";
        ThemeFile theme = VsCodeColours.ToTheme(Theme(faint, dark));
        Assert.Equal(dark ? ThemeShows.Dark : ThemeShows.Light, theme.Shows);
        List<string> below = [];
        foreach ((string fore, string ground, double least) in Pairs())
        {
            uint under = ground.StartsWith("TitleBar", StringComparison.Ordinal) ? Colour(theme, "TitleBar") : Colour(theme, "Panel");
            double ratio = Ratio(Colour(theme, fore), Over(Colour(theme, ground), under));
            if (ratio < least)
            {
                below.Add($"{fore} on {ground} is {ratio:0.00}:1");
            }
        }
        Assert.True(below.Count == 0, string.Join("\n", below));
        Assert.True(Math.Abs(CieLightness(Colour(theme, "Panel")) - CieLightness(Colour(theme, "Editor.Background"))) >= 2.5, "The tool windows' ground is the editor's");
    }

    [Fact]
    public void A_theme_kept_over_a_vs_code_theme_names_its_base_its_name_and_its_version_as_written() => Cases.All(
        ("a passenger theme names its vs code base and its own tokens win over the base s", A_passenger_theme_names_its_vs_code_base_and_its_own_tokens_win_over_the_base_s),
        ("a theme names a vs code theme beside its file and a code colour scheme may name one too", A_theme_names_a_vs_code_theme_beside_its_file_and_a_code_colour_scheme_may_name_one_too),
        ("a theme kept over a vs code theme is written as its file is read", () => Cases.Each<(string?, string?, string?, string?)>(
        [
            ("dracula-theme.theme-dracula", "Dracula", "2.25.1", null),
            (null, null, null, "vscode/dark.json"),
        ], row => A_theme_kept_over_a_vs_code_theme_is_written_as_its_file_is_read(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("a theme s name from a vs code theme s words is kept as written where a theme file holds it", () => Cases.Each<(string, string, string)>(
        [
            ("Dracula", " (Dracula Official)", "Dracula (Dracula Official)"),
            ("Line one\nline two", "", "Line one line two"),
        ], row => A_theme_s_name_from_a_vs_code_theme_s_words_is_kept_as_written_where_a_theme_file_holds_it(row.Item1, row.Item2, row.Item3))),
        ("a vs code base named wrongly refuses the theme saying why", () => Cases.Each<(string, string)>(
        [
            ("""{"vscode": "dracula"}""", "vscode is {\"id\": \"publisher.extension\""),
            ("""{"vscode": {"id": "no-dot", "theme": "Dracula"}}""", "it is an extension's Open VSX id"),
            ("""{"vscode": {"file": "../dark.json"}}""", "with no .."),
            ("""{"vscode": {"file": "dark.json", "id": "a.b"}}""", "given instead of an id"),
        ], row => A_vs_code_base_named_wrongly_refuses_the_theme_saying_why(row.Item1, row.Item2))));

    private void A_passenger_theme_names_its_vs_code_base_and_its_own_tokens_win_over_the_base_s()
    {
        Dictionary<string, ThemeTokenKind> kinds = new(StringComparer.Ordinal)
        {
            ["Accent"] = ThemeTokenKind.Colour, ["Frame"] = ThemeTokenKind.Colour, ["ControlRadius"] = ThemeTokenKind.Radius,
        };
        ThemeFile own = ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes("""
            {"format": 1, "name": "Mine", "vscode": {"id": "dracula-theme.theme-dracula", "theme": "Dracula", "version": "2.25.1"},
             "tokens": {"ControlRadius": 0}, "dark": {"Accent": "#123456"}}
            """), kinds).Read!;
        Assert.Equal(new VsCodeBase("dracula-theme.theme-dracula", "Dracula", "2.25.1"), own.Base);
        ThemeFile laid = ThemeFiles.Over(VsCodeColours.ToTheme(Theme(Lantern)), own);
        Assert.Equal(("Mine", ThemeShows.Dark), (laid.Name, laid.Shows));
        Assert.Equal(new ColourValue(0xFF123456, 1), laid.Dark["Accent"]);
        Assert.False(laid.Both.ContainsKey("Accent"), "The base's accent for both variants would win over the theme's own for Dark");
        Assert.True(laid.Light.ContainsKey("Accent"));
        Assert.Equal([0], ((SidesValue)laid.Both["ControlRadius"]).Numbers);
        Assert.Equal(0xFF191A21u, Colour(laid, "Frame"));
    }

    private void A_theme_names_a_vs_code_theme_beside_its_file_and_a_code_colour_scheme_may_name_one_too()
    {
        ThemeFile own = ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Mine", "vscode": {"file": "vscode/dark.json"}}"""),
            new Dictionary<string, ThemeTokenKind>()).Read!;
        Assert.Equal(new VsCodeBase(null, null, null, "vscode/dark.json"), own.Base);
        ThemeFile named = ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Mine", "vscode": {"file": "Theme.json"}}"""),
            new Dictionary<string, ThemeTokenKind>()).Read!;
        Assert.Equal(new VsCodeBase(null, null, null, "Theme.json"), named.Base);
        ThemeFile scheme = ThemeFiles.ReadCodeColours("Mine.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Mine", "vscode": {"id": "a.b", "theme": "T"}}"""),
            new Dictionary<string, ThemeTokenKind>()).Read!;
        Assert.Equal(new VsCodeBase("a.b", "T", null), scheme.Base);
    }

    private void As_a_code_colour_scheme_a_vs_code_theme_gives_only_the_code_s_colours_its_own_variant_s_as_drawn_and_the_other_s_moved_to_read()
    {
        ThemeFile scheme = VsCodeColours.ToScheme(Theme(Lantern));
        ThemeFile theme = VsCodeColours.ToTheme(Theme(Lantern));
        Assert.All(scheme.Dark.Keys.Concat(scheme.Light.Keys).Concat(scheme.Both.Keys), token => Assert.True(ThemeFiles.IsCode(token), $"{token} is not the code's"));
        Assert.Equal(theme.Both["Syntax.Keyword"], scheme.Dark["Syntax.Keyword"]);
        Assert.False(scheme.Dark.ContainsKey("Syntax.Brace1") || scheme.Dark.ContainsKey("Editor.Text"), "A scheme from a VS Code theme gives its token colours alone");
        uint light = ((ColourValue)scheme.Light["Syntax.Keyword"]).Argb;
        Assert.True(Ratio(light, 0xFFFFFFFF) >= 4.5 && Ratio(light, 0xFFECECEC) >= 4.5, $"The keyword under a light theme, {light:X8}, does not read");
        Assert.Equal(new TextStyleValue("Italic"), scheme.Both["SyntaxStyle.Comment"]);
    }

    private void A_theme_kept_over_a_vs_code_theme_is_written_as_its_file_is_read(string? id, string? theme, string? version, string? file)
    {
        VsCodeBase based = new(id, theme, version, file);
        ThemeRead<ThemeFile> read = ThemeFiles.ReadTheme("Kept.json", ThemeFiles.Wrapping("Dracula", based), new Dictionary<string, ThemeTokenKind>());
        Assert.True(read.Read is not null, $"The theme written over {based} does not read: {read.Refused}");
        Assert.Equal("Dracula", read.Read.Name);
        Assert.Equal(based, read.Read.Base);
        Assert.Empty(read.Problems);
    }

    private void A_theme_s_name_from_a_vs_code_theme_s_words_is_kept_as_written_where_a_theme_file_holds_it(string words, string after, string name) =>
        Assert.Equal(name, ThemeFiles.NameOf(words, after));

    private void A_vs_code_base_named_wrongly_refuses_the_theme_saying_why(string member, string why)
    {
        string json = $$"""{"format": 1, "name": "Mine", {{member[1..^1]}}}""";
        ThemeRead<ThemeFile> read = ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes(json), new Dictionary<string, ThemeTokenKind>());
        Assert.Null(read.Read);
        Assert.Contains(why, read.Refused, StringComparison.Ordinal);
    }

    /// <summary>The window's words and marks and what they are drawn on, as the window's rule tests hold every theme to them.</summary>
    private static IEnumerable<(string Fore, string Ground, double Least)> Pairs()
    {
        foreach (string ground in (string[])["Panel", "Frame", "Field", "Popup", "List", "Hover", "Selected", "RowSelected", "Editor.Background", "Banner", "Button", "Welcome.Tile"])
        {
            yield return ("Text", ground, 4.5);
            yield return ("TextMuted", ground, 4.5);
        }
        foreach (string ground in (string[])["Panel", "Frame", "Popup", "Editor.Background"])
        {
            yield return ("Link", ground, 4.5);
            yield return ("Error", ground, 4.5);
            yield return ("Warning", ground, 4.5);
            yield return ("Focus", ground, 3);
            yield return ("Icon", ground, 3);
        }
        yield return ("AccentText", "Accent", 4.5);
        yield return ("AccentText", "AccentHover", 4.5);
        yield return ("AccentText", "AccentPressed", 4.5);
        yield return ("Text", "ButtonHover", 4.5);
        yield return ("Text", "ButtonPressed", 4.5);
        yield return ("Menu.SelectedText", "Menu.Selected", 4.5);
        yield return ("Tab.ActiveText", "Tab.Active", 4.5);
        yield return ("TitleBar.Text", "TitleBar", 4.5);
        yield return ("Terminal.Foreground", "Terminal.Background", 4.5);
        foreach (string ground in (string[])["Editor.Background", "Editor.CurrentLine", "Editor.Selection", "Editor.ExecutionLine"])
        {
            yield return ("Editor.Text", ground, 4.5);
        }
        yield return ("Editor.LineNumber", "Editor.Gutter", 4.5);
        yield return ("Editor.Caret", "Editor.Background", 3);
        foreach (string kind in ThemePalettes.SyntaxKinds)
        {
            yield return ($"Syntax.{kind}", "Editor.Background", 4.5);
            yield return ($"Syntax.{kind}", "Editor.CurrentLine", 4.5);
        }
    }
}

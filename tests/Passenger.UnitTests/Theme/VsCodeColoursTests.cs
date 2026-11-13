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
    public void A_dark_theme_shows_dark_alone_its_grounds_and_code_colours_taken_as_it_gives_them()
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

    [Fact]
    public void A_code_colour_too_faint_to_read_is_moved_in_lightness_to_AA_on_the_editor_and_its_current_line_keeping_its_hue()
    {
        ThemeFile theme = VsCodeColours.ToTheme(Theme(Lantern));
        uint comment = Colour(theme, "Syntax.Comment");
        Assert.True(Ratio(0xFF6272A4, 0xFF282A36) < 4.5, "The test's comment colour reads already");
        Assert.True(Ratio(comment, Colour(theme, "Editor.Background")) >= 4.5 && Ratio(comment, Colour(theme, "Editor.CurrentLine")) >= 4.5,
            $"The comment {comment:X8} does not read at AA");
        // Still blue-grey: blue above red and green.
        Assert.True((comment & 0xFF) > ((comment >> 16) & 0xFF) && (comment & 0xFF) > ((comment >> 8) & 0xFF), $"The comment {comment:X8} lost its hue");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_word_and_mark_meets_WCAG_AA_on_what_it_is_drawn_on_whatever_the_theme_gives(bool dark)
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
    public void Every_kind_the_theme_leaves_uncoloured_takes_the_one_it_falls_back_on_or_the_palette_s()
    {
        ThemeFile theme = VsCodeColours.ToTheme(Theme("""{"colors": {"editor.background": "#000000"}, "tokenColors": [{"scope": "keyword", "settings": {"foreground": "#FF8000"}}]}"""));
        Assert.Equal(Colour(theme, "Syntax.Keyword"), Colour(theme, "Syntax.ControlKeyword"));
        Assert.Equal(Colour(theme, "Syntax.Keyword"), Colour(theme, "Syntax.Preprocessor"));
        foreach (string kind in ThemePalettes.SyntaxKinds)
        {
            Assert.True(theme.Both.ContainsKey($"Syntax.{kind}"), $"Syntax.{kind} is not given");
        }
    }

    [Fact]
    public void A_passenger_theme_names_its_vs_code_base_and_its_own_tokens_win_over_the_base_s()
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

    [Fact]
    public void Its_six_bracket_colours_are_carried_over_and_three_where_it_gives_three()
    {
        string[] six = ["#FFD700", "#DA70D6", "#179FFF", "#FF8C66", "#7FE07F", "#E0A0FF"];
        string colours = string.Join(", ", six.Select((c, i) => $"\"editorBracketHighlight.foreground{i + 1}\": \"{c}\""));
        ThemeFile theme = VsCodeColours.ToTheme(Theme($$$"""{"colors": {"editor.background": "#1E1E1E", {{{colours}}}}}"""));
        for (int level = 1; level <= 6; level++)
        {
            Assert.Equal(VsCodeThemes.Colour(six[level - 1])!.Value, Colour(theme, $"Syntax.Brace{level}"));
        }
        ThemeFile three = VsCodeColours.ToTheme(Theme("""{"colors": {"editor.background": "#1E1E1E", "editorBracketHighlight.foreground1": "#FFD700", "editorBracketHighlight.foreground5": "#7FE07F"}}"""));
        Assert.Equal(Colour(three, "Syntax.Brace1"), Colour(three, "Syntax.Brace3"));
        Assert.False(three.Both.ContainsKey("Syntax.Brace4") || three.Both.ContainsKey("Syntax.Brace5"), "A fifth colour after no fourth is a level given");
    }

    [Fact]
    public void Its_parameters_locals_namespaces_and_operators_take_its_colours_and_a_parameter_its_italics()
    {
        ThemeFile theme = VsCodeColours.ToTheme(Theme("""
            {"colors": {"editor.background": "#282A36", "editor.foreground": "#F8F8F2"},
             "tokenColors": [
               {"scope": "variable.parameter", "settings": {"foreground": "#FFB86C", "fontStyle": "italic"}},
               {"scope": "keyword.operator", "settings": {"foreground": "#FF79C6"}},
               {"scope": "entity.name.namespace", "settings": {"foreground": "#8BE9FD"}}]}
            """));
        Assert.Equal(0xFFFFB86Cu, Colour(theme, "Syntax.Parameter"));
        Assert.Equal(new TextStyleValue("Italic"), theme.Both["SyntaxStyle.Parameter"]);
        Assert.Equal(0xFFFF79C6u, Colour(theme, "Syntax.Operator"));
        Assert.Equal(0xFF8BE9FDu, Colour(theme, "Syntax.Namespace"));
        Assert.Equal(Colour(theme, "Editor.Text"), Colour(theme, "Syntax.Local"));
    }

    [Fact]
    public void A_file_leaving_out_the_plain_kinds_draws_them_in_its_own_plain_words_unless_it_is_laid_over_a_vs_code_theme()
    {
        Dictionary<string, ThemeTokenKind> kinds = new(StringComparer.Ordinal)
        {
            ["Editor.Text"] = ThemeTokenKind.Colour, ["Syntax.Parameter"] = ThemeTokenKind.Colour, ["Syntax.Local"] = ThemeTokenKind.Colour,
            ["Syntax.Namespace"] = ThemeTokenKind.Colour, ["Syntax.Operator"] = ThemeTokenKind.Colour,
        };
        ThemeFile own = ThemeFiles.ReadTheme("Old.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Old", "dark": {"Editor.Text": "#E2DACE"}, "light": {"Editor.Text": "#2A2520", "Syntax.Operator": "#AA0000"}}"""), kinds).Read!;
        Assert.Equal(new ColourValue(0xFFE2DACE, 1), own.Dark["Syntax.Parameter"]);
        Assert.Equal(new ColourValue(0xFF2A2520, 1), own.Light["Syntax.Namespace"]);
        Assert.False(own.Dark.ContainsKey("Syntax.Operator") || own.Light["Syntax.Operator"] != new ColourValue(0xFFAA0000, 1), "A kind the file gives is its own");
        ThemeFile based = ThemeFiles.ReadTheme("Over.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Over", "vscode": {"id": "a.b", "theme": "T"}, "dark": {"Editor.Text": "#E2DACE"}}"""), kinds).Read!;
        Assert.False(based.Dark.ContainsKey("Syntax.Parameter"), "Over a VS Code theme a parameter is the base's");
    }

    [Theory]
    [InlineData("""{"vscode": "dracula"}""", "vscode is {\"id\": \"publisher.extension\"")]
    [InlineData("""{"vscode": {"id": "no-dot", "theme": "Dracula"}}""", "it is an extension's Open VSX id")]
    [InlineData("""{"vscode": {"id": "a.b", "theme": "Dracula", "version": "1 2"}}""", "it is the extension's version")]
    [InlineData("""{"vscode": {"id": "a.b"}}""", "gives no theme")]
    public void A_vs_code_base_named_wrongly_refuses_the_theme_saying_why(string member, string why)
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

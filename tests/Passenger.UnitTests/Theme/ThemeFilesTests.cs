using System.Text;
using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>Theme format 1 as read: each kind of value a token takes, a theme's tokens for both variants and for each, a
/// code colour scheme's limit to the code, a source's index; what is passed over and named (a member or token this Passenger
/// does not know); and each refusal, which takes the whole file and says the file, the token and what was expected.</summary>
public class ThemeFilesTests
{
    private static readonly Dictionary<string, ThemeTokenKind> Tokens = new(StringComparer.Ordinal)
    {
        ["Frame"] = ThemeTokenKind.Colour,
        ["Accent"] = ThemeTokenKind.Colour,
        ["DropZone"] = ThemeTokenKind.Colour,
        ["TitleBar"] = ThemeTokenKind.Gradient,
        ["PopupShadow"] = ThemeTokenKind.Shadow,
        ["ControlRadius"] = ThemeTokenKind.Radius,
        ["TabMargin"] = ThemeTokenKind.Thickness,
        ["TabStripHeight"] = ThemeTokenKind.Number,
        ["Editor.Background"] = ThemeTokenKind.Colour,
        ["Syntax.Keyword"] = ThemeTokenKind.Colour,
        ["SyntaxStyle.Comment"] = ThemeTokenKind.TextStyle,
        ["Diff.Added"] = ThemeTokenKind.Colour,
    };

    private static ThemeRead<ThemeFile> Theme(string json) => ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes(json), Tokens);

    private static ThemeFile Read(string json)
    {
        ThemeRead<ThemeFile> read = Theme(json);
        Assert.True(read.Read is not null, read.Refused);
        return read.Read;
    }

    private static string Refused(string json)
    {
        ThemeRead<ThemeFile> read = Theme(json);
        Assert.Null(read.Read);
        return read.Refused!;
    }

    private static string Dark(string token, string value) => $$$"""{"format": 1, "name": "Mine", "dark": {"{{{token}}}": {{{value}}}}}""";

    [Fact]
    public void A_theme_gives_its_name_description_and_tokens_for_both_variants_and_for_each()
    {
        ThemeFile theme = Read("""
            {"format": 1, "name": "Mine", "description": "Quiet greys.",
             "tokens": {"ControlRadius": 8},
             "dark": {"Frame": "#151311"},
             "light": {"Frame": "#EEE8DC", "Accent": "#0F766E"}}
            """);
        Assert.Equal("Mine", theme.Name);
        Assert.Equal("Quiet greys.", theme.Description);
        Assert.Equal([8], ((SidesValue)theme.Both["ControlRadius"]).Numbers);
        Assert.Equal(new ColourValue(0xFF151311, 1), theme.Dark["Frame"]);
        Assert.Equal(new ColourValue(0xFFEEE8DC, 1), theme.Light["Frame"]);
        Assert.Equal(2, theme.Light.Count);
    }

    [Fact]
    public void A_theme_may_leave_out_its_description_and_any_section_whose_tokens_stay_the_defaults()
    {
        ThemeFile theme = Read("""{"format": 1, "name": "Mine"}""");
        Assert.Equal(string.Empty, theme.Description);
        Assert.Empty(theme.Both);
        Assert.Empty(theme.Dark);
        Assert.Empty(theme.Light);
    }

    [Theory]
    [InlineData("\"#3D78F5\"", 0xFF3D78F5u)]
    [InlineData("\"#3d78f5\"", 0xFF3D78F5u)]
    [InlineData("\"#1F000000\"", 0x1F000000u)]
    public void A_colour_is_six_hex_digits_opaque_or_eight_with_its_alpha_first(string value, uint argb) =>
        Assert.Equal(new ColourValue(argb, 1), Read(Dark("Frame", value)).Dark["Frame"]);

    [Fact]
    public void A_colour_may_be_drawn_at_an_opacity() =>
        Assert.Equal(new ColourValue(0xFF2F6BF2, 0.22), Read(Dark("DropZone", """{"colour": "#2F6BF2", "opacity": 0.22}""")).Dark["DropZone"]);

    [Fact]
    public void A_gradient_goes_from_its_start_to_its_end_through_its_stops()
    {
        GradientValue gradient = Assert.IsType<GradientValue>(Read(Dark("TitleBar",
            """{"start": [0, 0], "end": [1, 0.5], "stops": [{"offset": 0, "colour": "#1B3270"}, {"offset": 1, "colour": "#0E4552"}]}""")).Dark["TitleBar"]);
        Assert.Equal((0d, 0d, 1d, 0.5d), (gradient.StartX, gradient.StartY, gradient.EndX, gradient.EndY));
        Assert.Equal([new GradientStopValue(0, 0xFF1B3270), new GradientStopValue(1, 0xFF0E4552)], gradient.Stops);
    }

    [Fact]
    public void A_token_whose_default_is_a_gradient_also_takes_a_plain_colour() =>
        Assert.Equal(new ColourValue(0xFF151311, 1), Read(Dark("TitleBar", "\"#151311\"")).Dark["TitleBar"]);

    [Theory]
    [InlineData("0 6 20 0 #80000000")]
    [InlineData("0 1 2 0 #1F000000, inset 0 0 1 #FF000000")]
    [InlineData("2 2 #FF000000")]
    [InlineData("none")]
    public void Shadows_are_written_as_avalonia_writes_them(string text) =>
        Assert.Equal(new ShadowValue(text), Read(Dark("PopupShadow", $"\"{text}\"")).Dark["PopupShadow"]);

    [Fact]
    public void A_radius_or_thickness_is_one_number_for_every_side_or_four()
    {
        ThemeFile theme = Read("""{"format": 1, "name": "Mine", "tokens": {"ControlRadius": [10, 10, 0, 0], "TabMargin": [1, 6, 1, -2], "TabStripHeight": 40}}""");
        Assert.Equal([10, 10, 0, 0], ((SidesValue)theme.Both["ControlRadius"]).Numbers);
        Assert.Equal([1, 6, 1, -2], ((SidesValue)theme.Both["TabMargin"]).Numbers);
        Assert.Equal(new NumberValue(40), theme.Both["TabStripHeight"]);
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("Bold")]
    [InlineData("Italic")]
    [InlineData("Bold Italic")]
    public void A_syntax_style_is_one_of_its_four_words(string words) =>
        Assert.Equal(new TextStyleValue(words), Read(Dark("SyntaxStyle.Comment", $"\"{words}\"")).Dark["SyntaxStyle.Comment"]);

    [Theory]
    [InlineData("Frame", "\"red\"", "a colour")]
    [InlineData("Frame", "\"#12345\"", "a colour")]
    [InlineData("Frame", "\"#GG0000\"", "a colour")]
    [InlineData("Frame", "12", "a colour")]
    [InlineData("Accent", """{"start": [0, 0], "end": [1, 0], "stops": [{"offset": 0, "colour": "#000000"}, {"offset": 1, "colour": "#FFFFFF"}]}""", "a colour")]
    [InlineData("DropZone", """{"colour": "#2F6BF2", "opacity": 1.5}""", "a colour")]
    [InlineData("DropZone", """{"opacity": 0.5}""", "a colour")]
    [InlineData("TitleBar", """{"start": [0, 0], "end": [1, 0], "stops": [{"offset": 0, "colour": "#000000"}]}""", "a gradient")]
    [InlineData("TitleBar", """{"start": [0, 0], "end": [1, 0], "stops": [{"offset": 1, "colour": "#000000"}, {"offset": 0, "colour": "#FFFFFF"}]}""", "a gradient")]
    [InlineData("TitleBar", """{"end": [1, 0], "stops": [{"offset": 0, "colour": "#000000"}, {"offset": 1, "colour": "#FFFFFF"}]}""", "a gradient")]
    [InlineData("PopupShadow", "\"0 6 20 0 black\"", "shadows")]
    [InlineData("PopupShadow", "\"0 #80000000\"", "shadows")]
    [InlineData("PopupShadow", "\"0 6 -20 0 #80000000\"", "shadows")]
    [InlineData("ControlRadius", "-1", "a radius")]
    [InlineData("ControlRadius", "[1, 2]", "a radius")]
    [InlineData("ControlRadius", "\"8\"", "a radius")]
    [InlineData("TabMargin", "[1, 2, 3]", "a thickness")]
    [InlineData("TabMargin", "5000", "a thickness")]
    [InlineData("TabStripHeight", "\"40\"", "a number")]
    [InlineData("SyntaxStyle.Comment", "\"italic\"", "\"Normal\", \"Bold\", \"Italic\" or \"Bold Italic\"")]
    public void A_value_of_the_wrong_kind_refuses_the_whole_file_naming_the_token_and_what_it_takes(string token, string value, string expected)
    {
        string why = Refused(Dark(token, value));
        Assert.StartsWith($"Mine.json gives {token} in dark as ", why, StringComparison.Ordinal);
        Assert.Contains($"where it takes {expected}", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_this_Passenger_does_not_know_is_passed_over_and_named()
    {
        ThemeRead<ThemeFile> read = Theme("""{"format": 1, "name": "Mine", "dark": {"Fram": "#000000", "Passenger.Frame": "#000000", "Frame": "#111111"}}""");
        Assert.NotNull(read.Read);
        Assert.Equal(["Frame"], read.Read.Dark.Keys);
        Assert.Equal(["Mine.json: Fram in dark is no token this Passenger knows, so it was passed over",
            "Mine.json: Passenger.Frame in dark is no token this Passenger knows (a token is named without \"Passenger.\"), so it was passed over"], read.Problems);
    }

    [Fact]
    public void A_member_this_Passenger_does_not_know_is_passed_over_and_named_wherever_it_is()
    {
        ThemeRead<ThemeFile> read = Theme("""
            {"format": 1, "name": "Mine", "background": {"image": "sky.png"},
             "dark": {"DropZone": {"colour": "#2F6BF2", "opacity": 0.2, "blend": "multiply"}}}
            """);
        Assert.Equal(new ColourValue(0xFF2F6BF2, 0.2), read.Read!.Dark["DropZone"]);
        Assert.Equal(["Mine.json: background is no member of a theme this Passenger knows, so it was passed over",
            "Mine.json: blend is no member of DropZone in dark, a colour this Passenger knows, so it was passed over"], read.Problems);
    }

    [Theory]
    [InlineData("{\"format\": 1, \"name\": \"Mine\"", "is not JSON")]
    [InlineData("[1, 2]", "is no JSON object")]
    [InlineData("{\"name\": \"Mine\"}", "names no format")]
    [InlineData("{\"format\": 2, \"name\": \"Mine\"}", "is format 2, which this Passenger does not read: it reads format 1")]
    [InlineData("{\"format\": \"1\", \"name\": \"Mine\"}", "is format \"1\"")]
    [InlineData("{\"format\": 1}", "gives no name")]
    [InlineData("{\"format\": 1, \"name\": \"  \"}", "gives name as")]
    [InlineData("{\"format\": 1, \"name\": \"Mine\", \"description\": 4}", "gives description as 4")]
    [InlineData("{\"format\": 1, \"name\": \"Mine\", \"dark\": [\"Frame\"]}", "gives dark as")]
    [InlineData("{\"format\": 1, \"name\": \"Mine\", \"dark\": {\"Frame\": \"#000000\", \"Frame\": \"#111111\"}}", "is not JSON")]
    [InlineData("{\"format\": 1, \"name\": \"Mine\", \"tokens\": {\"Frame\": \"#000000\"}, \"light\": {\"Frame\": \"#111111\"}}", "gives Frame in tokens and in light")]
    public void A_file_that_is_no_theme_is_refused_whole_saying_why(string json, string why)
    {
        string refused = Refused(json);
        Assert.StartsWith("Mine.json ", refused, StringComparison.Ordinal);
        Assert.Contains(why, refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_saved_with_a_byte_order_mark_reads_as_one_without() =>
        Assert.Equal("Mine", ThemeFiles.ReadTheme("Mine.json", (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"format": 1, "name": "Mine"}""")], Tokens).Read?.Name);

    [Fact]
    public void A_file_nested_deeper_than_a_theme_is_refused() =>
        Assert.Contains("is not JSON", Refused(Dark("Frame", string.Concat(Enumerable.Repeat("[", 20)) + string.Concat(Enumerable.Repeat("]", 20)))), StringComparison.Ordinal);

    [Fact]
    public void A_file_larger_than_a_theme_may_be_is_refused_unread()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadTheme("Mine.json", new byte[ThemeFiles.MostBytes + 1], Tokens);
        Assert.Null(read.Read);
        Assert.Equal($"Mine.json is {ThemeFiles.MostBytes + 1} bytes, more than the {ThemeFiles.MostBytes} a theme file may hold", read.Refused);
    }

    [Fact]
    public void A_code_colour_scheme_gives_the_codes_colours_for_either_variant()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadCodeColours("Code.json", Encoding.UTF8.GetBytes("""
            {"format": 1, "name": "Code", "dark": {"Editor.Background": "#1E1F22", "Syntax.Keyword": "#CF8E6D", "SyntaxStyle.Comment": "Normal", "Diff.Added": "#294436"}}
            """), Tokens);
        Assert.Equal(4, read.Read!.Dark.Count);
        Assert.Empty(read.Read.Light);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void A_code_colour_scheme_giving_a_colour_of_the_window_is_refused()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadCodeColours("Code.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Code", "light": {"Frame": "#FFFFFF"}}"""), Tokens);
        Assert.Null(read.Read);
        Assert.StartsWith("Code.json gives Frame, which is not the code's", read.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_lists_its_themes_and_code_colour_schemes_by_paths_inside_the_source()
    {
        ThemeRead<ThemeIndex> read = ThemeFiles.ReadIndex(ThemeFiles.IndexFile, Encoding.UTF8.GetBytes("""
            {"format": 1, "themes": ["Harbour.json", "anime/Sakura/theme.json"], "codeColours": ["Code.json"], "extra": true}
            """));
        Assert.Equal(["Harbour.json", "anime/Sakura/theme.json"], read.Read!.Themes);
        Assert.Equal(["Code.json"], read.Read.CodeColours);
        Assert.Equal(["passenger-themes.json: extra is no member of an index this Passenger knows, so it was passed over"], read.Problems);
    }

    [Theory]
    [InlineData("../Other.json")]
    [InlineData("themes/../../Other.json")]
    [InlineData("/etc/Theme.json")]
    [InlineData("C:/Theme.json")]
    [InlineData("https://example.com/Theme.json")]
    [InlineData("themes\\\\Theme.json")]
    [InlineData("themes//Theme.json")]
    [InlineData("Theme.png")]
    [InlineData("")]
    public void An_index_entry_that_is_no_path_inside_the_source_refuses_the_index(string path)
    {
        ThemeRead<ThemeIndex> read = ThemeFiles.ReadIndex(ThemeFiles.IndexFile, Encoding.UTF8.GetBytes($$"""{"format": 1, "themes": ["{{path}}"]}"""));
        Assert.Null(read.Read);
        Assert.Contains("each is a path inside the source", read.Refused, StringComparison.Ordinal);
    }
}

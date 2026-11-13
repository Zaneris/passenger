using System.Buffers.Binary;
using System.Text;
using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>A theme's members beyond its tokens, as read: its background picture for each variant (its file, opacity, tint,
/// places, fit and alignment, and a variant's own), its fonts, and the code's tokens for a language over its own, in themes
/// and code colour schemes; each refusal, which takes the whole file; and a picture's kind and size read from its header
/// alone, refused past its bounds before anything decodes it.</summary>
public class ThemeBeyondTests
{
    private static readonly Dictionary<string, ThemeTokenKind> Tokens = new(StringComparer.Ordinal)
    {
        ["Frame"] = ThemeTokenKind.Colour,
        ["Syntax.Keyword"] = ThemeTokenKind.Colour,
        ["SyntaxStyle.Comment"] = ThemeTokenKind.TextStyle,
    };

    static ThemeBeyondTests() => ThemeFiles.LanguageIds = new HashSet<string>(["csharp", "json", "xaml"], StringComparer.Ordinal);

    private static ThemeRead<ThemeFile> Theme(string members) =>
        ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes($$"""{"format": 1, "name": "Mine", {{members}}}"""), Tokens);

    private static ThemeFile Read(string members)
    {
        ThemeRead<ThemeFile> read = Theme(members);
        Assert.True(read.Read is not null, read.Refused);
        return read.Read;
    }

    private static string Refused(string members)
    {
        ThemeRead<ThemeFile> read = Theme(members);
        Assert.Null(read.Read);
        return read.Refused!;
    }

    [Fact]
    public void A_background_names_its_picture_beside_the_theme_and_takes_Passengers_own_way_of_drawing_it_where_it_says_no_other()
    {
        ThemeFile theme = Read("""
            "background": {"image": "pictures/dusk.webp"}
            """);
        BackgroundValue expected = new("pictures/dusk.webp", 0.15, 0, 0, BackgroundPlaces.Editor, BackgroundFit.Cover, 0.5, 0.5);
        Assert.Equal(expected, theme.DarkBackground);
        Assert.Equal(expected, theme.LightBackground);
        Assert.Equal(["pictures/dusk.webp"], theme.Images);
    }

    [Fact]
    public void A_background_gives_its_opacity_tint_places_fit_and_alignment_and_a_variant_what_differs_or_none()
    {
        ThemeFile theme = Read("""
            "background": {"image": "dusk.webp", "opacity": 0.25, "tint": {"colour": "#101830", "strength": 0.2},
              "places": ["editor", "toolWindows", "frame", "welcome"], "fit": "contain", "align": "bottom right",
              "light": {"image": "day.jpg", "fit": "tile", "align": "top"}}
            """);
        Assert.Equal(new BackgroundValue("dusk.webp", 0.25, 0xFF101830, 0.2,
            BackgroundPlaces.Editor | BackgroundPlaces.ToolWindows | BackgroundPlaces.Frame | BackgroundPlaces.Welcome, BackgroundFit.Contain, 1, 1), theme.DarkBackground);
        Assert.Equal(theme.DarkBackground! with { Image = "day.jpg", Fit = BackgroundFit.Tile, AlignX = 0.5, AlignY = 0 }, theme.LightBackground);
        Assert.Equal(["dusk.webp", "day.jpg"], theme.Images);
        Assert.Null(Read("""
            "background": {"image": "dusk.webp", "light": "none"}
            """).LightBackground);
    }

    [Theory]
    [InlineData("left", 0, 0.5)]
    [InlineData("centre", 0.5, 0.5)]
    [InlineData("top left", 0, 0)]
    [InlineData("right bottom", 1, 1)]
    public void A_backgrounds_alignment_is_words_for_each_axis_the_middle_where_it_names_none(string align, double x, double y)
    {
        BackgroundValue background = Read($$"""
            "background": {"image": "a.png", "align": "{{align}}"}
            """).DarkBackground!;
        Assert.Equal((x, y), (background.AlignX, background.AlignY));
    }

    [Theory]
    [InlineData("\"background\": {\"opacity\": 0.2}", "gives a background with no image")]
    [InlineData("\"background\": {\"image\": \"../outside.png\"}", "a picture's file beside the theme's")]
    [InlineData("\"background\": {\"image\": \"https://example.com/a.png\"}", "a picture's file beside the theme's")]
    [InlineData("\"background\": {\"image\": \"a.gif\"}", "ending .png, .jpg, .jpeg or .webp")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"opacity\": 0.6}", "so the code over the picture stays readable")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"tint\": {\"colour\": \"pink\"}}", "a tint is {\"colour\": \"#RRGGBB\", \"strength\": 0 to 1}")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"tint\": {\"colour\": \"#FFFFFF\", \"strength\": 2}}", "a tint is")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"places\": []}", "places is a list of one or more of \"editor\", \"toolWindows\", \"frame\" or \"welcome\"")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"places\": [\"menus\"]}", "lists \"menus\" in background's places")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"fit\": \"stretch\"}", "it is \"cover\", \"contain\", \"tile\" or \"centre\"")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"align\": \"left right\"}", "gives background's align under dark")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"align\": \"centre top\"}", "gives background's align")]
    [InlineData("\"background\": {\"image\": \"a.png\", \"light\": \"dim\"}", "it is an object of what differs under light, or \"none\"")]
    [InlineData("\"background\": \"a.png\"", "background is an object naming its image")]
    public void A_background_that_cannot_be_drawn_as_written_refuses_the_theme_saying_why(string members, string why)
    {
        string refused = Refused(members);
        Assert.StartsWith("Mine.json ", refused, StringComparison.Ordinal);
        Assert.Contains(why, refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backgrounds_member_this_Passenger_does_not_know_is_passed_over_and_named()
    {
        ThemeRead<ThemeFile> read = Theme("""
            "background": {"image": "a.png", "blur": 4}
            """);
        Assert.NotNull(read.Read?.DarkBackground);
        Assert.Equal(["Mine.json: blur is no member of background this Passenger knows, so it was passed over"], read.Problems);
    }

    [Fact]
    public void Fonts_give_the_codes_family_and_size_and_the_windows_family()
    {
        Assert.Equal(new ThemeFonts("Fira Code", 14, "Inter"), Read("""
            "fonts": {"code": {"family": "Fira Code", "size": 14}, "window": {"family": "Inter"}}
            """).Fonts);
        Assert.Equal(new ThemeFonts(null, 15, null), Read("""
            "fonts": {"code": {"size": 15}}
            """).Fonts);
        Assert.Equal(ThemeFonts.None, Read("""
            "description": "No fonts of its own."
            """).Fonts);
    }

    [Theory]
    [InlineData("\"fonts\": {\"code\": {\"size\": 100}}", "a size is a number of points, 6 to 48")]
    [InlineData("\"fonts\": {\"code\": {\"family\": \"\"}}", "its family is words on one line")]
    [InlineData("\"fonts\": {\"window\": \"Inter\"}", "gives fonts' window as")]
    [InlineData("\"fonts\": [\"Inter\"]", "gives fonts as")]
    public void Fonts_of_the_wrong_kind_refuse_the_theme(string members, string why) => Assert.Contains(why, Refused(members), StringComparison.Ordinal);

    [Fact]
    public void A_language_gives_the_codes_tokens_for_both_variants_and_for_each_and_one_this_Passenger_does_not_know_is_passed_over()
    {
        ThemeRead<ThemeFile> read = Theme("""
            "languages": {"csharp": {"tokens": {"SyntaxStyle.Comment": "Italic"}, "dark": {"Syntax.Keyword": "#FF7AB8"}},
                          "cobol": {"dark": {"Syntax.Keyword": "#000000"}}}
            """);
        LanguageColours csharp = read.Read!.Languages["csharp"];
        Assert.Equal(new TextStyleValue("Italic"), csharp.Both["SyntaxStyle.Comment"]);
        Assert.Equal(new ColourValue(0xFFFF7AB8, 1), csharp.Dark["Syntax.Keyword"]);
        Assert.Empty(csharp.Light);
        Assert.False(read.Read.Languages.ContainsKey("cobol"));
        Assert.Equal(["Mine.json: cobol in languages is no language this Passenger knows, so it was passed over"], read.Problems);
    }

    [Theory]
    [InlineData("\"languages\": {\"csharp\": {\"dark\": {\"Frame\": \"#000000\"}}}", "gives Frame for csharp, which is not the code's")]
    [InlineData("\"languages\": {\"csharp\": {\"dark\": {\"Syntax.Keyword\": \"blue\"}}}", "gives Syntax.Keyword in dark of languages' csharp as \"blue\"")]
    [InlineData("\"languages\": {\"csharp\": {\"tokens\": {\"Syntax.Keyword\": \"#000000\"}, \"dark\": {\"Syntax.Keyword\": \"#111111\"}}}", "in tokens and in dark of languages' csharp")]
    [InlineData("\"languages\": [\"csharp\"]", "gives languages as")]
    public void A_language_whose_tokens_are_not_the_codes_or_of_the_wrong_kind_refuses_the_theme(string members, string why) =>
        Assert.Contains(why, Refused(members), StringComparison.Ordinal);

    [Fact]
    public void A_code_colour_scheme_gives_languages_but_no_background_or_fonts_which_it_passes_over()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadCodeColours("Code.json", Encoding.UTF8.GetBytes("""
            {"format": 1, "name": "Code", "languages": {"json": {"light": {"Syntax.Keyword": "#0000FF"}}}, "background": {"image": "a.png"}, "fonts": {}}
            """), Tokens);
        Assert.Equal(new ColourValue(0xFF0000FF, 1), read.Read!.Languages["json"].Light["Syntax.Keyword"]);
        Assert.Null(read.Read.DarkBackground);
        Assert.Equal(ThemeFonts.None, read.Read.Fonts);
        Assert.Equal(2, read.Problems.Count);
    }

    [Fact]
    public void A_pictures_kind_and_size_come_from_its_header_as_PNG_JPEG_and_each_kind_of_WebP_write_them()
    {
        Assert.Equal((1600, 1000), ThemeImages.SizeOf(Png(1600, 1000)));
        Assert.Equal((640, 480), ThemeImages.SizeOf(Jpeg(640, 480)));
        Assert.Equal((300, 200), ThemeImages.SizeOf(WebP("VP8 ", 300, 200)));
        Assert.Equal((301, 201), ThemeImages.SizeOf(WebP("VP8L", 301, 201)));
        Assert.Equal((5000, 3000), ThemeImages.SizeOf(WebP("VP8X", 5000, 3000)));
        Assert.Null(ThemeImages.SizeOf("GIF89a, no picture this reads, padded out"u8));
    }

    [Fact]
    public void A_picture_larger_than_a_background_may_be_is_refused_before_anything_decodes_it()
    {
        Assert.Null(ThemeImages.WhyNot("dusk.png", Png(3840, 2160)));
        Assert.Equal("huge.png is 8193×10 pixels, more than a background picture may be: at most 8192 along a side and 16 million in all",
            ThemeImages.WhyNot("huge.png", Png(8193, 10)));
        Assert.Contains("is 5000×5000 pixels", ThemeImages.WhyNot("bomb.webp", WebP("VP8X", 5000, 5000)), StringComparison.Ordinal);
        Assert.Equal("a.gif is no PNG, JPEG or WebP picture", ThemeImages.WhyNot("a.gif", "GIF89a"u8));
        Assert.StartsWith($"big.png is {ThemeFiles.MostImageBytes + 1} bytes", ThemeImages.WhyNot("big.png", new byte[ThemeFiles.MostImageBytes + 1]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dusk/theme.json", "dusk.webp", "Dusk/dusk.webp")]
    [InlineData("theme.json", "pictures/dusk.webp", "pictures/dusk.webp")]
    public void A_picture_is_read_beside_its_themes_file_in_the_source(string theme, string image, string path) =>
        Assert.Equal(path, ThemeImages.Beside(theme, image));

    private static byte[] Png(int width, int height)
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 0, 0, 0, 0, 0, 8, 2, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        return bytes;
    }

    private static byte[] Jpeg(int width, int height)
    {
        // An APP0 segment to step over, then the frame's start with its lines and samples per line.
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0, 0, 0, 0, 3, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(13), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(15), (ushort)width);
        return bytes;
    }

    private static byte[] WebP(string chunk, int width, int height)
    {
        byte[] bytes = new byte[40];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
        switch (chunk)
        {
            case "VP8X":
                Three(bytes.AsSpan(24), width - 1);
                Three(bytes.AsSpan(27), height - 1);
                break;
            case "VP8L":
                bytes[20] = 0x2F;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)(width - 1) | ((uint)(height - 1) << 14));
                break;
            default:
                (bytes[23], bytes[24], bytes[25]) = (0x9D, 0x01, 0x2A);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), (ushort)height);
                break;
        }
        return bytes;

        static void Three(Span<byte> at, int value) => (at[0], at[1], at[2]) = ((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }
}

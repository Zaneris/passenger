using System.Buffers.Binary;
using System.Text;
using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>A theme's members beyond its tokens, as read: its background picture for each variant (its file, opacity, tint,
/// places, fit and alignment, and a variant's own), its fonts, the code's tokens for a language over its own, in themes
/// and code colour schemes, and the carried theme it is laid over; each refusal, which takes the whole file; and a picture's
/// kind and size read from its header alone, refused past its bounds before anything decodes it.</summary>
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
    public void A_themes_background_fonts_and_languages_read_as_written() => Cases.All(
        ("a background names its picture beside the theme and takes Passengers own way of drawing it where it says no other", A_background_names_its_picture_beside_the_theme_and_takes_Passengers_own_way_of_drawing_it_where_it_says_no_other),
        ("a background gives its opacity tint places fit and alignment and a variant what differs or none", A_background_gives_its_opacity_tint_places_fit_and_alignment_and_a_variant_what_differs_or_none),
        ("fonts give the codes family and size and the windows family", Fonts_give_the_codes_family_and_size_and_the_windows_family),
        ("a language gives the codes tokens for both variants and for each and one this Passenger does not know is passed over", A_language_gives_the_codes_tokens_for_both_variants_and_for_each_and_one_this_Passenger_does_not_know_is_passed_over),
        ("a code colour scheme gives languages but no background or fonts which it passes over", A_code_colour_scheme_gives_languages_but_no_background_or_fonts_which_it_passes_over),
        ("a theme names a carried theme as its base and laid over it keeps all the base gives that it does not", A_theme_names_a_carried_theme_as_its_base_and_laid_over_it_keeps_all_the_base_gives_that_it_does_not),
        ("an icons saturation brightness and tint work on its colour as CSSs filters do keeping its alpha", An_icons_saturation_brightness_and_tint_work_on_its_colour_as_CSSs_filters_do_keeping_its_alpha),
        ("a picture is read beside its themes file in the source", () => Cases.Each<(string, string, string)>(
        [
            ("Dusk/theme.json", "dusk.webp", "Dusk/dusk.webp"),
            ("theme.json", "pictures/dusk.webp", "pictures/dusk.webp"),
        ], row => A_picture_is_read_beside_its_themes_file_in_the_source(row.Item1, row.Item2, row.Item3))));

    private void A_background_names_its_picture_beside_the_theme_and_takes_Passengers_own_way_of_drawing_it_where_it_says_no_other()
    {
        ThemeFile theme = Read("""
            "background": {"image": "pictures/dusk.webp"}
            """);
        BackgroundValue expected = new("pictures/dusk.webp", 0.15, 0, 0, BackgroundPlaces.Editor, BackgroundFit.Cover, 0.5, 0.5);
        Assert.Equal(expected, theme.DarkBackground);
        Assert.Equal(expected, theme.LightBackground);
        Assert.Equal(["pictures/dusk.webp"], theme.Images);
    }

    private void A_background_gives_its_opacity_tint_places_fit_and_alignment_and_a_variant_what_differs_or_none()
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

    [Fact]
    public void A_background_font_or_language_that_cannot_be_drawn_refuses_the_theme_and_a_picture_too_large_is_never_decoded() => Cases.All(
        ("a background that cannot be drawn as written refuses the theme saying why", () => Cases.Each<(string, string)>(
        [
            ("\"background\": {\"opacity\": 0.2}", "gives a background with no image"),
            ("\"background\": {\"image\": \"../outside.png\"}", "a picture's file beside the theme's"),
            ("\"background\": {\"image\": \"https://example.com/a.png\"}", "a picture's file beside the theme's"),
            ("\"background\": {\"image\": \"a.gif\"}", "ending .png, .jpg, .jpeg or .webp"),
            ("\"background\": {\"image\": \"a.png\", \"opacity\": 0.6}", "so the code over the picture stays readable"),
            ("\"background\": {\"image\": \"a.png\", \"places\": []}", "places is a list of one or more of \"editor\", \"toolWindows\", \"frame\" or \"welcome\""),
            ("\"background\": {\"image\": \"a.png\", \"fit\": \"stretch\"}", "it is \"cover\", \"contain\", \"tile\" or \"centre\""),
            ("\"background\": \"a.png\"", "background is an object naming its image"),
        ], row => A_background_that_cannot_be_drawn_as_written_refuses_the_theme_saying_why(row.Item1, row.Item2))),
        ("fonts of the wrong kind refuse the theme", () => Cases.Each<(string, string)>(
        [
            ("\"fonts\": {\"code\": {\"size\": 100}}", "a size is a number of points, 6 to 48"),
            ("\"fonts\": [\"Inter\"]", "gives fonts as"),
        ], row => Fonts_of_the_wrong_kind_refuse_the_theme(row.Item1, row.Item2))),
        ("a language whose tokens are not the codes or of the wrong kind refuses the theme", () => Cases.Each<(string, string)>(
        [
            ("\"languages\": {\"csharp\": {\"dark\": {\"Frame\": \"#000000\"}}}", "gives Frame for csharp, which is not the code's"),
            ("\"languages\": {\"csharp\": {\"tokens\": {\"Syntax.Keyword\": \"#000000\"}, \"dark\": {\"Syntax.Keyword\": \"#111111\"}}}", "in tokens and in dark of languages' csharp"),
            ("\"languages\": [\"csharp\"]", "gives languages as"),
        ], row => A_language_whose_tokens_are_not_the_codes_or_of_the_wrong_kind_refuses_the_theme(row.Item1, row.Item2))),
        ("a base Passenger does not carry refuses the theme", () => Cases.Each<(string, string)>(
        [
            ("\"base\": \"Harbour\"", "(Harbour is in Passenger's themes repository, not carried): a base is a theme Passenger carries, \"Passenger\", \"Cubicle\" or \"High Contrast\""),
            ("\"base\": \"cubicle\"", "gives base as \"cubicle\": a base is a theme Passenger carries"),
            ("\"base\": [\"Cubicle\"]", "gives base as"),
            ("\"base\": \"Cubicle\", \"vscode\": {\"file\": \"dark.json\"}", "gives both base and vscode"),
        ], row => A_base_Passenger_does_not_carry_refuses_the_theme(row.Item1, row.Item2))),
        ("a pictures kind and size come from its header as PNG JPEG and each kind of WebP write them", A_pictures_kind_and_size_come_from_its_header_as_PNG_JPEG_and_each_kind_of_WebP_write_them),
        ("a picture larger than a background may be is refused before anything decodes it", A_picture_larger_than_a_background_may_be_is_refused_before_anything_decodes_it));

    private void A_background_that_cannot_be_drawn_as_written_refuses_the_theme_saying_why(string members, string why)
    {
        string refused = Refused(members);
        Assert.StartsWith("Mine.json ", refused, StringComparison.Ordinal);
        Assert.Contains(why, refused, StringComparison.Ordinal);
    }

    private void Fonts_give_the_codes_family_and_size_and_the_windows_family()
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

    private void Fonts_of_the_wrong_kind_refuse_the_theme(string members, string why) => Assert.Contains(why, Refused(members), StringComparison.Ordinal);

    private void A_language_gives_the_codes_tokens_for_both_variants_and_for_each_and_one_this_Passenger_does_not_know_is_passed_over()
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

    private void A_language_whose_tokens_are_not_the_codes_or_of_the_wrong_kind_refuses_the_theme(string members, string why) =>
        Assert.Contains(why, Refused(members), StringComparison.Ordinal);

    private void A_base_Passenger_does_not_carry_refuses_the_theme(string members, string why) => Assert.Contains(why, Refused(members), StringComparison.Ordinal);

    private void A_code_colour_scheme_gives_languages_but_no_background_or_fonts_which_it_passes_over()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadCodeColours("Code.json", Encoding.UTF8.GetBytes("""
            {"format": 1, "name": "Code", "languages": {"json": {"light": {"Syntax.Keyword": "#0000FF"}}}, "background": {"image": "a.png"}, "fonts": {}}
            """), Tokens);
        Assert.Equal(new ColourValue(0xFF0000FF, 1), read.Read!.Languages["json"].Light["Syntax.Keyword"]);
        Assert.Null(read.Read.DarkBackground);
        Assert.Equal(ThemeFonts.None, read.Read.Fonts);
        Assert.Equal(2, read.Problems.Count);
    }

    private void A_theme_names_a_carried_theme_as_its_base_and_laid_over_it_keeps_all_the_base_gives_that_it_does_not()
    {
        ThemeFile under = Read("""
            "tokens": {"Frame": "#101010"}, "dark": {"Syntax.Keyword": "#202020"}, "fonts": {"code": {"family": "Fira Code", "size": 12}},
            "background": {"image": "under.png"}, "languages": {"csharp": {"tokens": {"Syntax.Keyword": "#303030"}}, "json": {"dark": {"Syntax.Keyword": "#404040"}}}
            """);
        ThemeFile over = Read("""
            "base": "Cubicle", "light": {"Frame": "#EEEEEE"}, "fonts": {"window": {"family": "Inter"}},
            "languages": {"csharp": {"dark": {"Syntax.Keyword": "#505050"}}}
            """);
        Assert.Equal("Cubicle", over.BuiltInBase);
        Assert.Null(Read("""
            "base": "Passenger"
            """).BuiltInBase);
        ThemeFile laid = ThemeFiles.Over(under, over);
        Assert.Equal(new ColourValue(0xFF101010, 1), laid.Dark["Frame"]);
        Assert.Equal(new ColourValue(0xFFEEEEEE, 1), laid.Light["Frame"]);
        Assert.Equal(new ColourValue(0xFF202020, 1), laid.Dark["Syntax.Keyword"]);
        Assert.Equal(new ThemeFonts("Fira Code", 12, "Inter"), laid.Fonts);
        Assert.Equal("under.png", laid.DarkBackground?.Image);
        Assert.Equal(new ColourValue(0xFF505050, 1), laid.Languages["csharp"].Dark["Syntax.Keyword"]);
        Assert.Equal(new ColourValue(0xFF303030, 1), laid.Languages["csharp"].Light["Syntax.Keyword"]);
        Assert.Equal(new ColourValue(0xFF404040, 1), laid.Languages["json"].Dark["Syntax.Keyword"]);
        Assert.True(ThemeFiles.CodeOf(laid) is { Both.Count: 0 } code && code.Dark.ContainsKey("Syntax.Keyword") && !code.Dark.ContainsKey("Frame"),
            "A code colour scheme over a carried theme takes more than the theme's code");
    }

    private void An_icons_saturation_brightness_and_tint_work_on_its_colour_as_CSSs_filters_do_keeping_its_alpha()
    {
        Assert.Equal(0x80FF0000u, IconLook.AsDrawn.Apply(0x80FF0000));
        Assert.Equal(0xFF363636u, new IconLook(0, 1, 0).Apply(0xFFFF0000));
        Assert.Equal(0xFF404040u, new IconLook(1, 0.5, 0).Apply(0xFF808080));
        Assert.Equal(0xC0800000u, new IconLook(1, 1, 0x80FF0000).Apply(0xC0000000));
        Assert.Equal(0xFFFF0000u, new IconLook(3, 1, 0).Apply(0xFFFF4040));
        // Saturation first, then brightness, then the tint, each clamped: a grey halved, then washed a quarter toward white.
        Assert.Equal(0xFF545454u, new IconLook(0, 0.5, 0x40FFFFFF).Apply(0xFFFF0000));
    }

    private void A_pictures_kind_and_size_come_from_its_header_as_PNG_JPEG_and_each_kind_of_WebP_write_them()
    {
        Assert.Equal((1600, 1000), ThemeImages.SizeOf(Png(1600, 1000)));
        Assert.Equal((640, 480), ThemeImages.SizeOf(Jpeg(640, 480)));
        Assert.Equal((300, 200), ThemeImages.SizeOf(WebP("VP8 ", 300, 200)));
        Assert.Equal((301, 201), ThemeImages.SizeOf(WebP("VP8L", 301, 201)));
        Assert.Equal((5000, 3000), ThemeImages.SizeOf(WebP("VP8X", 5000, 3000)));
        Assert.Null(ThemeImages.SizeOf("GIF89a, no picture this reads, padded out"u8));
    }

    private void A_picture_larger_than_a_background_may_be_is_refused_before_anything_decodes_it()
    {
        Assert.Null(ThemeImages.WhyNot("dusk.png", Png(3840, 2160)));
        Assert.Equal("huge.png is 8193×10 pixels, more than a background picture may be: at most 8192 along a side and 16 million in all",
            ThemeImages.WhyNot("huge.png", Png(8193, 10)));
        Assert.Contains("is 5000×5000 pixels", ThemeImages.WhyNot("bomb.webp", WebP("VP8X", 5000, 5000)), StringComparison.Ordinal);
        Assert.Equal("a.gif is no PNG, JPEG or WebP picture", ThemeImages.WhyNot("a.gif", "GIF89a"u8));
        Assert.Equal("big.png is 9.1 MB, more than the 8 MB a background picture may hold", ThemeImages.WhyNot("big.png", new byte[9_500_000]));
    }

    private void A_picture_is_read_beside_its_themes_file_in_the_source(string theme, string image, string path) =>
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

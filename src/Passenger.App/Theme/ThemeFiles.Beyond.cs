using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Where a theme's background picture is drawn: one or more of the window's areas.</summary>
[Flags]
public enum BackgroundPlaces
{
    /// <summary>Nowhere.</summary>
    None = 0,

    /// <summary>Over each editor's text, fitted to it.</summary>
    Editor = 1,

    /// <summary>Over each tool window's page, fitted to it.</summary>
    ToolWindows = 2,

    /// <summary>Over the window's frame (the toolbar, the stripes, the editor tabs' strip and the status bar), fitted to the
    /// window, so its parts show one picture.</summary>
    Frame = 4,

    /// <summary>Over the Welcome screen, fitted to it.</summary>
    Welcome = 8,
}

/// <summary>How a background picture fills its area.</summary>
public enum BackgroundFit
{
    /// <summary>Scaled to cover the whole area, its overflow cut off.</summary>
    Cover,

    /// <summary>Scaled to fit inside the area whole.</summary>
    Contain,

    /// <summary>At its own size, repeated across the area from its aligned place.</summary>
    Tile,

    /// <summary>At its own size, once.</summary>
    Centre,
}

/// <summary>A theme's background picture for one variant: its file beside the theme's, how much of it shows (0 to
/// <see cref="ThemeFiles.MostOpacity"/>), a colour washed over it at a strength (0 none, 1 the colour alone), where it is
/// drawn, how it fits and where it is aligned (0 left or top, 0.5 the middle, 1 right or bottom).</summary>
public sealed record BackgroundValue(string Image, double Opacity, uint TintArgb, double TintStrength, BackgroundPlaces Places, BackgroundFit Fit,
    double AlignX, double AlignY);

/// <summary>The fonts a theme gives: the code's family and size and the window's family, each null where it gives none.</summary>
public sealed record ThemeFonts(string? CodeFamily, double? CodeSize, string? WindowFamily)
{
    /// <summary>No fonts of the theme's own.</summary>
    public static ThemeFonts None { get; } = new(null, null, null);
}

/// <summary>The code's tokens a theme or scheme gives one language: for both variants and for Dark or Light alone.</summary>
public sealed record LanguageColours(IReadOnlyDictionary<string, TokenValue> Both, IReadOnlyDictionary<string, TokenValue> Dark,
    IReadOnlyDictionary<string, TokenValue> Light);

/// <summary>A theme's members beyond its tokens: <c>fonts</c>, <c>background</c>, <c>languages</c>, <c>layout</c>, <c>icons</c>,
/// <c>iconTheme</c>, and its base, <c>vscode</c> or <c>base</c> (docs/themes.md).</summary>
public static partial class ThemeFiles
{
    /// <summary>The most of a background picture that shows: every glyph keeps at least the rest of its difference from its
    /// ground, whatever the picture holds, as the picture is drawn over both alike.</summary>
    public const double MostOpacity = 0.3;

    /// <summary>The most a background picture's file may hold.</summary>
    public const int MostImageBytes = 8 * 1024 * 1024;

    /// <summary>The most pixels a background picture may have along either side.</summary>
    public const int MostImageSide = 8192;

    /// <summary>The most pixels a background picture may have in all, so a small file never decodes into a huge bitmap.</summary>
    public const long MostImagePixels = 4096L * 4096;

    /// <summary>The smallest size of the code's font, in points.</summary>
    public const double LeastFontSize = 6;

    /// <summary>The largest size of the code's font, in points.</summary>
    public const double MostFontSize = 48;

    /// <summary>The most an icon's SVG may hold.</summary>
    public const int MostIconBytes = 256 * 1024;

    private const double OwnOpacity = 0.15;
    private const int LongestLanguage = 40;

    private static readonly string[] Beyond = ["fonts", "background", "languages", "layout", "icons", Member.IconTheme, Member.VsCode, Member.Base];
    private static readonly string[] PictureTypes = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly (string Word, BackgroundPlaces Place)[] PlaceWords =
        [("editor", BackgroundPlaces.Editor), ("toolWindows", BackgroundPlaces.ToolWindows), ("frame", BackgroundPlaces.Frame), ("welcome", BackgroundPlaces.Welcome)];
    private static readonly (string Word, BackgroundFit Fit)[] FitWords =
        [("cover", BackgroundFit.Cover), ("contain", BackgroundFit.Contain), ("tile", BackgroundFit.Tile), ("centre", BackgroundFit.Centre)];

    /// <summary>The keys of the icons a theme may give in Passenger's place, as the App's icon sets name them; a key outside
    /// them is passed over and named. Empty, any key shaped as one (<c>Icon.Run</c>, <c>FileIcon.CSharp</c>) is taken.</summary>
    public static IReadOnlySet<string> IconNames { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The language ids a theme may give the code's colours for, as the App's languages name them; a language
    /// outside them is passed over and named. Empty, any id is taken.</summary>
    public static IReadOnlySet<string> LanguageIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    private static ThemeFonts Fonts(string file, JsonElement? fonts, List<string> problems)
    {
        if (fonts is null)
        {
            return ThemeFonts.None;
        }
        if (fonts.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives fonts as {ThemeJson.Shown(fonts.Value)}: fonts is {{\"code\": {{\"family\": …, \"size\": …}}, \"window\": {{\"family\": …}}}}");
        }
        PassOver(file, fonts.Value, ["code", "window"], "fonts", problems);
        JsonElement? code = Holder(fonts.Value, "code", "fonts");
        JsonElement? window = Holder(fonts.Value, "window", "fonts");
        PassOver(file, code ?? default, ["family", "size"], "fonts' code", problems);
        PassOver(file, window ?? default, ["family"], "fonts' window", problems);
        double? size = null;
        if (JsonMembers.Value(code, "size") is { } given)
        {
            size = Number(given, LeastFontSize, MostFontSize)
                ?? throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"gives fonts' code size as {ThemeJson.Shown(given)}: a size is a number of points, {LeastFontSize} to {MostFontSize}"));
        }
        return new ThemeFonts(Family(code, "code"), size, Family(window, "window"));

        static string? Family(JsonElement? holder, string what) =>
            holder is { } held && JsonMembers.Value(held, "family") is not null ? Words(held, "family", LongestName, needed: true) : null;
    }

    private static (BackgroundValue? Dark, BackgroundValue? Light) Background(string file, JsonElement? background, List<string> problems)
    {
        if (background is null)
        {
            return (null, null);
        }
        if (background.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives background as {ThemeJson.Shown(background.Value)}: background is an object naming its image beside the theme's file");
        }
        string[] members = ["image", "opacity", "tint", "places", "fit", "align"];
        PassOver(file, background.Value, [.. members, "dark", "light"], "background", problems);
        BackgroundValue? Variant(string name)
        {
            JsonElement? own = JsonMembers.Value(background.Value, name);
            if (own is { ValueKind: JsonValueKind.String } none && none.GetString() == "none")
            {
                return null;
            }
            if (own is { } given && given.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"gives background's {name} as {ThemeJson.Shown(given)}: it is an object of what differs under {name}, or \"none\"");
            }
            PassOver(file, own ?? default, members, $"background's {name}", problems);
            JsonElement? Of(string member) => JsonMembers.Value(own, member) ?? JsonMembers.Value(background.Value, member);
            return Picture(file, name, Of, problems);
        }
        BackgroundValue? dark = Variant("dark");
        BackgroundValue? light = Variant("light");
        if (dark is null && light is null && JsonMembers.Value(background.Value, "image") is null)
        {
            throw new FormatException("gives a background with no image: background's image is a picture's file beside the theme's (.png, .jpg or .webp)");
        }
        return (dark, light);
    }

    private static BackgroundValue? Picture(string file, string variant, Func<string, JsonElement?> of, List<string> problems)
    {
        if (of("image") is not { } image)
        {
            return null;
        }
        if (image.ValueKind != JsonValueKind.String || !Inside(image.GetString()!, PictureTypes))
        {
            throw new FormatException($"gives background's image under {variant} as {ThemeJson.Shown(image)}: it is a picture's file beside the theme's, "
                + "its folders joined by /, with no .., ending .png, .jpg, .jpeg or .webp");
        }
        double opacity = OwnOpacity;
        if (of("opacity") is { } shown)
        {
            opacity = Number(shown, 0, MostOpacity)
                ?? throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"gives background's opacity under {variant} as {ThemeJson.Shown(shown)}: it is 0 to {MostOpacity}, so the code over the picture stays readable"));
        }
        (uint tint, double strength) = (0, 0);
        if (of("tint") is { } washed)
        {
            PassOver(file, washed, ["colour", "strength"], "background's tint", problems);
            if (washed.ValueKind != JsonValueKind.Object || JsonMembers.Text(washed, "colour") is not { } colour || Hex(colour) is not { } argb
                || (JsonMembers.Value(washed, "strength") is not { } given ? 0.5 : Number(given, 0, 1)) is not { } how)
            {
                throw new FormatException($"gives background's tint under {variant} as {ThemeJson.Shown(washed)}: a tint is {{\"colour\": \"#RRGGBB\", \"strength\": 0 to 1}}");
            }
            (tint, strength) = (argb, how);
        }
        BackgroundPlaces places = BackgroundPlaces.Editor;
        if (of("places") is { } where)
        {
            places = BackgroundPlaces.None;
            if (where.ValueKind != JsonValueKind.Array || where.GetArrayLength() == 0)
            {
                throw new FormatException($"gives background's places under {variant} as {ThemeJson.Shown(where)}: places is a list of one or more of {Listed(PlaceWords.Select(p => p.Word))}");
            }
            foreach (JsonElement item in where.EnumerateArray())
            {
                int found = item.ValueKind == JsonValueKind.String ? Array.FindIndex(PlaceWords, p => p.Word == item.GetString()) : -1;
                if (found < 0)
                {
                    throw new FormatException($"lists {ThemeJson.Shown(item)} in background's places under {variant}: a place is {Listed(PlaceWords.Select(p => p.Word))}");
                }
                places |= PlaceWords[found].Place;
            }
        }
        BackgroundFit fit = BackgroundFit.Cover;
        if (of("fit") is { } fills)
        {
            int found = fills.ValueKind == JsonValueKind.String ? Array.FindIndex(FitWords, f => f.Word == fills.GetString()) : -1;
            fit = found >= 0 ? FitWords[found].Fit
                : throw new FormatException($"gives background's fit under {variant} as {ThemeJson.Shown(fills)}: it is {Listed(FitWords.Select(f => f.Word))}");
        }
        (double x, double y) = (0.5, 0.5);
        if (of("align") is { } align)
        {
            (x, y) = Aligned(align.ValueKind == JsonValueKind.String ? align.GetString()! : null)
                ?? throw new FormatException($"gives background's align under {variant} as {ThemeJson.Shown(align)}: it is \"centre\", or \"top\", \"bottom\", \"left\", \"right\" "
                    + "or one of each, \"bottom right\"");
        }
        return new BackgroundValue(image.GetString()!, opacity, tint, strength, places, fit, x, y);
    }

    /// <summary>Where words such as <c>bottom right</c> align a picture: each axis given at most once, the middle where not.</summary>
    private static (double X, double Y)? Aligned(string? words)
    {
        if (words is null)
        {
            return null;
        }
        double? x = null;
        double? y = null;
        string[] parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2)
        {
            return null;
        }
        foreach (string part in parts)
        {
            switch (part)
            {
                case "left" when x is null: x = 0; break;
                case "right" when x is null: x = 1; break;
                case "top" when y is null: y = 0; break;
                case "bottom" when y is null: y = 1; break;
                case "centre" when parts.Length == 1: return (0.5, 0.5);
                default: return null;
            }
        }
        return (x ?? 0.5, y ?? 0.5);
    }

    /// <summary>The icons a theme gives in Passenger's place under Dark and under Light, each key's SVG beside the theme's
    /// file, the same under both or <c>{"dark": …, "light": …}</c>, either of which may be left out.</summary>
    private static (Dictionary<string, string>? Dark, Dictionary<string, string>? Light) Icons(string file, JsonElement? icons, List<string> problems)
    {
        if (icons is null)
        {
            return (null, null);
        }
        if (icons.Value.ValueKind != JsonValueKind.Object || icons.Value.EnumerateObject().Count() > MostEntries)
        {
            throw new FormatException($"gives icons as {ThemeJson.Shown(icons.Value)}: icons is an object of an icon's key (\"FileIcon.CSharp\", \"Icon.Run\") and its SVG "
                + $"beside the theme's file, at most {Numbers.Said(MostEntries)}");
        }
        Dictionary<string, string> dark = new(StringComparer.Ordinal);
        Dictionary<string, string> light = new(StringComparer.Ordinal);
        foreach (JsonProperty icon in icons.Value.EnumerateObject())
        {
            if (!(IconNames.Count == 0 ? IconKey().IsMatch(icon.Name) : IconNames.Contains(icon.Name)))
            {
                problems.Add($"{file}: {icon.Name} in icons is no icon this Passenger draws, so it was passed over");
                continue;
            }
            if (icon.Value.ValueKind == JsonValueKind.Object)
            {
                PassOver(file, icon.Value, ["dark", "light"], $"icons' {icon.Name}", problems);
                foreach ((string variant, Dictionary<string, string> into) in ((string, Dictionary<string, string>)[])[("dark", dark), ("light", light)])
                {
                    if (JsonMembers.Value(icon.Value, variant) is { } given)
                    {
                        into[icon.Name] = Svg(icon.Name, $" under {variant}", given);
                    }
                }
                continue;
            }
            dark[icon.Name] = light[icon.Name] = Svg(icon.Name, string.Empty, icon.Value);
        }
        return (dark, light);

        static string Svg(string key, string variant, JsonElement value) =>
            value.ValueKind == JsonValueKind.String && Inside(value.GetString()!, [".svg"]) ? value.GetString()!
            : throw new FormatException($"gives icons' {key}{variant} as {ThemeJson.Shown(value)}: an icon is an SVG beside the theme's file, its folders joined by /, "
                + "with no .., ending .svg, or {\"dark\": …, \"light\": …}");
    }

    private static Dictionary<string, LanguageColours> Languages(string file, JsonElement? languages, IReadOnlyDictionary<string, ThemeTokenKind> tokens, bool plain,
        List<string> problems)
    {
        Dictionary<string, LanguageColours> read = new(StringComparer.Ordinal);
        if (languages is null)
        {
            return read;
        }
        if (languages.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives languages as {ThemeJson.Shown(languages.Value)}: languages is an object of a language's id (\"csharp\") and its code's tokens");
        }
        foreach (JsonProperty language in languages.Value.EnumerateObject())
        {
            if (language.Name.Length > LongestLanguage || language.Name.Any(char.IsControl) || (LanguageIds.Count > 0 && !LanguageIds.Contains(language.Name)))
            {
                problems.Add($"{file}: {language.Name} in languages is no language this Passenger knows, so it was passed over");
                continue;
            }
            if (language.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"gives languages' {language.Name} as {ThemeJson.Shown(language.Value)}: a language's are tokens, dark and light, as a theme's");
            }
            PassOver(file, language.Value, Sections, $"languages' {language.Name}", problems);
            Dictionary<string, TokenValue>[] sections = TokenSections(file, language.Value, tokens, code: true, language.Name, plain, problems);
            read[language.Name] = new LanguageColours(sections[0], sections[1], sections[2]);
        }
        return read;
    }

    /// <summary>The theme Passenger carries a file names as its <c>base</c>, or null where it names none or Passenger's own,
    /// which is under every theme; refused where it names another or a VS Code theme too.</summary>
    private static string? BuiltInBase(JsonElement root)
    {
        if (JsonMembers.Value(root, Member.Base) is not { } named)
        {
            return null;
        }
        string[] carried = [ThemeSource.Own, .. ThemeSource.Carried];
        string? name = named.ValueKind == JsonValueKind.String ? named.GetString() : null;
        if (name is null || !carried.Contains(name, StringComparer.Ordinal))
        {
            string moved = name is not null && ThemeSource.FormerlyCarried.Contains(name, StringComparer.Ordinal) ? $" ({name} is in Passenger's themes repository, not carried)" : string.Empty;
            throw new FormatException($"gives base as {ThemeJson.Shown(named)}{moved}: a base is a theme Passenger carries, {Listed(carried)}");
        }
        if (JsonMembers.Value(root, Member.VsCode) is not null)
        {
            throw new FormatException("gives both base and vscode: a theme is laid over one base, Passenger's or a VS Code theme");
        }
        return name == ThemeSource.Own ? null : name;
    }

    private static VsCodeBase? Base(string file, JsonElement? named, List<string> problems) =>
        Extension(file, named, Member.VsCode, "a VS Code theme's JSON", problems);

    /// <summary>The VS Code file icon theme a theme draws its files and folders with under Dark and under Light: one for both,
    /// or <c>{"dark": …, "light": …}</c>, either of which may be left out.</summary>
    private static (VsCodeBase? Dark, VsCodeBase? Light) IconTheme(string file, JsonElement? named, List<string> problems)
    {
        if (named is { ValueKind: JsonValueKind.Object } given && (JsonMembers.Value(given, "dark") is not null || JsonMembers.Value(given, "light") is not null))
        {
            PassOver(file, given, ["dark", "light"], Member.IconTheme, problems);
            return (IconThemeOf("dark"), IconThemeOf("light"));
        }
        VsCodeBase? both = IconThemeOf(null);
        return (both, both);

        VsCodeBase? IconThemeOf(string? variant) =>
            Extension(file, variant is null ? named : JsonMembers.Value(named!.Value, variant), variant is null ? Member.IconTheme : $"{Member.IconTheme}'s {variant}",
                "a VS Code file icon theme's JSON", problems);
    }

    /// <summary>A VS Code extension's theme a theme names under <paramref name="member"/>: by its extension's Open VSX id, its
    /// label (an icon theme's id too) and the version pinned, or by its JSON's file beside the theme's.</summary>
    private static VsCodeBase? Extension(string file, JsonElement? named, string member, string json, List<string> problems)
    {
        if (named is null)
        {
            return null;
        }
        if (named.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives {member} as {ThemeJson.Shown(named.Value)}: {member} is {{\"id\": \"publisher.extension\", \"theme\": \"its label\"}}, its version optional, "
                + $"or {{\"file\": \"{json} beside the theme's\"}}");
        }
        PassOver(file, named.Value, [Member.Id, Member.Theme, Member.Version, Member.File], member, problems);
        if (JsonMembers.Value(named.Value, Member.File) is { } beside)
        {
            if (beside.ValueKind != JsonValueKind.String || !Inside(beside.GetString()!, [".json"]) || JsonMembers.Value(named.Value, Member.Id) is not null)
            {
                throw new FormatException($"gives {member}'s file as {ThemeJson.Shown(beside)}: it is {json} beside the theme's file, its folders joined by /, "
                    + "with no .., ending .json, given instead of an id");
            }
            return new VsCodeBase(null, null, null, beside.GetString());
        }
        string id = Words(named.Value, Member.Id, LongestName * 4, needed: true);
        if (!ExtensionId().IsMatch(id))
        {
            throw new FormatException($"gives {member}'s id as \"{id}\": it is an extension's Open VSX id, publisher.extension");
        }
        string theme = Words(named.Value, Member.Theme, LongestName * 2, needed: true);
        string? version = JsonMembers.Value(named.Value, Member.Version) is null ? null : Words(named.Value, Member.Version, LongestName, needed: true);
        if (version is not null && !IsVersion(version))
        {
            throw new FormatException($"gives {member}'s version as \"{version}\": it is the extension's version, as 2.25.1");
        }
        return new VsCodeBase(id, theme, version);
    }

    /// <summary>A theme laid over a VS Code theme and giving nothing of its own, written as <see cref="Base"/> reads it: format
    /// 1, its name (one <see cref="NameOf"/> made), and its base by its extension's id, its label and the version pinned where
    /// there is one, or by its file beside the theme's.</summary>
    public static byte[] Wrapping(string name, VsCodeBase based)
    {
        using MemoryStream written = new();
        using (Utf8JsonWriter json = new(written, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber(Member.Format, Format);
            json.WriteString(Member.Name, name);
            json.WriteStartObject(Member.VsCode);
            if (based.File is { } file)
            {
                json.WriteString(Member.File, file);
            }
            else
            {
                json.WriteString(Member.Id, based.Id);
                json.WriteString(Member.Theme, based.Theme);
                if (based.Version is { } version)
                {
                    json.WriteString(Member.Version, version);
                }
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return written.ToArray();
    }

    /// <summary>A theme's name of some words and what follows them (<c>" (Extension)"</c>), as a theme file holds one: on one
    /// line, the words cut short with "…" where both are longer than its longest name, what follows kept whole where the
    /// words keep half of it, so a name with something after it is never the words' own.</summary>
    public static string NameOf(string words, string after = "")
    {
        if (words.Any(char.IsControl) || after.Any(char.IsControl))
        {
            return NameOf(OneLine(words), OneLine(after));
        }
        if (words.Length + after.Length <= LongestName)
        {
            return words + after;
        }
        string name = string.Concat(Cut(words, Math.Max(LongestName / 2, LongestName - 1 - after.Length)), after);
        return name.Length <= LongestName ? name : Cut(name, LongestName - 1);

        static string OneLine(string text) => new([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);

        // Never half of a character written as two.
        static string Cut(string text, int kept) =>
            text.Length <= kept ? text : string.Concat(text.AsSpan(0, char.IsHighSurrogate(text[kept - 1]) ? kept - 1 : kept), "…");
    }

    /// <summary>Whether words are an extension's version as a theme pins it, as 2.25.1.</summary>
    public static bool IsVersion(string words) => VersionWords().IsMatch(words);

    /// <summary>The members a theme over a VS Code theme gives, as <see cref="Wrapping"/> writes them and its read takes them.</summary>
    private static class Member
    {
        public const string Format = "format";
        public const string Name = "name";
        public const string VsCode = "vscode";
        public const string Id = "id";
        public const string Theme = "theme";
        public const string Version = "version";
        public const string File = "file";
        public const string Base = "base";
        public const string IconTheme = "iconTheme";
    }

    /// <summary>A theme laid over another, as over its base: the one under giving every token the one over leaves out, and
    /// its fonts, pictures, layout, languages' tokens and icons where the one over gives none; the one over its name, words
    /// and what it gives; a token the one over gives one variant alone keeps the one under's value for the other.</summary>
    public static ThemeFile Over(ThemeFile under, ThemeFile over)
    {
        (Dictionary<string, TokenValue> both, Dictionary<string, TokenValue> dark, Dictionary<string, TokenValue> light) =
            Laid(under.Both, under.Dark, under.Light, over.Both, over.Dark, over.Light);
        Dictionary<string, LanguageColours> languages = new(under.Languages, StringComparer.Ordinal);
        foreach ((string language, LanguageColours given) in over.Languages)
        {
            (Dictionary<string, TokenValue> Both, Dictionary<string, TokenValue> Dark, Dictionary<string, TokenValue> Light) laid = languages.TryGetValue(language, out LanguageColours? beneath)
                ? Laid(beneath.Both, beneath.Dark, beneath.Light, given.Both, given.Dark, given.Light)
                : (new(given.Both, StringComparer.Ordinal), new(given.Dark, StringComparer.Ordinal), new(given.Light, StringComparer.Ordinal));
            languages[language] = new LanguageColours(laid.Both, laid.Dark, laid.Light);
        }
        bool pictured = over.DarkBackground is not null || over.LightBackground is not null;
        return over with
        {
            Both = both,
            Dark = dark,
            Light = light,
            Shows = under.Shows,
            Fonts = new ThemeFonts(over.Fonts.CodeFamily ?? under.Fonts.CodeFamily, over.Fonts.CodeSize ?? under.Fonts.CodeSize, over.Fonts.WindowFamily ?? under.Fonts.WindowFamily),
            DarkBackground = pictured ? over.DarkBackground : under.DarkBackground,
            LightBackground = pictured ? over.LightBackground : under.LightBackground,
            Languages = languages,
            Layout = over.Layout ?? under.Layout,
            DarkIcons = Merged(under.DarkIcons, over.DarkIcons),
            LightIcons = Merged(under.LightIcons, over.LightIcons),
            DarkIconTheme = over.DarkIconTheme ?? under.DarkIconTheme,
            LightIconTheme = over.LightIconTheme ?? under.LightIconTheme,
        };

        static Dictionary<string, string> Merged(IReadOnlyDictionary<string, string> beneath, IReadOnlyDictionary<string, string> given)
        {
            Dictionary<string, string> icons = new(beneath, StringComparer.Ordinal);
            foreach ((string key, string path) in given)
            {
                icons[key] = path;
            }
            return icons;
        }
    }

    /// <summary>Tokens for both variants, Dark and Light, laid over others: a value for both variants the one over gives
    /// takes either variant's under it away, and one variant's moves a value for both under it to the other variant.</summary>
    private static (Dictionary<string, TokenValue> Both, Dictionary<string, TokenValue> Dark, Dictionary<string, TokenValue> Light) Laid(
        IReadOnlyDictionary<string, TokenValue> underBoth, IReadOnlyDictionary<string, TokenValue> underDark, IReadOnlyDictionary<string, TokenValue> underLight,
        IReadOnlyDictionary<string, TokenValue> overBoth, IReadOnlyDictionary<string, TokenValue> overDark, IReadOnlyDictionary<string, TokenValue> overLight)
    {
        Dictionary<string, TokenValue> both = new(underBoth, StringComparer.Ordinal);
        Dictionary<string, TokenValue> dark = new(underDark, StringComparer.Ordinal);
        Dictionary<string, TokenValue> light = new(underLight, StringComparer.Ordinal);
        foreach ((string token, TokenValue value) in overBoth)
        {
            both[token] = value;
            dark.Remove(token);
            light.Remove(token);
        }
        foreach ((Dictionary<string, TokenValue> variant, Dictionary<string, TokenValue> other, IReadOnlyDictionary<string, TokenValue> given) in
            ((Dictionary<string, TokenValue>, Dictionary<string, TokenValue>, IReadOnlyDictionary<string, TokenValue>)[])[(dark, light, overDark), (light, dark, overLight)])
        {
            foreach ((string token, TokenValue value) in given)
            {
                // A value for both variants would win over one variant's, so the one under's moves to the other's.
                if (both.Remove(token, out TokenValue? beneath))
                {
                    other.TryAdd(token, beneath);
                }
                variant[token] = value;
            }
        }
        return (both, dark, light);
    }

    /// <summary>The code's colours of a theme as a code colour scheme: its <c>Editor.*</c>, <c>Syntax.*</c>, <c>SyntaxStyle.*</c>
    /// and <c>Diff.*</c> tokens and its languages', nothing else of it.</summary>
    public static ThemeFile CodeOf(ThemeFile theme)
    {
        static Dictionary<string, TokenValue> Code(IReadOnlyDictionary<string, TokenValue> tokens) =>
            new(tokens.Where(t => IsCode(t.Key)), StringComparer.Ordinal);
        return new ThemeFile(theme.Name, theme.Description, Code(theme.Both), Code(theme.Dark), Code(theme.Light)) { Languages = theme.Languages };
    }

    private static JsonElement? Holder(JsonElement root, string member, string within)
    {
        JsonElement? value = JsonMembers.Value(root, member);
        if (value is { } given && given.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives {within}' {member} as {ThemeJson.Shown(given)}: {member} is an object");
        }
        return value;
    }

    /// <summary>Whether a path stays inside its source: its folders joined by /, no . or .., and one of the types given.</summary>
    private static bool Inside(string path, string[] types) =>
        path.Length <= 260 && types.Any(t => path.EndsWith(t, StringComparison.OrdinalIgnoreCase)) && path.IndexOfAny(['\\', ':']) < 0 && !path.Any(char.IsControl)
        && path.Split('/').All(name => name.Length > 0 && name is not ("." or ".."));

    // An extension's Open VSX id, publisher.name, each of letters, digits, '-' and '_'.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*\.[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex ExtensionId();

    [GeneratedRegex(@"^[0-9A-Za-z.+-]{1,64}$")]
    private static partial Regex VersionWords();

    // An icon's key as Passenger's icon sets name it.
    [GeneratedRegex(@"^(?:Icon|FileIcon)\.[A-Za-z0-9]{1,64}$")]
    private static partial Regex IconKey();

    private static string Listed(IEnumerable<string> words) => Plural.Either([.. words.Select(w => $"\"{w}\"")]);
}

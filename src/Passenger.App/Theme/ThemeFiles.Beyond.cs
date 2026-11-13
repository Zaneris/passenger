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

/// <summary>A theme's members beyond its tokens: <c>fonts</c>, <c>background</c>, <c>languages</c>, <c>layout</c> and <c>vscode</c>
/// (docs/themes.md).</summary>
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

    private const double OwnOpacity = 0.15;
    private const int LongestLanguage = 40;

    private static readonly string[] Beyond = ["fonts", "background", "languages", "layout", "vscode"];
    private static readonly string[] PictureTypes = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly (string Word, BackgroundPlaces Place)[] PlaceWords =
        [("editor", BackgroundPlaces.Editor), ("toolWindows", BackgroundPlaces.ToolWindows), ("frame", BackgroundPlaces.Frame), ("welcome", BackgroundPlaces.Welcome)];
    private static readonly (string Word, BackgroundFit Fit)[] FitWords =
        [("cover", BackgroundFit.Cover), ("contain", BackgroundFit.Contain), ("tile", BackgroundFit.Tile), ("centre", BackgroundFit.Centre)];

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
            throw new FormatException($"gives fonts as {Shown(fonts.Value)}: fonts is {{\"code\": {{\"family\": …, \"size\": …}}, \"window\": {{\"family\": …}}}}");
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
                ?? throw new FormatException($"gives fonts' code size as {Shown(given)}: a size is a number of points, {LeastFontSize} to {MostFontSize}");
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
            throw new FormatException($"gives background as {Shown(background.Value)}: background is an object naming its image beside the theme's file");
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
                throw new FormatException($"gives background's {name} as {Shown(given)}: it is an object of what differs under {name}, or \"none\"");
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
            throw new FormatException($"gives background's image under {variant} as {Shown(image)}: it is a picture's file beside the theme's, "
                + "its folders joined by /, with no .., ending .png, .jpg, .jpeg or .webp");
        }
        double opacity = OwnOpacity;
        if (of("opacity") is { } shown)
        {
            opacity = Number(shown, 0, MostOpacity)
                ?? throw new FormatException($"gives background's opacity under {variant} as {Shown(shown)}: it is 0 to {MostOpacity}, so the code over the picture stays readable");
        }
        (uint tint, double strength) = (0, 0);
        if (of("tint") is { } washed)
        {
            PassOver(file, washed, ["colour", "strength"], "background's tint", problems);
            if (washed.ValueKind != JsonValueKind.Object || JsonMembers.Text(washed, "colour") is not { } colour || Hex(colour) is not { } argb
                || (JsonMembers.Value(washed, "strength") is not { } given ? 0.5 : Number(given, 0, 1)) is not { } how)
            {
                throw new FormatException($"gives background's tint under {variant} as {Shown(washed)}: a tint is {{\"colour\": \"#RRGGBB\", \"strength\": 0 to 1}}");
            }
            (tint, strength) = (argb, how);
        }
        BackgroundPlaces places = BackgroundPlaces.Editor;
        if (of("places") is { } where)
        {
            places = BackgroundPlaces.None;
            if (where.ValueKind != JsonValueKind.Array || where.GetArrayLength() == 0)
            {
                throw new FormatException($"gives background's places under {variant} as {Shown(where)}: places is a list of one or more of {Listed(PlaceWords.Select(p => p.Word))}");
            }
            foreach (JsonElement item in where.EnumerateArray())
            {
                int found = item.ValueKind == JsonValueKind.String ? Array.FindIndex(PlaceWords, p => p.Word == item.GetString()) : -1;
                if (found < 0)
                {
                    throw new FormatException($"lists {Shown(item)} in background's places under {variant}: a place is {Listed(PlaceWords.Select(p => p.Word))}");
                }
                places |= PlaceWords[found].Place;
            }
        }
        BackgroundFit fit = BackgroundFit.Cover;
        if (of("fit") is { } fills)
        {
            int found = fills.ValueKind == JsonValueKind.String ? Array.FindIndex(FitWords, f => f.Word == fills.GetString()) : -1;
            fit = found >= 0 ? FitWords[found].Fit
                : throw new FormatException($"gives background's fit under {variant} as {Shown(fills)}: it is {Listed(FitWords.Select(f => f.Word))}");
        }
        (double x, double y) = (0.5, 0.5);
        if (of("align") is { } align)
        {
            (x, y) = Aligned(align.ValueKind == JsonValueKind.String ? align.GetString()! : null)
                ?? throw new FormatException($"gives background's align under {variant} as {Shown(align)}: it is \"centre\", or \"top\", \"bottom\", \"left\", \"right\" "
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

    private static Dictionary<string, LanguageColours> Languages(string file, JsonElement? languages, IReadOnlyDictionary<string, ThemeTokenKind> tokens, List<string> problems)
    {
        Dictionary<string, LanguageColours> read = new(StringComparer.Ordinal);
        if (languages is null)
        {
            return read;
        }
        if (languages.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives languages as {Shown(languages.Value)}: languages is an object of a language's id (\"csharp\") and its code's tokens");
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
                throw new FormatException($"gives languages' {language.Name} as {Shown(language.Value)}: a language's are tokens, dark and light, as a theme's");
            }
            PassOver(file, language.Value, Sections, $"languages' {language.Name}", problems);
            Dictionary<string, TokenValue>[] sections = TokenSections(file, language.Value, tokens, code: true, language.Name, problems);
            read[language.Name] = new LanguageColours(sections[0], sections[1], sections[2]);
        }
        return read;
    }

    private static VsCodeBase? Base(string file, JsonElement? named, List<string> problems)
    {
        if (named is null)
        {
            return null;
        }
        if (named.Value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives vscode as {Shown(named.Value)}: vscode is {{\"id\": \"publisher.extension\", \"theme\": \"its label\"}}, its version optional");
        }
        PassOver(file, named.Value, ["id", "theme", "version"], "vscode", problems);
        string id = Words(named.Value, "id", LongestName * 4, needed: true);
        if (!ExtensionId().IsMatch(id))
        {
            throw new FormatException($"gives vscode's id as \"{id}\": it is an extension's Open VSX id, publisher.extension");
        }
        string theme = Words(named.Value, "theme", LongestName * 2, needed: true);
        string? version = JsonMembers.Value(named.Value, "version") is null ? null : Words(named.Value, "version", LongestName, needed: true);
        if (version is not null && !VersionWords().IsMatch(version))
        {
            throw new FormatException($"gives vscode's version as \"{version}\": it is the extension's version, as 2.25.1");
        }
        return new VsCodeBase(id, theme, version);
    }

    /// <summary>A theme laid over another, as over its VS Code base: the one under giving every token the one over leaves
    /// out, the one over its name, words, tokens, fonts, pictures, languages and layout; a token the one over gives one
    /// variant alone keeps the one under's value for the other.</summary>
    public static ThemeFile Over(ThemeFile under, ThemeFile over)
    {
        Dictionary<string, TokenValue> both = new(under.Both, StringComparer.Ordinal);
        Dictionary<string, TokenValue> dark = new(under.Dark, StringComparer.Ordinal);
        Dictionary<string, TokenValue> light = new(under.Light, StringComparer.Ordinal);
        foreach ((string token, TokenValue value) in over.Both)
        {
            both[token] = value;
            dark.Remove(token);
            light.Remove(token);
        }
        foreach ((Dictionary<string, TokenValue> variant, Dictionary<string, TokenValue> other, IReadOnlyDictionary<string, TokenValue> given) in
            ((Dictionary<string, TokenValue>, Dictionary<string, TokenValue>, IReadOnlyDictionary<string, TokenValue>)[])[(dark, light, over.Dark), (light, dark, over.Light)])
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
        return over with { Both = both, Dark = dark, Light = light, Shows = under.Shows };
    }

    private static JsonElement? Holder(JsonElement root, string member, string within)
    {
        JsonElement? value = JsonMembers.Value(root, member);
        if (value is { } given && given.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"gives {within}' {member} as {Shown(given)}: {member} is an object");
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

    private static string Listed(IEnumerable<string> words)
    {
        string[] all = [.. words.Select(w => $"\"{w}\"")];
        return string.Join(", ", all[..^1]) + " or " + all[^1];
    }
}

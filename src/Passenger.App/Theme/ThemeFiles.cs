using System.Globalization;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The kind of value a token takes, as the default theme's value (Tokens.axaml) is.</summary>
public enum ThemeTokenKind
{
    /// <summary>A colour, <c>"#RRGGBB"</c> or <c>"#AARRGGBB"</c>, or one drawn at an opacity.</summary>
    Colour,

    /// <summary>A colour, as <see cref="Colour"/>, or a linear gradient.</summary>
    Gradient,

    /// <summary>Shadows, as Avalonia's box shadows write them.</summary>
    Shadow,

    /// <summary>A corner's radius: one number for every corner, or four.</summary>
    Radius,

    /// <summary>A thickness: one number for every side, or four.</summary>
    Thickness,

    /// <summary>A number.</summary>
    Number,

    /// <summary>How a kind of code's words are set: Normal, Bold, Italic or Bold Italic.</summary>
    TextStyle,
}

/// <summary>A token's value as a theme file gives it, checked against the kind its token takes.</summary>
public abstract record TokenValue;

/// <summary>A colour as <c>0xAARRGGBB</c>, drawn at an opacity: 1 where the file gives none.</summary>
public sealed record ColourValue(uint Argb, double Opacity) : TokenValue;

/// <summary>A linear gradient from one point to another, each a fraction of the area it fills (0, 0 its top left), through
/// its stops in order.</summary>
public sealed record GradientValue(double StartX, double StartY, double EndX, double EndY, IReadOnlyList<GradientStopValue> Stops) : TokenValue;

/// <summary>A gradient's colour at a place along it, 0 its start and 1 its end.</summary>
public readonly record struct GradientStopValue(double Offset, uint Argb);

/// <summary>Shadows in Avalonia's words, checked to be what they should: each <c>[inset] X Y [BLUR [SPREAD]] #COLOUR</c>, joined
/// by commas, or <c>none</c>.</summary>
public sealed record ShadowValue(string Text) : TokenValue;

/// <summary>A radius or a thickness: one number for every corner or side, or four (top left, top right, bottom right, bottom
/// left; left, top, right, bottom).</summary>
public sealed record SidesValue(IReadOnlyList<double> Numbers) : TokenValue;

/// <summary>A number.</summary>
public sealed record NumberValue(double Number) : TokenValue;

/// <summary>How a kind of code's words are set, in the words the file gives: Normal, Bold, Italic or Bold Italic.</summary>
public sealed record TextStyleValue(string Words) : TokenValue;

/// <summary>Which variants a theme shows in: both, as Passenger's dark or light setting chooses, or one whatever it says.</summary>
public enum ThemeShows
{
    /// <summary>Dark or Light, as the setting chooses.</summary>
    Both,

    /// <summary>Dark alone.</summary>
    Dark,

    /// <summary>Light alone.</summary>
    Light,
}

/// <summary>A VS Code colour theme a theme is laid over: by its extension's Open VSX id (<c>publisher.name</c>), the theme's
/// label there and the version pinned (null for the newest), or by its JSON's <see cref="File"/> beside the theme's file.</summary>
public sealed record VsCodeBase(string? Id, string? Theme, string? Version, string? File = null);

/// <summary>A theme or a code colour scheme as its file gives it: its name, its words for a person, and its tokens for both
/// variants and for Dark or Light alone, each named as its key without <c>Passenger.</c>; a theme's fonts and background
/// picture for each variant, the code's tokens for a language over the file's own, and its default layout.</summary>
public sealed record ThemeFile(string Name, string Description, IReadOnlyDictionary<string, TokenValue> Both,
    IReadOnlyDictionary<string, TokenValue> Dark, IReadOnlyDictionary<string, TokenValue> Light)
{
    /// <summary>The fonts the theme gives, each null where it leaves the window's or the code's as it is.</summary>
    public ThemeFonts Fonts { get; init; } = ThemeFonts.None;

    /// <summary>The picture drawn under Dark, or null for none.</summary>
    public BackgroundValue? DarkBackground { get; init; }

    /// <summary>The picture drawn under Light, or null for none.</summary>
    public BackgroundValue? LightBackground { get; init; }

    /// <summary>The code's tokens for a language by its id (<c>csharp</c>), laid over the file's own for that language's files.</summary>
    public IReadOnlyDictionary<string, LanguageColours> Languages { get; init; } = new Dictionary<string, LanguageColours>(StringComparer.Ordinal);

    /// <summary>The default layout the theme gives, or null for Passenger's own.</summary>
    public ThemeLayout? Layout { get; init; }

    /// <summary>The variants it shows in.</summary>
    public ThemeShows Shows { get; init; }

    /// <summary>The VS Code colour theme it is laid over, or null for none.</summary>
    public VsCodeBase? Base { get; init; }

    /// <summary>The theme Passenger carries it is laid over, by name (<see cref="ThemeSource.Carried"/>), or null for
    /// Passenger's own, under every theme.</summary>
    public string? BuiltInBase { get; init; }

    /// <summary>The icons it gives under Dark in Passenger's place, each by the key of the icon it stands for (<c>Icon.Run</c>,
    /// <c>FileIcon.CSharp</c>) and its SVG's path beside the file.</summary>
    public IReadOnlyDictionary<string, string> DarkIcons { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The icons it gives under Light, as <see cref="DarkIcons"/>.</summary>
    public IReadOnlyDictionary<string, string> LightIcons { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The icons it gives under a variant.</summary>
    public IReadOnlyDictionary<string, string> IconsOf(bool dark) => dark ? DarkIcons : LightIcons;

    /// <summary>The VS Code file icon theme drawing its files and folders under Dark (<c>iconTheme</c>), by its extension's id
    /// and its label, or by its JSON's file beside the theme's; null for none.</summary>
    public VsCodeBase? DarkIconTheme { get; init; }

    /// <summary>The VS Code file icon theme drawing its files and folders under Light, as <see cref="DarkIconTheme"/>.</summary>
    public VsCodeBase? LightIconTheme { get; init; }

    /// <summary>The VS Code file icon theme under a variant, or null for none.</summary>
    public VsCodeBase? IconThemeOf(bool dark) => dark ? DarkIconTheme : LightIconTheme;

    /// <summary>Each VS Code file icon theme it names, each once.</summary>
    public IEnumerable<VsCodeBase> IconThemes => ((VsCodeBase?[])[DarkIconTheme, LightIconTheme]).OfType<VsCodeBase>().Distinct();

    /// <summary>Every icon's file it names, each once.</summary>
    public IEnumerable<string> IconFiles => DarkIcons.Values.Concat(LightIcons.Values).Distinct(StringComparer.Ordinal);

    /// <summary>The picture drawn under a variant, or null for none.</summary>
    public BackgroundValue? BackgroundOf(bool dark) => dark ? DarkBackground : LightBackground;

    /// <summary>Every picture the file names, each once.</summary>
    public IEnumerable<string> Images => ((BackgroundValue?[])[DarkBackground, LightBackground]).OfType<BackgroundValue>().Select(b => b.Image).Distinct(StringComparer.Ordinal);
}

/// <summary>A source's index: the paths inside the source of its themes and of its code colour schemes, in its order.</summary>
public sealed record ThemeIndex(IReadOnlyList<string> Themes, IReadOnlyList<string> CodeColours);

/// <summary>What reading a file came to: what it gives, or why it was refused whole (<see cref="Read"/> null), and what it
/// passed over, each saying the file and where in it.</summary>
public sealed record ThemeRead<T>(T? Read, string? Refused, IReadOnlyList<string> Problems) where T : class;

/// <summary>Reads theme files, format 1 (docs/themes.md): a source's index, a theme and a code colour scheme, as data and
/// against the tokens the default theme gives, never more than <see cref="MostBytes"/> or nested deeper than JSON's few
/// levels. A file that is no JSON object, names another format, or gives a value of the wrong kind is refused whole, saying
/// why; a member or token this Passenger does not know is passed over and named in the problems, so a later Passenger's
/// additions leave a file readable and a typo shows.</summary>
public static partial class ThemeFiles
{
    /// <summary>The format these files are read as.</summary>
    public const int Format = 1;

    /// <summary>The index at a source's root.</summary>
    public const string IndexFile = "passenger-themes.json";

    /// <summary>The most a theme file may hold.</summary>
    public const int MostBytes = 1024 * 1024;

    private const int Deepest = 10;
    private const int LongestName = 64;
    private const int LongestDescription = 1000;
    private const int MostStops = 16;
    private const int MostShadows = 8;
    private const int MostEntries = 1000;
    private const double Farthest = 1000;

    private static readonly string[] CodePrefixes = ["Editor.", "Syntax.", "SyntaxStyle.", "Diff."];

    /// <summary>Tokens a theme may give that Passenger's own theme leaves out, each drawn as Passenger draws it without: the
    /// fourth to sixth rainbow brace colours, so depths cycle through as many as a theme gives, how a parameter is set, and
    /// how every icon is drawn beside its own colours (<see cref="IconLook"/>).</summary>
    public static readonly IReadOnlyDictionary<string, ThemeTokenKind> Optional = new Dictionary<string, ThemeTokenKind>(StringComparer.Ordinal)
    {
        ["Syntax.Brace4"] = ThemeTokenKind.Colour,
        ["Syntax.Brace5"] = ThemeTokenKind.Colour,
        ["Syntax.Brace6"] = ThemeTokenKind.Colour,
        ["SyntaxStyle.Parameter"] = ThemeTokenKind.TextStyle,
        [IconLook.SaturationToken] = ThemeTokenKind.Number,
        [IconLook.BrightnessToken] = ThemeTokenKind.Number,
        [IconLook.TintToken] = ThemeTokenKind.Colour,
    };

    /// <summary>Kinds of code drawn in the code's plain words unless a file colours them: one a file leaves out takes the
    /// file's <c>Editor.Text</c> where it gives that, so a file written before they were tokens draws them as it did.</summary>
    public static readonly IReadOnlyList<string> PlainKinds = ["Syntax.Parameter", "Syntax.Local", "Syntax.Namespace", "Syntax.Operator"];
    private static readonly string[] Styles = ["Normal", "Bold", "Italic", "Bold Italic"];
    private static readonly string[] Sections = ["tokens", "dark", "light"];

    /// <summary>Whether a token colours the code, all a code colour scheme may give.</summary>
    public static bool IsCode(string token) => CodePrefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal));

    /// <summary>A source's index, <paramref name="file"/> naming it in what the read says.</summary>
    public static ThemeRead<ThemeIndex> ReadIndex(string file, ReadOnlyMemory<byte> bytes) => Read(file, bytes, (root, problems) =>
    {
        PassOver(file, root, [Member.Format, "themes", "codeColours"], "an index", problems);
        return new ThemeIndex(Paths(root, "themes"), Paths(root, "codeColours"));
    });

    /// <summary>A theme, its tokens read against <paramref name="tokens"/>: every token's name and the kind it takes.</summary>
    public static ThemeRead<ThemeFile> ReadTheme(string file, ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ThemeTokenKind> tokens) =>
        Read(file, bytes, (root, problems) => Theme(file, root, tokens, code: false, problems));

    /// <summary>A code colour scheme, as <see cref="ReadTheme"/> but refused where it gives a token that is not the code's.</summary>
    public static ThemeRead<ThemeFile> ReadCodeColours(string file, ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ThemeTokenKind> tokens) =>
        Read(file, bytes, (root, problems) => Theme(file, root, tokens, code: true, problems));

    private static ThemeRead<T> Read<T>(string file, ReadOnlyMemory<byte> bytes, Func<JsonElement, List<string>, T> read) where T : class =>
        ThemeJson.Read(file, bytes, new JsonDocumentOptions { MaxDepth = Deepest, AllowDuplicateProperties = false }, $"format {Format}",
            $"a theme file is {{\"{Member.Format}\": {Format}, …}}", (root, problems) =>
            {
                CheckFormat(root);
                return read(root, problems);
            });

    private static void CheckFormat(JsonElement root)
    {
        JsonElement? format = JsonMembers.Value(root, Member.Format);
        if (format is null)
        {
            throw new FormatException($"names no format: a theme file begins {{\"format\": {Format}, …}}");
        }
        if (format is not { ValueKind: JsonValueKind.Number } number || !number.TryGetInt32(out int which) || which != Format)
        {
            throw new FormatException($"is format {ThemeJson.Shown(format.Value)}, which this Passenger does not read: it reads format {Format}");
        }
    }

    private static ThemeFile Theme(string file, JsonElement root, IReadOnlyDictionary<string, ThemeTokenKind> tokens, bool code, List<string> problems)
    {
        string what = code ? "a code colour scheme" : "a theme";
        PassOver(file, root, code ? [Member.Format, Member.Name, "description", .. Sections, "languages", Member.VsCode, Member.Base] : [Member.Format, Member.Name, "description", .. Sections, .. Beyond], what, problems);
        string name = Words(root, Member.Name, LongestName, needed: true);
        string description = Words(root, "description", LongestDescription, needed: false);
        string? carried = BuiltInBase(root);
        // Over a VS Code theme or one Passenger carries, the plain kinds a file leaves out are the base's own.
        bool plain = JsonMembers.Value(root, Member.VsCode) is null && carried is null;
        Dictionary<string, TokenValue>[] sections = TokenSections(file, root, tokens, code, null, plain, problems);
        if (!code)
        {
            WorkOutLaterTokens(sections, tokens);
        }
        (BackgroundValue? darkPicture, BackgroundValue? lightPicture) = code ? (null, null) : Background(file, JsonMembers.Value(root, "background"), problems);
        (Dictionary<string, string>? dark, Dictionary<string, string>? light) = code ? (null, null) : Icons(file, JsonMembers.Value(root, "icons"), problems);
        (VsCodeBase? iconDark, VsCodeBase? iconLight) = code ? (null, null) : IconTheme(file, JsonMembers.Value(root, Member.IconTheme), problems);
        return new ThemeFile(name, description, sections[0], sections[1], sections[2])
        {
            Fonts = code ? ThemeFonts.None : Fonts(file, JsonMembers.Value(root, "fonts"), problems),
            DarkBackground = darkPicture,
            LightBackground = lightPicture,
            Languages = Languages(file, JsonMembers.Value(root, "languages"), tokens, plain, problems),
            Layout = code ? null : Layout(file, JsonMembers.Value(root, "layout"), problems),
            DarkIcons = dark is null ? new Dictionary<string, string>(StringComparer.Ordinal) : dark,
            LightIcons = light is null ? new Dictionary<string, string>(StringComparer.Ordinal) : light,
            Base = Base(file, JsonMembers.Value(root, Member.VsCode), problems),
            BuiltInBase = carried,
            DarkIconTheme = iconDark,
            LightIconTheme = iconLight,
        };
    }

    /// <summary>A holder's tokens for both variants, Dark and Light, each in its section; <paramref name="language"/> names
    /// the language a theme's <c>languages</c> gives them for, whose tokens are the code's alone; with
    /// <paramref name="plain"/>, the plain kinds it leaves out take its plain words (<see cref="PlainKinds"/>).</summary>
    private static Dictionary<string, TokenValue>[] TokenSections(string file, JsonElement root, IReadOnlyDictionary<string, ThemeTokenKind> tokens, bool code,
        string? language, bool plain, List<string> problems)
    {
        Dictionary<string, TokenValue>[] sections = [new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        string inLanguage = language is null ? string.Empty : $" of languages' {language}";
        for (int i = 0; i < Sections.Length; i++)
        {
            JsonElement? section = JsonMembers.Value(root, Sections[i]);
            if (section is null)
            {
                continue;
            }
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"gives {Sections[i]}{inLanguage} as {ThemeJson.Shown(section.Value)}: {Sections[i]} is an object of tokens and their values");
            }
            foreach (JsonProperty token in section.Value.EnumerateObject())
            {
                if (!tokens.TryGetValue(token.Name, out ThemeTokenKind kind))
                {
                    string hint = token.Name.StartsWith("Passenger.", StringComparison.Ordinal) ? " (a token is named without \"Passenger.\")" : string.Empty;
                    problems.Add($"{file}: {token.Name} in {Sections[i]}{inLanguage} is no token this Passenger knows{hint}, so it was passed over");
                    continue;
                }
                if (code && !IsCode(token.Name))
                {
                    throw new FormatException(language is null
                        ? $"gives {token.Name}, which is not the code's: a code colour scheme gives only Editor.*, Syntax.*, SyntaxStyle.* and Diff.*"
                        : $"gives {token.Name} for {language}, which is not the code's: a language's tokens are only Editor.*, Syntax.*, SyntaxStyle.* and Diff.*");
                }
                // Both variants' value and one variant's would leave which wins to the reader.
                int before = i == 0 ? Array.FindIndex(sections, 1, s => s.ContainsKey(token.Name)) : sections[0].ContainsKey(token.Name) ? 0 : -1;
                if (before >= 0)
                {
                    throw new FormatException($"gives {token.Name} in {Sections[before]} and in {Sections[i]}{inLanguage}: a token is given once, in tokens for both variants or in dark or light for one");
                }
                sections[i][token.Name] = Value(file, $"{token.Name} in {Sections[i]}{inLanguage}", kind, token.Value, problems);
            }
        }
        foreach (string kind in PlainKinds.Where(k => plain && tokens.ContainsKey(k) && !sections.Any(s => s.ContainsKey(k))))
        {
            foreach (Dictionary<string, TokenValue> section in sections)
            {
                if (section.TryGetValue("Editor.Text", out TokenValue? words))
                {
                    section[kind] = words;
                }
            }
        }
        return sections;
    }

    /// <summary>The tokens a file written before them leaves out, worked out in each variant from its own colours there: the
    /// buttons' grounds under the pointer and pressed as Passenger's themes work them out (a plain button's hover and
    /// pressed washes laid over its ground, the default button's pressed as far past its hover as that is from its
    /// accent), and the Debug button's colours, its icons'; so the file draws them in its own colours.</summary>
    private static void WorkOutLaterTokens(Dictionary<string, TokenValue>[] sections, IReadOnlyDictionary<string, ThemeTokenKind> tokens)
    {
        foreach (Dictionary<string, TokenValue> section in sections[1..])
        {
            uint? Given(string token) => (section.TryGetValue(token, out TokenValue? value) || sections[0].TryGetValue(token, out value)) && value is ColourValue colour
                ? ((uint)Math.Round((colour.Argb >> 24) * colour.Opacity) << 24) | (colour.Argb & 0xFFFFFF) : null;
            void Work(string token, uint? worked)
            {
                if (worked is { } colour && tokens.ContainsKey(token) && !section.ContainsKey(token) && !sections[0].ContainsKey(token))
                {
                    section[token] = new ColourValue(colour, 1);
                }
            }
            uint? button = Given("Button");
            Work("ButtonHover", button is { } under && Given("Hover") is { } hover ? ThemeColours.Over(hover, ThemeColours.Opaque(under)) : null);
            Work("ButtonPressed", button is { } ground && Given("Selected") is { } selected ? ThemeColours.Over(selected, ThemeColours.Opaque(ground)) : null);
            Work("AccentPressed", Given("Accent") is { } accent && Given("AccentHover") is { } accentHover ? ThemeColours.Past(accent, accentHover) : null);
            // The Debug button in the file's own icon colours, legible where its icons are.
            Work("Debug", Given("Icon"));
            Work("TitleBar.Debug", Given("TitleBar.Icon"));
            // A measure's fine mark on the code in the file's own green.
            Work("Editor.Fine", Given("Test.Passed"));
            // A byte the debugged program changed in the file's own error colour.
            Work("Memory.Changed", Given("Error"));
        }
    }

    private static TokenValue Value(string file, string where, ThemeTokenKind kind, JsonElement value, List<string> problems)
    {
        TokenValue? read = kind switch
        {
            ThemeTokenKind.Colour => Colour(file, where, value, problems),
            ThemeTokenKind.Gradient => JsonMembers.Value(value, "stops") is null ? Colour(file, where, value, problems) : Gradient(file, where, value, problems),
            ThemeTokenKind.Shadow => value.ValueKind == JsonValueKind.String && Shadows(value.GetString()!) ? new ShadowValue(value.GetString()!) : null,
            ThemeTokenKind.Radius => Sides(value, 0),
            ThemeTokenKind.Thickness => Sides(value, -Farthest),
            ThemeTokenKind.Number => Number(value, 0, Farthest) is { } number ? new NumberValue(number) : null,
            ThemeTokenKind.TextStyle => value.ValueKind == JsonValueKind.String && Styles.Contains(value.GetString(), StringComparer.Ordinal) ? new TextStyleValue(value.GetString()!) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a token kind format 1 does not read"),
        };
        return read ?? throw new FormatException($"gives {where} as {ThemeJson.Shown(value)}, where it takes {Expected(kind)}");
    }

    private static ColourValue? Colour(string file, string where, JsonElement value, List<string> problems)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return Hex(value.GetString()!) is { } argb ? new ColourValue(argb, 1) : null;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        PassOver(file, value, ["colour", "opacity"], $"{where}, a colour", problems);
        return JsonMembers.Text(value, "colour") is { } colour && Hex(colour) is { } shade
            && (JsonMembers.Value(value, "opacity") is not { } opacity ? 1 : Number(opacity, 0, 1)) is { } drawn
            ? new ColourValue(shade, drawn) : null;
    }

    private static GradientValue? Gradient(string file, string where, JsonElement value, List<string> problems)
    {
        PassOver(file, value, ["start", "end", "stops"], $"{where}, a gradient", problems);
        if (Point(JsonMembers.Value(value, "start")) is not { } start || Point(JsonMembers.Value(value, "end")) is not { } end)
        {
            return null;
        }
        IReadOnlyList<JsonElement> items = JsonMembers.Items(value, "stops");
        if (items.Count is < 2 or > MostStops)
        {
            return null;
        }
        List<GradientStopValue> stops = [];
        foreach (JsonElement item in items)
        {
            PassOver(file, item, ["offset", "colour"], $"{where}, a gradient's stop", problems);
            if (JsonMembers.Value(item, "offset") is not { } at || Number(at, stops.Count == 0 ? 0 : stops[^1].Offset, 1) is not { } offset
                || JsonMembers.Text(item, "colour") is not { } colour || Hex(colour) is not { } argb)
            {
                return null;
            }
            stops.Add(new GradientStopValue(offset, argb));
        }
        return new GradientValue(start.X, start.Y, end.X, end.Y, stops);

        static (double X, double Y)? Point(JsonElement? point) =>
            point is { ValueKind: JsonValueKind.Array } pair && pair.GetArrayLength() == 2
            && Number(pair[0], -Farthest, Farthest) is { } x && Number(pair[1], -Farthest, Farthest) is { } y ? (x, y) : null;
    }

    private static SidesValue? Sides(JsonElement value, double least)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return Number(value, least, Farthest) is { } every ? new SidesValue([every]) : null;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            return null;
        }
        List<double> numbers = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (Number(item, least, Farthest) is not { } number)
            {
                return null;
            }
            numbers.Add(number);
        }
        return new SidesValue(numbers);
    }

    private static double? Number(JsonElement value, double least, double most) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number >= least && number <= most ? number : null;

    private static uint? Hex(string text)
    {
        if (text.Length is not (7 or 9) || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }
        return text.Length == 7 ? 0xFF000000 | value : value;
    }

    private static bool Shadows(string text)
    {
        if (text == "none")
        {
            return true;
        }
        string[] shadows = text.Split(',');
        return shadows.Length <= MostShadows && shadows.All(shadow =>
        {
            string[] words = shadow.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int first = words.Length > 0 && words[0] == "inset" ? 1 : 0;
            int count = words.Length - first - 1;
            return count is >= 2 and <= 4 && Hex(words[^1]) is not null
                && Enumerable.Range(0, count).All(i => double.TryParse(words[first + i], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out double number) && Math.Abs(number) <= Farthest && (i != 2 || number >= 0));
        });
    }

    private static string Words(JsonElement root, string member, int longest, bool needed)
    {
        JsonElement? value = JsonMembers.Value(root, member);
        if (value is null && !needed)
        {
            return string.Empty;
        }
        if (value is not { ValueKind: JsonValueKind.String } text || text.GetString() is not { } words || words.Trim().Length == 0
            || words.Length > longest || words.Any(char.IsControl))
        {
            throw new FormatException($"gives {(value is null ? "no " + member : $"{member} as {ThemeJson.Shown(value.Value)}")}: its {member} is words on one line, at most {Numbers.Said(longest)} characters");
        }
        return words;
    }

    private static List<string> Paths(JsonElement root, string member)
    {
        JsonElement? value = JsonMembers.Value(root, member);
        if (value is null)
        {
            return [];
        }
        if (value.Value.ValueKind != JsonValueKind.Array || value.Value.GetArrayLength() > MostEntries)
        {
            throw new FormatException($"gives {member} as {ThemeJson.Shown(value.Value)}: {member} is a list of paths inside the source, at most {Numbers.Said(MostEntries)}");
        }
        List<string> paths = [];
        foreach (JsonElement item in value.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Inside(item.GetString()!, [".json"]))
            {
                throw new FormatException($"lists {ThemeJson.Shown(item)} in {member}: each is a path inside the source, its folders joined by /, with no .. and ending .json");
            }
            paths.Add(item.GetString()!);
        }
        return paths;
    }

    private static void PassOver(string file, JsonElement holder, string[] known, string what, List<string> problems)
    {
        if (holder.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (JsonProperty member in holder.EnumerateObject())
        {
            if (!known.Contains(member.Name, StringComparer.Ordinal))
            {
                problems.Add($"{file}: {member.Name} is no member of {what} this Passenger knows, so it was passed over");
            }
        }
    }

    private static string Expected(ThemeTokenKind kind) => kind switch
    {
        ThemeTokenKind.Colour => "a colour: \"#RRGGBB\", \"#AARRGGBB\" or {\"colour\": \"#RRGGBB\", \"opacity\": 0 to 1}",
        ThemeTokenKind.Gradient => $"a gradient, {{\"start\": [x, y], \"end\": [x, y], \"stops\": [{{\"offset\": 0, \"colour\": \"#RRGGBB\"}}, …]}} "
            + $"with 2 to {Numbers.Said(MostStops)} stops in order from 0 to 1, or a colour: \"#RRGGBB\", \"#AARRGGBB\" or {{\"colour\": \"#RRGGBB\", \"opacity\": 0 to 1}}",
        ThemeTokenKind.Shadow => $"shadows: \"X Y BLUR SPREAD #AARRGGBB\" each (BLUR and SPREAD may be left out, \"inset\" may lead), up to {Numbers.Said(MostShadows)} joined by commas, or \"none\"",
        ThemeTokenKind.Radius => string.Create(CultureInfo.InvariantCulture, $"a radius: a number for every corner, or four [top left, top right, bottom right, bottom left], each 0 to {Farthest}"),
        ThemeTokenKind.Thickness => string.Create(CultureInfo.InvariantCulture, $"a thickness: a number for every side, or four [left, top, right, bottom], each -{Farthest} to {Farthest}"),
        ThemeTokenKind.Number => string.Create(CultureInfo.InvariantCulture, $"a number, 0 to {Farthest}"),
        ThemeTokenKind.TextStyle => Listed(Styles),
        _ => kind.ToString(),
    };

}

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

/// <summary>A theme or a code colour scheme as its file gives it: its name, its words for a person, and its tokens for both
/// variants and for Dark or Light alone, each named as its key without <c>Passenger.</c>.</summary>
public sealed record ThemeFile(string Name, string Description, IReadOnlyDictionary<string, TokenValue> Both,
    IReadOnlyDictionary<string, TokenValue> Dark, IReadOnlyDictionary<string, TokenValue> Light);

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
public static class ThemeFiles
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
    private static readonly string[] Styles = ["Normal", "Bold", "Italic", "Bold Italic"];
    private static readonly string[] Sections = ["tokens", "dark", "light"];

    /// <summary>Whether a token colours the code, all a code colour scheme may give.</summary>
    public static bool IsCode(string token) => CodePrefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal));

    /// <summary>A source's index, <paramref name="file"/> naming it in what the read says.</summary>
    public static ThemeRead<ThemeIndex> ReadIndex(string file, ReadOnlyMemory<byte> bytes) => Read(file, bytes, (root, problems) =>
    {
        PassOver(file, root, ["format", "themes", "codeColours"], "an index", problems);
        return new ThemeIndex(Paths(root, "themes"), Paths(root, "codeColours"));
    });

    /// <summary>A theme, its tokens read against <paramref name="tokens"/>: every token's name and the kind it takes.</summary>
    public static ThemeRead<ThemeFile> ReadTheme(string file, ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ThemeTokenKind> tokens) =>
        Read(file, bytes, (root, problems) => Theme(file, root, tokens, code: false, problems));

    /// <summary>A code colour scheme, as <see cref="ReadTheme"/> but refused where it gives a token that is not the code's.</summary>
    public static ThemeRead<ThemeFile> ReadCodeColours(string file, ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, ThemeTokenKind> tokens) =>
        Read(file, bytes, (root, problems) => Theme(file, root, tokens, code: true, problems));

    private static ThemeRead<T> Read<T>(string file, ReadOnlyMemory<byte> bytes, Func<JsonElement, List<string>, T> read) where T : class
    {
        if (bytes.Length > MostBytes)
        {
            return new ThemeRead<T>(null, $"{file} is {bytes.Length} bytes, more than the {MostBytes} a theme file may hold", []);
        }
        List<string> problems = [];
        try
        {
            // A byte order mark, as Windows' editors may save one, is no JSON of its own.
            ReadOnlyMemory<byte> text = bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = Deepest, AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("is no JSON object: a theme file is {\"format\": 1, …}");
            }
            CheckFormat(root);
            return new ThemeRead<T>(read(root, problems), null, problems);
        }
        catch (JsonException e)
        {
            return new ThemeRead<T>(null, $"{file} is not JSON as format {Format} reads it: {e.Message}", []);
        }
        catch (FormatException e)
        {
            return new ThemeRead<T>(null, $"{file} {e.Message}", []);
        }
    }

    private static void CheckFormat(JsonElement root)
    {
        JsonElement? format = JsonMembers.Value(root, "format");
        if (format is null)
        {
            throw new FormatException($"names no format: a theme file begins {{\"format\": {Format}, …}}");
        }
        if (format is not { ValueKind: JsonValueKind.Number } number || !number.TryGetInt32(out int which) || which != Format)
        {
            throw new FormatException($"is format {Shown(format.Value)}, which this Passenger does not read: it reads format {Format}");
        }
    }

    private static ThemeFile Theme(string file, JsonElement root, IReadOnlyDictionary<string, ThemeTokenKind> tokens, bool code, List<string> problems)
    {
        string what = code ? "a code colour scheme" : "a theme";
        PassOver(file, root, ["format", "name", "description", .. Sections], what, problems);
        string name = Words(root, "name", LongestName, needed: true);
        string description = Words(root, "description", LongestDescription, needed: false);
        Dictionary<string, TokenValue>[] sections = [new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        for (int i = 0; i < Sections.Length; i++)
        {
            JsonElement? section = JsonMembers.Value(root, Sections[i]);
            if (section is null)
            {
                continue;
            }
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"gives {Sections[i]} as {Shown(section.Value)}: {Sections[i]} is an object of tokens and their values");
            }
            foreach (JsonProperty token in section.Value.EnumerateObject())
            {
                if (!tokens.TryGetValue(token.Name, out ThemeTokenKind kind))
                {
                    string hint = token.Name.StartsWith("Passenger.", StringComparison.Ordinal) ? " (a token is named without \"Passenger.\")" : string.Empty;
                    problems.Add($"{file}: {token.Name} in {Sections[i]} is no token this Passenger knows{hint}, so it was passed over");
                    continue;
                }
                if (code && !IsCode(token.Name))
                {
                    throw new FormatException($"gives {token.Name}, which is not the code's: a code colour scheme gives only Editor.*, Syntax.*, SyntaxStyle.* and Diff.*");
                }
                // Both variants' value and one variant's would leave which wins to the reader.
                int before = i == 0 ? Array.FindIndex(sections, 1, s => s.ContainsKey(token.Name)) : sections[0].ContainsKey(token.Name) ? 0 : -1;
                if (before >= 0)
                {
                    throw new FormatException($"gives {token.Name} in {Sections[before]} and in {Sections[i]}: a token is given once, in tokens for both variants or in dark or light for one");
                }
                sections[i][token.Name] = Value(file, $"{token.Name} in {Sections[i]}", kind, token.Value, problems);
            }
        }
        return new ThemeFile(name, description, sections[0], sections[1], sections[2]);
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
        return read ?? throw new FormatException($"gives {where} as {Shown(value)}, where it takes {Expected(kind)}");
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
            throw new FormatException($"gives {(value is null ? "no " + member : $"{member} as {Shown(value.Value)}")}: its {member} is words on one line, at most {longest} characters");
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
            throw new FormatException($"gives {member} as {Shown(value.Value)}: {member} is a list of paths inside the source, at most {MostEntries}");
        }
        List<string> paths = [];
        foreach (JsonElement item in value.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Inside(item.GetString()!))
            {
                throw new FormatException($"lists {Shown(item)} in {member}: each is a path inside the source, its folders joined by /, with no .. and ending .json");
            }
            paths.Add(item.GetString()!);
        }
        return paths;

        static bool Inside(string path) =>
            path.Length <= 260 && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && path.IndexOfAny(['\\', ':']) < 0 && !path.Any(char.IsControl)
            && path.Split('/').All(name => name.Length > 0 && name is not ("." or ".."));
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
            + $"with 2 to {MostStops} stops in order from 0 to 1, or a colour: \"#RRGGBB\", \"#AARRGGBB\" or {{\"colour\": \"#RRGGBB\", \"opacity\": 0 to 1}}",
        ThemeTokenKind.Shadow => $"shadows: \"X Y BLUR SPREAD #AARRGGBB\" each (BLUR and SPREAD may be left out, \"inset\" may lead), up to {MostShadows} joined by commas, or \"none\"",
        ThemeTokenKind.Radius => $"a radius: a number for every corner, or four [top left, top right, bottom right, bottom left], each 0 to {Farthest}",
        ThemeTokenKind.Thickness => $"a thickness: a number for every side, or four [left, top, right, bottom], each -{Farthest} to {Farthest}",
        ThemeTokenKind.Number => $"a number, 0 to {Farthest}",
        ThemeTokenKind.TextStyle => string.Join(", ", Styles.SkipLast(1).Select(s => $"\"{s}\"")) + $" or \"{Styles[^1]}\"",
        _ => kind.ToString(),
    };

    private static string Shown(JsonElement value)
    {
        string raw = value.GetRawText();
        return raw.Length <= 40 ? raw : string.Concat(raw.AsSpan(0, 39), "…");
    }
}

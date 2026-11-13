using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A rule of a VS Code colour theme's <c>tokenColors</c>: the TextMate scope selectors it names (each a scope, or
/// scopes its parents' first, joined by spaces) and what it sets of them.</summary>
public sealed record VsCodeRule(IReadOnlyList<string> Selectors, uint? Foreground, string? FontStyle);

/// <summary>A style of a VS Code colour theme's <c>semanticTokenColors</c>: a colour, and bold or italic where it says.</summary>
public sealed record VsCodeStyle(uint? Foreground, bool? Bold, bool? Italic);

/// <summary>A VS Code colour theme as read: its name, whether it is dark (one variant, whatever Passenger's setting), its
/// workbench colours by their keys (<c>editor.background</c>), its token colours in order, and its semantic token colours.</summary>
public sealed record VsCodeTheme(string Name, bool Dark, IReadOnlyDictionary<string, uint> Colors, IReadOnlyList<VsCodeRule> TokenColors,
    IReadOnlyDictionary<string, VsCodeStyle> SemanticColors, bool SemanticHighlighting);

/// <summary>A colour theme an extension contributes: its label, its kind (<c>vs</c>, <c>vs-dark</c>, <c>hc-black</c> or
/// <c>hc-light</c>) and its file's path inside the extension.</summary>
public sealed record VsCodeContribution(string Label, string UiTheme, string Path)
{
    /// <summary>Whether it is a dark theme.</summary>
    public bool Dark => UiTheme is "vs-dark" or "hc-black";
}

/// <summary>An extension as its <c>package.json</c> names it: <c>publisher.name</c>, its version and name for a person, and the
/// colour themes it contributes.</summary>
public sealed record VsCodeExtension(string Id, string Version, string DisplayName, IReadOnlyList<VsCodeContribution> Themes);

/// <summary>A VS Code colour theme's files taken from its extension: the manifest and the files the theme was read from,
/// by their paths in the extension, its version, and the theme as read.</summary>
public sealed record VsCodeTaken(IReadOnlyDictionary<string, byte[]> Files, string Version, VsCodeTheme Theme, IReadOnlyList<string> Problems);

/// <summary>Reads VS Code colour themes as data: a theme's JSON with comments and trailing commas as VS Code allows, the files
/// it <c>include</c>s, and a <c>.vsix</c> read as a zip for its <c>extension/package.json</c> and the theme files it names.
/// Nothing of an extension runs; every file is held to <see cref="ThemeFiles.MostBytes"/>, a <c>.vsix</c> to
/// <see cref="MostVsixBytes"/> and <see cref="MostEntries"/> entries, and a theme to <see cref="MostIncludes"/> files. A
/// file that is no JSON object, or past a limit, is refused whole, saying why; a colour or member of the wrong kind is passed
/// over and named. Pure.</summary>
public static partial class VsCodeThemes
{
    /// <summary>The most a <c>.vsix</c> may hold.</summary>
    public const long MostVsixBytes = 32L * 1024 * 1024;

    /// <summary>The most entries a <c>.vsix</c> may have.</summary>
    public const int MostEntries = 5000;

    /// <summary>The most files one theme may be read from, itself and those it includes.</summary>
    public const int MostIncludes = 8;

    /// <summary>The extension's files' folder inside a <c>.vsix</c>.</summary>
    public const string ExtensionFolder = "extension/";

    /// <summary>The manifest naming an extension's themes, in its folder.</summary>
    public const string Manifest = "package.json";

    private const int Deepest = 32;
    private const int MostRules = 5000;
    private const int MostThemes = 100;
    private const int LongestLabel = 128;

    /// <summary>A file of an extension by its path inside it (<c>theme/dark.json</c>), or why not.</summary>
    public delegate (byte[]? Bytes, string? Why) ExtensionFile(string path);

    /// <summary>An extension's manifest: its id, version, name and the colour themes it contributes (none where it
    /// contributes only other things).</summary>
    public static ThemeRead<VsCodeExtension> ReadManifest(string file, ReadOnlyMemory<byte> bytes) => Parse(file, bytes, (root, problems) =>
    {
        string? publisher = JsonMembers.Text(root, "publisher");
        string? name = JsonMembers.Text(root, "name");
        if (publisher is null || name is null || !IdWord().IsMatch(publisher) || !IdWord().IsMatch(name))
        {
            throw new FormatException("names no publisher and name an extension's id is made of");
        }
        string version = JsonMembers.Text(root, "version") is { Length: > 0 and <= 64 } given && !given.Any(char.IsControl) ? given : "0.0.0";
        string display = JsonMembers.Text(root, "displayName") is { Length: > 0 and <= LongestLabel } shown && !shown.Any(char.IsControl) && !shown.StartsWith('%') ? shown : name;
        List<VsCodeContribution> themes = [];
        IReadOnlyList<JsonElement> contributed = JsonMembers.Items(JsonMembers.Value(root, "contributes"), "themes");
        if (contributed.Count > MostThemes)
        {
            throw new FormatException($"contributes {contributed.Count} colour themes, more than the {MostThemes} Passenger reads of one extension");
        }
        foreach (JsonElement theme in contributed)
        {
            string? label = JsonMembers.Text(theme, "label") ?? JsonMembers.Text(theme, "id");
            string? path = JsonMembers.Text(theme, "path");
            string ui = JsonMembers.Text(theme, "uiTheme") ?? "vs-dark";
            if (label is not { Length: > 0 and <= LongestLabel } || label.Any(char.IsControl) || path is null || Inside(path, ".") is null
                || ui is not ("vs" or "vs-dark" or "hc-black" or "hc-light"))
            {
                problems.Add($"{file}: a colour theme it contributes ({Shown(theme)}) names no label, path inside the extension or kind Passenger reads, so it was passed over");
                continue;
            }
            themes.Add(new VsCodeContribution(label, ui, Inside(path, ".")!));
        }
        return new VsCodeExtension($"{publisher}.{name}", version, display, themes);
    });

    /// <summary>A theme an extension contributes, read from its file and those it includes, each through
    /// <paramref name="files"/>.</summary>
    public static ThemeRead<VsCodeTheme> ReadContributed(VsCodeContribution theme, ExtensionFile files) =>
        ReadTheme(theme.Path, theme.Label, theme.Dark, files);

    /// <summary>A theme from its file at <paramref name="path"/> among <paramref name="files"/>, the files it includes read
    /// beside it: named <paramref name="label"/> (else its own name, else its file's), dark as <paramref name="dark"/> says
    /// (else as its <c>type</c> says, dark where it says none).</summary>
    public static ThemeRead<VsCodeTheme> ReadTheme(string path, string? label, bool? dark, ExtensionFile files)
    {
        Dictionary<string, uint> colors = new(StringComparer.Ordinal);
        List<VsCodeRule> rules = [];
        Dictionary<string, VsCodeStyle> semantic = new(StringComparer.Ordinal);
        List<string> problems = [];
        bool? semanticHighlighting = null;
        string? ownName = null;
        string? type = null;
        int read = 0;
        string? Load(string file, int depth)
        {
            if (++read > MostIncludes)
            {
                return $"{path} includes more than the {MostIncludes} files Passenger reads of one theme";
            }
            (byte[]? bytes, string? why) = files(file);
            if (bytes is null)
            {
                return why;
            }
            ThemeRead<Parsed> parsed = Parse(file, bytes, (root, _) => new Parsed(root.Clone()));
            if (parsed.Read is not { Root: var root })
            {
                return parsed.Refused;
            }
            // What a file includes is laid first, so the file's own wins.
            if (JsonMembers.Text(root, "include") is { } include)
            {
                if (Inside(include, Folder(file)) is not { } included)
                {
                    return $"{file} includes {include}, which is not a file inside the extension";
                }
                if (Load(included, depth + 1) is { } refused)
                {
                    return refused;
                }
            }
            ownName = depth == 0 ? JsonMembers.Text(root, "name") : ownName;
            type = depth == 0 || type is null ? JsonMembers.Text(root, "type") ?? type : type;
            semanticHighlighting = JsonMembers.Bool(root, "semanticHighlighting") ?? semanticHighlighting;
            foreach (JsonProperty colour in JsonMembers.Properties(root, "colors"))
            {
                if (colour.Value.ValueKind == JsonValueKind.Null)
                {
                    colors.Remove(colour.Name);
                }
                else if (colour.Value.ValueKind == JsonValueKind.String && Colour(colour.Value.GetString()!) is { } argb)
                {
                    colors[colour.Name] = argb;
                }
                else
                {
                    problems.Add($"{file}: colors' {colour.Name} is {Shown(colour.Value)}, no colour Passenger reads, so it was passed over");
                }
            }
            JsonElement? tokens = JsonMembers.Value(root, "tokenColors");
            if (tokens is { ValueKind: JsonValueKind.String })
            {
                return $"{file} takes its token colours from {tokens.Value.GetString()}, a TextMate theme file, which Passenger does not read";
            }
            foreach (JsonElement rule in tokens is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray() : [])
            {
                if (rules.Count == MostRules)
                {
                    return $"{file} gives more than the {MostRules} token colours Passenger reads of one theme";
                }
                if (Rule(rule) is { } taken)
                {
                    rules.Add(taken);
                }
            }
            foreach (JsonProperty style in JsonMembers.Properties(root, "semanticTokenColors"))
            {
                if (Style(style.Value) is { } taken)
                {
                    semantic[style.Name] = taken;
                }
            }
            return null;
        }
        if (Load(path, 0) is { } refused)
        {
            return new ThemeRead<VsCodeTheme>(null, refused, problems);
        }
        if (colors.Count == 0 && rules.Count == 0)
        {
            return new ThemeRead<VsCodeTheme>(null, $"{path} gives no colors and no tokenColors: it is no VS Code colour theme", problems);
        }
        string name = label ?? (ownName is { Length: > 0 and <= LongestLabel } own && !own.Any(char.IsControl) ? own : System.IO.Path.GetFileNameWithoutExtension(path));
        bool isDark = dark ?? type is not ("light" or "hcLight");
        return new ThemeRead<VsCodeTheme>(new VsCodeTheme(name, isDark, colors, rules, semantic, semanticHighlighting ?? false), null, problems);
    }

    /// <summary>A theme of a <c>.vsix</c> by its label, with the files it was read from; why not where the <c>.vsix</c>, its
    /// manifest or the theme cannot be read or it contributes no theme of that label.</summary>
    public static (VsCodeTaken? Taken, string? Why) FromVsix(byte[] vsix, string label)
    {
        if (Vsix(vsix, out string? unzipped) is not { } files)
        {
            return (null, unzipped);
        }
        Dictionary<string, byte[]> read = new(StringComparer.Ordinal);
        (byte[]? Bytes, string? Why) Kept(string path)
        {
            (byte[]? bytes, string? why) = files(path);
            if (bytes is not null)
            {
                read[path] = bytes;
            }
            return (bytes, why);
        }
        (byte[]? manifest, string? unread) = Kept(Manifest);
        if (manifest is null)
        {
            return (null, unread);
        }
        ThemeRead<VsCodeExtension> extension = ReadManifest(Manifest, manifest);
        if (extension.Read is not { } named)
        {
            return (null, extension.Refused);
        }
        if (named.Themes.FirstOrDefault(t => t.Label == label) is not { } contributed)
        {
            return (null, $"{named.Id} {named.Version} contributes no colour theme labelled {label}"
                + (named.Themes.Count == 0 ? string.Empty : $": it contributes {string.Join(", ", named.Themes.Select(t => t.Label))}"));
        }
        ThemeRead<VsCodeTheme> theme = ReadContributed(contributed, Kept);
        return theme.Read is { } whole ? (new VsCodeTaken(read, named.Version, whole, [.. extension.Problems, .. theme.Problems]), null) : (null, theme.Refused);
    }

    /// <summary>A <c>.vsix</c>'s files, read from its zip by their paths inside its extension folder, each held to the most a
    /// theme file may be; null with why where it is no zip Passenger reads.</summary>
    public static ExtensionFile? Vsix(byte[] bytes, out string? why)
    {
        why = null;
        if (bytes.Length > MostVsixBytes)
        {
            why = $"the .vsix is {bytes.Length} bytes, more than the {MostVsixBytes} Passenger reads";
            return null;
        }
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        try
        {
            ZipArchive zip = new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MostEntries)
            {
                why = $"the .vsix holds {zip.Entries.Count} files, more than the {MostEntries} Passenger reads";
                return null;
            }
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.StartsWith(ExtensionFolder, StringComparison.Ordinal))
                {
                    entries[entry.FullName[ExtensionFolder.Length..]] = entry;
                }
            }
        }
        catch (InvalidDataException error)
        {
            why = $"the .vsix is no zip Passenger reads: {error.Message}";
            return null;
        }
        if (!entries.ContainsKey(Manifest))
        {
            why = $"the .vsix holds no {ExtensionFolder}{Manifest}: it is no VS Code extension";
            return null;
        }
        return path =>
        {
            if (!entries.TryGetValue(path, out ZipArchiveEntry? entry))
            {
                // A theme's path in its manifest may differ in letter case from the file, which VS Code finds on most systems.
                entry = entries.FirstOrDefault(e => e.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).Value;
                if (entry is null)
                {
                    return (null, $"the .vsix holds no {path}");
                }
            }
            if (entry.Length > ThemeFiles.MostBytes)
            {
                return (null, $"the .vsix's {path} is {entry.Length} bytes, more than the {ThemeFiles.MostBytes} a theme file may hold");
            }
            try
            {
                lock (entries)
                {
                    using Stream stream = entry.Open();
                    using MemoryStream read = new();
                    byte[] chunk = new byte[16384];
                    int got;
                    while ((got = stream.Read(chunk)) > 0)
                    {
                        if (read.Length + got > ThemeFiles.MostBytes)
                        {
                            return (null, $"the .vsix's {path} is more than the {ThemeFiles.MostBytes} bytes a theme file may hold");
                        }
                        read.Write(chunk, 0, got);
                    }
                    return (read.ToArray(), null);
                }
            }
            catch (InvalidDataException error)
            {
                return (null, $"the .vsix's {path} could not be read: {error.Message}");
            }
        };
    }

    /// <summary>A path inside an extension from one a file names, beside <paramref name="folder"/> (its folders joined by
    /// <c>/</c>, <c>./</c> and <c>../</c> taken as they go), or null where it leaves the extension or is no theme's file.</summary>
    public static string? Inside(string path, string folder)
    {
        if (path.Length is 0 or > 260 || path.Any(char.IsControl) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return null;
        }
        List<string> parts = [.. folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".")];
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }
        return parts.Count > 0 && parts[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? string.Join('/', parts) : null;
    }

    /// <summary>A VS Code colour, <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>, as <c>0xAARRGGBB</c>.</summary>
    public static uint? Colour(string text)
    {
        if (text.Length is not (4 or 5 or 7 or 9) || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }
        static uint Twice(uint nibble) => nibble << 4 | nibble;
        return text.Length switch
        {
            4 => 0xFF000000 | Twice(value >> 8 & 0xF) << 16 | Twice(value >> 4 & 0xF) << 8 | Twice(value & 0xF),
            5 => Twice(value & 0xF) << 24 | Twice(value >> 12 & 0xF) << 16 | Twice(value >> 8 & 0xF) << 8 | Twice(value >> 4 & 0xF),
            7 => 0xFF000000 | value,
            _ => (value & 0xFF) << 24 | value >> 8,
        };
    }

    private static VsCodeRule? Rule(JsonElement rule)
    {
        JsonElement? settings = JsonMembers.Object(rule, "settings");
        if (settings is null)
        {
            return null;
        }
        JsonElement? scope = JsonMembers.Value(rule, "scope");
        List<string> selectors = [];
        foreach (string named in scope switch
        {
            { ValueKind: JsonValueKind.String } one => [one.GetString()!],
            { ValueKind: JsonValueKind.Array } many => many.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!),
            _ => [],
        })
        {
            selectors.AddRange(named.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        uint? foreground = JsonMembers.Text(settings, "foreground") is { } colour ? Colour(colour) : null;
        string? fontStyle = JsonMembers.Text(settings, "fontStyle");
        return foreground is null && fontStyle is null ? null : new VsCodeRule(selectors, foreground, fontStyle);
    }

    private static VsCodeStyle? Style(JsonElement style)
    {
        if (style.ValueKind == JsonValueKind.String)
        {
            return Colour(style.GetString()!) is { } colour ? new VsCodeStyle(colour, null, null) : null;
        }
        if (style.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        uint? foreground = JsonMembers.Text(style, "foreground") is { } text ? Colour(text) : null;
        bool? bold = JsonMembers.Bool(style, "bold");
        bool? italic = JsonMembers.Bool(style, "italic");
        if (JsonMembers.Text(style, "fontStyle") is { } words)
        {
            bold ??= words.Contains("bold", StringComparison.Ordinal);
            italic ??= words.Contains("italic", StringComparison.Ordinal);
        }
        return foreground is null && bold is null && italic is null ? null : new VsCodeStyle(foreground, bold, italic);
    }

    private static ThemeRead<T> Parse<T>(string file, ReadOnlyMemory<byte> bytes, Func<JsonElement, List<string>, T> read) where T : class
    {
        if (bytes.Length > ThemeFiles.MostBytes)
        {
            return new ThemeRead<T>(null, $"{file} is {bytes.Length} bytes, more than the {ThemeFiles.MostBytes} a theme file may hold", []);
        }
        List<string> problems = [];
        try
        {
            ReadOnlyMemory<byte> text = bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = Deepest, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("is no JSON object");
            }
            return new ThemeRead<T>(read(document.RootElement, problems), null, problems);
        }
        catch (JsonException e)
        {
            return new ThemeRead<T>(null, $"{file} is not JSON as VS Code reads it: {e.Message}", []);
        }
        catch (FormatException e)
        {
            return new ThemeRead<T>(null, $"{file} {e.Message}", []);
        }
    }

    /// <summary>A file's JSON, its own copy apart from the document it was read from.</summary>
    private sealed record Parsed(JsonElement Root);

    private static string Folder(string file) => file.LastIndexOf('/') is >= 0 and var slash ? file[..slash] : string.Empty;

    private static string Shown(JsonElement value)
    {
        string raw = value.GetRawText();
        return raw.Length <= 40 ? raw : string.Concat(raw.AsSpan(0, 39), "…");
    }

    // A publisher's or an extension's name as VS Code takes them: letters, digits, '-' and '_'.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex IdWord();
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Text;

/// <summary>What the .editorconfig files over a file say of it (editorconfig.org): each from the file's folder up to the
/// first marked <c>root = true</c>, the farthest read first, so the nearest file's sections, and a file's later ones, win
/// where they match the file; <c>unset</c> takes a property back. Read afresh each time, so a change on disk is seen.</summary>
public sealed class EditorConfig
{
    public const string FileName = ".editorconfig";

    /// <summary>Nothing said: no .editorconfig over the file.</summary>
    public static readonly EditorConfig None = new([]);

    /// <summary>Each glob as a regular expression, and the ranges its numbers must fall in, in the order they appear.</summary>
    private static readonly ConcurrentDictionary<string, (Regex Regex, IReadOnlyList<(long Min, long Max)> Ranges)> Globs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (string Value, string From)> properties;

    private EditorConfig(Dictionary<string, (string Value, string From)> properties) => this.properties = properties;

    /// <summary>The properties the .editorconfig files give a file at a (canonical) path.</summary>
    public static EditorConfig For(string path)
    {
        List<(string Folder, ConfigFile Config)> chain = [];
        for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Read(Path.Combine(folder, FileName)) is { } config)
            {
                chain.Add((folder, config));
                if (config.Root)
                {
                    break;
                }
            }
        }
        Dictionary<string, (string Value, string From)> properties = new(StringComparer.Ordinal);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            (string folder, ConfigFile config) = chain[i];
            string relative = "/" + Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
            foreach (Section section in config.Sections.Where(s => Matches(s.Glob, relative)))
            {
                foreach ((string key, string value) in section.Pairs)
                {
                    if (Word(value) == "unset")
                    {
                        properties.Remove(key);
                    }
                    else
                    {
                        properties[key] = (value, Path.Combine(folder, FileName));
                    }
                }
            }
        }
        return properties.Count == 0 ? None : new EditorConfig(properties);
    }

    /// <summary>A property's value as written, or null when no section gives it.</summary>
    public string? this[string name] => properties.TryGetValue(name, out (string Value, string From) found) ? found.Value : null;

    /// <summary>The indentation <c>indent_style</c>, <c>indent_size</c> and <c>tab_width</c> give, over
    /// <paramref name="fallback"/> for what they leave out (<c>tab_width</c> is <c>indent_size</c>'s unless given, and a
    /// tab's indent a tab wide unless sized); null when none of them is given.</summary>
    public IndentOptions? Indent(IndentOptions fallback)
    {
        bool? tabs = Word(this["indent_style"]) switch
        {
            "tab" => true,
            "space" => false,
            _ => null,
        };
        string? size = Word(this["indent_size"]);
        int? sized = Width(size);
        int? tabWidth = Width(Word(this["tab_width"]));
        if (tabs is null && sized is null && tabWidth is null && size != "tab")
        {
            return null;
        }
        int tab = tabWidth ?? sized ?? fallback.TabWidth;
        bool useTabs = tabs ?? fallback.UseTabs;
        int indent = sized ?? (size == "tab" || tabs == true ? tab : fallback.Size);
        return new IndentOptions(indent, useTabs, tab);
    }

    /// <summary>The .editorconfig that gave the indentation (the nearest of those that did), for saying where it came from.</summary>
    public string? IndentFrom => new[] { "indent_style", "indent_size", "tab_width" }
        .Select(name => properties.TryGetValue(name, out (string Value, string From) found) ? found.From : null)
        .OfType<string>()
        .OrderByDescending(file => file.Length)
        .FirstOrDefault();

    /// <summary>The line separator <c>end_of_line</c> asks for; null for none.</summary>
    public LineSeparator? EndOfLine => Word(this["end_of_line"]) switch
    {
        "lf" => LineSeparator.LF,
        "crlf" => LineSeparator.CRLF,
        "cr" => LineSeparator.CR,
        _ => null,
    };

    public bool TrimTrailingWhitespace => Word(this["trim_trailing_whitespace"]) == "true";

    public bool InsertFinalNewline => Word(this["insert_final_newline"]) == "true";

    /// <summary>The encoding <c>charset</c> asks a file be written in (UTF-16 with its BOM, the only way it is read back as
    /// UTF-16); null for none.</summary>
    public FileEncoding? Charset => Word(this["charset"]) switch
    {
        "latin1" => FileEncoding.Latin1,
        "utf-8" => FileEncoding.Utf8,
        "utf-8-bom" => FileEncoding.Utf8Bom,
        "utf-16be" => FileEncoding.Utf16BigEndian,
        "utf-16le" => FileEncoding.Utf16,
        _ => null,
    };

    /// <summary>What a property says and the .editorconfig it comes from, as the status bar's tips name it
    /// ("end_of_line = lf in /src/.editorconfig"); null when no section gives it.</summary>
    public string? Said(string name) =>
        properties.TryGetValue(name, out (string Value, string From) found) ? $"{name} = {Word(found.Value)} in {found.From}" : null;

    /// <summary>A value's first word, lower case: the known values are single words, and what follows one (an inline
    /// comment, which older versions of the format allowed) is not part of it.</summary>
    private static string? Word(string? value)
    {
        if (value is null)
        {
            return null;
        }
        int space = value.AsSpan().IndexOfAny(' ', '\t');
        return (space < 0 ? value : value[..space]).ToLowerInvariant();
    }

    /// <summary>A width in columns, or null for a value that is not one.</summary>
    private static int? Width(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int width) && width is >= 1 and <= 32 ? width : null;

    private sealed record Section(string Glob, List<(string Key, string Value)> Pairs);

    private sealed record ConfigFile(bool Root, List<Section> Sections);

    /// <summary>A .editorconfig read, or null when there is none to read: comments (<c>#</c> or <c>;</c> starting a line),
    /// <c>[glob]</c> section headers, and <c>key = value</c> pairs, keys in any case; <c>root</c> counts only before
    /// the first section, and a line that is none of these is passed over.</summary>
    private static ConfigFile? Read(string file)
    {
        string text;
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            text = File.ReadAllText(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        bool root = false;
        List<Section> sections = [];
        Section? current = null;
        bool inSections = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }
            if (line[0] == '[')
            {
                inSections = true;
                current = line.Length > 2 && line[^1] == ']' ? new Section(line[1..^1], []) : null;
                if (current is not null)
                {
                    sections.Add(current);
                }
                continue;
            }
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }
            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();
            if (current is not null)
            {
                current.Pairs.Add((key, value));
            }
            else if (!inSections && key == "root")
            {
                root = Word(value) == "true";
            }
        }
        return new ConfigFile(root, sections);
    }

    /// <summary>Whether a section's glob matches a file, by its path from the .editorconfig's folder ("/src/A.cs"): a
    /// glob with no '/' matches the name in any folder below, one with a '/' the path from the folder.</summary>
    public static bool Matches(string glob, string relative)
    {
        string pattern = glob.Contains('/', StringComparison.Ordinal) ? (glob.StartsWith('/') ? glob : "/" + glob) : "/**/" + glob;
        (Regex regex, IReadOnlyList<(long Min, long Max)> ranges) = Globs.GetOrAdd(pattern, Compile);
        Match match = regex.Match(relative);
        if (!match.Success)
        {
            return false;
        }
        for (int i = 0; i < ranges.Count; i++)
        {
            Group number = match.Groups["n" + i.ToString(CultureInfo.InvariantCulture)];
            if (!number.Success)
            {
                continue;
            }
            string digits = number.Value.TrimStart('+', '-');
            // "{1..10}" matches "07" no more than a shell's brace expansion would make it.
            if ((digits.Length > 1 && digits[0] == '0') || !long.TryParse(number.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
                || value < ranges[i].Min || value > ranges[i].Max)
            {
                return false;
            }
        }
        return true;
    }

    private static (Regex Regex, IReadOnlyList<(long Min, long Max)> Ranges) Compile(string pattern)
    {
        List<(long Min, long Max)> ranges = [];
        string regex = Translate(pattern, ranges);
        return (new Regex(@"\A" + regex + @"\z", RegexOptions.CultureInvariant | RegexOptions.Singleline), ranges);
    }

    /// <summary>A glob as a regular expression: <c>*</c> any characters but '/', <c>**</c> any at all ("/**/" none too),
    /// <c>?</c> one but '/', <c>[name]</c> one of those (<c>[!name]</c> none), <c>{s1,s2}</c> any of those globs,
    /// <c>{num1..num2}</c> a whole number between (checked once matched), and a backslash takes the next character as
    /// it is. A bracket or brace with no end is itself.</summary>
    private static string Translate(string pattern, List<(long Min, long Max)> ranges)
    {
        StringBuilder regex = new();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            switch (c)
            {
                case '\\' when i + 1 < pattern.Length:
                    regex.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '/' when pattern.AsSpan(i + 1).StartsWith("**/", StringComparison.Ordinal):
                    regex.Append("(?:/|/.*/)");
                    i += 3;
                    break;
                case '[' when ClassEnd(pattern, i) is int close and > 0:
                    regex.Append(CharacterClass(pattern[(i + 1)..close]));
                    i = close;
                    break;
                case '{' when BraceEnd(pattern, i) is int close and > 0:
                    string inner = pattern[(i + 1)..close];
                    Match range = Regex.Match(inner, @"^([+-]?\d+)\.\.([+-]?\d+)$", RegexOptions.CultureInvariant);
                    List<string> choices = Choices(inner);
                    if (range.Success && long.TryParse(range.Groups[1].Value, CultureInfo.InvariantCulture, out long first)
                        && long.TryParse(range.Groups[2].Value, CultureInfo.InvariantCulture, out long last))
                    {
                        regex.Append(CultureInfo.InvariantCulture, $"(?<n{ranges.Count}>[+-]?[0-9]+)");
                        ranges.Add((Math.Min(first, last), Math.Max(first, last)));
                    }
                    else if (choices.Count < 2)
                    {
                        // One choice is no choice: the braces are themselves.
                        regex.Append(@"\{").Append(Translate(inner, ranges)).Append(@"\}");
                    }
                    else
                    {
                        regex.Append("(?:").AppendJoin('|', choices.Select(choice => Translate(choice, ranges))).Append(')');
                    }
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return regex.ToString();
    }

    /// <summary>Where the ']' closing a '[' at <paramref name="open"/> is, or -1 when none does before a '/'.</summary>
    private static int ClassEnd(string pattern, int open)
    {
        for (int i = open + 1; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '/':
                    return -1;
                case ']' when i > open + 1:
                    return i;
            }
        }
        return -1;
    }

    /// <summary>A bracket's characters as a class of the regular expression, which never matches '/'.</summary>
    private static string CharacterClass(string inside)
    {
        StringBuilder regex = new("[");
        int i = 0;
        if (inside.Length > 0 && inside[0] is '!' or '^')
        {
            regex.Append("^/");
            i = 1;
        }
        for (; i < inside.Length; i++)
        {
            char c = inside[i];
            if (c == '\\' && i + 1 < inside.Length)
            {
                c = inside[++i];
                regex.Append('\\').Append(c);
            }
            else if (c is '[' or ']' or '^' or '\\')
            {
                regex.Append('\\').Append(c);
            }
            else
            {
                regex.Append(c);
            }
        }
        return regex.Append(']').ToString();
    }

    /// <summary>Where the '}' closing a '{' at <paramref name="open"/> is, braces inside counted, or -1 when none does.</summary>
    private static int BraceEnd(string pattern, int open)
    {
        int depth = 0;
        for (int i = open; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }
        return -1;
    }

    /// <summary>A brace's choices: its text split at the commas not inside another brace nor escaped.</summary>
    private static List<string> Choices(string inner)
    {
        List<string> choices = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '\\':
                    i++;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    choices.Add(inner[start..i]);
                    start = i + 1;
                    break;
            }
        }
        choices.Add(inner[start..]);
        return choices;
    }
}

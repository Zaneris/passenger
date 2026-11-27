using System.Collections.Concurrent;
using System.Globalization;
using Passenger.Core;

namespace Passenger.Text;

/// <summary>What the .editorconfig files over a file say of it (editorconfig.org): each from the file's folder up to the
/// first marked <c>root = true</c>, in the person's folders (<see cref="FoldersUp"/>), the farthest read first, so the
/// nearest file's sections, and a file's later ones, win where they match the file; <c>unset</c> takes a property back.
/// Read afresh each time, so a change on disk is seen. A section whose glob cannot be used (<see cref="Refused"/>) matches
/// no file, the log naming it once.</summary>
public sealed class EditorConfig
{
    public const string FileName = ".editorconfig";

    /// <summary>Nothing said: no .editorconfig over the file.</summary>
    public static readonly EditorConfig None = new([]);

    /// <summary>Each section's glob compiled, by its text as written.</summary>
    private static readonly ConcurrentDictionary<string, PathGlob> Globs = new(StringComparer.Ordinal);

    /// <summary>The sections matching no file the log has named, by their .editorconfig and glob.</summary>
    private static readonly ConcurrentDictionary<(string File, string Glob), bool> Told = new();

    private readonly Dictionary<string, (string Value, string From)> properties;

    private EditorConfig(Dictionary<string, (string Value, string From)> properties) => this.properties = properties;

    /// <summary>The properties the .editorconfig files give a file at a (canonical) path.</summary>
    public static EditorConfig For(string path)
    {
        List<(string Folder, ConfigText Config)> chain = [];
        foreach (string folder in Path.GetDirectoryName(path) is { } start ? FoldersUp.From(start) : [])
        {
            if (FoldersUp.Found(folder, FileName) is { } file && Read(file) is { } config)
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
            (string folder, ConfigText config) = chain[i];
            string file = Path.Combine(folder, FileName);
            string relative = "/" + PathNames.Relative(path, folder);
            foreach (ConfigSection section in config.Sections)
            {
                PathGlob glob = Compiled(section.Glob);
                if (glob.Refused is { } why && Told.TryAdd((file, section.Glob), true))
                {
                    PassengerLog.Write($"No file matches the section [{section.Glob}] on line {section.Line} of {file}: {why}");
                }
                if (!glob.Matches(relative))
                {
                    continue;
                }
                foreach ((string key, string value, _, _, _) in section.Pairs)
                {
                    if (Word(value) == "unset")
                    {
                        properties.Remove(key);
                    }
                    else
                    {
                        properties[key] = (value, file);
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
        int? tabWidth = Width(this["tab_width"]);
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
    public LineSeparator? EndOfLine => Word(this["end_of_line"]) is { } word && EndsOfLine.TryGetValue(word, out LineSeparator separator) ? separator : null;

    /// <summary>The line separators <c>end_of_line</c> names, by its words.</summary>
    private static readonly Dictionary<string, LineSeparator> EndsOfLine = new(StringComparer.Ordinal)
    {
        ["lf"] = LineSeparator.LF,
        ["crlf"] = LineSeparator.CRLF,
        ["cr"] = LineSeparator.CR,
    };

    public bool TrimTrailingWhitespace => Flag(this["trim_trailing_whitespace"]) == true;

    public bool InsertFinalNewline => FinalNewline == true;

    /// <summary>What <c>insert_final_newline</c> asks of a file's end: a line break (true), none (false), or nothing (null).</summary>
    public bool? FinalNewline => Flag(this["insert_final_newline"]);

    /// <summary>The encoding <c>charset</c> asks a file be written in (UTF-16 with its BOM, the only way it is read back as
    /// UTF-16); null for none.</summary>
    public FileEncoding? Charset => Word(this["charset"]) is { } word && Charsets.TryGetValue(word, out FileEncoding? encoding) ? encoding : null;

    /// <summary>The encodings <c>charset</c> names, by its words.</summary>
    private static readonly Dictionary<string, FileEncoding> Charsets = new(StringComparer.Ordinal)
    {
        ["latin1"] = FileEncoding.Latin1,
        ["utf-8"] = FileEncoding.Utf8,
        ["utf-8-bom"] = FileEncoding.Utf8Bom,
        ["utf-16be"] = FileEncoding.Utf16BigEndian,
        ["utf-16le"] = FileEncoding.Utf16,
    };

    /// <summary>The value that takes a key back, whatever the key.</summary>
    public const string Unset = "unset";

    /// <summary>The keys editorconfig.org's format defines and Passenger reads, each with what it does and the values it
    /// takes (none listed for a number's or a name's), as an .editorconfig's completion and hover card say them; <c>root</c>
    /// is the file's own, before its first section.</summary>
    public static readonly IReadOnlyList<StandardKey> Standard =
    [
        new("root", "Whether this is the top .editorconfig: none above its folder is read.", ["true", "false"], Numbered: false),
        new("indent_style", "Whether a line is indented with tabs or spaces.", ["tab", "space"], Numbered: false),
        new("indent_size", "How many columns an indent is, or tab for tab_width's.", ["tab"], Numbered: true),
        new("tab_width", "How many columns a tab is; indent_size's unless given.", [], Numbered: true),
        new("end_of_line", "The line break a save writes.", [.. EndsOfLine.Keys], Numbered: false),
        new("charset", "The encoding a save writes.", [.. Charsets.Keys], Numbered: false),
        new("trim_trailing_whitespace", "Whether a save takes the white space off each line's end.", ["true", "false"], Numbered: false),
        new("insert_final_newline", "Whether a save ends the file with a line break (true) or none (false).", ["true", "false"], Numbered: false),
    ];

    /// <summary>What a property says and the .editorconfig it comes from, as the status bar's tips name it
    /// ("end_of_line = lf in /src/.editorconfig"); null when no section gives it.</summary>
    public string? Said(string name) =>
        properties.TryGetValue(name, out (string Value, string From) found) ? $"{name} = {Word(found.Value)} in {found.From}" : null;

    /// <summary>A value's first word, lower case, or null for none: the values read are single words, so what follows one
    /// after a space, '#' or ';' (an inline comment, which older versions of the format allowed) is not part of it.</summary>
    public static string? Word(string? value)
    {
        ReadOnlySpan<char> text = value.AsSpan().TrimStart();
        int end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('#' or ';'))
        {
            end++;
        }
        return end == 0 ? null : text[..end].ToString().ToLowerInvariant();
    }

    /// <summary>A value's word as true or false, in any letter case, or null for one that is neither.</summary>
    public static bool? Flag(string? value) => Word(value) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>A value's word as a whole number written in digits alone, or null for one that is not.</summary>
    public static int? Number(string? value) =>
        int.TryParse(Word(value), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;

    /// <summary>A width in columns, or null for a value that is not one.</summary>
    private static int? Width(string? value) => Number(value) is int width and >= 1 and <= 32 ? width : null;

    /// <summary>A key editorconfig.org's format defines: its name, what it does, the words it takes, and whether it takes a
    /// number too.</summary>
    public sealed record StandardKey(string Name, string Said, IReadOnlyList<string> Values, bool Numbered);

    /// <summary>A key a section sets as its text has it: the key in lower case, its value as written, the line it is on (from
    /// 1), and where the key and the value are in the text.</summary>
    public readonly record struct ConfigPair(string Key, string Value, int Line, TextRange KeyAt, TextRange ValueAt);

    /// <summary>A section as its text has it: its glob, the line its header is on (from 1), where the glob is in the text,
    /// and its pairs.</summary>
    public sealed record ConfigSection(string Glob, int Line, TextRange GlobAt, IReadOnlyList<ConfigPair> Pairs);

    /// <summary>An .editorconfig's text as its reader takes it (<see cref="Lines"/>): whether it is marked <c>root = true</c>,
    /// the pairs before its first section, and its sections.</summary>
    public sealed record ConfigText(bool Root, IReadOnlyList<ConfigPair> Preamble, IReadOnlyList<ConfigSection> Sections)
    {
        /// <summary>The section an offset of the text is in, the header's line included; null before the first.</summary>
        public ConfigSection? SectionAt(int offset) => Sections.LastOrDefault(s => s.GlobAt.Start - 1 <= offset);
    }

    /// <summary>A key a section sets: the section's glob as written, the line the key is on (from 1), the key in lower case
    /// and its value as written.</summary>
    public readonly record struct Pair(string Glob, int Line, string Key, string Value);

    /// <summary>What an .editorconfig says: whether it is marked <c>root = true</c>, and each key its sections set, in the
    /// file's order.</summary>
    public sealed record Written(bool Root, IReadOnlyList<Pair> Pairs);

    /// <summary>What the .editorconfig at a path says, read as <see cref="For"/> reads it; null where there is none to read.</summary>
    public static Written? WrittenIn(string file) =>
        Read(file) is { } config
            ? new Written(config.Root, [.. config.Sections.SelectMany(s => s.Pairs.Select(p => new Pair(s.Glob, p.Line, p.Key, p.Value)))])
            : null;

    /// <summary>A section of an .editorconfig's text that matches no file: where its glob is in the text, and why.</summary>
    public readonly record struct RefusedSection(TextRange Glob, string Why);

    /// <summary>The sections of an .editorconfig's text whose glob cannot be used, so they match no file: one whose
    /// brackets hold a range in reverse order (<c>[z-a]</c>), or whose braces nest too deep.</summary>
    public static IReadOnlyList<RefusedSection> Refused(string text) => Refused(Lines(text));

    /// <summary>The sections of an .editorconfig's text, read, whose glob cannot be used (<see cref="Refused(string)"/>).</summary>
    public static IReadOnlyList<RefusedSection> Refused(ConfigText text) =>
        [.. text.Sections.Select(s => (Section: s, Compiled(s.Glob).Refused)).Where(s => s.Refused is not null)
            .Select(s => new RefusedSection(s.Section.GlobAt, s.Refused!))];

    /// <summary>A .editorconfig read, or null when there is none to read.</summary>
    private static ConfigText? Read(string file)
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
        return Lines(text);
    }

    /// <summary>An .editorconfig's text read: comments (<c>#</c> or <c>;</c> starting a line), <c>[glob]</c> section
    /// headers, and <c>key = value</c> pairs, keys in any case; <c>root</c> counts only before the first section, and a
    /// line that is none of these is passed over, as is a pair under a header that is no section's.</summary>
    public static ConfigText Lines(string text)
    {
        bool root = false;
        List<ConfigPair> preamble = [];
        List<ConfigSection> sections = [];
        List<ConfigPair>? current = null;
        bool inSections = false;
        int next = 0;
        for (int number = 1; next <= text.Length; number++)
        {
            int end = text.IndexOf('\n', next);
            end = end < 0 ? text.Length : end;
            string raw = text[next..end];
            int start = next + raw.Length - raw.TrimStart().Length;
            next = end + 1;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }
            if (line[0] == '[')
            {
                inSections = true;
                current = null;
                if (line.Length > 2 && line[^1] == ']')
                {
                    current = [];
                    sections.Add(new ConfigSection(line[1..^1], number, new TextRange(start + 1, line.Length - 2), current));
                }
                continue;
            }
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }
            string written = line[..equals].TrimEnd();
            string value = line[(equals + 1)..].Trim();
            int valueStart = start + equals + 1 + (line.Length - equals - 1 - line[(equals + 1)..].TrimStart().Length);
            ConfigPair pair = new(written.ToLowerInvariant(), value, number, new TextRange(start, written.Length), new TextRange(valueStart, value.Length));
            if (current is not null)
            {
                current.Add(pair);
            }
            else if (!inSections)
            {
                preamble.Add(pair);
                if (pair.Key == "root")
                {
                    root = Flag(value) == true;
                }
            }
        }
        return new ConfigText(root, preamble, sections);
    }

    /// <summary>Whether a section's glob matches a file, by its path from the .editorconfig's folder ("/src/A.cs"): a
    /// glob with no '/' matches the name in any folder below, one with a '/' the path from the folder.</summary>
    public static bool Matches(string glob, string relative) => Compiled(glob).Matches(relative);

    /// <summary>A section's glob compiled once, as the pattern its '/' makes it.</summary>
    private static PathGlob Compiled(string glob) =>
        Globs.GetOrAdd(glob, static written => PathGlob.Of(written.Contains('/', StringComparison.Ordinal) ? (written.StartsWith('/') ? written : "/" + written) : "/**/" + written));
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Localization;

/// <summary>How a language's strings are kept: a .resx (the neutral one or a satellite), or an XLIFF 1.2 file of Arcade's
/// XliffTasks (<c>xlf/Name.fr.xlf</c> beside the .resx), which makes the satellites as it builds.</summary>
public enum ResourceFormat
{
    Resx,
    Xliff,
}

/// <summary>One key's entry in a language's file: its name and where it is written (a <c>&lt;data name&gt;</c>'s value, a
/// <c>&lt;trans-unit id&gt;</c>'s), the value (a .resx's <c>&lt;value&gt;</c>, an XLIFF unit's <c>&lt;target&gt;</c>, null with
/// none), an XLIFF target's state, the whole entry's stretch, and a .resx's comment.</summary>
public sealed record ResourceEntry(string Name, string? Value, string? State, int NameStart, int NameEnd, int Start, int End, string? Comment = null)
{
    /// <summary>Whether it gives its key in its language: a .resx entry always, an XLIFF unit once its target is there and no
    /// longer <c>new</c>.</summary>
    public bool Translated => Value is not null && State != "new";
}

/// <summary>A language's file of a resource: its culture (empty for the neutral .resx), how it keeps its strings, and its entries,
/// read from its text as it was when read (an open file's as the editor has it).</summary>
public sealed record ResourceFile(string Path, string Culture, ResourceFormat Format, IReadOnlyList<ResourceEntry> Entries)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The language as a person reads it ("French", "French (Canada)"), or "the neutral language" for the neutral file.</summary>
    public string Language => Culture.Length == 0 ? "the neutral language" : ResourceFiles.LanguageOf(Culture);

    /// <summary>A key's entry, as ResourceManager looks it up (its letter case counts); the first of two of one name.</summary>
    public ResourceEntry? Entry(string key) => Entries.FirstOrDefault(e => e.Name == key);
}

/// <summary>Every file of one resource, its neutral .resx and each language's: what a localizer, a generated resource class or
/// XAML's <c>{x:Static}</c> of one reads. <see cref="BasePath"/> is its folder and name, no culture or extension.</summary>
public sealed record ResourceSet(string BasePath, IReadOnlyList<ResourceFile> Files)
{
    public string Name => Path.GetFileName(BasePath);

    public ResourceFile? Neutral => Files.FirstOrDefault(f => f.Culture.Length == 0);

    /// <summary>The cultures its files give, each once, in order of their names.</summary>
    public IReadOnlyList<string> Cultures => [.. Files.Where(f => f.Culture.Length > 0).Select(f => f.Culture).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every key any of its files has, each once, in order.</summary>
    public IReadOnlyList<string> Keys => [.. Files.SelectMany(f => f.Entries).Select(e => e.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>How a culture gives a key: the file and entry that translate it; else the file where it is missing (its XLIFF
    /// file over a satellite, as XliffTasks makes the satellite from it) and the untranslated entry there, if any.</summary>
    public (ResourceFile File, ResourceEntry? Entry, bool Translated) In(string culture, string key)
    {
        ResourceFile[] files = [.. Files.Where(f => f.Culture.Equals(culture, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Format == ResourceFormat.Xliff ? 0 : 1)];
        foreach (ResourceFile file in files)
        {
            if (file.Entry(key) is { Translated: true } entry)
            {
                return (file, entry, true);
            }
        }
        return (files[0], files[0].Entry(key), false);
    }

    /// <summary>Each language its files have that lacks a key: the file to give it in, and whether it is there untranslated (an
    /// XLIFF unit still new).</summary>
    public IEnumerable<ResourceGap> Lacking(string key)
    {
        foreach (string culture in Cultures)
        {
            (ResourceFile file, ResourceEntry? entry, bool translated) = In(culture, key);
            if (!translated)
            {
                yield return new ResourceGap(key, BasePath, file, culture, entry is not null);
            }
        }
    }

    /// <summary>What a key is in each language, the neutral file's first: its value, or that it has none, is not
    /// translated yet or wants review.</summary>
    public IReadOnlyList<string> Languages(string key)
    {
        List<string> lines = [];
        if (Neutral is { } neutral)
        {
            lines.Add(neutral.Entry(key) is { } given ? $"neutral: {given.Value}" : $"neutral: none ({neutral.Name})");
        }
        foreach (string culture in Cultures)
        {
            (ResourceFile file, ResourceEntry? entry, bool translated) = In(culture, key);
            lines.Add(translated ? $"{culture}: {entry!.Value}" + (entry.State is { } state && state.StartsWith("needs-review", StringComparison.Ordinal) ? " (needs review)" : string.Empty)
                : entry is not null ? $"{culture}: not translated yet ({file.Name})"
                : $"{culture}: none ({file.Name})");
        }
        return lines;
    }
}

/// <summary>A key a language of a resource lacks: the key, the resource's base path, the file to give it in, the culture,
/// and whether the file has it untranslated (an XLIFF unit still new).</summary>
public sealed record ResourceGap(string Key, string BasePath, ResourceFile File, string Culture, bool Untranslated)
{
    /// <summary>The words of a warning of it.</summary>
    public string Message => Untranslated ? $"'{Key}' is not translated to {File.Language} yet ({File.Name})" : $"'{Key}' has no {File.Language} translation ({File.Name})";
}

/// <summary>A resource's files found and read: where a resource's files are by its base path (<see cref="Of"/>) or by the
/// name ResourceManager looks it up by (<see cref="Named"/>, as MSBuild names an embedded .resx), and each language's
/// entries with their places. A file on disk is read again only once its length or time changed, an open one's text once
/// it changed; both read on any thread, so off the UI thread.</summary>
public static class ResourceFiles
{
    private static readonly FileMemo<ResourceFile> Read = new();
    private static readonly ConditionalWeakTable<TextSnapshot, ResourceFile> ReadOpen = [];
    private static readonly Lazy<HashSet<string>> KnownCultures = new(() =>
        new HashSet<string>(CultureInfo.GetCultures(CultureTypes.AllCultures).Select(c => c.Name).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether a file keeps a language's strings: a .resx or an XLIFF file.</summary>
    public static bool Holds(string path) =>
        path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every file of the resource at a base path (<c>Pages/Home</c>): <c>Home.resx</c>, each <c>Home.&lt;culture&gt;.resx</c>,
    /// and each <c>xlf/Home.&lt;culture&gt;.xlf</c> beside them; a file the editor has open read as it has it (<paramref name="open"/>,
    /// null for one not open), the rest from the disk. Empty where it has none.</summary>
    public static ResourceSet Of(string basePath, Func<string, TextSnapshot?>? open = null)
    {
        string folder = Path.GetDirectoryName(basePath) ?? string.Empty;
        string name = Path.GetFileName(basePath);
        List<ResourceFile> files = [];
        foreach (string path in FilesIn(folder, name + ".*resx"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string culture = stem.Length == name.Length ? string.Empty : CultureOf(stem);
            if (stem.Length == name.Length || culture.Length > 0 && stem.Length == name.Length + 1 + culture.Length && stem.StartsWith(name + ".", StringComparison.Ordinal))
            {
                if (Load(path, culture, ResourceFormat.Resx, open) is { } file)
                {
                    files.Add(file);
                }
            }
        }
        foreach (string path in FilesIn(Path.Combine(folder, "xlf"), name + ".*.xlf"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string culture = CultureOf(stem);
            if (culture.Length > 0 && stem.Length == name.Length + 1 + culture.Length && Load(path, culture, ResourceFormat.Xliff, open) is { } file)
            {
                files.Add(file);
            }
        }
        return new ResourceSet(basePath, [.. files.OrderBy(f => f.Culture, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Format)]);
    }

    /// <summary>A resource's files: those at the base paths beside the code that names it (a Designer file's .resx) first, else
    /// those MSBuild embeds under the name ResourceManager looks it up by, under its project's folder; null where none are.</summary>
    public static ResourceSet? Find(IEnumerable<string> beside, string projectFolder, string rootNamespace, string name, Func<string, TextSnapshot?>? open = null)
    {
        foreach (string basePath in beside.Concat(Named(projectFolder, rootNamespace, name)))
        {
            if (Of(basePath, open) is { Files.Count: > 0 } set)
            {
                return set;
            }
        }
        return null;
    }

    /// <summary>The base paths, under a project's folder, of the resources MSBuild embeds by a name (<c>Shop.Pages.Home</c>): a
    /// .resx is named its project's root namespace, its folders as identifiers (a '-' or a space an '_') and its file name as
    /// written, each joined by a '.', its culture left off (CreateManifestResourceName).</summary>
    public static IReadOnlyList<string> Named(string projectFolder, string rootNamespace, string name)
    {
        string rest = rootNamespace.Length == 0 ? name
            : name.StartsWith(rootNamespace + ".", StringComparison.Ordinal) ? name[(rootNamespace.Length + 1)..] : string.Empty;
        List<string> found = [];
        if (rest.Length > 0)
        {
            Walk(projectFolder, rest.Split('.'), 0, found, 0);
        }
        return found;
    }

    private static void Walk(string folder, string[] segments, int at, List<string> found, int depth)
    {
        string named = string.Join('.', segments[at..]);
        foreach (string path in FilesIn(folder, "*.*resx").Concat(FilesIn(Path.Combine(folder, "xlf"), "*.xlf")))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string culture = CultureOf(stem);
            string neutral = culture.Length > 0 ? stem[..^(culture.Length + 1)] : stem;
            string basePath = Path.GetFileName(Path.GetDirectoryName(path)) == "xlf" && path.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(folder, neutral) : Path.Combine(Path.GetDirectoryName(path)!, neutral);
            if (neutral == named && !found.Contains(basePath, StringComparer.Ordinal))
            {
                found.Add(basePath);
            }
        }
        if (depth > 16 || at >= segments.Length - 1)
        {
            return;
        }
        foreach (string sub in FoldersIn(folder))
        {
            string[] parts = Identifier(Path.GetFileName(sub)).Split('.');
            if (parts.Length < segments.Length - at && parts.Select((p, i) => p == segments[at + i]).All(same => same))
            {
                Walk(sub, segments, at + parts.Length, found, depth + 1);
            }
        }
    }

    /// <summary>A folder's name as MSBuild writes it in a resource's name (MakeValidEverettFolderIdentifier): each part between
    /// dots made an identifier, a character that cannot be in one an '_', one that cannot start one put after an '_'.</summary>
    internal static string Identifier(string name)
    {
        StringBuilder made = new(name.Length + 1);
        string[] parts = name.Split('.');
        for (int p = 0; p < parts.Length; p++)
        {
            if (p > 0)
            {
                made.Append('.');
            }
            string part = parts[p];
            for (int i = 0; i < part.Length; i++)
            {
                char c = part[i];
                bool valid = char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
                if (i == 0 && !(char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.ConnectorPunctuation))
                {
                    made.Append('_');
                    if (valid)
                    {
                        made.Append(c);
                    }
                    continue;
                }
                made.Append(valid ? c : '_');
            }
        }
        return made.ToString() == "_" ? "__" : made.ToString();
    }

    /// <summary>The culture a file's name ends in before its extension (<c>Home.fr</c>, <c>Home.zh-Hans</c>), as MSBuild's
    /// AssignCulture takes one: a culture .NET knows; empty for none.</summary>
    public static string CultureOf(string stem)
    {
        int dot = stem.LastIndexOf('.');
        return dot > 0 && stem[(dot + 1)..] is { Length: > 0 } last && KnownCultures.Value.Contains(last) ? last : string.Empty;
    }

    /// <summary>A culture's language in English ("French", "Spanish (Mexico)"); its name where .NET has no data for it.</summary>
    public static string LanguageOf(string culture)
    {
        try
        {
            return CultureInfo.GetCultureInfo(culture) is { EnglishName: { Length: > 0 } english } && !english.StartsWith("Unknown", StringComparison.Ordinal) ? english : culture;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    private static ResourceFile? Load(string path, string culture, ResourceFormat format, Func<string, TextSnapshot?>? open)
    {
        if (open?.Invoke(path) is { } snapshot)
        {
            return ReadOpen.GetValue(snapshot, text => Parse(path, culture, format, text.ToString()));
        }
        return Read.Get(path, file => Parse(file, culture, format, File.ReadAllText(file)));
    }

    /// <summary>A language's file read from its text: a .resx's string entries (an entry of another type, a file's or an
    /// image's, left out), an XLIFF file's units.</summary>
    public static ResourceFile Parse(string path, string culture, ResourceFormat format, string text)
    {
        XmlTree tree = XmlTree.Parse(text);
        List<ResourceEntry> entries = [];
        foreach (XmlElement element in tree.Elements)
        {
            if (format == ResourceFormat.Resx && element.Name == "data" && element.Parent?.Name == "root" && element.Attribute("name") is { HasValue: true } name
                && element.Attribute("type") is null && element.Attribute("mimetype") is null)
            {
                XmlElement? value = element.Children.FirstOrDefault(c => c.Name == "value");
                XmlElement? comment = element.Children.FirstOrDefault(c => c.Name == "comment");
                entries.Add(new ResourceEntry(name.Value, value is null ? string.Empty : ContentOf(text, value), null, name.ValueStart, name.ValueEnd, element.Start,
                    element.End, comment is null ? null : ContentOf(text, comment)));
            }
            else if (format == ResourceFormat.Xliff && element.Name == "trans-unit" && element.Attribute("id") is { HasValue: true } id)
            {
                XmlElement? target = element.Children.FirstOrDefault(c => c.Name == "target");
                entries.Add(new ResourceEntry(id.Value, target is null ? null : ContentOf(text, target), target?.Attribute("state")?.Value, id.ValueStart, id.ValueEnd,
                    element.Start, element.End));
            }
        }
        return new ResourceFile(path, culture, format, entries);
    }

    /// <summary>An element's content as a reader reads it: its character references read, a CDATA section's text as it is.</summary>
    private static string ContentOf(string text, XmlElement element)
    {
        TextRange content = element.Content;
        string raw = text.Substring(content.Start, Math.Min(content.Length, text.Length - content.Start));
        return raw.StartsWith("<![CDATA[", StringComparison.Ordinal) && raw.EndsWith("]]>", StringComparison.Ordinal) ? raw[9..^3] : XmlEntities.Decode(raw);
    }

    /// <summary>A .resx's text with a key given a value: a new <c>&lt;data&gt;</c> after its last string's, written as that one is
    /// (on one line or three, its indentation, xml:space) with no comment, or on three lines with xml:space where it has none (a
    /// file's or an image's type is never copied); every other character kept. Null where the text has no <c>&lt;root&gt;</c> to
    /// hold it, or has the key already.</summary>
    public static string? WithEntry(string text, string key, string value)
    {
        XmlTree tree = XmlTree.Parse(text);
        if (tree.Root is not { Name: "root" } root || !root.IsStartTagClosed || root.EndTagStart is not { } rootEnd
            || root.Children.Any(c => c.Name == "data" && c.Attribute("name")?.Value == key))
        {
            return null;
        }
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string name = XmlEntities.Encode(key, '"');
        string written = Escaped(value);
        XmlElement? last = root.Children.LastOrDefault(c => c.Name == "data" && c.Attribute("name") is { HasValue: true } && c.Attribute("type") is null
            && c.Attribute("mimetype") is null && c.Children.Any(v => v.Name == "value") && c.IsClosed);
        if (last is not null)
        {
            XmlAttribute named = last.Attribute("name")!;
            XmlElement held = last.Children.First(c => c.Name == "value");
            List<(int Start, int End, string Text)> changes = [(named.ValueStart, named.ValueEnd, named.Quote == '\'' ? XmlEntities.Encode(key, '\'') : name)];
            changes.Add(held.IsSelfClosing ? (held.Start, held.End, $"<value>{written}</value>") : (held.Content.Start, held.Content.End, written));
            if (last.Children.FirstOrDefault(c => c.Name == "comment") is { } comment)
            {
                // The comment goes with the white space before it, back to what came before it in the entry.
                int before = last.Children.Where(c => c.End <= comment.Start).Select(c => c.End).DefaultIfEmpty(last.StartTagEnd).Max();
                changes.Add((before, comment.End, string.Empty));
            }
            StringBuilder entry = new(text, last.Start, last.End - last.Start, last.End - last.Start + written.Length);
            foreach ((int start, int end, string replaced) in changes.OrderByDescending(c => c.Start))
            {
                entry.Remove(start - last.Start, end - start).Insert(start - last.Start, replaced);
            }
            return text[..last.End] + newline + IndentOf(text, last.Start, "  ") + entry + text[last.End..];
        }
        string made = $"  <data name=\"{name}\" xml:space=\"preserve\">{newline}    <value>{written}</value>{newline}  </data>{newline}";
        int lineStart = text.LastIndexOf('\n', Math.Max(0, rootEnd - 1)) + 1;
        return text.AsSpan(lineStart, rootEnd - lineStart).IsWhiteSpace()
            ? text[..lineStart] + made + text[lineStart..]
            : text[..rootEnd] + newline + made + text[rootEnd..];
    }

    /// <summary>An XLIFF file's text with a key's unit translated: its target filled in and its state made <c>translated</c>,
    /// or, for a key it has no unit of, the unit XliffTasks would write for it (its id, the neutral text as its source, the
    /// target, the .resx's comment or an empty note) put among the others in order of their ids, as XliffTasks keeps them;
    /// every other character kept. Null where the text has no <c>&lt;body&gt;</c> to hold a new unit.</summary>
    public static string? WithTarget(string text, string key, string value, string source, string? note)
    {
        XmlTree tree = XmlTree.Parse(text);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string written = Escaped(value);
        XmlElement[] units = [.. tree.Elements.Where(e => e.Name == "trans-unit" && e.Attribute("id") is { HasValue: true })];
        if (units.FirstOrDefault(u => u.Attribute("id")!.Value == key) is { } unit)
        {
            if (unit.Children.FirstOrDefault(c => c.Name == "target") is { } target)
            {
                if (target.IsSelfClosing)
                {
                    return text[..target.Start] + $"<target state=\"translated\">{written}</target>" + text[target.End..];
                }
                string filled = text[..target.Content.Start] + written + text[target.Content.End..];
                return target.Attribute("state") is { HasValue: true } state
                    ? filled[..state.ValueStart] + "translated" + filled[state.ValueEnd..]
                    : filled[..target.NameEnd] + " state=\"translated\"" + filled[target.NameEnd..];
            }
            if (unit.Children.FirstOrDefault(c => c.Name == "source") is not { } given)
            {
                return null;
            }
            return text[..given.End] + newline + IndentOf(text, given.Start, "        ") + $"<target state=\"translated\">{written}</target>" + text[given.End..];
        }
        XmlElement? neighbour = units.FirstOrDefault(u => string.CompareOrdinal(u.Attribute("id")!.Value, key) > 0);
        XmlElement? model = neighbour ?? units.LastOrDefault();
        string outer = model is null ? "      " : IndentOf(text, model.Start, "      ");
        string inner = model?.Children.FirstOrDefault() is { } child ? IndentOf(text, child.Start, outer + "  ") : outer + "  ";
        string made = $"{outer}<trans-unit id=\"{XmlEntities.Encode(key, '"')}\">{newline}{inner}<source>{Escaped(source)}</source>{newline}"
            + $"{inner}<target state=\"translated\">{written}</target>{newline}{inner}{(note is { Length: > 0 } said ? $"<note>{Escaped(said)}</note>" : "<note />")}{newline}{outer}</trans-unit>";
        if (neighbour is not null)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, neighbour.Start - 1)) + 1;
            return text[..lineStart] + made + newline + text[lineStart..];
        }
        if (model is not null)
        {
            return text[..model.End] + newline + made + text[model.End..];
        }
        if (tree.Elements.FirstOrDefault(e => e.Name == "body") is not { EndTagStart: { } bodyEnd })
        {
            return null;
        }
        int bodyLine = text.LastIndexOf('\n', Math.Max(0, bodyEnd - 1)) + 1;
        return text.AsSpan(bodyLine, bodyEnd - bodyLine).IsWhiteSpace()
            ? text[..bodyLine] + made + newline + text[bodyLine..]
            : text[..bodyEnd] + newline + made + newline + text[bodyEnd..];
    }

    /// <summary>A value as an element's content holds it: <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> escaped.</summary>
    private static string Escaped(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>The white space an element's line starts with up to it, else the indentation given.</summary>
    private static string IndentOf(string text, int start, string otherwise)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        return start > lineStart && text.AsSpan(lineStart, start - lineStart).IsWhiteSpace() ? text[lineStart..start] : start == lineStart ? string.Empty : otherwise;
    }

    private static IEnumerable<string> FilesIn(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> FoldersIn(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. Directory.EnumerateDirectories(folder).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "node_modules" or ".git" or "xlf"))]
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

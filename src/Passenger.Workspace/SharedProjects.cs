using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Host;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>A shared project (.shproj): a solution's project whose files each project importing its items file (.projitems)
/// compiles as its own. It is never evaluated (its imports are Visual Studio's alone): what it holds is the items its items
/// file names, read as text, <c>$(MSBuildThisFileDirectory)</c> being that file's folder, and a file made, moved or
/// deleted in its folder is written into or out of that file, through <see cref="XmlText"/> as <see cref="ProjectEdits"/>
/// writes a project, the rest of its text as it was.</summary>
public static class SharedProjects
{
    public const string Extension = ".shproj";

    /// <summary>How an items file names its own folder, as Visual Studio writes every path in one.</summary>
    private const string ThisFolder = "$(MSBuildThisFileDirectory)";

    /// <summary>The item types that are files, beside which an item naming a file that is there is one too.</summary>
    private static readonly HashSet<string> FileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "None", "Content", "EmbeddedResource", "AdditionalFiles", "Page", "Resource", "ApplicationDefinition", "Folder",
    };

    public static bool Is(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsItemsFile(string path) => Path.GetExtension(path).Equals(EvaluatedItem.SharedItemsExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The items file a shared project imports (its Import labelled Shared, else the first naming a .projitems),
    /// else the one named as it is beside it.</summary>
    public static string ItemsFileOf(string shproj)
    {
        string folder = Path.GetDirectoryName(shproj)!;
        if (Text(shproj) is { } text && Parsed(text) is { Root: { } root })
        {
            List<(bool Labelled, string Path)> imports = [.. root.Descendants().Where(e => e.Name.LocalName == "Import")
                .Select(e => (Labelled: (string?)e.Attribute("Label") == "Shared", Path: Resolved(folder, (string?)e.Attribute("Project") ?? string.Empty)))
                .Where(i => i.Path is { } path && IsItemsFile(path))
                .Select(i => (i.Labelled, i.Path!))];
            if (imports.Count > 0)
            {
                return PathNames.Canonical(imports.OrderByDescending(i => i.Labelled).First().Path);
            }
        }
        return PathNames.Canonical(Path.ChangeExtension(shproj, EvaluatedItem.SharedItemsExtension));
    }

    /// <summary>The files an items file names as they are on disk now, a wildcard's found under its folder; none for one
    /// that is not there or does not read.</summary>
    public static IReadOnlyList<EvaluatedItem> ItemsOf(string projitems) => Text(projitems) is { } text
        ? ItemsIn(text, projitems, folder => PathNames.FilesUnder(folder, ProjectFolders.Enters).SelectMany(f => f.Files))
        : [];

    /// <summary>The files an items file's text names, each by its canonical path once: every item of a file's type (or naming
    /// a path with no wildcard) in its item groups, but those it hides (Visible false), a wildcard's from what
    /// <paramref name="filesUnder"/> finds under its folder, then what an Exclude or a later Remove takes out. A value naming
    /// anything else MSBuild expands (a property but its own folder, an item list) names nothing here.</summary>
    public static IReadOnlyList<EvaluatedItem> ItemsIn(string text, string projitems, Func<string, IEnumerable<string>> filesUnder)
    {
        if (Parsed(text) is not { Root: { } root })
        {
            return [];
        }
        string folder = Path.GetDirectoryName(projitems)!;
        List<EvaluatedItem> items = [];
        foreach (XElement item in ItemsIn(root))
        {
            string type = item.Name.LocalName;
            if (Patterns(folder, Value(item, "Remove")) is { Count: > 0 } removed)
            {
                items.RemoveAll(i => i.Type == type && removed.Any(r => r(i.Path)));
            }
            if (Value(item, "Include") is not { } include || "false".Equals(Value(item, "Visible")?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            List<Func<string, bool>> excluded = Patterns(folder, Value(item, "Exclude"));
            string? link = Value(item, "Link") is { Length: > 0 } linked ? linked : null;
            string? dependent = Value(item, "DependentUpon") is { Length: > 0 } upon ? upon : null;
            foreach (string part in Parts(include))
            {
                if (PathOf(folder, part) is not { } named)
                {
                    continue;
                }
                IEnumerable<string> found = named.Glob ? Glob(named, filesUnder) : [System.IO.Path.GetFullPath(named.Whole)];
                foreach (string path in found.Where(p => !excluded.Any(e => e(p))))
                {
                    if (!items.Any(i => i.Type == type && PathNames.Same(i.Path, path)) && (FileTypes.Contains(type) || File.Exists(path)))
                    {
                        items.Add(new EvaluatedItem(type, path, named.Glob ? null : link, dependent));
                    }
                }
            }
        }
        return items;
    }

    /// <summary>The namespace its files' code takes, as Visual Studio names it in the items file (Import_RootNamespace);
    /// null where it names none.</summary>
    public static string? RootNamespaceOf(string projitems) => Text(projitems) is { } text && Parsed(text) is { Root: { } root }
        ? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Import_RootNamespace" && !e.HasElements)?.Value.Trim() is { Length: > 0 } named ? named : null
        : null;

    /// <summary>Files made in a shared project's folder written into its items file, those it names already left as they
    /// are. Whether it changed; a file that does not read as a project throws.</summary>
    public static bool Add(string projitems, IReadOnlyCollection<string> files) => Write(projitems, text => WithAdded(text, projitems, files));

    /// <summary>The files and folders deleted taken out of an items file: every item naming one, or a file under one.
    /// Whether it changed.</summary>
    public static bool Remove(string projitems, IReadOnlyCollection<string> paths) => Write(projitems, text => WithRemoved(text, projitems, paths));

    /// <summary>An items file following files and folders moved: what moved within its folder is named where it went
    /// (<see cref="ProjectEdits.MoveItems"/>), what left it is taken out, and the files of what came into it from elsewhere
    /// are written in (<paramref name="came"/>, those now under it). Whether it changed.</summary>
    public static bool Follow(string projitems, IReadOnlyList<(string From, string To)> moves, Func<string, IEnumerable<string>> came)
    {
        string folder = Path.GetDirectoryName(projitems)!;
        string was = moves.FirstOrDefault(m => PathNames.Within(projitems, m.To)) is { To: not null } back ? PathNames.Moved(folder, back.To, back.From) : folder;
        bool changed = ProjectEdits.MoveItems(projitems, moves);
        List<string> left = [.. moves.Where(m => PathNames.IsUnder(m.From, was) && !PathNames.IsUnder(m.To, folder)).Select(m => m.To)];
        List<string> come = [.. moves.Where(m => !PathNames.IsUnder(m.From, was) && PathNames.IsUnder(m.To, folder)).SelectMany(m => came(m.To))];
        changed |= left.Count > 0 && Remove(projitems, left);
        changed |= come.Count > 0 && Add(projitems, come);
        return changed;
    }

    /// <summary>An items file's text with files written in, each as Visual Studio writes one (from
    /// <c>$(MSBuildThisFileDirectory)</c>, with the separators its items have, backslashes where none), of the type its
    /// kind takes (<see cref="TypeOf"/>): after the last item of that type every build sees, else the last of any, else in
    /// an item group of its own before &lt;/Project&gt;. A file it names already is left out.</summary>
    public static string WithAdded(string text, string projitems, IEnumerable<string> files)
    {
        XmlText xml = new(text);
        string folder = Path.GetDirectoryName(projitems)!;
        IReadOnlyList<EvaluatedItem> named = ItemsIn(text, projitems, _ => []);
        List<string> includes = [.. ItemsIn(xml.Root).Select(e => Value(e, "Include")).OfType<string>()];
        bool forward = includes.Any(v => v.Contains('/')) && !includes.Any(v => v.Contains('\\'));
        foreach (string file in files.Distinct(PathNames.Comparer))
        {
            if (named.Any(i => PathNames.Same(i.Path, file)) || !PathNames.IsUnder(file, folder))
            {
                continue;
            }
            string type = TypeOf(file);
            string relative = System.IO.Path.GetRelativePath(folder, file);
            string written = ThisFolder + ItemPaths.Named(forward ? relative.Replace('\\', '/') : relative.Replace('/', '\\'));
            string element = $"<{type} Include=\"{XmlEntities.Encode(written, '"')}\" />";
            List<XElement> seen = [.. ItemsIn(xml.Root).Where(e => e.Attribute("Include") is not null && Unconditioned(e))];
            if ((seen.LastOrDefault(e => e.Name.LocalName == type) ?? seen.LastOrDefault()) is { } after)
            {
                xml.After(after, element);
            }
            else
            {
                string outer = xml.IndentIn(xml.Root);
                xml.Append(xml.Root, $"<ItemGroup>{xml.NewLine}{outer}{xml.Unit}{element}{xml.NewLine}{outer}</ItemGroup>", ownLine: true);
            }
        }
        return xml.Text;
    }

    /// <summary>An items file's text without the items naming one of these files or folders, or a file under one, an item
    /// group left empty going with its last.</summary>
    public static string WithRemoved(string text, string projitems, IReadOnlyCollection<string> paths)
    {
        XmlText xml = new(text);
        string folder = Path.GetDirectoryName(projitems)!;
        while (ItemsIn(xml.Root).FirstOrDefault(e => Value(e, "Include") is { } include && Parts(include) is [{ } one]
            && PathOf(folder, one) is { Glob: false } named && paths.Any(p => PathNames.Within(System.IO.Path.GetFullPath(named.Whole), p))) is { } gone)
        {
            if (gone.Parent is { } group && group.Nodes().All(n => n == gone || n is XText { Value: var space } && string.IsNullOrWhiteSpace(space)))
            {
                xml.Remove(group, spaced: true);
            }
            else
            {
                xml.Remove(gone);
            }
        }
        return xml.Text;
    }

    /// <summary>The item type a file goes into an items file as: C# and Visual Basic code compiled, a .resx embedded, a
    /// Razor component or page as content, anything else none.</summary>
    public static string TypeOf(string file) => RazorFiles.Is(file) ? "Content" : System.IO.Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".cs" or ".vb" => "Compile",
        ".resx" => "EmbeddedResource",
        _ => "None",
    };

    /// <summary>A shared project's or items file's text as a save reads it; null for one that is not there or is no text.</summary>
    private static string? Text(string path)
    {
        try
        {
            return FileText.Load(path)?.Text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static XDocument? Parsed(string text)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Changes an items file's text and writes it back as a save does, unless nothing changed.</summary>
    private static bool Write(string projitems, Func<string, string> change)
    {
        ProjectEdits.ProjectText file = new(projitems);
        string changed;
        try
        {
            changed = change(file.Text);
        }
        catch (XmlException error)
        {
            throw new IOException($"{System.IO.Path.GetFileName(projitems)} does not read as XML: {error.Message}", error);
        }
        if (changed == file.Text)
        {
            return false;
        }
        file.Write(changed);
        return true;
    }

    /// <summary>The items of the file's own item groups, in its order: none a target makes.</summary>
    private static IEnumerable<XElement> ItemsIn(XElement root) =>
        root.Descendants().Where(e => e.Parent is { Name.LocalName: "ItemGroup" } group && !group.Ancestors().Any(a => a.Name.LocalName == "Target"));

    private static bool Unconditioned(XElement item) => !Conditioned(item) && item.Parent is { } group && !Conditioned(group) && group.Parent?.Name.LocalName == "Project";

    private static bool Conditioned(XElement element) => element.Attributes().Any(a => a.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase));

    /// <summary>An item's attribute or metadata element of a name, XML's entities read; null where it has neither.</summary>
    private static string? Value(XElement item, string name) =>
        (string?)item.Attribute(name) ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value;

    private static string[] Parts(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A value's path from the items file's folder (its own folder named as such, else relative, else whole); null
    /// for one naming anything else MSBuild expands.</summary>
    private static ItemPath? PathOf(string folder, string written)
    {
        string relative = written.StartsWith(ThisFolder, StringComparison.OrdinalIgnoreCase) ? written[ThisFolder.Length..] : written;
        return ItemPaths.Read(relative.Replace('\\', '/')) is { } read
            ? read with { Path = System.IO.Path.Combine(folder, read.Path) }
            : null;
    }

    /// <summary>What an Exclude or Remove names, each a test of a path: a wildcard's by its pattern, else the path itself.</summary>
    private static List<Func<string, bool>> Patterns(string folder, string? value) =>
        value is null ? [] : [.. Parts(value).Select(p => PathOf(folder, p)).OfType<ItemPath>().Select(Matches)];

    private static Func<string, bool> Matches(ItemPath named)
    {
        if (!named.Glob)
        {
            string whole = System.IO.Path.GetFullPath(named.Whole);
            return path => PathNames.Same(path, whole);
        }
        string head = System.IO.Path.GetFullPath(named.Path);
        Regex pattern = Pattern(named);
        return path => PathNames.IsUnder(path, head) && pattern.IsMatch(path[head.Length..].Replace('\\', '/'));
    }

    /// <summary>The files a wildcard finds under its folder, each matched by what follows the folder.</summary>
    private static IEnumerable<string> Glob(ItemPath named, Func<string, IEnumerable<string>> filesUnder)
    {
        string head = System.IO.Path.GetFullPath(named.Path);
        Func<string, bool> matches = Matches(named);
        return filesUnder(head).Where(matches);
    }

    /// <summary>A wildcard's part after its folder as a pattern over '/'-separated paths: ** any folders, * any part of a
    /// name, ? one character of it.</summary>
    private static Regex Pattern(ItemPath named)
    {
        string rest = (named.Twice ? ItemPaths.Unescaped(ItemPaths.Unescaped(named.Rest)) : ItemPaths.Unescaped(named.Rest)).Replace('\\', '/');
        System.Text.StringBuilder written = new("^");
        for (int at = 0; at < rest.Length; at++)
        {
            if (rest.AsSpan(at).StartsWith("**/"))
            {
                written.Append("(?:.*/)?");
                at += 2;
            }
            else if (rest.AsSpan(at).StartsWith("**"))
            {
                written.Append(".*");
                at++;
            }
            else
            {
                written.Append(rest[at] switch { '*' => "[^/]*", '?' => "[^/]", char c => Regex.Escape(c.ToString()) });
            }
        }
        return new Regex(written.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>An Import's path from a file's folder (its own folder named as such), canonical; null for one naming
    /// anything else MSBuild expands.</summary>
    private static string? Resolved(string folder, string written) =>
        PathOf(folder, written.Trim()) is { Glob: false } named ? System.IO.Path.GetFullPath(named.Whole) : null;
}

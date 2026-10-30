using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>Add Reference and Remove on a project reference, a package reference added, its paths following a file
/// moved, and a property set (Add &gt; Docker Support's container properties), made to the project file's text through
/// <see cref="XmlText"/>: a reference goes beside the project's others, or in an item group of its own before
/// &lt;/Project&gt;, written relative with backslashes; taking it out takes the group it leaves empty, so the file is as
/// it was. What a comment holds is never read or changed, and the rest of the file keeps its bytes, its byte order mark,
/// line endings and indentation. Paths are canonical. An edit returns null when it was done, else why not; a file that
/// cannot be read or written throws.</summary>
public static class ProjectEdits
{
    /// <summary>A condition's Exists('path'), which an Import's condition checks its own path with.</summary>
    private static readonly Regex ExistsIn = new(@"Exists\(\s*'(?<path>[^']*)'\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? AddReference(string project, string referenced) => Change(project, xml =>
    {
        if (References(xml, project).Any(r => PathNames.Same(r.Path, referenced)))
        {
            return $"{Path.GetFileNameWithoutExtension(project)} already references {Path.GetFileNameWithoutExtension(referenced)}";
        }
        string include = Path.GetRelativePath(Path.GetDirectoryName(project)!, referenced).Replace('/', '\\');
        string element = $"<ProjectReference Include=\"{XmlText.Quoted(include, '"')}\" />";
        if (References(xml, project).LastOrDefault(r => r.Element.Parent is { } group && IsItemGroup(group) && !Conditioned(group)).Element is { } last)
        {
            xml.After(last, element);
            return null;
        }
        InGroupOfItsOwn(xml, element);
        return null;
    });

    /// <summary>A package reference added as dotnet add package writes one, its version given (none under central package
    /// management, whose Directory.Packages.props holds it): after the project's last package reference, else in an item
    /// group of its own before &lt;/Project&gt;. Null once done, else why not.</summary>
    public static string? AddPackage(string project, string id, string? version) => Change(project, xml =>
    {
        List<XElement> references = PackageReferences(xml);
        if (references.Any(r => string.Equals((string?)r.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{Path.GetFileNameWithoutExtension(project)} already references {id}";
        }
        string include = XmlText.Quoted(id, '"');
        string element = version is null ? $"<PackageReference Include=\"{include}\" />" : $"<PackageReference Include=\"{include}\" Version=\"{XmlText.Quoted(version, '"')}\" />";
        if (references.LastOrDefault() is { } last)
        {
            xml.After(last, element);
        }
        else
        {
            InGroupOfItsOwn(xml, element);
        }
        return null;
    });

    /// <summary>Whether a project file's text references a package; false for text that does not read as XML.</summary>
    public static bool HasPackage(string text, string id) =>
        Read(text) is { } xml && PackageReferences(xml).Any(r => string.Equals((string?)r.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The package references a project file's text gives a version, each by its package and that version, in the
    /// file's order; none for text that does not read as XML.</summary>
    public static IReadOnlyList<(string Id, string Version)> VersionedPackages(string text) =>
        Read(text) is { } xml ? [.. PackageReferences(xml).Select(Versioned).OfType<(string, string)>()] : [];

    /// <summary>A project file's text in central package management's form, its versions held by Directory.Packages.props
    /// (<paramref name="central"/>, by package): each package reference of those given (all, with none given) has its
    /// version taken out, kept as a VersionOverride where it differs from the central one and the folder allows one
    /// (<paramref name="overrides"/>); a package with no central version is named, with its version, among those to pin
    /// there. The text unchanged and nothing to pin where it does not read as XML.</summary>
    public static (string Text, IReadOnlyList<(string Id, string Version)> Pinned) Centralized(string text, IReadOnlyDictionary<string, string> central,
        bool overrides, IReadOnlyCollection<string>? only = null)
    {
        if (Read(text) is not { } xml)
        {
            return (text, []);
        }
        List<(string Id, string Version)> pinned = [];
        // Each change reads the text again, so the next reference to change is looked up afresh.
        while (PackageReferences(xml).FirstOrDefault(r => Versioned(r) is { } named && (only is null || only.Contains(named.Id, StringComparer.OrdinalIgnoreCase)))
            is { } reference)
        {
            (string id, string version) = Versioned(reference)!.Value;
            string? held = central.GetValueOrDefault(id) ?? pinned.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).Version;
            if (held is null)
            {
                pinned.Add((id, version));
            }
            bool overriding = overrides && held is not null && !string.Equals(held, version, StringComparison.OrdinalIgnoreCase);
            if (reference.Attribute("Version") is not null)
            {
                if (overriding)
                {
                    xml.RenameAttribute(reference, "Version", "VersionOverride");
                }
                else
                {
                    xml.RemoveAttribute(reference, "Version");
                }
            }
            else if (reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version" && !e.HasElements) is { } element)
            {
                if (overriding)
                {
                    xml.Replace(element, $"<VersionOverride>{XmlText.Quoted(version)}</VersionOverride>");
                }
                else
                {
                    xml.Remove(element);
                }
            }
        }
        return (xml.Text, pinned);
    }

    public static string? RemoveReference(string project, string referenced) => Change(project, xml =>
    {
        if (References(xml, project).FirstOrDefault(r => PathNames.Same(r.Path, referenced)).Element is not { } reference)
        {
            return $"{Path.GetFileNameWithoutExtension(project)} does not reference {Path.GetFileNameWithoutExtension(referenced)}";
        }
        if (reference.Parent is { } group && IsItemGroup(group) && group.Nodes().All(n => n == reference || n is XText { Value: var space } && string.IsNullOrWhiteSpace(space)))
        {
            // The group is left empty: it goes too, with the blank line that set it apart from the next.
            xml.Remove(group, spaced: true);
        }
        else
        {
            xml.Remove(reference);
        }
        return null;
    });

    /// <summary>Sets a property in the project file, as Add > Docker Support sets the SDK's container properties and Manage User
    /// Secrets a UserSecretsId (<see cref="WithProperty"/>). Null once done, else why not.</summary>
    public static string? SetProperty(string project, string name, string value)
    {
        ProjectText file = new(project);
        (string? text, string? problem) = WithProperty(file.Text, project, name, value);
        if (text is not null && text != file.Text)
        {
            file.Write(text);
        }
        return problem;
    }

    /// <summary>A project file's text with a property set: where an unconditioned property group sets it already, its value
    /// there; else at the end of the first unconditioned property group, indented as that group's last property; else in a
    /// property group of its own before &lt;/Project&gt;. The text, else why not.</summary>
    public static (string? Text, string? Problem) WithProperty(string text, string project, string name, string value)
    {
        if (Read(text) is not { } xml)
        {
            return (null, $"{Path.GetFileName(project)} does not read as XML");
        }
        if (xml.Root.Name.LocalName != "Project")
        {
            return (null, $"{Path.GetFileName(project)} is not a project file");
        }
        string written = System.Security.SecurityElement.Escape(value);
        List<XElement> groups = PropertyGroups(xml);
        foreach (XElement group in groups)
        {
            if (group.Elements().FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements) is not { } set)
            {
                continue;
            }
            if (xml.InnerOf(set) is not { } inner)
            {
                xml.Replace(set, $"<{name}>{written}</{name}>");
            }
            else if (xml.Written(inner) != written)
            {
                xml.Apply([(inner.Start, inner.Length, written)]);
            }
            return (xml.Text, null);
        }
        string line = $"<{name}>{written}</{name}>";
        if (groups.FirstOrDefault() is { } first)
        {
            xml.Append(first, line);
            return (xml.Text, null);
        }
        string outer = xml.IndentIn(xml.Root);
        xml.Append(xml.Root, $"<PropertyGroup>{xml.NewLine}{outer}{xml.Unit}{line}{xml.NewLine}{outer}</PropertyGroup>", ownLine: true);
        return (xml.Text, null);
    }

    /// <summary>A property's value as the project file's own text sets it in an unconditioned property group; null where it
    /// sets none, or the text does not read as XML.</summary>
    public static string? PropertyIn(string text, string name) =>
        Read(text) is { } xml
            ? PropertyGroups(xml).SelectMany(g => g.Elements()).FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value.Trim()
            : null;

    /// <summary>The project (at its path now) following files and folders that moved or were renamed: each path it names
    /// from its folder (an item's Include, Update, Remove or Exclude, a glob by its folder; an Import's Project and the
    /// Exists of its Condition, not an SDK's; a HintPath) names where that went, a list or a property staying as written,
    /// and a DependentUpon or LastGenOutput (from its item's file) names it there. A project that moved reads its paths
    /// from where it was, so one naming a file outside it still finds it. Whether it changed.</summary>
    public static bool MoveItems(string project, IReadOnlyList<(string From, string To)> moves)
    {
        ProjectText file = new(project);
        if (Read(file.Text) is not { } xml)
        {
            return false;
        }
        string folder = Path.GetDirectoryName(project)!;
        string Moved(string path) => moves.FirstOrDefault(m => PathNames.Within(path, m.From)) is { From: not null } move ? PathNames.Moved(path, move.From, move.To) : path;
        string before = Path.GetDirectoryName(moves.FirstOrDefault(m => PathNames.Within(project, m.To)) is { To: not null } back ? PathNames.Moved(project, back.To, back.From) : project)!;
        List<(int Start, int Length, string With)> edits = [];
        foreach (XElement item in xml.Root.Descendants())
        {
            switch (item.Name.LocalName)
            {
                case "Import" when item.Attribute("Sdk") is null:
                    Follow(xml.ValueOf(item, "Project"));
                    if (xml.ValueOf(item, "Condition") is { } condition)
                    {
                        foreach (Group exists in ExistsIn.Matches(xml.Written(condition)).Select(m => m.Groups["path"]))
                        {
                            Follow((condition.Start + exists.Index, exists.Length), xml.Text[condition.Start - 1]);
                        }
                    }
                    continue;
                case "HintPath" when !item.HasElements:
                    Follow(xml.InnerOf(item), '\0');
                    continue;
            }
            Follow(xml.ValueOf(item, "HintPath"));
            string? own = null;
            foreach (string kind in item.Attributes().Select(a => a.Name.LocalName).Where(n => n is "Include" or "Update" or "Remove" or "Exclude").ToList())
            {
                if (xml.ValueOf(item, kind) is not { } span)
                {
                    continue;
                }
                string items = XmlText.Unquoted(xml.Written(span));
                own ??= kind is "Include" or "Update" && OneFile(items) ? items : null;
                Follow(span);
            }
            if (own is null)
            {
                continue;
            }
            // What the item names beside it, relative to its file: where that is now, from where its file is now.
            string was = Path.GetDirectoryName(Full(before, own))!;
            string at = Path.GetDirectoryName(Moved(Full(before, own)))!;
            IEnumerable<((int Start, int Length)? Span, char? Quote)> related = ((string[])["DependentUpon", "LastGenOutput"]).Select(n => (xml.ValueOf(item, n), (char?)null))
                .Concat(item.Elements().Where(e => e.Name.LocalName is "DependentUpon" or "LastGenOutput" && !e.HasElements).Select(e => (xml.InnerOf(e), (char?)'\0')));
            foreach (((int Start, int Length)? named, char? quote) in related)
            {
                if (named is not { } span)
                {
                    continue;
                }
                string value = XmlText.Unquoted(xml.Written(span));
                string target = Moved(Full(was, value.Trim()));
                if (value.Trim().Length > 0 && !PathNames.Same(Full(at, value.Trim()), target))
                {
                    edits.Add((span.Start, span.Length, XmlText.Quoted(Written(Path.GetRelativePath(at, target), value), quote ?? xml.Text[span.Start - 1])));
                }
            }
        }
        if (edits.Count == 0)
        {
            return false;
        }
        xml.Apply(edits);
        file.Write(xml.Text);
        return true;

        // A path named from the project's folder names where that is now; a glob, by its folder. Read and written as XML.
        void Follow((int Start, int Length)? at, char? quote = null)
        {
            if (at is not { } span)
            {
                return;
            }
            string raw = xml.Written(span);
            string written = raw.Trim();
            int start = span.Start + raw.Length - raw.TrimStart().Length;
            string items = XmlText.Unquoted(written);
            int wild = items.IndexOfAny(['*', '?']);
            string path = (wild < 0 ? items : items[..Math.Max(0, items.LastIndexOfAny(['\\', '/'], wild))]).TrimEnd('\\', '/');
            if (path.Length == 0 || items.IndexOfAny([';', '$', '@', '%']) >= 0)
            {
                return;
            }
            string now = Moved(Full(before, path));
            if (!PathNames.Same(Full(folder, path), now))
            {
                string moved = Written(Path.GetRelativePath(folder, now), path) + items[path.Length..];
                edits.Add((start, written.Length, XmlText.Quoted(moved, quote ?? xml.Text[span.Start - 1])));
            }
        }

        static bool OneFile(string items) => items.Trim().Length > 0 && items.IndexOfAny(['*', '?', ';', '$', '@', '%']) < 0;

        static string Full(string folder, string relative) => Path.GetFullPath(Path.Combine(folder, relative.Replace('\\', '/')));
    }

    /// <summary>Reads the project, makes a change on its text, and writes it back unless the change says why not.</summary>
    private static string? Change(string project, Func<XmlText, string?> change)
    {
        ProjectText file = new(project);
        if (Read(file.Text) is not { } xml)
        {
            return $"{Path.GetFileName(project)} does not read as XML";
        }
        if (xml.Root.Name.LocalName != "Project")
        {
            return $"{Path.GetFileName(project)} is not a project file";
        }
        if (change(xml) is { } problem)
        {
            return problem;
        }
        if (xml.Text != file.Text)
        {
            file.Write(xml.Text);
        }
        return null;
    }

    private static XmlText? Read(string text)
    {
        try
        {
            return new XmlText(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>An item put in an item group of its own before &lt;/Project&gt;, indented as the file indents.</summary>
    private static void InGroupOfItsOwn(XmlText xml, string element)
    {
        string outer = xml.IndentIn(xml.Root);
        xml.Append(xml.Root, $"<ItemGroup>{xml.NewLine}{outer}{xml.Unit}{element}{xml.NewLine}{outer}</ItemGroup>", ownLine: true);
    }

    /// <summary>The project's references, in the file's order, each with the project it names.</summary>
    private static List<(XElement Element, string Path)> References(XmlText xml, string project)
    {
        string folder = Path.GetDirectoryName(project)!;
        return [.. xml.Root.Descendants().Where(e => e.Name.LocalName == "ProjectReference" && e.Attribute("Include") is not null)
            .Select(e => (e, PathNames.Canonical(Path.Combine(folder, e.Attribute("Include")!.Value.Replace('\\', '/')))))];
    }

    private static List<XElement> PackageReferences(XmlText xml) => [.. xml.Root.Descendants().Where(e => e.Name.LocalName == "PackageReference")];

    /// <summary>A package reference's package and the version it writes (its Version attribute, else element); null for
    /// one that writes none.</summary>
    private static (string Id, string Version)? Versioned(XElement reference) =>
        ((string?)reference.Attribute("Include"))?.Trim() is { Length: > 0 } id
        && ((string?)reference.Attribute("Version") ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version" && !e.HasElements)?.Value)?.Trim() is { Length: > 0 } version
            ? (id, version) : null;

    /// <summary>The project's own property groups that no condition holds, in the file's order.</summary>
    private static List<XElement> PropertyGroups(XmlText xml) =>
        [.. xml.Root.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && !Conditioned(e))];

    private static bool IsItemGroup(XElement element) => element.Name.LocalName == "ItemGroup";

    private static bool Conditioned(XElement element) => element.Attributes().Any(a => a.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase));

    /// <summary>A path as a project writes it, with the separators the value it replaces had.</summary>
    private static string Written(string relative, string was) => was.Contains('\\') ? relative.Replace('/', '\\') : relative.Replace('\\', '/');

    /// <summary>A project file read for one change as a save reads a file: its encoding, byte order mark and line breaks,
    /// which go back as they came.</summary>
    private sealed class ProjectText
    {
        private readonly string path;
        private readonly FileText file;

        public ProjectText(string path)
        {
            this.path = path;
            file = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
            Text = file.Text;
        }

        public string Text { get; }

        /// <summary>Writes the project back as an editor's save does (<see cref="FileText.Save"/>), in the encoding and
        /// line breaks it came with.</summary>
        public void Write(string text) => FileText.Save(path, text, file.Encoding, file.HasBom, file.CrBreaks);
    }
}

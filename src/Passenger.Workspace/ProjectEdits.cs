using System.Xml;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>Add Reference and Remove on a project reference, a package reference added, its items following a file
/// moved, and a property set (Add &gt; Docker Support's container properties), made to the project file's text through
/// <see cref="XmlText"/>: a reference goes beside the project's others, or in an item group of its own before
/// &lt;/Project&gt;, written relative with backslashes; taking it out takes the group it leaves empty, so the file is as
/// it was. What a comment holds is never read or changed, and the rest of the file keeps its bytes, its byte order mark,
/// line endings and indentation. Paths are canonical. An edit returns null when it was done, else why not; a file that
/// cannot be read or written throws.</summary>
public static class ProjectEdits
{
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

    /// <summary>The project (at its path now) following files and folders that moved or were renamed: an item's Include,
    /// Update, Remove or Exclude naming a file, or a glob's folder, names where it went (a list or a property stays as
    /// written), and a DependentUpon or LastGenOutput naming one (relative to its item's file) names it there. A project
    /// that moved itself reads its paths from where it was, so one naming a file outside it still finds it. Whether it
    /// changed.</summary>
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
            string? own = null;
            foreach (string kind in item.Attributes().Select(a => a.Name.LocalName).Where(n => n is "Include" or "Update" or "Remove" or "Exclude").ToList())
            {
                if (xml.ValueOf(item, kind) is not { } span)
                {
                    continue;
                }
                // Each path names where its file is now, from where the project is now; a glob by the folder it begins in.
                string items = xml.Written(span);
                own ??= kind is "Include" or "Update" && OneFile(items) ? items : null;
                int wild = items.IndexOfAny(['*', '?']);
                string path = (wild < 0 ? items : items[..Math.Max(0, items.LastIndexOfAny(['\\', '/'], wild))]).TrimEnd('\\', '/');
                if (path.Trim().Length == 0 || items.IndexOfAny([';', '$', '@', '%']) >= 0)
                {
                    continue;
                }
                string now = Moved(Full(before, path));
                if (!PathNames.Same(Full(folder, path), now))
                {
                    edits.Add((span.Start, span.Length, Written(Path.GetRelativePath(folder, now), path) + items[path.Length..]));
                }
            }
            if (own is null)
            {
                continue;
            }
            // What the item names beside it, relative to its file: where that is now, from where its file is now.
            string was = Path.GetDirectoryName(Full(before, own))!;
            string at = Path.GetDirectoryName(Moved(Full(before, own)))!;
            IEnumerable<(int Start, int Length)?> related = ((string[])["DependentUpon", "LastGenOutput"]).Select(n => xml.ValueOf(item, n))
                .Concat(item.Elements().Where(e => e.Name.LocalName is "DependentUpon" or "LastGenOutput" && !e.HasElements).Select(xml.InnerOf));
            foreach ((int Start, int Length) span in related.OfType<(int Start, int Length)>())
            {
                string value = xml.Written(span);
                string target = Moved(Full(was, value.Trim()));
                if (value.Trim().Length > 0 && !PathNames.Same(Full(at, value.Trim()), target))
                {
                    edits.Add((span.Start, span.Length, Written(Path.GetRelativePath(at, target), value)));
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

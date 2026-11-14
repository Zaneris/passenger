using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>freedesktop's list of recently used files (<c>recently-used.xbel</c> in the data home), which GTK, KDE and
/// other programs share: KDE's activity manager reads it into the Recent Files of each program's icon, and file managers
/// list it as Recent. A program is an application element of a file's bookmark, so taking Passenger's off a file leaves
/// every other program's use of it as it was, and the list is held to the size the desktop holds it to
/// (<see cref="Keeping"/>). A list that is no xbel is given back as it was, so nothing writes over it.</summary>
public static class RecentlyUsed
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";

    private static readonly XNamespace Mime = "http://www.freedesktop.org/standards/shared-mime-info";

    /// <summary>The metadata owner the specification names for these entries.</summary>
    private const string Owner = "http://freedesktop.org";

    /// <summary>The mime type of a folder.</summary>
    public const string Folder = "inode/directory";

    /// <summary>A program as the list names it: its desktop entry's id (without <c>.desktop</c>) and its entry's Exec as
    /// written, which KDE matches against the entries' to know the program's icon.</summary>
    public sealed record Program(string Name, string Exec);

    /// <summary>How the desktop holds the list: whether a program adds to it at all, how many days a bookmark not used
    /// since is kept (null: for good), and how many bookmarks it holds, the least lately used dropped past it.</summary>
    public sealed record Keeping(bool Adds, int? Days, int Most)
    {
        /// <summary>GTK's own holding where nothing says otherwise: 30 days, and 1,000 bookmarks.</summary>
        public static Keeping Gtk { get; } = new(true, 30, 1000);

        /// <summary>KDE's (<c>[RecentDocuments]</c> of kdeglobals, the person's file first): UseRecent and MaxEntries,
        /// 300 unless set; nothing added where either turns it off.</summary>
        public static Keeping OfKde(IEnumerable<string?> kdeglobals)
        {
            List<string?> files = [.. kdeglobals];
            bool uses = Setting(files, "RecentDocuments", "UseRecent") is not { } use || Truth(use) != false;
            int most = int.TryParse(Setting(files, "RecentDocuments", "MaxEntries"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int set) ? set : 300;
            return new Keeping(uses && most > 0, null, Math.Max(most, 0));
        }

        /// <summary>GTK's from its settings.ini files (<c>[Settings]</c>, the person's first): gtk-recent-files-enabled and
        /// gtk-recent-files-max-age, where 0 keeps nothing and a negative age keeps for good.</summary>
        public static Keeping OfGtk(IEnumerable<string?> settings)
        {
            List<string?> files = [.. settings];
            bool enabled = Setting(files, "Settings", "gtk-recent-files-enabled") is not { } on || Truth(on) != false;
            int age = int.TryParse(Setting(files, "Settings", "gtk-recent-files-max-age"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int set) ? set : 30;
            return OfGtk(enabled, age);
        }

        /// <summary>GNOME's, which its GTK programs take: org.gnome.desktop.privacy as <c>gsettings list-recursively</c>
        /// lists it; null where the listing names neither key.</summary>
        public static Keeping? OfGnome(string listing)
        {
            bool? remember = null;
            int? age = null;
            foreach (string line in listing.Split('\n'))
            {
                string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words is [_, "remember-recent-files", .., string value])
                {
                    remember = Truth(value);
                }
                else if (words is [_, "recent-files-max-age", .., string days]
                    && int.TryParse(days, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int read))
                {
                    age = read;
                }
            }
            return remember is null && age is null ? null : OfGtk(remember != false, age ?? -1);
        }

        private static Keeping OfGtk(bool enabled, int age) => new(enabled && age != 0, age > 0 ? age : null, Gtk.Most);

        /// <summary>A key's value in a group of the first file that sets it.</summary>
        private static string? Setting(IEnumerable<string?> files, string group, string key)
        {
            foreach (string? text in files)
            {
                bool inGroup = false;
                foreach (string raw in (text ?? string.Empty).Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith('['))
                    {
                        inGroup = line == $"[{group}]";
                    }
                    else if (inGroup && line.IndexOf('=', StringComparison.Ordinal) is > 0 and int equals
                        && line[..equals].Trim() is { } name && (name == key || name.StartsWith(key + "[", StringComparison.Ordinal)))
                    {
                        return line[(equals + 1)..].Trim();
                    }
                }
            }
            return null;
        }

        private static bool? Truth(string value) => value.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null,
        };
    }

    /// <summary>The list's file: XDG_DATA_HOME's, else ~/.local/share's; null with neither (no home).</summary>
    public static string? FileIn(Func<string, string?> variable) =>
        variable("XDG_DATA_HOME") is { Length: > 0 } data && Path.IsPathRooted(data) ? Path.Combine(data, "recently-used.xbel")
        : variable("HOME") is { Length: > 0 } home && Path.IsPathRooted(home) ? Path.Combine(home, ".local", "share", "recently-used.xbel")
        : null;

    /// <summary>What changed between two recent lists (each path with when it was last opened): those opened since, the
    /// oldest first, and those let go of.</summary>
    public static (IReadOnlyList<(string Path, DateTimeOffset When)> Opened, IReadOnlyList<string> Gone) Between(
        IReadOnlyDictionary<string, DateTimeOffset> before, IReadOnlyDictionary<string, DateTimeOffset> now) =>
        ([.. now.Where(n => !before.TryGetValue(n.Key, out DateTimeOffset was) || n.Value > was).OrderBy(n => n.Value).Select(n => (n.Key, n.Value))],
         [.. before.Keys.Where(path => !now.ContainsKey(path)).Order(StringComparer.Ordinal)]);

    /// <summary>The mime type a recent folder, solution or project is given: a folder's, or a solution's or C# project's as
    /// Passenger's shared-mime-info file names them, another project an MSBuild file's.</summary>
    public static string MimeTypeOf(string path, bool folder) => folder ? Folder : Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sln" => "application/x-ms-sln",
        ".slnx" => "application/x-ms-slnx",
        ".slnf" => "application/x-ms-slnf",
        ".csproj" => "application/x-ms-csproj",
        _ => "application/xml",
    };

    /// <summary>The list with <paramref name="path"/> used by <paramref name="program"/> <paramref name="when"/>: its
    /// bookmark made, or visited again, and the program's count on it one more; a list begun where there was none (null),
    /// and one that is no xbel, or already holds this use, given back as it was.</summary>
    public static XDocument Used(XDocument? list, string path, string mimeType, Program program, DateTimeOffset when)
    {
        if (list is not null && (list.Root is not { Name.LocalName: "xbel" } existing
            || BookmarkOf(existing, path) is { } held && ApplicationOf(held, program.Name) is { } had && StampOf(had) >= Truncated(when)))
        {
            return list;
        }
        XDocument document = list is null ? Empty() : new XDocument(list);
        XElement root = document.Root!;
        string stamp = Stamp(when);
        XElement? bookmark = BookmarkOf(root, path);
        if (bookmark is null)
        {
            bookmark = new XElement("bookmark", new XAttribute("href", UriOf(path)), new XAttribute("added", stamp));
            root.Add(bookmark);
        }
        foreach (string moved in (string[])["modified", "visited"])
        {
            if (StampOf(bookmark, moved) is not { } before || before < Truncated(when))
            {
                bookmark.SetAttributeValue(moved, stamp);
            }
        }
        XElement metadata = Child(Child(bookmark, "info"), "metadata", e => (string?)e.Attribute("owner") == Owner,
            () => new XElement("metadata", new XAttribute("owner", Owner)));
        Child(metadata, Mime + "mime-type").SetAttributeValue("type", mimeType);
        XElement applications = Child(metadata, Bookmark + "applications");
        XElement application = Child(applications, Bookmark + "application", e => (string?)e.Attribute("name") == program.Name,
            () => new XElement(Bookmark + "application", new XAttribute("name", program.Name)));
        int count = int.TryParse((string?)application.Attribute("count"), NumberStyles.None, CultureInfo.InvariantCulture, out int was) ? was : 0;
        application.SetAttributeValue("exec", program.Exec);
        application.SetAttributeValue("modified", stamp);
        application.SetAttributeValue("count", (count + 1).ToString(CultureInfo.InvariantCulture));
        return document;
    }

    /// <summary>The list without <paramref name="program"/>'s use of <paramref name="path"/>: its application element
    /// gone, and the bookmark too once no program is left on it; the list itself where the program never used it.</summary>
    public static XDocument Forgotten(XDocument list, string path, string program)
    {
        if (list.Root is not { Name.LocalName: "xbel" } root || BookmarkOf(root, path) is not { } found || ApplicationOf(found, program) is null)
        {
            return list;
        }
        XDocument document = new(list);
        XElement bookmark = BookmarkOf(document.Root!, path)!;
        ApplicationOf(bookmark, program)!.Remove();
        if (!Applications(bookmark).Any())
        {
            bookmark.Remove();
        }
        return document;
    }

    /// <summary>The list held as the desktop holds it, <paramref name="now"/>: the bookmarks not modified for more than its
    /// days dropped, then the least lately modified past its most; the list itself where none goes.</summary>
    public static XDocument Kept(XDocument list, Keeping keeping, DateTimeOffset now)
    {
        if (list.Root is not { Name.LocalName: "xbel" } root)
        {
            return list;
        }
        List<(int Index, DateTimeOffset? Modified)> bookmarks = [.. root.Elements("bookmark").Select((e, i) => (i, StampOf(e, "modified")))];
        HashSet<int> dropped = [.. bookmarks.Where(b => keeping.Days is { } days && b.Modified is { } modified && (int)(now - modified).TotalDays > days).Select(b => b.Index)];
        List<(int Index, DateTimeOffset? Modified)> left = [.. bookmarks.Where(b => !dropped.Contains(b.Index))];
        dropped.UnionWith(left.OrderBy(b => b.Modified ?? DateTimeOffset.MinValue).ThenBy(b => b.Index).Take(Math.Max(left.Count - keeping.Most, 0)).Select(b => b.Index));
        if (dropped.Count == 0)
        {
            return list;
        }
        XDocument document = new(list);
        List<XElement> each = [.. document.Root!.Elements("bookmark")];
        foreach (int index in dropped)
        {
            each[index].Remove();
        }
        return document;
    }

    /// <summary>A path as a file URI, escaped as GLib's g_filename_to_uri escapes one, so a file GTK listed is found as
    /// the same bookmark.</summary>
    public static string UriOf(string path)
    {
        StringBuilder uri = new("file://");
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            if (b < 0x80 && (char.IsAsciiLetterOrDigit((char)b) || "!$&'()*+,-./:=@_~".Contains((char)b, StringComparison.Ordinal)))
            {
                uri.Append((char)b);
            }
            else
            {
                uri.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return uri.ToString();
    }

    /// <summary>The path a bookmark's file URI names, however it was escaped; null for any other kind of URI, or a file
    /// of another machine.</summary>
    public static string? PathOf(string href)
    {
        const string scheme = "file://";
        if (!href.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string rest = href[scheme.Length..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 || rest[..slash] is not ("" or "localhost") ? null : Uri.UnescapeDataString(rest[slash..]);
    }

    /// <summary>The list as XML a reader takes, a document type and the layout's white space passed over, so the list is
    /// written indented whole; an <see cref="XmlException"/> where it is no XML.</summary>
    public static XDocument Read(Stream stream)
    {
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true });
        return XDocument.Load(reader);
    }

    /// <summary>The list's bytes as its file holds them: UTF-8, indented.</summary>
    public static byte[] Bytes(XDocument list)
    {
        using MemoryStream bytes = new();
        using (XmlWriter writer = XmlWriter.Create(bytes, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            list.Save(writer);
        }
        return bytes.ToArray();
    }

    /// <summary>The program a desktop entry's Exec runs: its first word, unquoted.</summary>
    public static string CommandOf(string exec)
    {
        string line = exec.TrimStart();
        if (line.StartsWith('"'))
        {
            int end = line.IndexOf('"', 1);
            return end < 0 ? line[1..] : line[1..end];
        }
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? line : line[..space];
    }

    private static XDocument Empty() => new(new XDeclaration("1.0", "UTF-8", null),
        new XElement("xbel", new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "bookmark", Bookmark.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "mime", Mime.NamespaceName)));

    private static XElement? BookmarkOf(XElement root, string path) =>
        root.Elements("bookmark").FirstOrDefault(e => (string?)e.Attribute("href") is { } href && PathOf(href) is { } named && PathNames.Same(named, path));

    private static IEnumerable<XElement> Applications(XElement bookmark) =>
        bookmark.Elements("info").Elements("metadata").Elements(Bookmark + "applications").Elements(Bookmark + "application");

    private static XElement? ApplicationOf(XElement bookmark, string program) =>
        Applications(bookmark).FirstOrDefault(e => (string?)e.Attribute("name") == program);

    private static XElement Child(XElement parent, XName name) => Child(parent, name, _ => true, () => new XElement(name));

    private static XElement Child(XElement parent, XName name, Func<XElement, bool> fits, Func<XElement> made)
    {
        if (parent.Elements(name).FirstOrDefault(fits) is { } found)
        {
            return found;
        }
        XElement child = made();
        parent.Add(child);
        return child;
    }

    private static DateTimeOffset? StampOf(XElement element, string attribute = "modified") =>
        DateTimeOffset.TryParse((string?)element.Attribute(attribute), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when) ? when : null;

    /// <summary>A time as its stamp keeps it, to the microsecond.</summary>
    private static DateTimeOffset Truncated(DateTimeOffset when) => new(when.UtcTicks - when.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>A time as GLib writes one: UTC to the microsecond.</summary>
    private static string Stamp(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

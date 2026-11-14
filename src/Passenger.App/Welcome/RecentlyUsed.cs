using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Passenger.Core;

namespace Passenger.App.Welcome;

/// <summary>freedesktop's list of recently used files (<c>recently-used.xbel</c> in the data home), which GTK, KDE and
/// Electron programs share: KDE's activity manager reads it into the task manager's Recent Files of each program's icon,
/// and file managers list it as Recent. A program is an application element of a file's bookmark, so taking Passenger's
/// off a file leaves every other program's use of it as it was. A missing list is begun empty; one that can't be read, or
/// is no xbel, is never written over, so a failed read never loses the other programs' entries.</summary>
internal static class RecentlyUsed
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";

    private static readonly XNamespace Mime = "http://www.freedesktop.org/standards/shared-mime-info";

    /// <summary>The metadata owner the specification names for these entries.</summary>
    private const string Owner = "http://freedesktop.org";

    /// <summary>The mime type of a folder.</summary>
    public const string Folder = "inode/directory";

    /// <summary>A program as the list names it: its desktop entry's id (without <c>.desktop</c>) and the command that opens
    /// a file in it, its entry's Exec as written: KDE knows the program by the command's first word, quotes and all, and
    /// takes that word itself for the program where no entry's Exec begins with it.</summary>
    public sealed record Program(string Name, string Exec);

    /// <summary>The list's file: XDG_DATA_HOME's, else ~/.local/share's; null with neither (no home).</summary>
    public static string? FileIn(Func<string, string?> variable) =>
        variable("XDG_DATA_HOME") is { Length: > 0 } data && Path.IsPathRooted(data) ? Path.Combine(data, "recently-used.xbel")
        : variable("HOME") is { Length: > 0 } home && Path.IsPathRooted(home) ? Path.Combine(home, ".local", "share", "recently-used.xbel")
        : null;

    /// <summary>The list with <paramref name="path"/> used by <paramref name="program"/> now: its bookmark made, or
    /// visited again, and the program's count on it one more; a list begun where there was none (null), and one that is
    /// no xbel given back as it was.</summary>
    public static XDocument Used(XDocument? list, string path, string mimeType, Program program, DateTimeOffset now)
    {
        if (list is not null && list.Root?.Name.LocalName != "xbel")
        {
            return list;
        }
        XDocument document = list is null ? Empty() : new XDocument(list);
        XElement root = document.Root!;
        string stamp = Stamp(now);
        XElement? bookmark = BookmarkOf(root, path);
        if (bookmark is null)
        {
            bookmark = new XElement("bookmark", new XAttribute("href", UriOf(path)), new XAttribute("added", stamp));
            root.Add(bookmark);
        }
        bookmark.SetAttributeValue("modified", stamp);
        bookmark.SetAttributeValue("visited", stamp);
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
        if (list.Root is not { Name.LocalName: "xbel" } root || BookmarkOf(root, path) is not { } found
            || Applications(found).FirstOrDefault(e => (string?)e.Attribute("name") == program) is null)
        {
            return list;
        }
        XDocument document = new(list);
        XElement bookmark = BookmarkOf(document.Root!, path)!;
        List<XElement> applications = [.. Applications(bookmark)];
        applications.First(e => (string?)e.Attribute("name") == program).Remove();
        if (applications.Count == 1)
        {
            bookmark.Remove();
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

    /// <summary>The path a bookmark's file URI names, however it was escaped; null for any other kind of URI.</summary>
    public static string? PathOf(string href)
    {
        const string scheme = "file://";
        if (!href.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string rest = href[scheme.Length..];
        // A host before the path (file://localhost/…) names this machine.
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? null : Uri.UnescapeDataString(rest[slash..]);
    }

    /// <summary>The list read from its file: true with null where there is no file yet, false where it can't be read or
    /// is no XML a reader takes, which nothing may then write over.</summary>
    public static bool TryRead(string file, out XDocument? list)
    {
        list = null;
        try
        {
            list = XDocument.Load(file);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
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

    /// <summary>The list written whole over its file, through any link to it, the folder made where missing.</summary>
    public static void Write(string file, XDocument list)
    {
        string target = AtomicFile.Target(file);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using MemoryStream bytes = new();
        using (XmlWriter writer = XmlWriter.Create(bytes, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            list.Save(writer);
        }
        AtomicFile.Write(target, bytes.ToArray(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static XDocument Empty() => new(new XDeclaration("1.0", "UTF-8", null),
        new XElement("xbel", new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "bookmark", Bookmark.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "mime", Mime.NamespaceName)));

    private static XElement? BookmarkOf(XElement root, string path) =>
        root.Elements("bookmark").FirstOrDefault(e => (string?)e.Attribute("href") is { } href && PathOf(href) is { } named && PathNames.Same(named, path));

    private static IEnumerable<XElement> Applications(XElement bookmark) =>
        bookmark.Elements("info").Elements("metadata").Elements(Bookmark + "applications").Elements(Bookmark + "application");

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

    /// <summary>A time as GLib writes one: UTC to the microsecond.</summary>
    private static string Stamp(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

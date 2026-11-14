using System.Text;
using System.Xml.Linq;
using Passenger.App.Welcome;

namespace Passenger.UnitTests.Launch;

/// <summary>freedesktop's recently used list as Passenger changes it (<see cref="RecentlyUsed"/>): a folder or solution
/// opened is a bookmark with Passenger's application on it, found again however another program escaped its URI, and
/// forgetting it takes only Passenger's use away, so the list GTK, KDE and other programs share keeps theirs; the list is
/// held as the desktop holds it, and written in the form GLib and KDE read.</summary>
public class RecentlyUsedTests
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";

    private static readonly XNamespace Mime = "http://www.freedesktop.org/standards/shared-mime-info";

    private static readonly RecentlyUsed.Program Passenger = new("io.github.zaneris.passenger", "\"/opt/passenger/passenger\" %f");

    private static readonly RecentlyUsed.Program Dolphin = new("org.kde.dolphin", "dolphin %u");

    private static readonly DateTimeOffset Monday = new(2026, 11, 9, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_folder_opened_in_an_empty_list_is_a_bookmark_naming_Passenger_as_the_program_that_used_it()
    {
        XDocument list = RecentlyUsed.Used(null, "/home/me/code/Shop", RecentlyUsed.Folder, Passenger, Monday);

        XElement bookmark = Assert.Single(list.Root!.Elements("bookmark"));
        XElement application = Assert.Single(Applications(bookmark));
        Assert.True((string?)bookmark.Attribute("href") == "file:///home/me/code/Shop"
            && (string?)bookmark.Element("info")?.Element("metadata")?.Element(Mime + "mime-type")?.Attribute("type") == "inode/directory"
            && (string?)application.Attribute("name") == "io.github.zaneris.passenger"
            && (string?)application.Attribute("exec") == "\"/opt/passenger/passenger\" %f"
            && (string?)application.Attribute("count") == "1"
            && (string?)application.Attribute("modified") == "2026-11-09T08:30:00.000000Z"
            && new[] { "added", "modified", "visited" }.All(stamp => (string?)bookmark.Attribute(stamp) == "2026-11-09T08:30:00.000000Z"),
            $"The list made was {list}, so KDE's task manager would not list the folder under Passenger's icon");
    }

    [Fact]
    public void A_solution_opened_again_is_counted_on_its_one_bookmark_and_another_programs_use_of_it_stays()
    {
        XDocument byCode = RecentlyUsed.Used(null, "/home/me/Shop.sln", "application/x-ms-sln", new RecentlyUsed.Program("com.visualstudio.code", "/usr/share/code/code %f"), Monday);

        XDocument list = RecentlyUsed.Used(RecentlyUsed.Used(byCode, "/home/me/Shop.sln", "application/x-ms-sln", Passenger, Monday.AddHours(1)),
            "/home/me/Shop.sln", "application/x-ms-sln", Passenger, Monday.AddHours(2));

        XElement bookmark = Assert.Single(list.Root!.Elements("bookmark"));
        string[] programs = [.. Applications(bookmark).Select(a => $"{a.Attribute("name")?.Value}×{a.Attribute("count")?.Value}")];
        Assert.True(programs is ["com.visualstudio.code×1", "io.github.zaneris.passenger×2"]
            && (string?)bookmark.Attribute("added") == "2026-11-09T08:30:00.000000Z" && (string?)bookmark.Attribute("visited") == "2026-11-09T10:30:00.000000Z",
            $"Opened twice after another program it is listed as [{string.Join(", ", programs)}], added {bookmark.Attribute("added")?.Value} and visited {bookmark.Attribute("visited")?.Value}, so another program's use, its first opening or its last was lost");
    }

    [Fact]
    public void A_use_the_list_already_holds_is_not_counted_again()
    {
        XDocument once = RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday.AddTicks(7));

        XDocument again = RecentlyUsed.Used(once, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday.AddTicks(7));

        Assert.True(ReferenceEquals(again, once), $"The same opening followed twice was counted twice: {again}");
    }

    [Fact]
    public void A_use_older_than_another_programs_leaves_the_bookmarks_later_stamps_as_they_were()
    {
        XDocument byDolphin = RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, Dolphin, Monday.AddHours(3));

        XDocument list = RecentlyUsed.Used(byDolphin, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday);

        XElement bookmark = Assert.Single(list.Root!.Elements("bookmark"));
        Assert.True((string?)bookmark.Attribute("modified") == "2026-11-09T11:30:00.000000Z" && (string?)bookmark.Attribute("visited") == "2026-11-09T11:30:00.000000Z",
            $"Passenger's earlier opening moved the folder's last use back to {bookmark.Attribute("visited")?.Value}, so the desktop would list it as older than it is");
    }

    [Fact]
    public void A_path_another_program_escaped_otherwise_is_found_as_the_same_bookmark()
    {
        XDocument gtk = XDocument.Parse("""
            <xbel version="1.0" xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks" xmlns:mime="http://www.freedesktop.org/standards/shared-mime-info">
              <bookmark href="file:///home/me/caf%c3%a9%20shop" added="2026-01-01T00:00:00Z" modified="2026-01-01T00:00:00Z" visited="2026-01-01T00:00:00Z"/>
            </xbel>
            """);

        XDocument list = RecentlyUsed.Used(gtk, "/home/me/café shop", RecentlyUsed.Folder, Passenger, Monday);

        Assert.True(list.Root!.Elements("bookmark").Count() == 1,
            $"Passenger's opening of a folder another program listed made a second bookmark of it, which GLib refuses to read: {list}");
    }

    [Theory]
    [InlineData("/home/me/a b#c/é;x", "file:///home/me/a%20b%23c/%C3%A9%3Bx")]
    [InlineData("/home/me/Shop (copy)/it's+1@2", "file:///home/me/Shop%20(copy)/it's+1@2")]
    [InlineData("/home/me/[x]{y}^z`%\"<>?|\\", "file:///home/me/%5Bx%5D%7By%7D%5Ez%60%25%22%3C%3E%3F%7C%5C")]
    public void A_path_is_escaped_as_GLib_escapes_one(string path, string uri)
    {
        string escaped = RecentlyUsed.UriOf(path);

        Assert.True(escaped == uri, $"{path} was written as {escaped}, where GTK writes {uri}, so the two programs list it twice");
    }

    [Fact]
    public void Forgetting_a_path_another_program_used_too_takes_only_Passengers_use_away()
    {
        XDocument both = RecentlyUsed.Used(RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, Dolphin, Monday),
            "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday);

        XDocument list = RecentlyUsed.Forgotten(both, "/home/me/Shop", Passenger.Name);

        string[] programs = [.. list.Root!.Elements("bookmark").SelectMany(Applications).Select(a => a.Attribute("name")?.Value ?? "")];
        Assert.True(programs is ["org.kde.dolphin"], $"After Passenger forgot the folder its programs were [{string.Join(", ", programs)}], where Dolphin's use should stay alone");
    }

    [Fact]
    public void Forgetting_a_path_Passenger_alone_used_takes_its_bookmark_away()
    {
        XDocument used = RecentlyUsed.Used(null, "/home/me/Shop.sln", "application/x-ms-sln", Passenger, Monday);

        XDocument list = RecentlyUsed.Forgotten(used, "/home/me/Shop.sln", Passenger.Name);

        Assert.True(!list.Root!.Elements("bookmark").Any(), $"After Passenger forgot the only program's solution the list still held {list}, so file managers list it as recent");
    }

    [Fact]
    public void Forgetting_a_path_Passenger_never_used_leaves_the_list_as_it_was()
    {
        XDocument byDolphin = RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, Dolphin, Monday);

        XDocument list = RecentlyUsed.Forgotten(byDolphin, "/home/me/Shop", Passenger.Name);

        Assert.True(ReferenceEquals(list, byDolphin), "Forgetting what Passenger never used gave a new list, which the file would be written again for");
    }

    [Fact]
    public void A_list_that_is_no_xbel_is_given_back_as_it_was_so_nothing_writes_over_it()
    {
        XDocument other = XDocument.Parse("<html><body>not a list</body></html>");

        XDocument list = RecentlyUsed.Used(other, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday);

        Assert.True(ReferenceEquals(list, other), $"A file holding no xbel was made into {list}, which would be written over what it held");
    }

    [Fact]
    public void The_paths_opened_since_and_those_let_go_of_are_the_changes_the_oldest_opening_first()
    {
        Dictionary<string, DateTimeOffset> before = new() { ["/a"] = Monday, ["/b"] = Monday.AddHours(1), ["/c"] = Monday.AddHours(2) };
        Dictionary<string, DateTimeOffset> now = new() { ["/a"] = Monday, ["/b"] = Monday.AddHours(5), ["/d"] = Monday.AddHours(4) };

        (IReadOnlyList<(string Path, DateTimeOffset When)> opened, IReadOnlyList<string> gone) = RecentlyUsed.Between(before, now);

        Assert.True(opened.Select(o => o.Path).SequenceEqual(["/d", "/b"]) && gone.SequenceEqual(["/c"]),
            $"The changes were opened [{string.Join(", ", opened.Select(o => o.Path))}] and gone [{string.Join(", ", gone)}], where /d then /b were opened and /c let go of, /a untouched");
    }

    [Fact]
    public void Bookmarks_unused_for_longer_than_the_desktop_keeps_them_go_and_the_rest_stay()
    {
        XDocument list = Listed(("/old", Monday.AddDays(-31)), ("/month", Monday.AddDays(-29)), ("/new", Monday));

        XDocument kept = RecentlyUsed.Kept(list, new RecentlyUsed.Keeping(true, 30, 1000), Monday);

        string[] paths = Paths(kept);
        Assert.True(paths is ["/month", "/new"], $"Held to 30 days the list kept [{string.Join(", ", paths)}], where GTK drops the bookmark unused for 31 days alone");
    }

    [Fact]
    public void Past_the_most_the_desktop_holds_the_least_lately_used_bookmarks_go()
    {
        XDocument list = Listed(("/b", Monday.AddHours(2)), ("/a", Monday), ("/c", Monday.AddHours(1)));

        XDocument kept = RecentlyUsed.Kept(list, new RecentlyUsed.Keeping(true, null, 2), Monday.AddDays(400));

        string[] paths = Paths(kept);
        Assert.True(paths is ["/b", "/c"], $"Held to two the list kept [{string.Join(", ", paths)}], where the least lately used, /a, goes, and none for its age");
    }

    [Fact]
    public void A_list_within_what_the_desktop_holds_is_given_back_as_it_was()
    {
        XDocument list = Listed(("/a", Monday), ("/b", Monday));

        XDocument kept = RecentlyUsed.Kept(list, RecentlyUsed.Keeping.Gtk, Monday);

        Assert.True(ReferenceEquals(kept, list), "A list the desktop would keep whole was made anew, which the file would be written again for");
    }

    [Theory]
    [InlineData(null, null, true, 300)]
    [InlineData("[RecentDocuments]\nUseRecent=false\n", null, false, 300)]
    [InlineData("[General]\nMaxEntries=5\n[RecentDocuments]\nMaxEntries=50\n", "[RecentDocuments]\nMaxEntries=20\n", true, 50)]
    [InlineData(null, "[RecentDocuments]\nMaxEntries=0\n", false, 0)]
    public void KDE_holds_the_list_as_its_settings_say_the_persons_over_the_systems(string? person, string? system, bool adds, int most)
    {
        RecentlyUsed.Keeping keeping = RecentlyUsed.Keeping.OfKde([person, system]);

        Assert.True(keeping == new RecentlyUsed.Keeping(adds, null, most),
            $"kdeglobals [{person}] over [{system}] was read as {keeping}, where KDE adds {(adds ? "up to " + most : "nothing")}");
    }

    [Theory]
    [InlineData(null, true, 30)]
    [InlineData("[Settings]\ngtk-recent-files-enabled=false\n", false, 30)]
    [InlineData("[Settings]\ngtk-recent-files-max-age=-1\n", true, null)]
    [InlineData("[Settings]\ngtk-recent-files-max-age=0\n", false, null)]
    [InlineData("[Settings]\ngtk-recent-files-max-age = 7\n", true, 7)]
    public void GTK_holds_the_list_as_its_settings_file_says(string? settings, bool adds, int? days)
    {
        RecentlyUsed.Keeping keeping = RecentlyUsed.Keeping.OfGtk([settings]);

        Assert.True(keeping == new RecentlyUsed.Keeping(adds, days, 1000), $"settings.ini [{settings}] was read as {keeping}");
    }

    [Theory]
    [InlineData("org.gnome.desktop.privacy remember-recent-files true\norg.gnome.desktop.privacy recent-files-max-age -1\n", true, null)]
    [InlineData("org.gnome.desktop.privacy recent-files-max-age 7\norg.gnome.desktop.privacy remember-recent-files false\n", false, 7)]
    [InlineData("org.gnome.desktop.privacy recent-files-max-age 7\n", true, 7)]
    public void GNOME_holds_the_list_as_its_privacy_settings_say(string listing, bool adds, int? days)
    {
        RecentlyUsed.Keeping? keeping = RecentlyUsed.Keeping.OfGnome(listing);

        Assert.True(keeping == new RecentlyUsed.Keeping(adds, days, 1000), $"gsettings' [{listing}] was read as {keeping?.ToString() ?? "nothing"}");
    }

    [Fact]
    public void A_listing_without_GNOMEs_privacy_keys_says_nothing()
    {
        RecentlyUsed.Keeping? keeping = RecentlyUsed.Keeping.OfGnome("org.gnome.desktop.privacy disable-camera false\n");

        Assert.True(keeping is null, $"A listing without the recent files' keys was read as {keeping}, in place of GTK's own settings");
    }

    [Fact]
    public void The_list_written_names_its_elements_by_the_prefixes_KDE_reads_and_keeps_what_another_program_wrote()
    {
        XDocument glib = RecentlyUsed.Read(new MemoryStream(Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <xbel version="1.0"
                  xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks"
                  xmlns:mime="http://www.freedesktop.org/standards/shared-mime-info"
            >
              <bookmark href="file:///home/me/notes.txt" added="2026-11-01T10:00:00.000000Z" modified="2026-11-01T10:00:00.000000Z" visited="2026-11-01T10:00:00.000000Z">
                <info>
                  <metadata owner="http://freedesktop.org">
                    <mime:mime-type type="text/plain"/>
                    <bookmark:groups>
                      <bookmark:group>TextEditor</bookmark:group>
                    </bookmark:groups>
                    <bookmark:applications>
                      <bookmark:application name="org.gnome.TextEditor" exec="&apos;gnome-text-editor %u&apos;" modified="2026-11-01T10:00:00.000000Z" count="1"/>
                    </bookmark:applications>
                    <bookmark:private/>
                  </metadata>
                </info>
              </bookmark>
            </xbel>
            """)));

        byte[] bytes = RecentlyUsed.Bytes(RecentlyUsed.Used(glib, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday));

        string text = Encoding.UTF8.GetString(bytes);
        XDocument read = RecentlyUsed.Read(new MemoryStream(bytes));
        Assert.True(text.Contains("\n  <bookmark href=\"file:///home/me/Shop\"", StringComparison.Ordinal),
            $"Passenger's bookmark was written into the list unindented, among the others' indented lines: {text}");
        Assert.True(text.Contains("<bookmark:application name=\"io.github.zaneris.passenger\"", StringComparison.Ordinal)
            && text.Contains("<mime:mime-type type=\"inode/directory\"", StringComparison.Ordinal)
            && text.Contains("<bookmark:group>TextEditor</bookmark:group>", StringComparison.Ordinal) && text.Contains("<bookmark:private />", StringComparison.Ordinal)
            && text.Contains("exec=\"'gnome-text-editor %u'\"", StringComparison.Ordinal) && Paths(read) is ["/home/me/notes.txt", "/home/me/Shop"],
            $"The list was written as {text}, where KDE reads its elements by the bookmark: and mime: prefixes and the text editor's bookmark stays whole");
    }

    [Theory]
    [InlineData("/home/me/Shop", true, "inode/directory")]
    [InlineData("/home/me/Shop/Shop.SLN", false, "application/x-ms-sln")]
    [InlineData("/home/me/Shop/Shop.slnx", false, "application/x-ms-slnx")]
    [InlineData("/home/me/Shop/Shop.slnf", false, "application/x-ms-slnf")]
    [InlineData("/home/me/Shop/Shop.csproj", false, "application/x-ms-csproj")]
    [InlineData("/home/me/Shop/Shop.fsproj", false, "application/xml")]
    public void Each_recent_one_has_the_mime_type_Passengers_desktop_entry_opens(string path, bool folder, string type)
    {
        string given = RecentlyUsed.MimeTypeOf(path, folder);

        Assert.True(given == type, $"{path} was given {given}, not {type}, so the desktop would not offer Passenger to open it");
    }

    [Theory]
    [InlineData("\"/home/me/.local/opt/passenger/passenger\" %f", "/home/me/.local/opt/passenger/passenger")]
    [InlineData("/opt/passenger/passenger %f", "/opt/passenger/passenger")]
    [InlineData("\"/home/me/My Apps/passenger\" %f", "/home/me/My Apps/passenger")]
    public void A_desktop_entrys_Exec_names_the_program_it_runs(string exec, string program)
    {
        string command = RecentlyUsed.CommandOf(exec);

        Assert.True(command == program, $"Exec={exec} was read as running {command}, not {program}, so an installed Passenger would not know its own entry");
    }

    [Theory]
    [InlineData("file:///home/me/a%20b", "/home/me/a b")]
    [InlineData("file://localhost/home/me/x", "/home/me/x")]
    [InlineData("file://otherhost/home/me/x", null)]
    [InlineData("https://example.invalid/x", null)]
    public void A_bookmarks_file_URI_names_its_path(string href, string? path)
    {
        string? named = RecentlyUsed.PathOf(href);

        Assert.True(named == path, $"{href} was read as {named ?? "no path"}, not {path ?? "no path"}");
    }

    [Fact]
    public void The_list_is_in_the_data_home_else_under_home()
    {
        string?[] found =
        [
            RecentlyUsed.FileIn(name => name == "HOME" ? "/home/me" : null),
            RecentlyUsed.FileIn(name => name switch { "XDG_DATA_HOME" => "/data", "HOME" => "/home/me", _ => null }),
            RecentlyUsed.FileIn(name => name switch { "XDG_DATA_HOME" => "relative", "HOME" => "/home/me", _ => null }),
            RecentlyUsed.FileIn(_ => null),
        ];

        string under = Path.Combine("/home/me", ".local", "share", "recently-used.xbel");
        Assert.True(found.SequenceEqual([under, Path.Combine("/data", "recently-used.xbel"), under, null]),
            $"The list was looked for at [{string.Join(", ", found)}], where it is in XDG_DATA_HOME when that is a full path, else ~/.local/share, else nowhere");
    }

    private static XDocument Listed(params (string Path, DateTimeOffset Modified)[] used) =>
        used.Aggregate((XDocument?)null, (list, u) => RecentlyUsed.Used(list, u.Path, RecentlyUsed.Folder, Passenger, u.Modified))!;

    private static string[] Paths(XDocument list) => [.. list.Root!.Elements("bookmark").Select(b => RecentlyUsed.PathOf((string)b.Attribute("href")!) ?? "")];

    private static IEnumerable<XElement> Applications(XElement bookmark) =>
        bookmark.Elements("info").Elements("metadata").Elements(Bookmark + "applications").Elements(Bookmark + "application");
}

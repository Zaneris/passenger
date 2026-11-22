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
    public void A_use_is_kept_in_the_desktops_list_beside_what_other_programs_wrote_there() => Cases.All(
        ("a folder opened in an empty list is a bookmark naming Passenger as the program that used it", A_folder_opened_in_an_empty_list_is_a_bookmark_naming_Passenger_as_the_program_that_used_it),
        ("a solution opened again is counted on its one bookmark and another programs use of it stays", A_solution_opened_again_is_counted_on_its_one_bookmark_and_another_programs_use_of_it_stays),
        ("a use older than another programs leaves the bookmarks later stamps as they were", A_use_older_than_another_programs_leaves_the_bookmarks_later_stamps_as_they_were),
        ("a path is escaped as GLib escapes one", () => Cases.Each<(string, string)>(
        [
            ("/home/me/a b#c/é;x", "file:///home/me/a%20b%23c/%C3%A9%3Bx"),
            ("/home/me/[x]{y}^z`%\"<>?|\\", "file:///home/me/%5Bx%5D%7By%7D%5Ez%60%25%22%3C%3E%3F%7C%5C"),
        ], row => A_path_is_escaped_as_GLib_escapes_one(row.Item1, row.Item2))),
        ("the list written names its elements by the prefixes KDE reads and keeps what another program wrote", The_list_written_names_its_elements_by_the_prefixes_KDE_reads_and_keeps_what_another_program_wrote),
        ("each recent one has the mime type Passengers desktop entry opens", () => Cases.Each<(string, bool, string)>(
        [
            ("/home/me/Shop", true, "inode/directory"),
            ("/home/me/Shop/Shop.SLN", false, "application/x-ms-sln"),
            ("/home/me/Shop/Shop.csproj", false, "application/x-ms-csproj"),
            ("/home/me/Shop/Shop.fsproj", false, "application/xml"),
        ], row => Each_recent_one_has_the_mime_type_Passengers_desktop_entry_opens(row.Item1, row.Item2, row.Item3))));

    private void A_folder_opened_in_an_empty_list_is_a_bookmark_naming_Passenger_as_the_program_that_used_it()
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

    private void A_solution_opened_again_is_counted_on_its_one_bookmark_and_another_programs_use_of_it_stays()
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

    private void A_use_older_than_another_programs_leaves_the_bookmarks_later_stamps_as_they_were()
    {
        XDocument byDolphin = RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, Dolphin, Monday.AddHours(3));

        XDocument list = RecentlyUsed.Used(byDolphin, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday);

        XElement bookmark = Assert.Single(list.Root!.Elements("bookmark"));
        Assert.True((string?)bookmark.Attribute("modified") == "2026-11-09T11:30:00.000000Z" && (string?)bookmark.Attribute("visited") == "2026-11-09T11:30:00.000000Z",
            $"Passenger's earlier opening moved the folder's last use back to {bookmark.Attribute("visited")?.Value}, so the desktop would list it as older than it is");
    }

    private void A_path_is_escaped_as_GLib_escapes_one(string path, string uri)
    {
        string escaped = RecentlyUsed.UriOf(path);

        Assert.True(escaped == uri, $"{path} was written as {escaped}, where GTK writes {uri}, so the two programs list it twice");
    }

    [Fact]
    public void Forgetting_or_trimming_the_list_takes_only_Passengers_own_and_writes_over_nothing_that_does_not_read() => Cases.All(
        ("forgetting a path another program used too takes only Passengers use away", Forgetting_a_path_another_program_used_too_takes_only_Passengers_use_away),
        ("forgetting a path Passenger alone used takes its bookmark away", Forgetting_a_path_Passenger_alone_used_takes_its_bookmark_away),
        ("a list that is no xbel is given back as it was so nothing writes over it", A_list_that_is_no_xbel_is_given_back_as_it_was_so_nothing_writes_over_it),
        ("bookmarks unused for longer than the desktop keeps them go and the rest stay", Bookmarks_unused_for_longer_than_the_desktop_keeps_them_go_and_the_rest_stay),
        ("past the most the desktop holds the least lately used bookmarks go", Past_the_most_the_desktop_holds_the_least_lately_used_bookmarks_go));

    private void Forgetting_a_path_another_program_used_too_takes_only_Passengers_use_away()
    {
        XDocument both = RecentlyUsed.Used(RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, Dolphin, Monday),
            "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday);

        XDocument list = RecentlyUsed.Forgotten(both, "/home/me/Shop", Passenger.Name);

        string[] programs = [.. list.Root!.Elements("bookmark").SelectMany(Applications).Select(a => a.Attribute("name")?.Value ?? "")];
        Assert.True(programs is ["org.kde.dolphin"], $"After Passenger forgot the folder its programs were [{string.Join(", ", programs)}], where Dolphin's use should stay alone");
    }

    private void Forgetting_a_path_Passenger_alone_used_takes_its_bookmark_away()
    {
        XDocument used = RecentlyUsed.Used(null, "/home/me/Shop.sln", "application/x-ms-sln", Passenger, Monday);

        XDocument list = RecentlyUsed.Forgotten(used, "/home/me/Shop.sln", Passenger.Name);

        Assert.True(!list.Root!.Elements("bookmark").Any(), $"After Passenger forgot the only program's solution the list still held {list}, so file managers list it as recent");
    }

    private void A_list_that_is_no_xbel_is_given_back_as_it_was_so_nothing_writes_over_it()
    {
        XDocument other = XDocument.Parse("<html><body>not a list</body></html>");

        XDocument list = RecentlyUsed.Used(other, "/home/me/Shop", RecentlyUsed.Folder, Passenger, Monday);

        Assert.True(ReferenceEquals(list, other), $"A file holding no xbel was made into {list}, which would be written over what it held");
    }

    private void Bookmarks_unused_for_longer_than_the_desktop_keeps_them_go_and_the_rest_stay()
    {
        XDocument list = Listed(("/old", Monday.AddDays(-31)), ("/month", Monday.AddDays(-29)), ("/new", Monday));

        XDocument kept = RecentlyUsed.Kept(list, new RecentlyUsed.Keeping(true, 30, 1000), Monday);

        string[] paths = Paths(kept);
        Assert.True(paths is ["/month", "/new"], $"Held to 30 days the list kept [{string.Join(", ", paths)}], where GTK drops the bookmark unused for 31 days alone");
    }

    private void Past_the_most_the_desktop_holds_the_least_lately_used_bookmarks_go()
    {
        XDocument list = Listed(("/b", Monday.AddHours(2)), ("/a", Monday), ("/c", Monday.AddHours(1)));

        XDocument kept = RecentlyUsed.Kept(list, new RecentlyUsed.Keeping(true, null, 2), Monday.AddDays(400));

        string[] paths = Paths(kept);
        Assert.True(paths is ["/b", "/c"], $"Held to two the list kept [{string.Join(", ", paths)}], where the least lately used, /a, goes, and none for its age");
    }

    [Fact]
    public void Each_desktop_holds_the_list_where_and_as_its_settings_say() => Cases.All(
        ("KDE holds the list as its settings say the persons over the systems", () => Cases.Each<(string?, string?, bool, int)>(
        [
            ("[RecentDocuments]\nUseRecent=false\n", null, false, 300),
            ("[General]\nMaxEntries=5\n[RecentDocuments]\nMaxEntries=50\n", "[RecentDocuments]\nMaxEntries=20\n", true, 50),
            (null, "[RecentDocuments]\nMaxEntries=0\n", false, 0),
        ], row => KDE_holds_the_list_as_its_settings_say_the_persons_over_the_systems(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("GTK holds the list as its settings file says", () => Cases.Each<(string?, bool, int?)>(
        [
            ("[Settings]\ngtk-recent-files-enabled=false\n", false, 30),
            ("[Settings]\ngtk-recent-files-max-age=0\n", false, null),
            ("[Settings]\ngtk-recent-files-max-age = 7\n", true, 7),
        ], row => GTK_holds_the_list_as_its_settings_file_says(row.Item1, row.Item2, row.Item3))),
        ("GNOME holds the list as its privacy settings say", () => Cases.Each<(string, bool, int?)>(
        [
            ("org.gnome.desktop.privacy recent-files-max-age 7\norg.gnome.desktop.privacy remember-recent-files false\n", false, 7),
            ("org.gnome.desktop.privacy recent-files-max-age 7\n", true, 7),
        ], row => GNOME_holds_the_list_as_its_privacy_settings_say(row.Item1, row.Item2, row.Item3))));

    private void KDE_holds_the_list_as_its_settings_say_the_persons_over_the_systems(string? person, string? system, bool adds, int most)
    {
        RecentlyUsed.Keeping keeping = RecentlyUsed.Keeping.OfKde([person, system]);

        Assert.True(keeping == new RecentlyUsed.Keeping(adds, null, most),
            $"kdeglobals [{person}] over [{system}] was read as {keeping}, where KDE adds {(adds ? "up to " + most : "nothing")}");
    }

    private void GTK_holds_the_list_as_its_settings_file_says(string? settings, bool adds, int? days)
    {
        RecentlyUsed.Keeping keeping = RecentlyUsed.Keeping.OfGtk([settings]);

        Assert.True(keeping == new RecentlyUsed.Keeping(adds, days, 1000), $"settings.ini [{settings}] was read as {keeping}");
    }

    private void GNOME_holds_the_list_as_its_privacy_settings_say(string listing, bool adds, int? days)
    {
        RecentlyUsed.Keeping? keeping = RecentlyUsed.Keeping.OfGnome(listing);

        Assert.True(keeping == new RecentlyUsed.Keeping(adds, days, 1000), $"gsettings' [{listing}] was read as {keeping?.ToString() ?? "nothing"}");
    }

    private void The_list_written_names_its_elements_by_the_prefixes_KDE_reads_and_keeps_what_another_program_wrote()
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

    private void Each_recent_one_has_the_mime_type_Passengers_desktop_entry_opens(string path, bool folder, string type)
    {
        string given = RecentlyUsed.MimeTypeOf(path, folder);

        Assert.True(given == type, $"{path} was given {given}, not {type}, so the desktop would not offer Passenger to open it");
    }

    private static XDocument Listed(params (string Path, DateTimeOffset Modified)[] used) =>
        used.Aggregate((XDocument?)null, (list, u) => RecentlyUsed.Used(list, u.Path, RecentlyUsed.Folder, Passenger, u.Modified))!;

    private static string[] Paths(XDocument list) => [.. list.Root!.Elements("bookmark").Select(b => RecentlyUsed.PathOf((string)b.Attribute("href")!) ?? "")];

    private static IEnumerable<XElement> Applications(XElement bookmark) =>
        bookmark.Elements("info").Elements("metadata").Elements(Bookmark + "applications").Elements(Bookmark + "application");
}

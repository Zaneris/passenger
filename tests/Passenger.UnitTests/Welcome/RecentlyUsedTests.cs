using System.Xml.Linq;
using Passenger.App.Welcome;

namespace Passenger.UnitTests.Welcome;

/// <summary>freedesktop's recently used list as Passenger changes it (<see cref="RecentlyUsed"/>): a folder or solution
/// opened is a bookmark with Passenger's application on it, found again however another program escaped its URI, and
/// forgetting it takes only Passenger's use away, so the list GTK, KDE and Electron programs share keeps theirs.</summary>
public class RecentlyUsedTests
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";

    private static readonly XNamespace Mime = "http://www.freedesktop.org/standards/shared-mime-info";

    private static readonly RecentlyUsed.Program Passenger = new("io.github.zaneris.passenger", "\"/opt/passenger/passenger\" %f");

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
            && (string?)bookmark.Attribute("visited") == "2026-11-09T08:30:00.000000Z",
            $"The list made was {list}, so KDE's task manager would not list the folder under Passenger's icon");
    }

    [Fact]
    public void A_solution_opened_again_is_counted_on_its_one_bookmark_and_another_programs_use_of_it_stays()
    {
        XDocument byCode = RecentlyUsed.Used(null, "/home/me/Shop.sln", "application/x-ms-sln", new RecentlyUsed.Program("com.microsoft.VSCode", "/usr/share/code/code %f"), Monday);

        XDocument list = RecentlyUsed.Used(RecentlyUsed.Used(byCode, "/home/me/Shop.sln", "application/x-ms-sln", Passenger, Monday.AddHours(1)),
            "/home/me/Shop.sln", "application/x-ms-sln", Passenger, Monday.AddHours(2));

        XElement bookmark = Assert.Single(list.Root!.Elements("bookmark"));
        string[] programs = [.. Applications(bookmark).Select(a => $"{a.Attribute("name")?.Value}×{a.Attribute("count")?.Value}")];
        Assert.True(programs is ["com.microsoft.VSCode×1", "io.github.zaneris.passenger×2"]
            && (string?)bookmark.Attribute("added") == "2026-11-09T08:30:00.000000Z",
            $"Opened twice after VS Code it is listed as [{string.Join(", ", programs)}], added {bookmark.Attribute("added")?.Value}, so another program's use or its first opening was lost");
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
            $"Passenger's opening of a folder another program listed made a second bookmark of it: {list}");
    }

    [Theory]
    [InlineData("/home/me/a b#c/é;x", "file:///home/me/a%20b%23c/%C3%A9%3Bx")]
    [InlineData("/home/me/Shop (copy)/it's+1@2", "file:///home/me/Shop%20(copy)/it's+1@2")]
    public void A_path_is_escaped_as_GLib_escapes_one(string path, string uri)
    {
        string escaped = RecentlyUsed.UriOf(path);

        Assert.True(escaped == uri, $"{path} was written as {escaped}, where GTK writes {uri}, so the two programs list it twice");
    }

    [Fact]
    public void Forgetting_a_path_another_program_used_too_takes_only_Passengers_use_away()
    {
        XDocument both = RecentlyUsed.Used(RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, new RecentlyUsed.Program("org.kde.dolphin", "dolphin %u"), Monday),
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
        XDocument byDolphin = RecentlyUsed.Used(null, "/home/me/Shop", RecentlyUsed.Folder, new RecentlyUsed.Program("org.kde.dolphin", "dolphin %u"), Monday);

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
    [InlineData("https://example.invalid/x", null)]
    public void A_bookmarks_file_URI_names_its_path(string href, string? path)
    {
        string? named = RecentlyUsed.PathOf(href);

        Assert.True(named == path, $"{href} was read as {named ?? "no path"}, not {path ?? "no path"}");
    }

    private static IEnumerable<XElement> Applications(XElement bookmark) =>
        bookmark.Elements("info").Elements("metadata").Elements(Bookmark + "applications").Elements(Bookmark + "application");
}

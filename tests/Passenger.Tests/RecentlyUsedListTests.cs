using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Welcome;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>freedesktop's recently used list as an installed Passenger a person sees keeps it on Linux, a list of the
/// test's own standing in for the person's: each folder and solution the window opens becomes a bookmark naming Passenger,
/// beside what another program listed, and one forgotten on the Welcome screen loses Passenger's use.</summary>
public sealed class RecentlyUsedListTests
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";

    private static readonly XNamespace Mime = "http://www.freedesktop.org/standards/shared-mime-info";

    private static readonly RecentlyUsed.Program Installed = new("io.github.zaneris.passenger", "\"/opt/passenger/passenger\" %f");

    /// <summary>A text editor's bookmark as GLib writes one.</summary>
    private const string TextEditors = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xbel version="1.0"
              xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks"
              xmlns:mime="http://www.freedesktop.org/standards/shared-mime-info"
        >
          <bookmark href="file:///home/me/notes.txt" added="2026-11-01T10:00:00.000000Z" modified="2026-11-01T10:00:00.000000Z" visited="2026-11-01T10:00:00.000000Z">
            <info>
              <metadata owner="http://freedesktop.org">
                <mime:mime-type type="text/plain"/>
                <bookmark:applications>
                  <bookmark:application name="org.gnome.TextEditor" exec="&apos;gnome-text-editor %u&apos;" modified="2026-11-01T10:00:00.000000Z" count="1"/>
                </bookmark:applications>
              </metadata>
            </info>
          </bookmark>
        </xbel>
        """;

    [AvaloniaFact]
    public void Each_folder_and_solution_opened_is_listed_as_used_by_Passenger_beside_another_programs_and_one_forgotten_leaves_the_list()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("freedesktop's recently used list names its files by POSIX paths, which Windows has none of");
        }
        string file = Path.Combine(TestFolders.Create("recently-used-"), "recently-used.xbel");
        File.WriteAllText(file, TextEditors);
        string state = TestFolders.Create("recently-used-state-");
        string solution = TestFolders.Write(PathNames.Canonical(TestFolders.Create("recently-used-game-")), "Game.slnx", "<Solution />\n");
        string shop = PathNames.Canonical(TestFolders.Create("recently-used-shop-"));
        LinuxRecentlyUsed.StandIn = new LinuxRecentlyUsed.Place(file, Installed, RecentlyUsed.Keeping.Gtk);
        try
        {
            MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = state };
            window.Show();
            window.KeepLayout();
            window.OpenFolder(solution);
            Ui.Settle();
            window.OpenFolder(shop);
            Ui.Settle();

            Dictionary<string, (string? Mime, string Programs)> listed = Listed(file);
            Assert.True(listed.Count == 3 && listed.GetValueOrDefault("/home/me/notes.txt") == ("text/plain", "org.gnome.TextEditor×1")
                && listed.GetValueOrDefault(solution) == ("application/x-ms-slnx", "io.github.zaneris.passenger×1")
                && listed.GetValueOrDefault(shop) == ("inode/directory", "io.github.zaneris.passenger×1"),
                $"The recently used list holds {Said(listed)}, where KDE was to find the solution and the folder opened under Passenger's icon and the text editor's file kept");
            Assert.True(OperatingSystem.IsWindows() || File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                $"The list was written with mode {(OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file))}, so another user may read what the person opened");

            window.ForgetRecent(solution);
            Ui.Settle();

            listed = Listed(file);
            Assert.True(listed.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "/home/me/notes.txt", shop }.Order(StringComparer.Ordinal)),
                $"After the solution was forgotten on the Welcome screen the list holds {Said(listed)}, so the desktop still lists it as recent");
        }
        finally
        {
            LinuxRecentlyUsed.StandIn = null;
        }
    }

    /// <summary>Each bookmark's path, mime type, and the programs on it with their counts.</summary>
    private static Dictionary<string, (string? Mime, string Programs)> Listed(string file) =>
        XDocument.Load(file).Root!.Elements("bookmark").ToDictionary(
            b => RecentlyUsed.PathOf((string)b.Attribute("href")!) ?? (string)b.Attribute("href")!,
            b => ((string?)b.Descendants(Mime + "mime-type").FirstOrDefault()?.Attribute("type"),
                string.Join(", ", b.Descendants(Bookmark + "application").Select(a => $"{a.Attribute("name")?.Value}×{a.Attribute("count")?.Value}"))));

    private static string Said(Dictionary<string, (string? Mime, string Programs)> listed) =>
        string.Join("; ", listed.Select(l => $"{l.Key} ({l.Value.Mime}: {l.Value.Programs})"));
}

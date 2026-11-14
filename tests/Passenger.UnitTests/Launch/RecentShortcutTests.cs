using Passenger.App.Welcome;

namespace Passenger.UnitTests.Launch;

/// <summary>What the taskbar's jump list and the Dock's menu make of the recent folders and solutions: each in the order
/// given, titled by its name, its path its tip and its start's one argument; those gone from disk left out, and looked for
/// only as far as the list is read.</summary>
public class RecentShortcutTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Games\" : "/games/";

    [Fact]
    public void Each_is_a_start_naming_its_path_alone_titled_by_its_name()
    {
        (string, string)[] newest = [(Root + "Tool/Tool.csproj", "Tool"), (Root + "Racer/Racer.slnx", "Racer"), (Root + "My Space", "My Space")];

        (string Path, RecentShortcut Shortcut)[] listed = [.. RecentShortcut.Recent(newest, _ => true)];

        Assert.True(listed.Select(l => (l.Path, l.Shortcut.Title)).SequenceEqual(newest),
            $"The list would not keep the order and names given: {string.Join(", ", listed.Select(l => $"{l.Shortcut.Title} ({l.Path})"))}");
        Assert.True(listed.All(l => l.Shortcut.Arguments.SequenceEqual([l.Path]) && l.Shortcut.Tip == l.Path),
            $"An entry would start Passenger on something other than its path, or tip another: {string.Join("; ", listed.Select(l => $"{string.Join(' ', l.Shortcut.Arguments)} / {l.Shortcut.Tip}"))}");
    }

    [Fact]
    public void One_gone_from_disk_is_left_out_and_only_those_read_are_looked_for()
    {
        (string, string)[] newest = [(Root + "Space", "Space"), (Root + "Gone", "Gone"), (Root + "Old", "Old")];
        List<string> looked = [];

        (string Path, RecentShortcut Shortcut)[] first = [.. RecentShortcut.Recent(newest, path =>
        {
            looked.Add(path);
            return path != Root + "Gone";
        }).Take(1)];
        string[] all = [.. RecentShortcut.Recent(newest, path => path != Root + "Gone").Select(l => l.Path)];

        Assert.True(first is [{ Path: var only }] && only == Root + "Space" && looked.SequenceEqual([Root + "Space"]),
            $"A list of one would look on disk for {string.Join(", ", looked)}, where it needs the newest alone");
        Assert.True(all.SequenceEqual([Root + "Space", Root + "Old"]), $"A folder gone from disk would be offered, failing as it is chosen: {string.Join(", ", all)}");
    }
}

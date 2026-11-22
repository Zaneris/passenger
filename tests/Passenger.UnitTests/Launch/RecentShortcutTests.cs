using Passenger.App.Welcome;

namespace Passenger.UnitTests.Launch;

/// <summary>What the taskbar's jump list and the Dock's menu make of the recent folders and solutions: each in the order
/// given, titled by its name, its path its tip and its start's one argument; those gone from disk left out, and looked for
/// only as far as the list is read; and those of one name told apart in their titles by where they are.</summary>
public class RecentShortcutTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Games\" : "/games/";

    [Fact]
    public void The_systems_recent_shortcuts_start_each_recent_one_told_apart_by_its_name() => Cases.All(
        ("each is a start naming its path alone titled by its name", Each_is_a_start_naming_its_path_alone_titled_by_its_name),
        ("one gone from disk is left out and only those read are looked for", One_gone_from_disk_is_left_out_and_only_those_read_are_looked_for),
        ("recent ones of one name are told apart by as few of their folders last names as differ", Recent_ones_of_one_name_are_told_apart_by_as_few_of_their_folders_last_names_as_differ),
        ("recent ones of one name in one folder are told apart by their own names after it", Recent_ones_of_one_name_in_one_folder_are_told_apart_by_their_own_names_after_it));

    private void Each_is_a_start_naming_its_path_alone_titled_by_its_name()
    {
        (string, string)[] newest = [(Root + "Tool/Tool.csproj", "Tool"), (Root + "Racer/Racer.slnx", "Racer"), (Root + "My Space", "My Space")];

        (string Path, RecentShortcut Shortcut)[] listed = [.. RecentShortcut.Recent(newest, _ => true)];

        Assert.True(listed.Select(l => (l.Path, l.Shortcut.Title)).SequenceEqual(newest),
            $"The list would not keep the order and names given: {string.Join(", ", listed.Select(l => $"{l.Shortcut.Title} ({l.Path})"))}");
        Assert.True(listed.All(l => l.Shortcut.Arguments.SequenceEqual([l.Path]) && l.Shortcut.Tip == l.Path),
            $"An entry would start Passenger on something other than its path, or tip another: {string.Join("; ", listed.Select(l => $"{string.Join(' ', l.Shortcut.Arguments)} / {l.Shortcut.Tip}"))}");
    }

    private void One_gone_from_disk_is_left_out_and_only_those_read_are_looked_for()
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

    private void Recent_ones_of_one_name_are_told_apart_by_as_few_of_their_folders_last_names_as_differ()
    {
        string[] paths = [In("shop", "Api", "Api.sln"), In("Notes"), In("garden", "Api", "Api.sln"), In("Tools", "Api")];

        string[] titles = Titles(paths, ["Api", "Notes", "Api", "Api"]);

        string[] expected = ["Api – " + Path.Combine("shop", "Api"), "Notes", "Api – " + Path.Combine("garden", "Api"), "Api – Tools"];
        Assert.True(titles.SequenceEqual(expected),
            $"The list would title them [{string.Join(", ", titles)}], where [{string.Join(", ", expected)}] tells each Api from the others by the folder it is in, and leaves Notes alone");
    }

    private void Recent_ones_of_one_name_in_one_folder_are_told_apart_by_their_own_names_after_it()
    {
        string[] paths = [In("work", "Api", "Api.sln"), In("work", "Api", "Api.csproj"), In("play", "Api", "Api.sln")];

        string[] titles = Titles(paths, ["Api", "Api", "Api"]);

        string[] expected = ["Api – " + Path.Combine("work", "Api", "Api.sln"), "Api – " + Path.Combine("work", "Api", "Api.csproj"), "Api – " + Path.Combine("play", "Api")];
        Assert.True(titles.SequenceEqual(expected),
            $"The list would title them [{string.Join(", ", titles)}], where [{string.Join(", ", expected)}] tells the solution from the project beside it, which the folder alone cannot");
    }

    private static string In(params string[] names) => Path.Combine([Root, .. names]);

    private static string[] Titles(string[] paths, string[] names, Func<string, string>? shown = null)
    {
        (string Path, RecentShortcut Shortcut)[] listed = [.. RecentShortcut.Recent(paths.Zip(names), _ => true)];
        return [.. RecentShortcut.ToldApart(listed, shown ?? (path => path)).Select(s => s.Title)];
    }
}

using Passenger.App.Welcome;

namespace Passenger.UnitTests.Welcome;

/// <summary>The recent folders and solutions as the jump list and the Dock menu show them (<see cref="RecentLists"/>):
/// the newest still on disk, each by a name a person can tell from the others, and a link the person removed from the
/// jump list known again by the path its arguments name.</summary>
public class RecentListsTests
{
    [Fact]
    public void The_lists_show_the_newest_ten_still_on_disk()
    {
        (string Path, string Name)[] newest = [.. Enumerable.Range(1, 14).Select(i => ($"/code/App{i}", $"App{i}"))];

        IReadOnlyList<(string Path, string Title)> listed = RecentLists.Titled(newest, path => path != "/code/App2", path => path);

        string[] titles = [.. listed.Select(e => e.Title)];
        Assert.True(titles is ["App1", "App3", "App4", "App5", "App6", "App7", "App8", "App9", "App10", "App11"],
            $"The lists showed [{string.Join(", ", titles)}], where the ten newest on disk were App1 and App3 to App11");
    }

    [Fact]
    public void Two_solutions_of_one_name_are_told_apart_by_their_paths()
    {
        (string Path, string Name)[] newest = [("/home/me/work/Api.sln", "Api"), ("/home/me/Shop", "Shop"), ("/home/me/play/Api.sln", "Api")];

        IReadOnlyList<(string Path, string Title)> listed = RecentLists.Titled(newest, _ => true, path => "~" + path["/home/me".Length..]);

        string[] titles = [.. listed.Select(e => e.Title)];
        Assert.True(titles is ["Api – ~/work/Api.sln", "Shop", "Api – ~/play/Api.sln"],
            $"The lists showed [{string.Join(", ", titles)}], so a person can't tell which Api opens which solution");
    }

    [Theory]
    [InlineData(@"C:\code\Shop.sln")]
    [InlineData(@"C:\My Code\Shop")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\Shop ""copy""")]
    public void A_link_removed_from_the_jump_list_is_known_by_the_path_its_arguments_name(string path)
    {
        string arguments = RecentLists.ArgumentsOf(path);

        IReadOnlyList<string> removed = RecentLists.RemovedOf([@"C:\other", path], [arguments]);

        Assert.True(removed.Count == 1 && removed[0] == path,
            $"A link whose arguments were {arguments} was taken as removing [{string.Join(", ", removed)}], so {path} would be written again and Windows refuse the list");
    }
}

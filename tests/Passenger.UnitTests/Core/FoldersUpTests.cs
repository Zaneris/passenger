using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A walk up from a folder reads the person's own folders and root's, and ends before one another user owns or
/// others may write: on macOS and Linux by its owner and permissions, a group the person's own alone writing; on Windows by
/// its owner, the system's, the Administrators' and a service's counting as the person's. A global.json in a parent every
/// user may write is passed over, and nothing above it is read.</summary>
public sealed class FoldersUpTests : IDisposable
{
    private const uint Me = 501;
    private const uint Bob = 502;
    private const uint Staff = 20;
    private const uint MyGroup = 1000;
    private const string MySid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const UnixFileMode Shown = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static string? Unix(uint owner, uint group, UnixFileMode mode) =>
        FoldersUp.WhyNot(owner, group, mode, Me, g => g == MyGroup, u => u == Bob ? "bob" : $"uid {u}", g => g == Staff ? "staff" : $"gid {g}");

    [Fact]
    public void The_persons_own_folders_and_roots_are_read_however_they_may_read_them()
    {
        Assert.Null(Unix(Me, Staff, Shown));
        Assert.Null(Unix(0, 0, Shown));
        Assert.Null(Unix(Me, Staff, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    [Fact]
    public void A_folder_another_user_owns_ends_the_walk_naming_them()
    {
        Assert.Equal("another user, bob, owns it", Unix(Bob, Staff, Shown));
    }

    [Fact]
    public void A_folder_every_user_may_write_ends_the_walk_as_tmp_does_its_sticky_bit_notwithstanding()
    {
        Assert.Equal("every user may write in it", Unix(0, 0, Shown | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit));
        Assert.Equal("every user may write in it", Unix(Me, Staff, Shown | UnixFileMode.OtherWrite));
    }

    [Fact]
    public void A_group_may_write_the_folder_only_where_it_is_the_persons_own()
    {
        Assert.Null(Unix(Me, MyGroup, Shown | UnixFileMode.GroupWrite));
        Assert.Equal("the group staff may write in it", Unix(Me, Staff, Shown | UnixFileMode.GroupWrite));
        Assert.Equal("the group staff may write in it", Unix(0, Staff, Shown | UnixFileMode.GroupWrite));
    }

    [Fact]
    public void A_group_is_the_persons_own_where_it_is_named_as_they_are_and_lists_no_one_else()
    {
        Assert.True(FoldersUp.IsOwnGroup("me", ("me", [])));
        Assert.True(FoldersUp.IsOwnGroup("me", ("me", ["me"])));
        Assert.False(FoldersUp.IsOwnGroup("me", ("me", ["me", "bob"])));
        Assert.False(FoldersUp.IsOwnGroup("me", ("staff", [])));
        Assert.False(FoldersUp.IsOwnGroup("me", null));
        Assert.False(FoldersUp.IsOwnGroup(null, ("me", [])));
    }

    [Fact]
    public void On_Windows_the_persons_the_systems_the_Administrators_and_a_services_folders_are_read()
    {
        Assert.Null(FoldersUp.WhyNot(MySid, MySid));
        Assert.Null(FoldersUp.WhyNot("S-1-5-18", MySid));
        Assert.Null(FoldersUp.WhyNot("S-1-5-32-544", MySid));
        Assert.Null(FoldersUp.WhyNot("S-1-5-80-956008885-3425357094-1281409187-2287563520-2266215720", MySid));
    }

    [Fact]
    public void On_Windows_a_folder_another_account_owns_or_whose_owner_is_unread_ends_the_walk()
    {
        Assert.Equal("another account, S-1-5-21-1004336348-1177238915-682003330-1002, owns it",
            FoldersUp.WhyNot("S-1-5-21-1004336348-1177238915-682003330-1002", MySid));
        Assert.Equal("another account, S-1-5-32-545, owns it", FoldersUp.WhyNot("S-1-5-32-545", MySid));
        Assert.Equal("its owner could not be read", FoldersUp.WhyNot(null, MySid));
    }

    [Fact]
    public void A_global_json_in_a_parent_every_user_may_write_is_passed_over_and_the_persons_own_parent_still_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' folders have no mode that lets every user write; the walk asks their owner");
        File.WriteAllText(Path.Join(root, "global.json"), "{}\n");
        string own = Folder("own", Shown);
        File.WriteAllText(Path.Join(own, "global.json"), "{}\n");
        string shared = Folder("shared", Shown | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        File.WriteAllText(Path.Join(shared, "global.json"), "{}\n");

        Assert.Equal(Path.Join(own, "global.json"), Dotnet.GlobalJsonIn(Folder(Path.Join("own", "project"), Shown))?.FullName);
        string project = Folder(Path.Join("shared", "project"), Shown);
        Assert.True(Dotnet.GlobalJsonIn(project) is null,
            $"{Dotnet.GlobalJsonIn(project)?.FullName} was read over {project}, though every user may write it, so another user's global.json chose its SDK");
        Assert.Equal([project], FoldersUp.From(project));
    }

    [Fact]
    public void A_folder_not_there_is_passed_and_the_walk_goes_on_above_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' folders have no mode to make the person's here");
        string own = Folder("own", Shown);
        string[] walked = [.. FoldersUp.From(Path.Join(own, "gone", "deeper"))];
        Assert.Equal(own, walked[0]);
        Assert.Equal(root, walked[1]);
    }

    private string Folder(string name, UnixFileMode mode)
    {
        string folder = Path.Join(root, name);
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, mode);
        }
        return folder;
    }
}

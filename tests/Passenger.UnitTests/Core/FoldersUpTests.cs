using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A walk up from a folder reads the person's own folders, whatever their mode, and root's, and ends before one
/// another user owns or every user may write: on macOS and Linux by its owner and permissions, a group the person's own
/// alone writing; on Windows by its owner, the system's, the Administrators' and a service's counting as the person's, and
/// a volume keeping no owners (FAT, exFAT) the person's. What it finds in a folder of the walk is taken only where it, and
/// each folder down to it, is the person's: a global.json or a node_modules another user put there is passed over.</summary>
public sealed class FoldersUpTests : IDisposable
{
    private const uint Me = 501;
    private const uint Bob = 502;
    private const uint Staff = 20;
    private const uint MyGroup = 1000;
    private const string MySid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const UnixFileMode Shown = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Everyones = Shown | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static string? Unix(uint owner, uint group, UnixFileMode mode) =>
        FoldersUp.WhyNot(owner, group, mode, Me, g => g == MyGroup, u => u == Bob ? "bob" : $"uid {u}", g => g == Staff ? "staff" : $"gid {g}");

    [Fact]
    public void The_persons_own_folders_are_read_whatever_their_mode_and_roots_where_no_other_may_write()
    {
        Assert.Null(Unix(Me, Staff, Shown));
        Assert.Null(Unix(Me, Staff, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        Assert.Null(Unix(Me, Staff, Everyones));
        Assert.Null(Unix(Me, Staff, Everyones | UnixFileMode.StickyBit));
        Assert.Null(Unix(0, 0, Shown));
    }

    [Fact]
    public void A_folder_another_user_owns_ends_the_walk_naming_them_whatever_its_mode()
    {
        Assert.Equal("another user, bob, owns it", Unix(Bob, Staff, Shown));
        Assert.Equal("another user, bob, owns it", Unix(Bob, Staff, UnixFileMode.UserRead | UnixFileMode.UserExecute));
    }

    [Fact]
    public void A_folder_root_owns_that_every_user_may_write_ends_the_walk_as_tmp_does_its_sticky_bit_notwithstanding()
    {
        Assert.Equal("every user may write in it", Unix(0, 0, Everyones | UnixFileMode.StickyBit));
        Assert.Equal("every user may write in it", Unix(0, 0, Shown | UnixFileMode.OtherWrite));
    }

    [Fact]
    public void A_group_may_write_a_folder_root_owns_only_where_it_is_the_persons_own()
    {
        Assert.Null(Unix(0, MyGroup, Shown | UnixFileMode.GroupWrite));
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
        Assert.Null(FoldersUp.WhyNot(MySid, MySid, secured: true));
        Assert.Null(FoldersUp.WhyNot("S-1-5-18", MySid, secured: true));
        Assert.Null(FoldersUp.WhyNot("S-1-5-32-544", MySid, secured: true));
        Assert.Null(FoldersUp.WhyNot("S-1-5-80-956008885-3425357094-1281409187-2287563520-2266215720", MySid, secured: true));
    }

    [Fact]
    public void On_Windows_a_folder_another_account_owns_or_whose_owner_is_unread_ends_the_walk()
    {
        Assert.Equal("another account, S-1-5-21-1004336348-1177238915-682003330-1002, owns it",
            FoldersUp.WhyNot("S-1-5-21-1004336348-1177238915-682003330-1002", MySid, secured: true));
        Assert.Equal("another account, S-1-5-32-545, owns it", FoldersUp.WhyNot("S-1-5-32-545", MySid, secured: true));
        Assert.Equal("its owner could not be read", FoldersUp.WhyNot(null, MySid, secured: true));
    }

    [Fact]
    public void On_Windows_a_volume_keeping_no_owners_is_the_persons_as_Windows_lets_everyone_write_there()
    {
        Assert.Null(FoldersUp.WhyNot(null, MySid, secured: false));
        Assert.Null(FoldersUp.WhyNot("S-1-5-21-1004336348-1177238915-682003330-1002", MySid, secured: false));
    }

    [Fact]
    public void A_global_json_in_a_parent_another_user_owns_is_passed_over_and_nothing_above_it_read_while_the_persons_own_parent_is()
    {
        File.WriteAllText(Path.Join(root, "global.json"), "{}\n");
        string own = Folder("own", Shown);
        File.WriteAllText(Path.Join(own, "global.json"), "{}\n");
        string shared = Folder("shared", Shown);
        File.WriteAllText(Path.Join(shared, "global.json"), "{}\n");

        Assert.Equal(Path.Join(own, "global.json"), Dotnet.GlobalJsonIn(Folder(Path.Join("own", "project"), Shown))?.FullName);
        string project = Folder(Path.Join("shared", "project"), Shown);
        using (FoldersUp.HeldByAnother(shared))
        {
            Assert.True(Dotnet.GlobalJsonIn(project) is null,
                $"{Dotnet.GlobalJsonIn(project)?.FullName} was read over {project}, though another user owns {shared}, so their global.json chose its SDK");
            Assert.Equal([project], FoldersUp.From(project));
        }
    }

    [Fact]
    public void A_global_json_in_the_persons_own_parent_every_user_may_write_is_still_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' folders have no mode that lets every user write in them");
        string open = Folder("open", Everyones);
        File.WriteAllText(Path.Join(open, "global.json"), "{}\n");
        Assert.Equal(Path.Join(open, "global.json"), Dotnet.GlobalJsonIn(Folder(Path.Join("open", "project"), Shown))?.FullName);
        Folder("open", Shown);
    }

    [Fact]
    public void A_global_json_another_user_put_in_the_persons_own_parent_is_passed_over_for_the_next_one_up()
    {
        File.WriteAllText(Path.Join(root, "global.json"), "{}\n");
        string own = Folder("own", Shown);
        string planted = Path.Join(own, "global.json");
        File.WriteAllText(planted, "{}\n");
        string project = Folder(Path.Join("own", "project"), Shown);
        using (FoldersUp.HeldByAnother(planted))
        {
            Assert.Equal(Path.Join(root, "global.json"), Dotnet.GlobalJsonIn(project)?.FullName);
        }
        Assert.Equal(planted, Dotnet.GlobalJsonIn(project)?.FullName);
    }

    [Fact]
    public void A_node_modules_another_user_made_in_an_accepted_folder_is_passed_over_with_all_it_holds()
    {
        string package = Path.Join(root, "node_modules", "eslint", "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.WriteAllText(package, "{ \"name\": \"eslint\", \"version\": \"9.0.0\" }\n");
        string relative = Path.Join("node_modules", "eslint", "package.json");

        Assert.Equal(package, FoldersUp.Found(root, relative));
        using (FoldersUp.HeldByAnother(Path.Join(root, "node_modules")))
        {
            Assert.Null(FoldersUp.Found(root, relative));
        }
        using (FoldersUp.HeldByAnother(Path.Join(root, "node_modules", "eslint")))
        {
            Assert.Null(FoldersUp.Found(root, relative));
        }
        Assert.Null(FoldersUp.Found(root, Path.Join("node_modules", "prettier", "package.json")));
        File.WriteAllText(Path.Join(root, "beside.json"), "{}\n");
        Assert.Null(FoldersUp.Found(Path.Join(root, "node_modules"), Path.Join("..", "beside.json")));
    }

    [Fact]
    public void A_folder_not_there_is_passed_and_the_walk_goes_on_above_it()
    {
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

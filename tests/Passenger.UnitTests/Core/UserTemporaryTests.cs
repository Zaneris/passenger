using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A state folder's own folders in the user's temporary folder: one each, named by the state whatever its
/// spelling, a kind of it beside its own; a folder made the user's alone, a link in its place refused.</summary>
public class UserTemporaryTests
{
    private static readonly string State = Path.Combine(Path.DirectorySeparatorChar + "no-such-folder", "Passenger");

    [Fact]
    public void A_states_folders_are_in_the_users_temporary_folder_its_kinds_named_beside_its_own()
    {
        string own = UserTemporary.Of(State);
        string updates = UserTemporary.Of(State, "updates");
        Assert.True(Path.GetDirectoryName(own) == Path.TrimEndingDirectorySeparator(UserTemporary.Folder()),
            $"The state's folder {own} is not in the user's temporary folder, {UserTemporary.Folder()}");
        Assert.Matches("^passenger-[0-9a-f]{10}$", Path.GetFileName(own));
        Assert.True(updates == $"{own}-updates", $"The state's updates folder is {updates}, not its own folder's name with its kind, {own}-updates");
    }

    [Fact]
    public void Two_states_have_folders_of_their_own_and_one_state_spelled_two_ways_has_one()
    {
        Assert.Equal(UserTemporary.Of(State, "updates"), UserTemporary.Of(State + Path.DirectorySeparatorChar, "updates"));
        Assert.NotEqual(UserTemporary.Of(State, "updates"), UserTemporary.Of(State + "-other", "updates"));
    }

    [Fact]
    public void A_folder_made_the_users_own_is_theirs_alone_and_a_link_in_its_place_is_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows' temporary folder, under the user's profile, is theirs alone already");
            return;
        }
        string parent = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            string open = Path.Combine(parent, "open");
            Directory.CreateDirectory(open, Owner | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(open, Owner | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            Assert.Equal(open, UserTemporary.Own(open));
            Assert.True(File.GetUnixFileMode(open) == Owner, $"A folder made the user's own has mode {File.GetUnixFileMode(open)}, so others may still write in it");

            string link = Path.Combine(parent, "link");
            Directory.CreateSymbolicLink(link, open);
            Assert.Throws<IOException>(() => UserTemporary.Own(link));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}

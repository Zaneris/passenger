using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A state folder's own folders in the user's temporary folder: one each, named by the state whatever its
/// spelling, a kind of it beside its own.</summary>
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
}

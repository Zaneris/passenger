using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>The copies of a Godot game one configuration starts: each named for its place, the first by the configuration's
/// own name, its configuration and place told by its plan and never read from a name (a configuration may be named as a copy
/// is), and each copy's own arguments its line of the configuration's, none for a copy past them.</summary>
public class GameCopiesTests
{
    /// <summary>A game's copies are named for their place and configuration, each with its own arguments.</summary>
    [Fact]
    public void A_games_copies_are_named_and_given_their_own_arguments() => Cases.All(
        (nameof(A_copy_is_named_for_its_place_and_the_first_by_its_configuration), A_copy_is_named_for_its_place_and_the_first_by_its_configuration),
        (nameof(Each_copy_takes_its_own_line_of_arguments_and_one_past_them_none), Each_copy_takes_its_own_line_of_arguments_and_one_past_them_none));

    private void A_copy_is_named_for_its_place_and_the_first_by_its_configuration()
    {
        Assert.Equal("Lobby", GameCopies.Name("Lobby", 1));
        Assert.Equal("Lobby #3", GameCopies.Name("Lobby", 3));
    }

    private void Each_copy_takes_its_own_line_of_arguments_and_one_past_them_none()
    {
        string[] lines = ["-- server", "-- client --name \"Player Two\""];
        Assert.Equal(["--", "server"], GameCopies.Own(lines, 1));
        Assert.Equal(["--", "client", "--name", "Player Two"], GameCopies.Own(lines, 2));
        Assert.Empty(GameCopies.Own(lines, 3));
        Assert.Empty(GameCopies.Own([], 1));
    }
}

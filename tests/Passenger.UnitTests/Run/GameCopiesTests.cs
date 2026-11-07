using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>The copies of a Godot game one configuration starts: each named for its place, the first by the configuration's
/// own name, its configuration and place told by its plan and never read from a name (a configuration may be named as a copy
/// is), and each copy's own arguments its line of the configuration's, none for a copy past them.</summary>
public class GameCopiesTests
{
    [Fact]
    public void A_copy_is_named_for_its_place_and_the_first_by_its_configuration()
    {
        Assert.Equal("Lobby", GameCopies.Name("Lobby", 1));
        Assert.Equal("Lobby #3", GameCopies.Name("Lobby", 3));
    }

    [Fact]
    public void A_program_is_the_copy_its_plan_says_and_a_name_alone_is_the_first_of_its_own_configuration()
    {
        Assert.Equal(new GameCopy("Arena", 2), GameCopies.Of(new GameCopy("Arena", 2), "Arena #2"));
        Assert.Equal(new GameCopy("Arena #2", 1), GameCopies.Of(new GameCopy("Arena #2", 1), "Arena #2"));
        Assert.Equal(new GameCopy("Arena #2", 1), GameCopies.Of(null, "Arena #2"));
        Assert.Equal(new GameCopy("Level #1", 1), GameCopies.Of(null, "Level #1"));
    }

    [Fact]
    public void Each_copy_takes_its_own_line_of_arguments_and_one_past_them_none()
    {
        string[] lines = ["-- server", "-- client --name \"Player Two\""];
        Assert.Equal(["--", "server"], GameCopies.Own(lines, 1));
        Assert.Equal(["--", "client", "--name", "Player Two"], GameCopies.Own(lines, 2));
        Assert.Empty(GameCopies.Own(lines, 3));
        Assert.Empty(GameCopies.Own([], 1));
    }
}

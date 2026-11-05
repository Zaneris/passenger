using Passenger.App.Run;

namespace Passenger.UnitTests.Run;

/// <summary>The copies of a Godot game one configuration starts: each named for its place, the first by the configuration's
/// own name, a name read back as its configuration and place (a companion tab's and a name of its own never a copy's), and
/// each copy's own arguments its line of the configuration's, none for a copy past them.</summary>
public class GameCopiesTests
{
    [Fact]
    public void A_copy_is_named_for_its_place_and_read_back_as_its_configuration_and_place()
    {
        Assert.Equal("Lobby", GameCopies.Name("Lobby", 1));
        Assert.Equal("Lobby #3", GameCopies.Name("Lobby", 3));
        Assert.Equal(("Lobby on Godot", 3), GameCopies.Of("Lobby on Godot #3"));
        Assert.Equal(("Lobby on Godot", 1), GameCopies.Of("Lobby on Godot"));
        Assert.Equal(("Lobby #2 (GDScript)", 1), GameCopies.Of("Lobby #2 (GDScript)"));
        Assert.Equal(("Level #1", 1), GameCopies.Of("Level #1"));
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

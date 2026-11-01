using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>What Passenger's helper in a Godot game is sent: a key app_key names, by Avalonia's name, as Godot names it for
/// <c>OS.find_keycode_from_string</c>, and the node paths its live editing names; and the helper as Passenger carries it.</summary>
public class GodotAgentTests
{
    [Theory]
    [InlineData("Back", "Backspace")]
    [InlineData("Enter", "Enter")]
    [InlineData("Escape", "Escape")]
    [InlineData("D7", "7")]
    [InlineData("OemPlus", "Plus")]
    [InlineData("OemComma", "Comma")]
    [InlineData("PageDown", "PageDown")]
    [InlineData("A", "A")]
    [InlineData("F5", "F5")]
    public void A_key_is_sent_by_the_name_Godot_gives_it(string avalonia, string godot) =>
        Assert.Equal(godot, GodotAgent.KeyName(avalonia));

    [Fact]
    public void A_node_path_is_absolute_from_the_root_and_relative_otherwise()
    {
        Assert.True(GodotAgent.PathOf("/root") is { Names: ["root"], Subnames: [], Absolute: true }, "The root's path was not written as /root");
        Assert.Equal(["..", GodotAgent.NodeName], GodotAgent.PathOf($"../{GodotAgent.NodeName}").Names);
        Assert.False(GodotAgent.PathOf("..").Absolute, "A path from the scene was written as one from the root");
    }

    [Fact]
    public void A_helpers_error_is_told_by_its_scripts_file_wherever_its_stack_says_it_was_raised()
    {
        GodotError own = new(0, "/state/cache/godot/passenger_agent.gd", "click", 12, "Invalid call", string.Empty, false, []);
        GodotError deeper = new(0, "scene/gui/control.cpp", "grab_focus", 1, "Not in tree", string.Empty, false,
            [new GodotFrame("/state/cache/godot/passenger_agent.gd", "_focus", 3)]);
        GodotError games = new(0, "res://Main.cs", "_Process", 19, "Spinner fell over", string.Empty, false, [new GodotFrame("res://Main.cs", "_Process", 19)]);
        Assert.True(GodotAgent.Raised(own) && GodotAgent.Raised(deeper), "An error Passenger's helper raised was taken for the game's");
        Assert.False(GodotAgent.Raised(games), "The game's own error was taken for Passenger's helper's");
    }
}

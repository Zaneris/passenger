using Avalonia.Input;
using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>What Passenger's helper in a Godot game is sent: a key app_key names, by Avalonia's key, as Godot names it for
/// <c>OS.find_keycode_from_string</c> (one that types a character by the one it types without Shift), and the node paths
/// its live editing names; and the helper as Passenger carries it.</summary>
public class GodotAgentTests
{
    [Theory]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.Enter, "Enter")]
    [InlineData(Key.Escape, "Escape")]
    [InlineData(Key.D7, "7")]
    [InlineData(Key.OemPlus, "Equal")]
    [InlineData(Key.OemComma, "Comma")]
    [InlineData(Key.OemQuestion, "Slash")]
    [InlineData(Key.OemSemicolon, "Semicolon")]
    [InlineData(Key.OemQuotes, "Apostrophe")]
    [InlineData(Key.OemOpenBrackets, "BracketLeft")]
    [InlineData(Key.OemCloseBrackets, "BracketRight")]
    [InlineData(Key.OemPipe, "Backslash")]
    [InlineData(Key.OemTilde, "QuoteLeft")]
    [InlineData(Key.PageDown, "PageDown")]
    [InlineData(Key.A, "A")]
    [InlineData(Key.F5, "F5")]
    public void A_key_is_sent_by_the_name_Godot_gives_it(Key avalonia, string godot) =>
        Assert.True(GodotAgent.KeyName(avalonia) == godot, $"{avalonia} was sent as Godot's {GodotAgent.KeyName(avalonia)}, not {godot}, so the game hears another key");

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

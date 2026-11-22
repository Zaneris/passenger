using Avalonia.Input;
using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>What Passenger's helper in a Godot game is sent: a key app_key names, by Avalonia's key, as Godot names it for
/// <c>OS.find_keycode_from_string</c> (one that types a character by the one it types without Shift), and the node paths
/// its live editing names; and the helper as Passenger carries it.</summary>
public class GodotAgentTests
{
    /// <summary>An agent's keys, node paths and helper errors are given as Godot names them.</summary>
    [Fact]
    public void An_agents_keys_paths_and_errors_are_given_as_godot_names_them() => Cases.All(
        (nameof(A_key_is_sent_by_the_name_Godot_gives_it), A_key_is_sent_by_the_name_Godot_gives_it),
        (nameof(A_node_path_is_absolute_from_the_root_and_relative_otherwise), A_node_path_is_absolute_from_the_root_and_relative_otherwise),
        (nameof(A_helpers_error_is_told_by_its_scripts_file_wherever_its_stack_says_it_was_raised),
            A_helpers_error_is_told_by_its_scripts_file_wherever_its_stack_says_it_was_raised));

    private void A_key_is_sent_by_the_name_Godot_gives_it() => Cases.Each<(Key Avalonia, string Godot)>(
    [
        (Key.Back, "Backspace"),
        (Key.D7, "7"),
        (Key.OemPlus, "Equal"),
        (Key.OemPipe, "Backslash"),
        (Key.OemTilde, "QuoteLeft"),
        (Key.F5, "F5"),
    ], row => A_key_is_sent_by_the_name_Godot_gives_it(row.Avalonia, row.Godot));

    private void A_key_is_sent_by_the_name_Godot_gives_it(Key avalonia, string godot) =>
        Assert.True(GodotAgent.KeyName(avalonia) == godot, $"{avalonia} was sent as Godot's {GodotAgent.KeyName(avalonia)}, not {godot}, so the game hears another key");

    private void A_node_path_is_absolute_from_the_root_and_relative_otherwise()
    {
        Assert.True(GodotAgent.PathOf("/root") is { Names: ["root"], Subnames: [], Absolute: true }, "The root's path was not written as /root");
        Assert.Equal(["..", GodotAgent.NodeName], GodotAgent.PathOf($"../{GodotAgent.NodeName}").Names);
        Assert.False(GodotAgent.PathOf("..").Absolute, "A path from the scene was written as one from the root");
    }

    private void A_helpers_error_is_told_by_its_scripts_file_wherever_its_stack_says_it_was_raised()
    {
        GodotError own = new(0, "/state/cache/godot/passenger_agent.gd", "click", 12, "Invalid call", string.Empty, false, []);
        GodotError deeper = new(0, "scene/gui/control.cpp", "grab_focus", 1, "Not in tree", string.Empty, false,
            [new GodotFrame("/state/cache/godot/passenger_agent.gd", "_focus", 3)]);
        GodotError games = new(0, "res://Main.cs", "_Process", 19, "Spinner fell over", string.Empty, false, [new GodotFrame("res://Main.cs", "_Process", 19)]);
        Assert.True(GodotAgent.Raised(own) && GodotAgent.Raised(deeper), "An error Passenger's helper raised was taken for the game's");
        Assert.False(GodotAgent.Raised(games), "The game's own error was taken for Passenger's helper's");
    }
}

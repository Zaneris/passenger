using Passenger.App;
using Passenger.App.Godot;

namespace Passenger.Tests;

/// <summary>How the Game window holds a game, on each system (<see cref="MainWindow.InPaneChoice"/>): its layer in it
/// on macOS from Godot 4.5, and a game nobody sees, or one while the window holds another, in a window of its own.
/// Nothing here needs a window: the choice is the window's words alone.</summary>
[Collection(Beside.Name)]
public sealed class GamePaneChoiceTests
{
    /// <summary>A game draws in the game window as a layer or held window where its Godot and system let it, else in
    /// its own.</summary>
    [Fact]
    public void A_game_draws_where_its_godot_and_system_let_it()
    {
        Flow.Step(nameof(On_macOS_a_godot_from_4_5_draws_its_game_as_a_layer_in_the_game_window),
            On_macOS_a_godot_from_4_5_draws_its_game_as_a_layer_in_the_game_window);
        Flow.Step(nameof(A_game_nobody_sees_or_one_while_the_game_window_holds_another_draws_in_its_own),
            A_game_nobody_sees_or_one_while_the_game_window_holds_another_draws_in_its_own);
    }

    private void On_macOS_a_godot_from_4_5_draws_its_game_as_a_layer_in_the_game_window()
    {
        Assert.Equal((GamePaneHolds.Layer, (string?)null), Said(MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: true, holding: null)));
    }

    private void A_game_nobody_sees_or_one_while_the_game_window_holds_another_draws_in_its_own()
    {
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameHeadless("Ticker").Shown),
            Said(MainWindow.InPaneChoice("Ticker", unseen: true, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: true, holding: null)));
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameWindowHolds("Ticker #2").Shown),
            Said(MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: true, holding: "Ticker #2")));
    }

    /// <summary>A choice with its words why as the window says them.</summary>
    private static (GamePaneHolds Holds, string? WhyNot) Said((GamePaneHolds Holds, Passenger.Core.Words? WhyNot) choice) => (choice.Holds, choice.WhyNot?.Shown);
}

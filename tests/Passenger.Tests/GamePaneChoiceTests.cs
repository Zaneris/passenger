using Passenger.App;
using Passenger.App.Godot;

namespace Passenger.Tests;

/// <summary>How the Game window holds a game, on each system (<see cref="MainWindow.InPaneChoice"/>): the game's own window
/// over it on Windows and X11 from Godot 4.4, its layer in it on macOS from Godot 4.5 where QuartzCore has a CALayerHost,
/// and otherwise none, saying why in Refusals' words. Nothing here needs a window: the choice is the window's words alone.</summary>
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
        Flow.Step(nameof(On_macOS_a_godot_before_4_5_draws_in_a_window_of_its_own_saying_the_game_window_takes_4_5),
            On_macOS_a_godot_before_4_5_draws_in_a_window_of_its_own_saying_the_game_window_takes_4_5);
        Flow.Step(nameof(On_a_macOS_with_no_calayerhost_a_game_draws_in_a_window_of_its_own_saying_so),
            On_a_macOS_with_no_calayerhost_a_game_draws_in_a_window_of_its_own_saying_so);
        Flow.Step(nameof(Elsewhere_a_godot_from_4_4_with_an_embedding_holds_its_window_over_the_game_window_and_none_without_one),
            Elsewhere_a_godot_from_4_4_with_an_embedding_holds_its_window_over_the_game_window_and_none_without_one);
        Flow.Step(nameof(A_game_nobody_sees_or_one_while_the_game_window_holds_another_draws_in_its_own),
            A_game_nobody_sees_or_one_while_the_game_window_holds_another_draws_in_its_own);
    }

    private void On_macOS_a_godot_from_4_5_draws_its_game_as_a_layer_in_the_game_window()
    {
        Assert.Equal((GamePaneHolds.Layer, (string?)null), MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: true, holding: null));
    }

    private void On_macOS_a_godot_before_4_5_draws_in_a_window_of_its_own_saying_the_game_window_takes_4_5()
    {
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameEmbedsOnMacFrom("4.4.1")),
            MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.4.1", macOS: true, embedding: false, layerHost: true, holding: null));
    }

    private void On_a_macOS_with_no_calayerhost_a_game_draws_in_a_window_of_its_own_saying_so()
    {
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameNoLayerHost),
            MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: false, holding: null));
    }

    private void Elsewhere_a_godot_from_4_4_with_an_embedding_holds_its_window_over_the_game_window_and_none_without_one()
    {
        Assert.Equal((GamePaneHolds.Window, (string?)null), MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.4", macOS: false, embedding: true, layerHost: false, holding: null));
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameNotEmbeddedHere),
            MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.7.2", macOS: false, embedding: false, layerHost: false, holding: null));
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameEmbedsFrom("4.3")),
            MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.3", macOS: false, embedding: true, layerHost: false, holding: null));
    }

    private void A_game_nobody_sees_or_one_while_the_game_window_holds_another_draws_in_its_own()
    {
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameHeadless("Ticker")),
            MainWindow.InPaneChoice("Ticker", unseen: true, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: true, holding: null));
        Assert.Equal((GamePaneHolds.Nothing, Refusals.GameWindowHolds("Ticker #2")),
            MainWindow.InPaneChoice("Ticker", unseen: false, headless: false, "4.7.2", macOS: true, embedding: false, layerHost: true, holding: "Ticker #2"));
    }
}

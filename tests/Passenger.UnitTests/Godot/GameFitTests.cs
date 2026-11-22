using System.Buffers.Binary;
using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>Where a game shows in the Game window: its own shape as large as the pane holds it, at the pane's middle, never
/// past the pane's edges, what a pointer over the pane comes to in the game, and where a place the game names comes to over
/// the pane.</summary>
public class GameFitTests
{
    /// <summary>A game fits its pane in its own shape, and the pointer reaches it from its own top left.</summary>
    [Fact]
    public void A_game_fits_its_pane_in_its_shape_and_takes_the_pointer_inside_it() => Cases.All(
        (nameof(A_game_of_no_size_known_takes_the_whole_pane), A_game_of_no_size_known_takes_the_whole_pane),
        (nameof(A_game_never_falls_outside_the_pane_whatever_their_sizes), A_game_never_falls_outside_the_pane_whatever_their_sizes),
        (nameof(A_pointer_comes_to_the_game_from_its_own_top_left_and_stays_inside_it_past_its_edges),
            A_pointer_comes_to_the_game_from_its_own_top_left_and_stays_inside_it_past_its_edges),
        (nameof(A_press_on_the_panes_bars_is_no_press_on_the_game), A_press_on_the_panes_bars_is_no_press_on_the_game));

    /// <summary>A place the game names, a warp among them, comes to the pane as its Godot draws it.</summary>
    [Fact]
    public void Places_the_game_names_come_to_the_pane_as_its_godot_draws_them() => Cases.All(
        (nameof(A_place_a_game_names_comes_to_the_pane_from_the_games_own_top_left_in_what_its_godot_says),
            A_place_a_game_names_comes_to_the_pane_from_the_games_own_top_left_in_what_its_godot_says),
        (nameof(A_game_takes_its_whole_place_in_the_pane_whatever_its_allow_hidpi_says),
            A_game_takes_its_whole_place_in_the_pane_whatever_its_allow_hidpi_says));

    private void A_game_of_no_size_known_takes_the_whole_pane()
    {
        Assert.Equal(new GamePlace(0, 0, 700, 238), GameFit.Within((700, 238), (0, 0)));
    }

    private void A_game_never_falls_outside_the_pane_whatever_their_sizes()
    {
        Random random = new(1305);
        for (int i = 0; i < 2000; i++)
        {
            (int Width, int Height) pane = (random.Next(1, 4000), random.Next(1, 3000));
            (int Width, int Height) game = (random.Next(1, 8000), random.Next(1, 8000));
            GamePlace place = GameFit.Within(pane, game);
            Assert.True(place.X >= 0 && place.Y >= 0 && place.X + place.Width <= pane.Width && place.Y + place.Height <= pane.Height && place.Width >= 1 && place.Height >= 1,
                $"A game of {game} in a pane of {pane} draws at {place}, over what lies outside the pane");
            Assert.True(place.Width == pane.Width || place.Height == pane.Height || Math.Abs(pane.Width - place.Width) <= 1 || Math.Abs(pane.Height - place.Height) <= 1,
                $"A game of {game} in a pane of {pane} draws at {place}, smaller than the pane holds it");
        }
    }

    private void A_pointer_comes_to_the_game_from_its_own_top_left_and_stays_inside_it_past_its_edges()
    {
        GamePlace place = new(382, 0, 635, 476);
        Assert.Equal((9d, 20d), GameFit.Inside(200, 20, place, 2));
        Assert.Equal((0d, 20d), GameFit.Inside(10, 20, place, 2));
        Assert.Equal((317.5, 238d), GameFit.Inside(699, 400, place, 2));
    }

    private void A_press_on_the_panes_bars_is_no_press_on_the_game()
    {
        GamePlace place = new(382, 0, 635, 476);
        Assert.False(GameFit.Over(100, 100, place, 2));
        Assert.True(GameFit.Over(350, 100, place, 2));
        Assert.False(GameFit.Over(600, 100, place, 2));
    }

    private void A_place_a_game_names_comes_to_the_pane_from_the_games_own_top_left_in_what_its_godot_says()
    {
        GamePlace place = new(382, 40, 635, 476);
        Assert.Equal((291d, 70d), GameFit.FromGame(100, 50, place, scale: 2, perUnit: 1));
        Assert.Equal((241d, 45d), GameFit.FromGame(100, 50, place, scale: 2, perUnit: 2));
        Assert.Equal((482d, 90d), GameFit.FromGame(100, 50, new GamePlace(382, 40, 635, 476), scale: 1, perUnit: 1));
    }

    /// <summary>What Godot's embedded display makes of a size at the scale it was told, as its _window_set_size works them
    /// out: the points its layer takes and the pixels it draws in. Before 4.7 it reads no allow_hidpi; from then one left off
    /// draws a pixel a point, in a layer as large (the display and the pointer's points came in one change).</summary>
    private static ((double Width, double Height) Layer, (int Width, int Height) Drawn) Displayed((int Width, int Height) size, float scale, string version, bool allowHidpi)
    {
        double render = GodotExecutables.TakesPointerInPoints(version) && !allowHidpi ? 1 : scale;
        return ((size.Width / (double)scale, size.Height / (double)scale), ((int)(size.Width / scale * render), (int)(size.Height / scale * render)));
    }

    private void A_game_takes_its_whole_place_in_the_pane_whatever_its_allow_hidpi_says() => Cases.Each<(string Version, bool AllowHidpi)>(
    [
        ("4.5", true),
        ("4.5", false),
        ("4.7.2", true),
        ("4.7.2", false),
    ], row => A_game_takes_its_whole_place_in_the_pane_whatever_its_allow_hidpi_says(row.Version, row.AllowHidpi));

    private void A_game_takes_its_whole_place_in_the_pane_whatever_its_allow_hidpi_says(string version, bool allowHidpi)
    {
        const int Scale = 2;
        (int Width, int Height) pane = (700 * Scale, 238 * Scale);
        GamePlace place = GameFit.Within(pane, (720, 540));
        float told = BinaryPrimitives.ReadSingleLittleEndian(GodotInputCodec.DisplayState(Scale, 72 * Scale, Scale, 1));

        ((double Width, double Height) layer, _) = Displayed((place.Width, place.Height), told, version, allowHidpi);

        Assert.Equal(((double)place.Width / Scale, (double)place.Height / Scale), layer);
        Assert.True(layer.Width <= 700 && layer.Height <= 238, $"Godot {version}'s layer of {layer} points overruns a pane of 700 by 238, so the game shows cropped");
    }

}

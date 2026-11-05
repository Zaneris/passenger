using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>Where a game shows in the Game window: its own shape as large as the pane holds it, at the pane's middle, never
/// past the pane's edges, and what a pointer over the pane comes to in the game.</summary>
public class GameFitTests
{
    [Fact]
    public void A_game_wider_than_the_pane_is_tall_for_fills_its_height_at_the_panes_middle()
    {
        Assert.Equal(new GamePlace(382, 0, 635, 476), GameFit.Within((1400, 476), (720, 540)));
    }

    [Fact]
    public void A_pane_larger_than_the_game_draws_it_larger_in_its_shape()
    {
        Assert.Equal(new GamePlace(13, 0, 1373, 1030), GameFit.Within((1400, 1030), (720, 540)));
    }

    [Fact]
    public void A_tall_game_in_a_wide_pane_and_a_wide_game_in_a_tall_pane_keep_their_shapes()
    {
        Assert.Equal(new GamePlace(350, 0, 300, 450), GameFit.Within((1000, 450), (480, 720)));
        Assert.Equal(new GamePlace(0, 306, 400, 225), GameFit.Within((400, 837), (1280, 720)));
    }

    [Fact]
    public void A_game_of_no_size_known_takes_the_whole_pane()
    {
        Assert.Equal(new GamePlace(0, 0, 700, 238), GameFit.Within((700, 238), (0, 0)));
    }

    [Fact]
    public void A_game_never_falls_outside_the_pane_whatever_their_sizes()
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

    [Fact]
    public void A_resolution_of_the_games_own_arguments_is_its_size_before_its_projects()
    {
        (int, int) project = (1152, 648);
        Assert.Equal((400, 800), GameFit.OwnSize(["--debug-collisions", "--resolution", "400x800"], project));
        Assert.Equal(project, GameFit.OwnSize(["--debug-collisions"], project));
        Assert.Equal(project, GameFit.OwnSize(["--", "--resolution", "400x800"], project));
        Assert.Equal(project, GameFit.OwnSize(["--resolution"], project));
        Assert.Equal(project, GameFit.OwnSize(["--resolution", "large"], project));
    }

    [Fact]
    public void A_pointer_comes_to_the_game_from_its_own_top_left_and_stays_inside_it_past_its_edges()
    {
        GamePlace place = new(382, 0, 635, 476);
        Assert.Equal((9d, 20d), GameFit.Inside(200, 20, place, 2));
        Assert.Equal((0d, 20d), GameFit.Inside(10, 20, place, 2));
        Assert.Equal((317.5, 238d), GameFit.Inside(699, 400, place, 2));
    }

    [Fact]
    public void A_press_on_the_panes_bars_is_no_press_on_the_game()
    {
        GamePlace place = new(382, 0, 635, 476);
        Assert.False(GameFit.Over(100, 100, place, 2));
        Assert.True(GameFit.Over(350, 100, place, 2));
        Assert.False(GameFit.Over(600, 100, place, 2));
    }

    [Fact]
    public void Godot_takes_the_pointer_on_its_embedded_display_in_points_from_4_7()
    {
        Assert.False(GodotExecutables.TakesPointerInPoints("4.6.2"));
        Assert.True(GodotExecutables.TakesPointerInPoints("4.7"));
        Assert.True(GodotExecutables.TakesPointerInPoints("4.8.1"));
        Assert.False(GodotExecutables.TakesPointerInPoints(null));
    }
}

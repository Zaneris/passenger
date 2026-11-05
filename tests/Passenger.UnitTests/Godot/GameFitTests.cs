using System.Buffers.Binary;
using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.UnitTests.Godot;

/// <summary>Where a game shows in the Game window: its own shape as large as the pane holds it, at the pane's middle, never
/// past the pane's edges, what a pointer over the pane comes to in the game, and where a place the game names comes to over
/// the pane.</summary>
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
    public void A_games_warp_of_the_pointer_reads_as_the_place_it_names_as_godot_sends_one()
    {
        byte[] frame = GodotMessage.Frame(GodotWords.EmbeddedWarpMouse, new GodotMath(GodotType.Vector2I, [320, -4]));

        GodotMessage read = GodotMessage.Read(frame.AsSpan(4));

        Assert.Equal("game_view:warp_mouse", read.Name);
        Assert.Equal((320, -4), GameFit.Warped(read.Data));
    }

    [Fact]
    public void A_warp_message_of_any_other_form_names_no_place()
    {
        Assert.Null(GameFit.Warped([]));
        Assert.Null(GameFit.Warped([320L, 4L]));
        Assert.Null(GameFit.Warped([new GodotMath(GodotType.Vector2, [320, 4])]));
    }

    [Fact]
    public void A_place_a_game_names_comes_to_the_pane_from_the_games_own_top_left_in_what_its_godot_says()
    {
        GamePlace place = new(382, 40, 635, 476);
        Assert.Equal((291d, 70d), GameFit.FromGame(100, 50, place, scale: 2, perUnit: 1));
        Assert.Equal((241d, 45d), GameFit.FromGame(100, 50, place, scale: 2, perUnit: 2));
        Assert.Equal((482d, 90d), GameFit.FromGame(100, 50, new GamePlace(382, 40, 635, 476), scale: 1, perUnit: 1));
    }

    [Fact]
    public void A_place_a_game_names_past_its_edges_stays_on_the_game()
    {
        GamePlace place = new(382, 40, 635, 476);
        Assert.Equal((191d, 20d), GameFit.FromGame(-30, -30, place, scale: 2, perUnit: 1));
        (double x, double y) = GameFit.FromGame(5000, 5000, place, scale: 2, perUnit: 1);
        Assert.True(GameFit.Over(x, y, place, 2), $"A place past the game's far corner came to {x},{y}, off the game placed at {place}, on what lies beside the pane");
    }

    [Fact]
    public void A_place_a_game_names_is_where_a_pointer_there_reaches_it_again()
    {
        GamePlace place = new(382, 40, 635, 476);
        foreach (double perUnit in new[] { 1d, 2d })
        {
            (double x, double y) = GameFit.FromGame(120, 90, place, 2, perUnit);
            (double backX, double backY) = GameFit.Inside(x, y, place, 2);
            Assert.Equal((120d, 90d), (backX * perUnit, backY * perUnit));
        }
    }

    /// <summary>What Godot's embedded display makes of a size at the scale it was told, as its _window_set_size works them
    /// out: the points its layer takes and the pixels it draws in. Before 4.7 it reads no allow_hidpi; from then one left off
    /// draws a pixel a point, in a layer as large (the display and the pointer's points came in one change).</summary>
    private static ((double Width, double Height) Layer, (int Width, int Height) Drawn) Displayed((int Width, int Height) size, float scale, string version, bool allowHidpi)
    {
        double render = GodotExecutables.TakesPointerInPoints(version) && !allowHidpi ? 1 : scale;
        return ((size.Width / (double)scale, size.Height / (double)scale), ((int)(size.Width / scale * render), (int)(size.Height / scale * render)));
    }

    [Theory]
    [InlineData("4.5", true)]
    [InlineData("4.5", false)]
    [InlineData("4.6.3", true)]
    [InlineData("4.6.3", false)]
    [InlineData("4.7.2", true)]
    [InlineData("4.7.2", false)]
    public void A_game_takes_its_whole_place_in_the_pane_whatever_its_allow_hidpi_says(string version, bool allowHidpi)
    {
        const int Scale = 2;
        (int Width, int Height) pane = (700 * Scale, 238 * Scale);
        GamePlace place = GameFit.Within(pane, (720, 540));
        float told = BinaryPrimitives.ReadSingleLittleEndian(GodotInputCodec.DisplayState(Scale, 72 * Scale, Scale, 1));

        ((double Width, double Height) layer, _) = Displayed((place.Width, place.Height), told, version, allowHidpi);

        Assert.Equal(((double)place.Width / Scale, (double)place.Height / Scale), layer);
        Assert.True(layer.Width <= 700 && layer.Height <= 238, $"Godot {version}'s layer of {layer} points overruns a pane of 700 by 238, so the game shows cropped");
    }

    [Fact]
    public void Only_a_godot_that_reads_allow_hidpi_draws_more_coarsely_for_it()
    {
        Assert.Equal((1400, 476), Displayed((1400, 476), 2, "4.6.3", allowHidpi: false).Drawn);
        Assert.Equal((700, 238), Displayed((1400, 476), 2, "4.7.2", allowHidpi: false).Drawn);
        Assert.Equal((1400, 476), Displayed((1400, 476), 2, "4.7.2", allowHidpi: true).Drawn);
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

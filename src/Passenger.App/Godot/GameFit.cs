namespace Passenger.App.Godot;

/// <summary>A game's place in the Game window, in pixels from the pane's top left.</summary>
public readonly record struct GamePlace(int X, int Y, int Width, int Height);

/// <summary>Where a game shows in the Game window: its own shape as large as the pane holds it, at the pane's middle, where
/// a pointer over the pane falls in it, and where a place the game names falls over the pane.</summary>
public static class GameFit
{
    /// <summary>The largest place of a game's shape in a pane of a size in pixels, up or down from the game's own size, at the
    /// pane's middle; the whole pane for a game of no size known.</summary>
    public static GamePlace Within((int Width, int Height) pane, (int Width, int Height) game)
    {
        if (pane.Width <= 0 || pane.Height <= 0 || game.Width <= 0 || game.Height <= 0)
        {
            return new GamePlace(0, 0, pane.Width, pane.Height);
        }
        double ratio = Math.Min((double)pane.Width / game.Width, (double)pane.Height / game.Height);
        int width = Math.Clamp((int)Math.Round(game.Width * ratio), 1, pane.Width);
        int height = Math.Clamp((int)Math.Round(game.Height * ratio), 1, pane.Height);
        return new GamePlace((pane.Width - width) / 2, (pane.Height - height) / 2, width, height);
    }

    /// <summary>The size a game's window opens at: what its own arguments' <c>--resolution</c> says, else its project's.</summary>
    public static (int Width, int Height) OwnSize(IReadOnlyList<string> arguments, (int Width, int Height) project)
    {
        int at = arguments.TakeWhile(a => a is not ("--" or "++")).ToList().LastIndexOf("--resolution");
        return at >= 0 && at + 1 < arguments.Count && arguments[at + 1].Split('x') is [string wide, string high]
            && int.TryParse(wide, System.Globalization.CultureInfo.InvariantCulture, out int width)
            && int.TryParse(high, System.Globalization.CultureInfo.InvariantCulture, out int height) && width > 0 && height > 0
            ? (width, height) : project;
    }

    /// <summary>A place over the pane, in its logical units, from the top left of a game placed in it at a scale, held
    /// inside the game: a press held past its edge stays the game's.</summary>
    public static (double X, double Y) Inside(double x, double y, GamePlace place, double scale) =>
        (Math.Clamp(x - (place.X / scale), 0, place.Width / scale), Math.Clamp(y - (place.Y / scale), 0, place.Height / scale));

    /// <summary>The place a game's <see cref="GodotWords.EmbeddedWarpMouse"/> names; null for a message of any other form.</summary>
    public static (int X, int Y)? Warped(IReadOnlyList<object?> data) =>
        data is [GodotMath { Type: GodotType.Vector2I, Values: [double x, double y] }] ? ((int)x, (int)y) : null;

    /// <summary>A place a game names, from its top left in what its display takes the pointer in, <paramref name="perUnit"/>
    /// of them to one of the pane's logical units, as a place over the pane in those units; held on the game, as a place past
    /// its edge would put the pointer on what lies beside the pane.</summary>
    public static (double X, double Y) FromGame(double x, double y, GamePlace place, double scale, double perUnit) =>
        ((place.X / scale) + Math.Clamp(x / perUnit, 0, Math.Max(0, (place.Width / scale) - 1)),
            (place.Y / scale) + Math.Clamp(y / perUnit, 0, Math.Max(0, (place.Height / scale) - 1)));

    /// <summary>Whether a place over the pane, in its logical units, falls on a game placed in it at a scale.</summary>
    public static bool Over(double x, double y, GamePlace place, double scale) =>
        x >= place.X / scale && x < (place.X + place.Width) / scale && y >= place.Y / scale && y < (place.Y + place.Height) / scale;
}

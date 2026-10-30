namespace Passenger.Core;

/// <summary>Where a window opens on a screen, as every window the IDE opens is fitted: its client area no larger than
/// the screen's working area leaves inside its frame (title bar and borders), though never under its least size, and its
/// frame wholly on the area where it fits, else from the area's top-left so its title bar can be reached; centred over
/// what it opens over, or kept where it was placed and only moved back onto the area. Sizes are in the window's own
/// units, places in the screen's pixels.</summary>
public static class WindowFit
{
    /// <summary>A rectangle in a screen's pixels.</summary>
    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }

    /// <summary>A window's client size in its own units and its frame's top-left in the screen's pixels.</summary>
    public readonly record struct Fitted(double Width, double Height, int X, int Y)
    {
        /// <summary>The frame's whole rectangle in the screen's pixels, given what the frame adds round the client area.</summary>
        public Box Frame(double scaling, double frameWidth, double frameHeight) =>
            new(X, Y, Pixels(Width + frameWidth, scaling), Pixels(Height + frameHeight, scaling));
    }

    /// <summary>A window asking for <paramref name="wanted"/> (its least <paramref name="least"/>, which it keeps), its frame
    /// adding <paramref name="frame"/>, fitted to a working area at <paramref name="scaling"/> pixels a unit: centred over
    /// <paramref name="over"/> (its owner's frame, or the area), or kept at <paramref name="at"/>.</summary>
    public static Fitted Within(Box area, double scaling, (double Width, double Height) wanted, (double Width, double Height) least,
        (double Width, double Height) frame, Box over, (int X, int Y)? at = null)
    {
        double width = Math.Max(least.Width, Math.Min(wanted.Width, Math.Floor((area.Width / scaling) - frame.Width)));
        double height = Math.Max(least.Height, Math.Min(wanted.Height, Math.Floor((area.Height / scaling) - frame.Height)));
        int frameWidth = Pixels(width + frame.Width, scaling);
        int frameHeight = Pixels(height + frame.Height, scaling);
        (int x, int y) = at ?? (over.X + ((over.Width - frameWidth) / 2), over.Y + ((over.Height - frameHeight) / 2));
        return new Fitted(width, height,
            Math.Clamp(x, area.X, Math.Max(area.X, area.Right - frameWidth)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - frameHeight)));
    }

    /// <summary>A length in units as a screen's pixels, rounded up as a window's size is.</summary>
    private static int Pixels(double units, double scaling) => (int)Math.Ceiling(units * scaling);
}

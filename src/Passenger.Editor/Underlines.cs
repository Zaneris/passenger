using Avalonia;
using Avalonia.Media;

namespace Passenger.Editor;

/// <summary>The shape of the line under a marked range.</summary>
public enum Underline
{
    Wave,
    Dashes,
    Dots,
}

/// <summary>The line under a marked range, each severity a shape of its own as well as its colour, so an error, a warning
/// and a suggestion are told apart without their colours: an error a wave, a warning dashes, a suggestion dots, from the
/// strongest to the weakest. Each is made at the origin along its length, its pattern repeating every <see cref="Period"/>.</summary>
public static class Underlines
{
    private const double WaveStep = 2.5;
    private const double Dash = 4;
    private const double Dot = 1.5;

    /// <summary>The shape a severity's line takes.</summary>
    public static Underline Of(MarkerKind kind) => kind switch
    {
        MarkerKind.Error => Underline.Wave,
        MarkerKind.Warning => Underline.Dashes,
        _ => Underline.Dots,
    };

    /// <summary>How far along its pattern repeats.</summary>
    public static double Period(Underline shape) => shape switch
    {
        Underline.Wave => WaveStep * 2,
        Underline.Dashes => Dash + 2,
        _ => Dot * 2,
    };

    /// <summary>Traces a shape's line from the origin rightward, <paramref name="length"/> long, the wave from its top two
    /// above the origin down to it and the others between.</summary>
    public static void Trace(StreamGeometryContext path, Underline shape, double length)
    {
        if (shape == Underline.Wave)
        {
            path.BeginFigure(new Point(0, 0), false);
            bool up = true;
            for (double x = WaveStep; x < length + WaveStep; x += WaveStep)
            {
                path.LineTo(new Point(Math.Min(x, length), up ? -2 : 0));
                up = !up;
            }
            path.EndFigure(false);
            return;
        }
        double piece = shape == Underline.Dashes ? Dash : Dot;
        for (double x = 0; x < length; x += Period(shape))
        {
            path.BeginFigure(new Point(x, -1), false);
            path.LineTo(new Point(Math.Min(x + piece, length), -1));
            path.EndFigure(false);
        }
    }
}

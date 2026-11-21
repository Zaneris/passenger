using Avalonia.Media;

namespace Passenger.Tests;

/// <summary>Colours as eyes without one kind of cone see them, by Machado, Oliveira and Fernandes' 2009 model at full
/// severity over linear sRGB, and how far apart two colours look, as OKLab measures it: about 0.02 for two hard to tell
/// apart, 0.1 and more for two plainly different.</summary>
public static class ColourVision
{
    /// <summary>Each kind of colour blindness the model gives, by name, and its matrix.</summary>
    public static readonly IReadOnlyList<(string Name, double[,] Matrix)> Kinds =
    [
        ("protanopia", new[,] { { 0.152286, 1.052583, -0.204868 }, { 0.114503, 0.786281, 0.099216 }, { -0.003882, -0.048116, 1.051998 } }),
        ("deuteranopia", new[,] { { 0.367322, 0.860646, -0.227968 }, { 0.280085, 0.672501, 0.047413 }, { -0.011820, 0.042940, 0.968881 } }),
        ("tritanopia", new[,] { { 1.255528, -0.076749, -0.178779 }, { -0.078411, 0.930809, 0.147602 }, { 0.004733, 0.691367, 0.303900 } }),
    ];

    /// <summary>A colour as seen through a kind's matrix.</summary>
    public static Color Seen(Color colour, double[,] matrix)
    {
        double[] linear = [Linear(colour.R), Linear(colour.G), Linear(colour.B)];
        byte Channel(int row) => (byte)Math.Round(Math.Clamp(Gamma(Math.Clamp((matrix[row, 0] * linear[0]) + (matrix[row, 1] * linear[1]) + (matrix[row, 2] * linear[2]), 0, 1)), 0, 1) * 255);
        return Color.FromRgb(Channel(0), Channel(1), Channel(2));
    }

    /// <summary>How far apart two colours look, in OKLab.</summary>
    public static double Apart(Color one, Color other)
    {
        (double l1, double a1, double b1) = Lab(one);
        (double l2, double a2, double b2) = Lab(other);
        return Math.Sqrt(((l1 - l2) * (l1 - l2)) + ((a1 - a2) * (a1 - a2)) + ((b1 - b2) * (b1 - b2)));
    }

    private static (double L, double A, double B) Lab(Color colour)
    {
        (double r, double g, double b) = (Linear(colour.R), Linear(colour.G), Linear(colour.B));
        double l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        double m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        double s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        return ((0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
}

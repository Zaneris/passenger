using System.Globalization;

namespace Passenger.Core;

/// <summary>A size in bytes as a person reads it, one way wherever it is said: plain under a kilobyte, then in KB, MB and GB
/// of 1,024 each, to a tenth ("512 B", "1.5 KB", "39.8 MB", "2 GB"), its digits as the IDE's words write them whatever the
/// person's culture.</summary>
public static class Sizes
{
    private const double Kilobyte = 1024;

    /// <summary>The size in words, its sign kept.</summary>
    public static string Said(long bytes)
    {
        double size = Math.Abs((double)bytes);
        (double shown, string unit) = size < Kilobyte ? (size, "B")
            : size < Kilobyte * Kilobyte ? (size / Kilobyte, "KB")
            : size < Kilobyte * Kilobyte * Kilobyte ? (size / (Kilobyte * Kilobyte), "MB")
            : (size / (Kilobyte * Kilobyte * Kilobyte), "GB");
        string sign = bytes < 0 ? "-" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{shown:#,0.#} {unit}");
    }
}

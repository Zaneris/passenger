namespace Passenger.Core;

/// <summary>A size in bytes as a person reads it, one way wherever it is said: plain under a kilobyte, then in KB, MB and GB
/// of 1,024 each, to a tenth ("512 B", "1.5 KB", "39.8 MB", "2 GB"), its digits the IDE's own (Numbers), or as Words each
/// language says by its own units (<see cref="Words(long)"/>).</summary>
public static class Sizes
{
    private const double Kilobyte = 1024;

    /// <summary>The size in words, its sign kept.</summary>
    public static string Said(long bytes)
    {
        (double shown, string unit) = Measured(bytes);
        string written = unit switch
        {
            "byte" => "B",
            "kilobyte" => "KB",
            "megabyte" => "MB",
            _ => "GB",
        };
        string sign = bytes < 0 ? "-" : string.Empty;
        return $"{sign}{Numbers.Said(shown, 1)} {written}";
    }

    /// <summary>The size as Words, for a message's argument: its English as <see cref="Said"/> says it, in a carried
    /// language the same number and unit by that language's patterns.</summary>
    public static Words Words(long bytes) => Core.Words.Of(new SaidSize(bytes));

    /// <summary>The size unsigned in its unit, and the unit: byte, kilobyte, megabyte or gigabyte.</summary>
    private static (double Shown, string Unit) Measured(long bytes)
    {
        double size = Math.Abs((double)bytes);
        return size < Kilobyte ? (size, "byte")
            : size < Kilobyte * Kilobyte ? (size / Kilobyte, "kilobyte")
            : size < Kilobyte * Kilobyte * Kilobyte ? (size / (Kilobyte * Kilobyte), "megabyte")
            : (size / (Kilobyte * Kilobyte * Kilobyte), "gigabyte");
    }

    private sealed record SaidSize(long Bytes) : ISaid
    {
        public string In(UiLanguage language)
        {
            (double shown, string unit) = Measured(Bytes);
            return Numbers.Measured(language, Bytes < 0 ? -shown : shown, 1, unit) ?? Said(Bytes);
        }
    }
}

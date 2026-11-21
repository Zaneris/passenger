namespace Passenger.Core;

/// <summary>A while as a person reads it, one way wherever it is said: in milliseconds under a second, seconds under a
/// minute, minutes under an hour, then hours, each to a tenth and in the largest unit it reaches once so rounded ("0.4 ms",
/// "250 ms", "1.5 s", "1 minute", "2.5 hours"), its digits the IDE's own (Numbers).</summary>
public static class Durations
{
    private const double Second = 1000;
    private const double Minute = 60 * Second;
    private const double Hour = 60 * Minute;

    /// <summary>The while in words, its sign kept.</summary>
    public static string Said(TimeSpan time)
    {
        double milliseconds = Math.Abs(time.TotalMilliseconds);
        (double shown, string unit) = Tenth(milliseconds) < Second ? (Tenth(milliseconds), "ms")
            : Tenth(milliseconds / Second) < 60 ? (Tenth(milliseconds / Second), "s")
            : Tenth(milliseconds / Minute) < 60 ? (Tenth(milliseconds / Minute), "minute")
            : (Tenth(milliseconds / Hour), "hour");
        string plural = unit is "minute" or "hour" && shown != 1 ? "s" : string.Empty;
        string sign = time < TimeSpan.Zero && shown > 0 ? "-" : string.Empty;
        return $"{sign}{Numbers.Said(shown, 1)} {unit}{plural}";
    }

    private static double Tenth(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}

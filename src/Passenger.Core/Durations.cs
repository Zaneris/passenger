namespace Passenger.Core;

/// <summary>A while as a person reads it, one way wherever it is said: in milliseconds under a second, seconds under a
/// minute, minutes under an hour, then hours, each to a tenth and in the largest unit it reaches once so rounded ("0.4 ms",
/// "250 ms", "1.5 s", "1 minute", "2.5 hours"), its digits the IDE's own (Numbers), or as Words each language says by its
/// own units (<see cref="Words(TimeSpan)"/>).</summary>
public static class Durations
{
    private const double Second = 1000;
    private const double Minute = 60 * Second;
    private const double Hour = 60 * Minute;

    /// <summary>The while in words, its sign kept.</summary>
    public static string Said(TimeSpan time)
    {
        (double shown, string unit) = Measured(time);
        string written = unit switch
        {
            "millisecond" => "ms",
            "second" => "s",
            _ when shown == 1 => unit,
            _ => unit + "s",
        };
        string sign = time < TimeSpan.Zero && shown > 0 ? "-" : string.Empty;
        return $"{sign}{Numbers.Said(shown, 1)} {written}";
    }

    /// <summary>The while as Words, for a message's argument: its English as <see cref="Said"/> says it, in a carried
    /// language the same number and unit by that language's patterns.</summary>
    public static Words Words(TimeSpan time) => Core.Words.Of(new SaidWhile(time));

    /// <summary>The while's size to a tenth, unsigned, and its unit: millisecond, second, minute or hour.</summary>
    internal static (double Shown, string Unit) Measured(TimeSpan time)
    {
        double milliseconds = Math.Abs(time.TotalMilliseconds);
        return Tenth(milliseconds) < Second ? (Tenth(milliseconds), "millisecond")
            : Tenth(milliseconds / Second) < 60 ? (Tenth(milliseconds / Second), "second")
            : Tenth(milliseconds / Minute) < 60 ? (Tenth(milliseconds / Minute), "minute")
            : (Tenth(milliseconds / Hour), "hour");
    }

    private static double Tenth(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private sealed record SaidWhile(TimeSpan Time) : ISaid
    {
        public string In(UiLanguage language)
        {
            (double shown, string unit) = Measured(Time);
            return Numbers.Measured(language, Time < TimeSpan.Zero && shown > 0 ? -shown : shown, 1, unit) ?? Said(Time);
        }
    }
}

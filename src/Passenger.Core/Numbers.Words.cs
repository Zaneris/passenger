using System.Globalization;

namespace Passenger.Core;

public static partial class Numbers
{
    /// <summary>A count as Words, for a message's argument: "1,234" in English, in a carried language as its culture writes it.</summary>
    public static Words Words(long count) => Core.Words.Of(new SaidNumber(count, 0));

    /// <summary>A measure as Words, rounded to at most <paramref name="places"/> places as <see cref="Said(double, int)"/> rounds it.</summary>
    public static Words Words(double value, int places) => Core.Words.Of(new SaidNumber(value, places));

    /// <summary>A measure in a unit as a carried language writes it: its number in the asked culture, at most
    /// <paramref name="places"/> places, in the unit's pattern of the plural category the number takes; null in English and
    /// in a language whose patterns are not carried.</summary>
    internal static string? Measured(UiLanguage language, double value, int places, string unit)
    {
        if (language.Reached is not { } reached)
        {
            return null;
        }
        string number = value.ToString(AtMost[places], language.Formats);
        return LanguagePatterns.Measure(reached, unit, number, PluralRules.Of(reached, value, Places(value, places)));
    }

    /// <summary>The digits after the point a measure is written with, rounded to at most <paramref name="places"/>.</summary>
    private static int Places(double value, int places)
    {
        string written = value.ToString(AtMost[places], CultureInfo.InvariantCulture);
        int point = written.IndexOf('.', StringComparison.Ordinal);
        return point < 0 ? 0 : written.Length - point - 1;
    }

    private sealed record SaidNumber(double Value, int Places) : ISaid
    {
        public string In(UiLanguage language) => language.Reached is null ? Said(Value, Places) : Value.ToString(AtMost[Places], language.Formats);
    }
}

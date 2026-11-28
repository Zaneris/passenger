using System.Globalization;

namespace Passenger.Core;

/// <summary>The operands of a number CLDR's plural rules read: its absolute value (n), its whole digits (i), how many digits
/// follow its point as written (v) and without trailing zeros (w), those digits as a number (f) and without trailing zeros
/// (t), and its exponent in compact notation (e, never used here).</summary>
public readonly record struct PluralOperands(decimal N, long I, int V, int W, long F, long T, int E)
{
    /// <summary>The operands of a number as it is written with <paramref name="places"/> digits after its point.</summary>
    public static PluralOperands Of(double value, int places)
    {
        decimal n = Math.Round(Math.Abs((decimal)value), places, MidpointRounding.AwayFromZero);
        string written = n.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int point = written.IndexOf('.');
        string fraction = point < 0 ? string.Empty : written[(point + 1)..];
        string trimmed = fraction.TrimEnd('0');
        return new PluralOperands(n, (long)decimal.Truncate(n), fraction.Length, trimmed.Length,
            fraction.Length == 0 ? 0 : long.Parse(fraction, CultureInfo.InvariantCulture),
            trimmed.Length == 0 ? 0 : long.Parse(trimmed, CultureInfo.InvariantCulture), 0);
    }
}

/// <summary>Which plural category a count takes in a language (zero, one, two, few, many or other), by the Unicode CLDR's
/// rules for it (PluralRules.Cldr.cs, from tools/cldr.py): a message's plural chooses its case by this. A culture CLDR
/// gives no rules of takes its parent's, and at the end of its chain English's.</summary>
public static partial class PluralRules
{
    /// <summary>The category of <paramref name="value"/>, written with <paramref name="places"/> digits after its point, in
    /// <paramref name="culture"/>.</summary>
    public static string Of(CultureInfo culture, double value, int places = 0)
    {
        PluralOperands operands = PluralOperands.Of(value, places);
        for (CultureInfo at = culture; at.Name.Length > 0; at = at.Parent)
        {
            if (Category(at.Name, operands) is { } category)
            {
                return category;
            }
        }
        return Category("en", operands)!;
    }

    /// <summary>Whether a value CLDR's n reads is whole and within a range, as its rules read <c>n = 2..10</c>.</summary>
    private static bool In(decimal value, decimal low, decimal high) => value == decimal.Truncate(value) && value >= low && value <= high;
}

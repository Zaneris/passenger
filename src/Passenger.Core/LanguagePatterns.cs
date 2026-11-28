using System.Globalization;

namespace Passenger.Core;

/// <summary>How a language joins a list, by and or by or, and writes a measure's unit, by the Unicode CLDR's patterns for it
/// (LanguagePatterns.Cldr.cs, from tools/cldr.py), read from the nearest culture of its chain that has them: null where
/// none has, as for English, whose are Passenger's own (<see cref="Plural.Listed(IReadOnlyList{string})"/>,
/// <see cref="Durations.Said"/>, <see cref="Sizes.Said"/>).</summary>
public static partial class LanguagePatterns
{
    /// <summary>The items joined as <paramref name="culture"/>'s sentences list them, by or where <paramref name="either"/>:
    /// a pair by its pair's pattern, more by its start's, middles' and end's; null where no culture of its chain has them.</summary>
    public static string? Joined(CultureInfo culture, IReadOnlyList<string> items, bool either)
    {
        if (items.Count <= 1)
        {
            return string.Concat(items);
        }
        if (Nearest(culture, Lists) is not { } parts)
        {
            return null;
        }
        int first = either ? 4 : 0;
        if (items.Count == 2)
        {
            return Put(parts[first + 3], items[0], items[1]);
        }
        string joined = Put(parts[first + 2], items[^2], items[^1]);
        for (int i = items.Count - 3; i > 0; i--)
        {
            joined = Put(parts[first + 1], items[i], joined);
        }
        return Put(parts[first], items[0], joined);
    }

    /// <summary>A measure in <paramref name="unit"/> (millisecond, second, minute, hour, byte, kilobyte, megabyte, gigabyte)
    /// as <paramref name="culture"/> writes it: its number, written already, in the pattern of the plural category it takes,
    /// else of other; null where no culture of its chain has them.</summary>
    public static string? Measure(CultureInfo culture, string unit, string number, string category)
    {
        if (Nearest(culture, language => Units(language, unit)) is not { } patterns)
        {
            return null;
        }
        string? other = null;
        for (int i = 0; i + 1 < patterns.Length; i += 2)
        {
            if (patterns[i] == category)
            {
                return Put(patterns[i + 1], number, string.Empty);
            }
            other = patterns[i] == "other" ? patterns[i + 1] : other;
        }
        return other is null ? null : Put(other, number, string.Empty);
    }

    private static string[]? Nearest(CultureInfo culture, Func<string, string[]?> of)
    {
        foreach (CultureInfo at in UiLanguage.Chain(culture))
        {
            if (of(at.Name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static string Put(string pattern, string first, string second) => string.Format(CultureInfo.InvariantCulture, pattern, first, second);
}

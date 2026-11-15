using System.Globalization;
using Passenger.Core;

namespace Passenger.Text;

public static partial class WideCharacters
{
    /// <summary>Whether a character is drawn two columns wide: within one of the ranges, found by halving their firsts.</summary>
    public static bool Contains(int rune)
    {
        ReadOnlySpan<int> ranges = Ranges;
        if (rune < ranges[0] || rune > ranges[^1])
        {
            return false;
        }
        int range = Sorted.FirstAfter(ranges.Length / 2, ranges, static (table, i) => table[i * 2], rune) - 1;
        return range >= 0 && rune <= ranges[(range * 2) + 1];
    }

    /// <summary>How many columns a character takes on screen, as xterm.js's Unicode 11 widths and wcwidth give it: none
    /// for a combining mark, a format character (a joiner, a direction mark) or a Hangul vowel or final that joins its
    /// syllable; two for East Asian Wide and Fullwidth characters and emoji (<see cref="Contains"/>); one else.</summary>
    public static int Cells(int rune)
    {
        if (rune < 0x300)
        {
            return 1;
        }
        if (rune is >= 0x1160 and <= 0x11FF or >= 0xD7B0 and <= 0xD7FF)
        {
            return 0;
        }
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }
        return Contains(rune) ? 2 : 1;
    }
}

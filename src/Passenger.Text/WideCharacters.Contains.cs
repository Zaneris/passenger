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
}

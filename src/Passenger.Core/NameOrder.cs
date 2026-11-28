using System.Text;

namespace Passenger.Core;

/// <summary>The order a listing's names go in (a folder's files and folders, a solution's folders and projects): character by
/// character, punctuation, spaces and other signs before digits and digits before letters, letters whatever their case, so
/// a name begun with an underscore leads those begun with a letter; a name before the longer ones it begins. Two names
/// alike but for case go by their characters' values, so the order never hangs on how they were listed. The same on every
/// machine: no culture's rules, which differ by ICU, NLS and locale.</summary>
public sealed class NameOrder : IComparer<string>
{
    public static readonly NameOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }
        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            Rune.DecodeFromUtf16(x.AsSpan(i), out Rune one, out int oneLength);
            Rune.DecodeFromUtf16(y.AsSpan(j), out Rune other, out int otherLength);
            int byKind = Kind(one).CompareTo(Kind(other));
            if (byKind != 0)
            {
                return byKind;
            }
            int byValue = Rune.ToUpperInvariant(one).Value.CompareTo(Rune.ToUpperInvariant(other).Value);
            if (byValue != 0)
            {
                return byValue;
            }
            i += oneLength;
            j += otherLength;
        }
        int byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }

    /// <summary>Where a character's kind goes: anything but a digit or a letter first, then digits, then letters.</summary>
    private static int Kind(Rune character) => Rune.IsLetter(character) ? 2 : Rune.IsDigit(character) ? 1 : 0;
}

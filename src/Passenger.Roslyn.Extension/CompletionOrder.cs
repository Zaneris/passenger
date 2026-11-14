using System.Globalization;

namespace Passenger.Roslyn.Extension;

/// <summary>A whole C# completion list's sort texts made to keep the server's order however long the list. The server
/// begins each item's sort text with its place in the list in four digits at least, so in a list of more than 10,000 a
/// place of five digits sorts among the thousands (10003 after 1000, before 1001). Each place is written again in as many
/// digits as the list's last place takes, the rest as the server wrote it, as resolving an item reads its sort text's end.
/// A list's later part (<see cref="CompletionParts"/>) has each place marked to sort after any of the first part's.
/// Compiled into the extension, which writes them, and the unit tests.</summary>
public static class CompletionOrder
{
    /// <summary>The fewest digits the server writes a place in.</summary>
    private const int Digits = 4;

    /// <summary>What begins each place of a later part: after every digit, before the '~' of the templates listed last.</summary>
    public const string Later = ":";

    /// <summary>The sort texts of a list's items in its order, each place widened to the last place's digits, and marked
    /// where they are a later part's; null where none needs it (a first part or whole list of 10,000 items or fewer), or
    /// where one does not begin with its place as the server writes it.</summary>
    public static string[]? Widened(IReadOnlyList<string?> sortTexts, bool later = false)
    {
        int width = (sortTexts.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        if (width <= Digits && !later)
        {
            return null;
        }
        string wide = "D" + Math.Max(width, Digits).ToString(CultureInfo.InvariantCulture);
        string mark = later ? Later : string.Empty;
        string[] widened = new string[sortTexts.Count];
        for (int i = 0; i < widened.Length; i++)
        {
            string place = i.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
            if (sortTexts[i] is not { } sort || !sort.StartsWith(place, StringComparison.Ordinal))
            {
                return null;
            }
            widened[i] = string.Concat(mark, i.ToString(wide, CultureInfo.InvariantCulture), sort.AsSpan(place.Length));
        }
        return widened;
    }
}

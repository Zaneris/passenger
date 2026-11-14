using System.Globalization;

namespace Passenger.Roslyn.Extension;

/// <summary>A whole C# completion list's sort texts made to keep the server's order however long the list. The server
/// begins each item's sort text with its place in the list in four digits at least, so in a list of more than 10,000 a
/// place of five digits sorts among the thousands (10003 after 1000, before 1001). Each place is written again in as many
/// digits as the list's last place takes, the rest as the server wrote it, as resolving an item reads its sort text's end.
/// Compiled into the extension, which writes them, and the unit tests.</summary>
public static class CompletionOrder
{
    /// <summary>The fewest digits the server writes a place in.</summary>
    private const int Digits = 4;

    /// <summary>The sort texts of a list's items in its order, each place widened to the last place's digits; null where none
    /// needs it (10,000 items or fewer), or where one does not begin with its place as the server writes it.</summary>
    public static string[]? Widened(IReadOnlyList<string?> sortTexts)
    {
        int width = (sortTexts.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        if (width <= Digits)
        {
            return null;
        }
        string wide = "D" + width.ToString(CultureInfo.InvariantCulture);
        string[] widened = new string[sortTexts.Count];
        for (int i = 0; i < widened.Length; i++)
        {
            string place = i.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
            if (sortTexts[i] is not { } sort || !sort.StartsWith(place, StringComparison.Ordinal))
            {
                return null;
            }
            widened[i] = string.Concat(i.ToString(wide, CultureInfo.InvariantCulture), sort.AsSpan(place.Length));
        }
        return widened;
    }
}

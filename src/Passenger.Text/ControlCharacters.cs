using System.Buffers;
using System.Globalization;

namespace Passenger.Text;

/// <summary>The control characters a line shows as marks of their own, as Vim shows them: each below a space but a tab and
/// a line feed, and DEL, as a caret and a letter (^L, ^?); each from U+0080 to U+009F as its code in angle brackets
/// (&lt;85&gt;). A mark is as many columns as it has characters (<see cref="TextNavigation.ColumnAfter(ReadOnlySpan{char}, int, int)"/>).</summary>
public static class ControlCharacters
{
    private static readonly string?[] Marks = Made();

    private static readonly SearchValues<char> Marked = SearchValues.Create([.. Enumerable.Range(0, Marks.Length).Where(c => Marks[c] is not null).Select(c => (char)c)]);

    /// <summary>The mark a character is shown as, null where it is drawn as itself.</summary>
    public static string? MarkOf(char c) => c < Marks.Length ? Marks[c] : null;

    /// <summary>Where the first character shown as a mark stands in a piece of text, -1 where none is.</summary>
    public static int FirstMarked(ReadOnlySpan<char> text) => text.IndexOfAny(Marked);

    private static string?[] Made()
    {
        string?[] marks = new string?[0xA0];
        for (int c = 0; c < 0x20; c++)
        {
            marks[c] = c is '\t' or '\n' ? null : "^" + (char)(c + '@');
        }
        marks[0x7F] = "^?";
        for (int c = 0x80; c < 0xA0; c++)
        {
            marks[c] = "<" + c.ToString("x2", CultureInfo.InvariantCulture) + ">";
        }
        return marks;
    }
}

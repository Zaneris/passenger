using System.Buffers;
using System.Globalization;
using System.Text;

namespace Passenger.App.Mcp;

/// <summary>A call's arguments as the person's question shows them, each on a line of its own: a value's line breaks, tabs,
/// backslashes and the characters that move, join or hide text (controls, direction marks, zero widths, tags, variation
/// selectors, halves of a pair alone) written as escapes, read a character as Unicode counts them, so no value reads as
/// another argument or shows other than it is, and one longer than <see cref="Longest"/> characters cut there, saying how
/// many it had. Pure.</summary>
public static class AskedArguments
{
    /// <summary>How many characters of a value the question shows.</summary>
    public const int Longest = 400;

    /// <summary>The characters shown as they are without a look at what they are: printable ASCII but the backslash.</summary>
    private static readonly SearchValues<char> Plain = SearchValues.Create(
        [.. Enumerable.Range(' ', '~' - ' ' + 1).Select(c => (char)c).Where(c => c != '\\')]);

    /// <summary>The arguments, one per line, as name: value.</summary>
    public static string Of(IReadOnlyList<(string Name, string Value)> arguments) =>
        string.Join('\n', arguments.Select(a => $"{Escaped(a.Name)}: {Shown(a.Value)}"));

    /// <summary>A value on one line, escaped, its first <see cref="Longest"/> characters where it has more, saying so.</summary>
    public static string Shown(string value)
    {
        if (value.Length <= Longest)
        {
            return Escaped(value);
        }
        // Never cut between a character's two halves.
        int cut = char.IsHighSurrogate(value[Longest - 1]) ? Longest - 1 : Longest;
        return string.Create(CultureInfo.InvariantCulture, $"{Escaped(value[..cut])}… (cut at {cut:N0} of {value.Length:N0} characters)");
    }

    /// <summary>Text with each backslash doubled, so an escape is never the text's own, and each character that breaks a line
    /// or hides as an escape: \n, \r, \t, else \u and its code in four hex digits, \U and eight for one past the Basic
    /// Multilingual Plane, and a half of a pair alone by its own code.</summary>
    public static string Escaped(string text)
    {
        if (!text.AsSpan().ContainsAnyExcept(Plain))
        {
            return text;
        }
        StringBuilder written = new(text.Length + 16);
        ReadOnlySpan<char> rest = text;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out Rune rune, out int read) != OperationStatus.Done)
            {
                written.Append(CultureInfo.InvariantCulture, $"\\u{(int)rest[0]:X4}");
                rest = rest[1..];
                continue;
            }
            switch (rune.Value)
            {
                case '\\':
                    written.Append("\\\\");
                    break;
                case '\n':
                    written.Append("\\n");
                    break;
                case '\r':
                    written.Append("\\r");
                    break;
                case '\t':
                    written.Append("\\t");
                    break;
                default:
                    if (!Hidden(rune))
                    {
                        written.Append(rest[..read]);
                    }
                    else if (rune.IsBmp)
                    {
                        written.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
                    }
                    else
                    {
                        written.Append(CultureInfo.InvariantCulture, $"\\U{rune.Value:X8}");
                    }
                    break;
            }
            rest = rest[read..];
        }
        return written.ToString();
    }

    /// <summary>Whether a character breaks a line, or shows nothing of itself while it changes how text reads or carries what
    /// is not seen: Unicode's controls, formats and separators, and the rest of what it says to draw as nothing
    /// (<see cref="Unseen"/>).</summary>
    private static bool Hidden(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || Unseen(rune.Value);

    /// <summary>Unicode's default ignorable code points that are no format character: the combining grapheme joiner, the
    /// Hangul fillers, Khmer's inherent vowels, Mongolian's and the other variation selectors, and the code points kept for
    /// more, the tag block's whole plane-14 range among them.</summary>
    private static bool Unseen(int code) => code is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F)
        or 0x2065 or 0x3164 or (>= 0xFE00 and <= 0xFE0F) or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8) or (>= 0xE0000 and <= 0xE0FFF);
}

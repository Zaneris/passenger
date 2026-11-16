using System.Globalization;
using System.Text;

namespace Passenger.App.Mcp;

/// <summary>A call's arguments as the person's question shows them, each on a line of its own: a value's line breaks, tabs
/// and the characters that move, join or hide text (controls, direction marks, zero widths) written as escapes, so no value
/// reads as another argument or shows other than it is, and one longer than <see cref="Longest"/> characters cut there,
/// saying how many it had. Pure.</summary>
public static class AskedArguments
{
    /// <summary>How many characters of a value the question shows.</summary>
    public const int Longest = 400;

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

    /// <summary>Text with each character that breaks a line or hides as an escape (\n, \r, \t, else \u and its code).</summary>
    public static string Escaped(string text)
    {
        if (!text.Any(Hidden))
        {
            return text;
        }
        StringBuilder written = new(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
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
                    if (Hidden(c))
                    {
                        written.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    }
                    else
                    {
                        written.Append(c);
                    }
                    break;
            }
        }
        return written.ToString();
    }

    /// <summary>Whether a character breaks a line, or shows nothing of itself while it changes how text reads.</summary>
    private static bool Hidden(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}

using System.Globalization;
using System.Text;

namespace Passenger.Text;

/// <summary>XML's character references, as every reader of a value or an element's text decodes them and every writer of
/// one escapes them: a reference that names no character is left as written, never the whole value with it.</summary>
public static class XmlEntities
{
    private const string CdataStart = "<![CDATA[";
    private const string CdataEnd = "]]>";

    /// <summary>A value as written (between quotes, or an element's text) with its character references read as the
    /// characters they name; any other ampersand kept.</summary>
    public static string Decode(string raw)
    {
        int amp = raw.IndexOf('&');
        if (amp < 0)
        {
            return raw;
        }
        StringBuilder decoded = new(raw.Length);
        decoded.Append(raw, 0, amp);
        Decode(raw, amp, raw.Length, decoded);
        return decoded.ToString();
    }

    /// <summary>An element's text as written, read as XML reads it: each CDATA section's content as it stands, the rest
    /// decoded (<see cref="Decode(string)"/>).</summary>
    public static string DecodeText(string raw)
    {
        int cdata = raw.IndexOf(CdataStart, StringComparison.Ordinal);
        if (cdata < 0)
        {
            return Decode(raw);
        }
        StringBuilder decoded = new(raw.Length);
        int at = 0;
        while (cdata >= 0)
        {
            Decode(raw, at, cdata, decoded);
            int content = cdata + CdataStart.Length;
            int end = raw.IndexOf(CdataEnd, content, StringComparison.Ordinal);
            if (end < 0)
            {
                decoded.Append(raw, content, raw.Length - content);
                return decoded.ToString();
            }
            decoded.Append(raw, content, end - content);
            at = end + CdataEnd.Length;
            cdata = raw.IndexOf(CdataStart, at, StringComparison.Ordinal);
        }
        Decode(raw, at, raw.Length, decoded);
        return decoded.ToString();
    }

    /// <summary>A value as the file writes it: between quotes of a kind (<c>"</c> or <c>'</c>) its <c>&amp;</c>,
    /// <c>&lt;</c> and that quote escaped; as an element's text (any other <paramref name="quote"/>) its <c>&amp;</c>,
    /// <c>&lt;</c> and <c>&gt;</c>.</summary>
    public static string Encode(string value, char quote = '\0')
    {
        string escaped = value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
        return quote switch
        {
            '"' => escaped.Replace("\"", "&quot;", StringComparison.Ordinal),
            '\'' => escaped.Replace("'", "&apos;", StringComparison.Ordinal),
            _ => escaped.Replace(">", "&gt;", StringComparison.Ordinal),
        };
    }

    /// <summary>The characters a character reference at <paramref name="at"/> names (<c>&amp;lt;</c>, <c>&amp;#39;</c>)
    /// and how long it is written; null where none starts there, or one names no character.</summary>
    public static string? ReferenceAt(string text, int at, out int length) => ReferenceAt(text, at, text.Length, out length);

    private static string? ReferenceAt(string raw, int at, int to, out int length)
    {
        // A reference read ends within ten characters of its '&'.
        int semicolon = at < to && raw[at] == '&' ? raw.IndexOf(';', at, Math.Min(to - at, 11)) : -1;
        string? named = semicolon > at ? Entity(raw.AsSpan(at + 1, semicolon - at - 1)) : null;
        length = named is null ? 0 : semicolon + 1 - at;
        return named;
    }

    private static void Decode(string raw, int from, int to, StringBuilder decoded)
    {
        int i = from;
        while (i < to)
        {
            if (ReferenceAt(raw, i, to, out int length) is { } replaced)
            {
                decoded.Append(replaced);
                i += length;
                continue;
            }
            decoded.Append(raw[i]);
            i++;
        }
    }

    private static string? Entity(ReadOnlySpan<char> name)
    {
        switch (name)
        {
            case "lt":
                return "<";
            case "gt":
                return ">";
            case "amp":
                return "&";
            case "quot":
                return "\"";
            case "apos":
                return "'";
        }
        if (name.Length > 1 && name[0] == '#')
        {
            bool hex = name[1] is 'x' or 'X';
            ReadOnlySpan<char> digits = hex ? name[2..] : name[1..];
            // A surrogate's reference (an emoji written as two, &#xD83D;&#xDE00;) is no character XML allows: left as written.
            if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out int code)
                && code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
            {
                return char.ConvertFromUtf32(code);
            }
        }
        return null;
    }
}

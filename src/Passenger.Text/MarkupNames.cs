namespace Passenger.Text;

/// <summary>Markup's names and declarations, read one way by Enter's indentation, tag pairing, folding and XAML's syntax:
/// HTML's void elements, which never close; the characters an XML name starts with and goes on in; and a declaration
/// (a DOCTYPE), whose internal subset's brackets hold any '&gt;' before the one that ends it.</summary>
public static class MarkupNames
{
    private static readonly HashSet<string> Voids = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>The void elements by a name read in place, with no string made for it.</summary>
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> VoidNames = Voids.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Whether an HTML element never closes (br, img, input, ...), its name in any case.</summary>
    public static bool IsVoid(ReadOnlySpan<char> name) => VoidNames.Contains(name);

    /// <summary>Whether a character can start a name, by XML's NameStartChar, a character past the basic plane by either half.</summary>
    public static bool IsNameStart(char c) => c is ':' or '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '\u00C0' and <= '\u00D6') or (>= '\u00D8' and <= '\u00F6')
        or (>= '\u00F8' and <= '\u02FF') or (>= '\u0370' and <= '\u037D') or (>= '\u037F' and <= '\u1FFF') or '\u200C' or '\u200D' or (>= '\u2070' and <= '\u218F')
        or (>= '\u2C00' and <= '\u2FEF') or (>= '\u3001' and <= '\uDB7F') or (>= '\uDC00' and <= '\uDFFF') or (>= '\uF900' and <= '\uFDCF') or (>= '\uFDF0' and <= '\uFFFD');

    /// <summary>Whether a character goes on in a name, by XML's NameChar: a combining mark as much as a letter.</summary>
    public static bool IsNameChar(char c) => IsNameStart(c) || c is '-' or '.' or (>= '0' and <= '9') or '\u00B7' or (>= '\u0300' and <= '\u036F') or '\u203F' or '\u2040';

    /// <summary>Reads one character of a declaration after its "&lt;!", counting its internal subset's brackets: whether it
    /// is the '&gt;' that ends it.</summary>
    public static bool EndsDeclaration(char c, ref int brackets)
    {
        brackets += c == '[' ? 1 : c == ']' ? -1 : 0;
        return c == '>' && brackets <= 0;
    }

    /// <summary>Where the declaration that starts at <paramref name="at"/> (its "&lt;!") ends, after its '&gt;'; -1 when the
    /// text ends first.</summary>
    public static int DeclarationEnd(ReadOnlySpan<char> text, int at)
    {
        int brackets = 0;
        for (int i = at + 2; i < text.Length; i++)
        {
            if (EndsDeclaration(text[i], ref brackets))
            {
                return i + 1;
            }
        }
        return -1;
    }
}

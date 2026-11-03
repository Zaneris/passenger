namespace Passenger.Workspace;

/// <summary>Where each character of a string a Godot text scene or resource writes lies in the file, its escapes read as
/// Godot reads them (<c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\uXXXX</c>, <c>\UXXXXXX</c>): so a place in the string's value (a
/// line and column of a Shader's code) is a place in the file. Pure.</summary>
public static class GodotStringPlaces
{
    /// <summary>The file's offset of each character of the value written from <paramref name="start"/> (just after its
    /// opening quote) to <paramref name="end"/> (its closing quote), and one more for the value's end: an escape's characters
    /// each at its backslash.</summary>
    public static int[] Of(string text, int start, int end)
    {
        List<int> places = new(end - start + 1);
        int at = start;
        while (at < end)
        {
            if (text[at] == '\\' && at + 1 < text.Length)
            {
                char escaped = text[at + 1];
                int digits = escaped == 'u' ? 4 : escaped == 'U' ? 6 : 0;
                if (digits > 0 && at + 1 + digits < text.Length
                    && int.TryParse(text.AsSpan(at + 2, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out int code))
                {
                    // A code point past the first plane is two characters of the value, both at its escape.
                    int characters = digits == 6 && code is > 0xFFFF and <= 0x10FFFF ? 2 : 1;
                    for (int i = 0; i < characters; i++)
                    {
                        places.Add(at);
                    }
                    at += digits + 2;
                    continue;
                }
                places.Add(at);
                at += 2;
                continue;
            }
            places.Add(at);
            at++;
        }
        places.Add(Math.Min(at, end));
        return [.. places];
    }

    /// <summary>The file's range of a range of the value, by <see cref="Of"/>'s places: from its first character's place to
    /// where the character after its last begins.</summary>
    public static (int Start, int End) RangeOf(int[] places, int start, int end)
    {
        int last = places.Length - 1;
        int from = places[Math.Clamp(start, 0, last)];
        int to = places[Math.Clamp(end, 0, last)];
        return (from, Math.Max(from, to));
    }
}

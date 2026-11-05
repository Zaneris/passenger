namespace Passenger.Workspace;

/// <summary>A C# string literal read as its text says it, plain or verbatim, quotes and all: where a place in its text falls in
/// the value it says, and a value written the way it writes one. Pure.</summary>
public static class CSharpLiterals
{
    /// <summary>How many characters of a literal's value lie before an offset in its text.</summary>
    public static int ValueOffset(string literal, int offset)
    {
        bool verbatim = literal.StartsWith('@');
        int at = verbatim ? 2 : 1;
        int value = 0;
        int last = literal.Length - 1;
        while (at < offset && at < last)
        {
            (int written, int said) = verbatim ? (literal[at] == '"' ? 2 : 1, 1) : literal[at] == '\\' ? Escape(literal, at, last) : (1, 1);
            at += written;
            value += said;
        }
        return value;
    }

    /// <summary>Whether a literal's text says a value of that many characters: what a place read earlier in it can be held to.</summary>
    public static bool Says(string literal, string value) =>
        literal.Length >= 2 && literal[^1] == '"' && literal[0] is '"' or '@' && ValueOffset(literal, literal.Length - 1) == value.Length;

    /// <summary>A name as a plain literal writes it among its characters.</summary>
    public static string Escaped(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>A value as a literal of <paramref name="like"/>'s kind writes it, quotes and all.</summary>
    public static string Written(string value, string like) =>
        like.StartsWith('@') ? $"@\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : $"\"{Escaped(value)}\"";

    /// <summary>How many characters of a literal an escape takes up, and how many of its value it says.</summary>
    private static (int Written, int Said) Escape(string literal, int at, int last)
    {
        char kind = at + 1 < last ? literal[at + 1] : '\0';
        if (kind == 'x')
        {
            int digits = 0;
            while (digits < 4 && at + 2 + digits < last && char.IsAsciiHexDigit(literal[at + 2 + digits]))
            {
                digits++;
            }
            return (2 + digits, 1);
        }
        return kind == 'u' ? (6, 1) : kind == 'U' ? (10, literal.AsSpan(at + 2).StartsWith("0000") ? 1 : 2) : (2, 1);
    }
}

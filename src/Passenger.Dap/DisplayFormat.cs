using System.Globalization;
using System.Text;

namespace Passenger.Dap;

/// <summary>What a tracepoint's message and a type's [DebuggerDisplay] are written in, read one way by every debugger: words,
/// and each <c>{expression}</c> read where the program stands, to its own closing brace (braces nested in it and anything
/// in its quotes passed over), its format specifiers after its last comma outside brackets and quotes (<c>{name,nq}</c>),
/// which show its value one way in every debugger (<see cref="Shown"/>); <c>\{</c> and <c>\}</c> are braces, and a brace
/// that never closes is itself.</summary>
public static class DisplayFormat
{
    /// <summary>A format in pieces: its words, and each expression as written between its braces, specifiers and all.</summary>
    public static IReadOnlyList<(string Text, bool Expression)> Pieces(string format)
    {
        List<(string, bool)> pieces = [];
        StringBuilder words = new();
        for (int at = 0; at < format.Length; at++)
        {
            char c = format[at];
            if (c == '\\' && at + 1 < format.Length && format[at + 1] is '{' or '}')
            {
                words.Append(format[++at]);
                continue;
            }
            if (c != '{' || ExpressionEnd(format, at) is not int end)
            {
                words.Append(c);
                continue;
            }
            if (words.Length > 0)
            {
                pieces.Add((words.ToString(), false));
                words.Clear();
            }
            pieces.Add((format[(at + 1)..end], true));
            at = end;
        }
        if (words.Length > 0)
        {
            pieces.Add((words.ToString(), false));
        }
        return pieces;
    }

    /// <summary>Why a tracepoint's message would be misread, or null where it reads as written: a brace written twice
    /// (<c>{{</c>, <c>}}</c>) outside an expression, meant as one brace but read as two, or as an expression in braces.</summary>
    public static string? Misread(string? message)
    {
        for (int at = 0; message is not null && at < message.Length; at++)
        {
            char c = message[at];
            if (c == '\\' && at + 1 < message.Length && message[at + 1] is '{' or '}')
            {
                at++;
            }
            else if (c is '{' or '}' && at + 1 < message.Length && message[at + 1] == c)
            {
                return $"A brace in a log message is written \\{{ or \\}}, not \"{c}{c}\": braces hold an expression read at each hit, "
                    + "as in count is {count}.";
            }
            else if (c == '{' && ExpressionEnd(message, at) is int end)
            {
                at = end;
            }
        }
        return null;
    }

    /// <summary>An expression as written between its braces in its own words and the format specifiers after its last comma
    /// outside brackets and quotes: "Items.Count, d" is the count, in decimal.</summary>
    public static (string Expression, IReadOnlyList<string> Specifiers) Specifiers(string written)
    {
        int depth = 0;
        char? quote = null;
        int comma = -1;
        for (int i = 0; i < written.Length; i++)
        {
            char c = written[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{' or '<':
                    depth++;
                    break;
                case ')' or ']' or '}' or '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    comma = i;
                    break;
            }
        }
        if (comma < 0)
        {
            return (written.Trim(), []);
        }
        string[] specifiers = written[(comma + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Only words are specifiers: "x, 2" is not one.
        return specifiers.All(s => s.All(char.IsAsciiLetterOrDigit) && char.IsAsciiLetter(s[0]))
            ? (written[..comma].Trim(), specifiers)
            : (written.Trim(), []);
    }

    /// <summary>A value an expression read, as its specifiers show it in every debugger: a string bare under <c>nq</c> or
    /// <c>raw</c>, else quoted as its debugger quotes one; a whole number under <c>h</c> in hexadecimal, two digits to each
    /// byte the program holds it in (an int's 14 is 0x0000000e, its -1 0xffffffff), under <c>d</c> in decimal, the last of
    /// the two written winning; anything else, and under any other specifier, as its debugger shows it.</summary>
    public static string Shown(DisplayValue value, IReadOnlyList<string> specifiers)
    {
        if (value.Text is { } text && (specifiers.Contains("nq") || specifiers.Contains("raw")))
        {
            return text;
        }
        if (value.Whole is not { } whole)
        {
            return value.Shown;
        }
        return specifiers.LastOrDefault(s => s is "h" or "d") switch
        {
            "h" => "0x" + ((UInt128)whole & (UInt128.MaxValue >> (128 - (8 * value.Bytes)))).ToString($"x{2 * value.Bytes}", CultureInfo.InvariantCulture),
            "d" => whole.ToString(CultureInfo.InvariantCulture),
            _ => value.Shown,
        };
    }

    /// <summary>Where an expression opened at <paramref name="open"/> closes, past braces nested in it and anything inside
    /// quotes; null for one that never closes.</summary>
    private static int? ExpressionEnd(string format, int open)
    {
        int depth = 0;
        char? quote = null;
        for (int i = open; i < format.Length; i++)
        {
            char c = format[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }
        return null;
    }
}

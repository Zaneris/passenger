namespace Passenger.Text;

/// <summary>The lines a language's reader takes as going on from the line before, whose indentation it reads as no block's. Pure.</summary>
public static class ContinuedLines
{
    /// <summary>The statements Python never writes inside brackets, so a line beginning with one is read as outside them, however
    /// a string read otherwise left them open.</summary>
    private static readonly HashSet<string> StatementsAlone = new(StringComparer.Ordinal)
    {
        "assert", "break", "class", "continue", "def", "del", "elif", "except", "finally", "global", "import", "nonlocal", "pass", "raise",
        "return", "try", "while", "with",
    };

    /// <summary>The lines of a Python file that begin inside brackets opened before them, or follow a line ending in a backslash,
    /// read outside strings and comments: Python's tokenizer reads no indentation there.</summary>
    public static IReadOnlySet<int> Python(TextSnapshot text)
    {
        HashSet<int> lines = [];
        int depth = 0;
        string? quote = null;
        bool joined = false;
        for (int i = 0; i < text.LineCount; i++)
        {
            string line = text.GetLineText(i).TrimEnd('\r');
            int lead = line.Length - line.TrimStart(' ', '\t', '\f').Length;
            if (quote is null && depth > 0 && StatementsAlone.Contains(WordAt(line, lead)))
            {
                depth = 0;
            }
            else if (quote is null && (depth > 0 || joined))
            {
                lines.Add(i);
            }
            joined = false;
            for (int at = 0; at < line.Length; at++)
            {
                char c = line[at];
                if (quote is not null)
                {
                    if (c == '\\')
                    {
                        at++;
                        joined = at >= line.Length;
                    }
                    else if (string.CompareOrdinal(line, at, quote, 0, quote.Length) == 0)
                    {
                        at += quote.Length - 1;
                        quote = null;
                    }
                }
                else if (c == '#')
                {
                    break;
                }
                else if (c is '"' or '\'')
                {
                    quote = at + 2 < line.Length && line[at + 1] == c && line[at + 2] == c ? new string(c, 3) : c.ToString();
                    at += quote.Length - 1;
                }
                else if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    depth = Math.Max(depth - 1, 0);
                }
                else if (c == '\\' && at == line.Length - 1)
                {
                    joined = true;
                }
            }
            // A string of one quote ends with its line, but for a backslash before the line's end.
            if (quote is { Length: 1 } && !joined)
            {
                quote = null;
            }
        }
        return lines;
    }

    private static string WordAt(string line, int at)
    {
        int end = at;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
        {
            end++;
        }
        return line[at..end];
    }
}

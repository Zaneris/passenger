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
        PythonLines reading = new();
        for (int i = 0; i < text.LineCount; i++)
        {
            if (reading.Next(text.GetLineText(i).AsSpan().TrimEnd('\r')) is Begins.Continued)
            {
                lines.Add(i);
            }
        }
        return lines;
    }

    /// <summary>The lines of a Python file whose indentation is no block's, by its lines as <c>\n</c> ends them: those going on
    /// from the line before (<see cref="Python"/>) and those beginning inside a string, a docstring's text among them.</summary>
    public static IReadOnlySet<int> PythonBlockless(ReadOnlySpan<char> text)
    {
        HashSet<int> lines = [];
        PythonLines reading = new();
        for (int i = 0; ; i++)
        {
            int feed = text.IndexOf('\n');
            if (reading.Next((feed < 0 ? text : text[..feed]).TrimEnd('\r')) is not Begins.Code)
            {
                lines.Add(i);
            }
            if (feed < 0)
            {
                return lines;
            }
            text = text[(feed + 1)..];
        }
    }

    private enum Begins
    {
        Code,
        Continued,
        InString,
    }

    /// <summary>A Python file read a line at a time, outside strings and comments: the brackets open, the string open, and
    /// whether the line before ended in a backslash.</summary>
    private struct PythonLines
    {
        private int depth;
        private char quote;
        private int quotes;
        private bool joined;

        /// <summary>Where the next line begins, then reads it.</summary>
        public Begins Next(ReadOnlySpan<char> line)
        {
            int lead = line.Length - line.TrimStart(" \t\f").Length;
            Begins begins = quotes > 0 ? Begins.InString : Begins.Code;
            if (quotes == 0 && depth > 0 && StatementsAlone.GetAlternateLookup<ReadOnlySpan<char>>().Contains(WordAt(line, lead)))
            {
                depth = 0;
            }
            else if (quotes == 0 && (depth > 0 || joined))
            {
                begins = Begins.Continued;
            }
            joined = false;
            for (int at = 0; at < line.Length; at++)
            {
                char c = line[at];
                if (quotes > 0)
                {
                    if (c == '\\')
                    {
                        at++;
                        joined = at >= line.Length;
                    }
                    else if (c == quote && (quotes == 1 || line[at..].StartsWith([quote, quote, quote])))
                    {
                        at += quotes - 1;
                        quotes = 0;
                    }
                }
                else if (c == '#')
                {
                    break;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                    quotes = at + 2 < line.Length && line[at + 1] == c && line[at + 2] == c ? 3 : 1;
                    at += quotes - 1;
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
            if (quotes == 1 && !joined)
            {
                quotes = 0;
            }
            return begins;
        }

        private static ReadOnlySpan<char> WordAt(ReadOnlySpan<char> line, int at)
        {
            int end = at;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            {
                end++;
            }
            return line[at..end];
        }
    }
}

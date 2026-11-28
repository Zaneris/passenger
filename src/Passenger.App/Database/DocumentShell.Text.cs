using System.Globalization;
using System.Text;

namespace Passenger.App.Database;

/// <summary>MongoDB's shell as its words are read without its driver: where its strings and comments stand, its statements,
/// and what each does to its database, which a console's marks and an agent's refusals go by.</summary>
internal static partial class DocumentShell
{
    /// <summary>The calls on a collection that only read.</summary>
    private static readonly HashSet<string> Reading = new(StringComparer.Ordinal)
    {
        "find", "findOne", "aggregate", "countDocuments", "estimatedDocumentCount", "count", "distinct", "getIndexes", "stats", "explain",
        "getCollectionNames", "getName", "listCollections",
    };

    /// <summary>The calls that change a collection's or database's shape rather than its documents.</summary>
    private static readonly HashSet<string> Shaping = new(StringComparer.Ordinal)
    {
        "createIndex", "createIndexes", "dropIndex", "dropIndexes", "drop", "renameCollection", "createCollection", "createView", "dropDatabase",
    };

    /// <summary>The commands db.runCommand runs that only read.</summary>
    private static readonly HashSet<string> ReadingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping", "buildInfo", "serverStatus", "dbStats", "collStats", "listCollections", "listIndexes", "count", "find", "distinct", "hello", "isMaster",
        "connectionStatus", "hostInfo", "listDatabases",
    };

    /// <summary>Where a string or comment starting at <paramref name="at"/> ends, or null where none starts there.</summary>
    public static int? Skipped(string text, int at)
    {
        char c = text[at];
        char next = at + 1 < text.Length ? text[at + 1] : '\0';
        if (c == '/' && next == '/')
        {
            int end = text.IndexOf('\n', at);
            return end < 0 ? text.Length : end;
        }
        if (c == '/' && next == '*')
        {
            int end = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + 2;
        }
        if (c is '"' or '\'' or '`')
        {
            for (int i = at + 1; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == c)
                {
                    return i + 1;
                }
            }
            return text.Length;
        }
        return null;
    }

    /// <summary>Each statement of a console, from its first word to its semicolon, or to the end of its line where its brackets
    /// are closed and the next line does not go on with a chained call.</summary>
    public static List<(int Start, int End)> Statements(string text)
    {
        List<(int Start, int End)> statements = [];
        int start = -1;
        int last = -1;
        int depth = 0;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (Skipped(text, i) is { } past)
            {
                bool comment = c == '/';
                if (!comment)
                {
                    start = start < 0 ? i : start;
                    last = past;
                }
                i = past;
                continue;
            }
            if (c == ';' && depth == 0)
            {
                Close();
                i++;
                continue;
            }
            if (c == '\n' && depth == 0 && start >= 0 && !GoesOn(text, i + 1))
            {
                Close();
                i++;
                continue;
            }
            if (!char.IsWhiteSpace(c))
            {
                start = start < 0 ? i : start;
                last = i + 1;
                depth += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
                depth = Math.Max(0, depth);
            }
            i++;
        }
        Close();
        return statements;

        void Close()
        {
            if (start >= 0 && last > start)
            {
                statements.Add((start, last));
            }
            start = -1;
            last = -1;
            depth = 0;
        }
    }

    /// <summary>Whether the text after a line break goes on with the statement before: its first word a chained call (.sort).</summary>
    private static bool GoesOn(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }
            if (Skipped(text, i) is { } past && text[i] == '/')
            {
                i = past - 1;
                continue;
            }
            return text[i] == '.';
        }
        return false;
    }

    /// <summary>What a statement does to its database, read from its words: a call that only reads reads, an aggregation writing
    /// out ($out, $merge) and every call that writes documents changes data, one that makes or drops a collection, index or
    /// database changes the schema, db.runCommand reads only for a command known to; a call not known taken as changing data.</summary>
    public static SqlEffect EffectOf(string run)
    {
        string text = run.Trim();
        if (text.StartsWith("show ", StringComparison.Ordinal))
        {
            return SqlEffect.Reads;
        }
        (string? method, int open) = CalledAt(text, 0);
        if (method == "getCollection" && open >= 0 && Closing(text, open) is var past and >= 0)
        {
            (method, open) = CalledAt(text, past + 1);
        }
        return method switch
        {
            null => SqlEffect.ChangesData,
            "aggregate" when WritesOut(run) => SqlEffect.ChangesData,
            "runCommand" => FirstKey(text, open + 1) is { } command && ReadingCommands.Contains(command) ? SqlEffect.Reads : SqlEffect.ChangesSchema,
            _ when Reading.Contains(method) => SqlEffect.Reads,
            _ when Shaping.Contains(method) => SqlEffect.ChangesSchema,
            _ => SqlEffect.ChangesData,
        };
    }

    /// <summary>Whether an aggregation writes out: a name or string of it, its escapes read as the shell's reader decodes
    /// them ("\u0024out" is $out), is $out or $merge.</summary>
    private static bool WritesOut(string run)
    {
        for (int i = 0; i < run.Length;)
        {
            if (Skipped(run, i) is { } past)
            {
                if (run[i] != '/' && Decoded(run[(i + 1)..Math.Max(i + 1, past - 1)]) is "$out" or "$merge")
                {
                    return true;
                }
                i = past;
                continue;
            }
            int end = i;
            while (end < run.Length && (char.IsLetterOrDigit(run[end]) || run[end] is '_' or '$'))
            {
                end++;
            }
            if (end > i && run[i..end] is "$out" or "$merge")
            {
                return true;
            }
            i = Math.Max(end, i + 1);
        }
        return false;
    }

    /// <summary>A quoted string's text with its escapes read: \uXXXX, \u{…} and \xXX by their code, any other the
    /// character after the backslash.</summary>
    private static string Decoded(string quoted)
    {
        if (!quoted.Contains('\\', StringComparison.Ordinal))
        {
            return quoted;
        }
        StringBuilder text = new(quoted.Length);
        for (int i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] != '\\' || i + 1 >= quoted.Length)
            {
                text.Append(quoted[i]);
                continue;
            }
            char next = quoted[++i];
            int close = next == 'u' && i + 1 < quoted.Length && quoted[i + 1] == '{' ? quoted.IndexOf('}', i) : -1;
            (int from, int length) = close > 0 ? (i + 2, close - i - 2) : next == 'u' ? (i + 1, 4) : next == 'x' ? (i + 1, 2) : (0, 0);
            if (length > 0 && from + length <= quoted.Length
                && int.TryParse(quoted.AsSpan(from, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code) && code is >= 0 and <= 0x10FFFF)
            {
                text.Append(char.ConvertFromUtf32(code is >= 0xD800 and <= 0xDFFF ? 0xFFFD : code));
                i = close > 0 ? close : from + length - 1;
                continue;
            }
            text.Append(next);
        }
        return text.ToString();
    }

    /// <summary>The name called first from a place on, outside strings and comments, and where its parenthesis stands.</summary>
    private static (string? Method, int Open) CalledAt(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (Skipped(text, i) is { } past)
            {
                i = past - 1;
                continue;
            }
            if (text[i] == '(')
            {
                int end = i;
                while (end > from && char.IsWhiteSpace(text[end - 1]))
                {
                    end--;
                }
                int start = end;
                while (start > from && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$'))
                {
                    start--;
                }
                return (start < end ? text[start..end] : null, i);
            }
        }
        return (null, -1);
    }

    /// <summary>Where the parenthesis opened at <paramref name="open"/> closes, outside strings and comments; -1 where it never does.</summary>
    private static int Closing(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (Skipped(text, i) is { } past)
            {
                i = past - 1;
                continue;
            }
            depth += text[i] is '(' or '[' or '{' ? 1 : text[i] is ')' or ']' or '}' ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A command document's first key, bare or quoted, from a place on: runCommand({ ping: 1 })'s ping.</summary>
    private static string? FirstKey(string text, int from)
    {
        int i = from;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '{'))
        {
            i++;
        }
        if (i < text.Length && text[i] is '"' or '\'' && Skipped(text, i) is { } past)
        {
            return text[(i + 1)..(past - 1)];
        }
        int start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
        {
            i++;
        }
        return i > start ? text[start..i] : null;
    }
}

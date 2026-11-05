namespace Passenger.Workspace;

/// <summary>A word or string of a GDScript's code: what it says, where its characters lie, whether they are written as it
/// says (no escape among them), and for a string given straight to a call, the function's name and which argument it is
/// (from zero); for a word, whether a '.' comes before it, so it names a member of what is before.</summary>
public sealed record GodotScriptToken(bool Word, string Text, int Start, int End, bool Plain, string? Call, int Argument, bool AfterDot)
{
    /// <summary>What a string's call is made on: null for no receiver, the word before the call's '.', or empty for anything
    /// else (a call's result, a chain of members).</summary>
    public string? Receiver { get; init; }

    /// <summary>Whether a string is the whole of an argument, joined to nothing (<c>"a" + "b"</c> is neither's), and no parameter's
    /// default; a branch of an <c>if</c> expression is whole, as the argument is that string when it is taken.</summary>
    public bool Whole { get; init; }
}

/// <summary>A GDScript's words and strings as Godot's tokenizer reads them: its comments passed over, a string's quotes
/// (single, double or tripled), its <c>r</c>, <c>&amp;</c> and <c>^</c> prefixes and its escapes read, and each string
/// written straight in a call's parentheses told by the call's name, what it is made on and its place among the arguments,
/// so what a script gives Godot's calls by name (<c>Input.is_action_pressed("jump")</c>) is read without its types. Pure.</summary>
public static class GodotScriptStrings
{
    public static IReadOnlyList<GodotScriptToken> Read(string text)
    {
        List<GodotScriptToken> tokens = [];
        // Each open bracket: the call it opens (null for an array, a dictionary or a group), the argument it is at and what the call is made on.
        List<(string? Call, int Argument, string? Receiver)> open = [];
        string? lastWord = null;
        string? lastReceiver = null;
        string dotted = string.Empty;
        bool lastDotted = false;
        bool wordLast = false;
        char before = '\0';
        int at = 0;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '#')
            {
                at = text.IndexOf('\n', at) is var end and >= 0 ? end : text.Length;
                continue;
            }
            if (c is '"' or '\'' || c is 'r' or '&' or '^' && at + 1 < text.Length && text[at + 1] is '"' or '\'' && !(c == 'r' && at > 0 && IsWordChar(text[at - 1])))
            {
                bool raw = c == 'r';
                int quote = c is '"' or '\'' ? at : at + 1;
                (string said, int start, int stop, int next) = Quoted(text, quote, raw);
                (string? call, int argument, string? receiver) = open.Count > 0 ? open[^1] : (null, 0, null);
                bool opens = before is '(' or ',' || wordLast && lastWord == "else";
                tokens.Add(new GodotScriptToken(false, said, start, stop, stop - start == said.Length, call, argument, false)
                {
                    Receiver = receiver,
                    Whole = opens && Closes(text, next),
                });
                at = next;
                wordLast = false;
                before = '"';
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = at;
                while (at < text.Length && IsWordChar(text[at]))
                {
                    at++;
                }
                string word = text[start..at];
                lastDotted = before == '.';
                tokens.Add(new GodotScriptToken(true, word, start, at, true, null, 0, lastDotted));
                lastReceiver = lastDotted ? dotted : null;
                lastWord = word;
                wordLast = true;
                before = 'a';
                continue;
            }
            if (char.IsDigit(c))
            {
                while (at < text.Length && (IsWordChar(text[at]) || text[at] == '.'))
                {
                    at++;
                }
                wordLast = false;
                before = '0';
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                bool called = c == '(' && wordLast;
                open.Add((called ? lastWord : null, 0, called ? lastReceiver : null));
            }
            else if (c is ')' or ']' or '}' && open.Count > 0)
            {
                open.RemoveAt(open.Count - 1);
            }
            else if (c == ',' && open.Count > 0)
            {
                open[^1] = open[^1] with { Argument = open[^1].Argument + 1 };
            }
            else if (c == '.')
            {
                dotted = wordLast && !lastDotted ? lastWord! : string.Empty;
            }
            if (!char.IsWhiteSpace(c))
            {
                wordLast = false;
                before = c;
            }
            at++;
        }
        return tokens;
    }

    /// <summary>The strings a script gives straight to a call by one of some names, each the whole of the argument that takes the name.</summary>
    public static IEnumerable<GodotScriptToken> GivenTo(IReadOnlyList<GodotScriptToken> tokens, IReadOnlyDictionary<string, int[]> calls) =>
        tokens.Where(t => !t.Word && t.Whole && t.Call is { } call && calls.TryGetValue(call, out int[]? arguments) && arguments.Contains(t.Argument));

    /// <summary>What a string of a quote writes for some characters so that it says them, its escapes as Godot's tokenizer reads
    /// them; null for a raw string, whose only escape keeps its backslash, holding its own quote.</summary>
    public static string? Written(string said, char quote, bool raw) => raw ? said.Contains(quote) ? null : said
        : said.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(quote.ToString(), $"\\{quote}", StringComparison.Ordinal);

    /// <summary>Whether what comes after a string, past blanks and comments, ends its argument: a ',', a ')', or an <c>if</c>.</summary>
    private static bool Closes(string text, int at)
    {
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '#')
            {
                at = text.IndexOf('\n', at) is var end and >= 0 ? end : text.Length;
            }
            else if (char.IsWhiteSpace(c) || c == '\\')
            {
                at++;
            }
            else
            {
                return c is ')' or ',' || string.CompareOrdinal(text, at, "if", 0, 2) == 0 && (at + 2 >= text.Length || !IsWordChar(text[at + 2]));
            }
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>A string from its opening quote: what it says, where its characters start and end, and where the code goes on.</summary>
    private static (string Said, int Start, int End, int Next) Quoted(string text, int quote, bool raw)
    {
        char mark = text[quote];
        bool tripled = quote + 2 < text.Length && text[quote + 1] == mark && text[quote + 2] == mark;
        int start = quote + (tripled ? 3 : 1);
        System.Text.StringBuilder said = new();
        int at = start;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '\\' && at + 1 < text.Length)
            {
                // A raw string keeps its backslash, though one still keeps its quote from closing it.
                char escaped = text[at + 1];
                said.Append(raw ? text.AsSpan(at, 2) : Escaped(escaped).AsSpan());
                at += 2;
                continue;
            }
            if (c == mark && (!tripled || at + 2 < text.Length && text[at + 1] == mark && text[at + 2] == mark))
            {
                return (said.ToString(), start, at, at + (tripled ? 3 : 1));
            }
            if (c == '\n' && !tripled)
            {
                return (said.ToString(), start, at, at);
            }
            said.Append(c);
            at++;
        }
        return (said.ToString(), start, at, at);
    }

    private static string Escaped(char c) => c switch
    {
        'n' => "\n",
        't' => "\t",
        'r' => "\r",
        '0' => "\0",
        _ => c.ToString(),
    };
}

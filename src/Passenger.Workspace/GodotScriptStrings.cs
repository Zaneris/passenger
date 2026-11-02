namespace Passenger.Workspace;

/// <summary>A word or string of a GDScript's code: what it says, where its characters lie, whether they are written as it
/// says (no escape among them), and for a string given straight to a call, the function's name and which argument it is
/// (from zero); for a word, whether a '.' comes before it, so it names a member of what is before.</summary>
public sealed record GodotScriptToken(bool Word, string Text, int Start, int End, bool Plain, string? Call, int Argument, bool AfterDot);

/// <summary>A GDScript's words and strings as Godot's tokenizer reads them: its comments passed over, a string's quotes
/// (single, double or tripled), its <c>r</c>, <c>&amp;</c> and <c>^</c> prefixes and its escapes read, and each string
/// written straight in a call's parentheses told by the call's name and its place among the arguments, so what a script
/// gives Godot's calls by name (<c>Input.is_action_pressed("jump")</c>) is read without its types. Pure.</summary>
public static class GodotScriptStrings
{
    public static IReadOnlyList<GodotScriptToken> Read(string text)
    {
        List<GodotScriptToken> tokens = [];
        // Each open bracket: the call it opens (null for an array, a dictionary or a group) and the argument it is at.
        List<(string? Call, int Argument)> open = [];
        string? lastWord = null;
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
                (string? call, int argument) = open.Count > 0 ? open[^1] : (null, 0);
                tokens.Add(new GodotScriptToken(false, said, start, stop, stop - start == said.Length, call, argument, false));
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
                tokens.Add(new GodotScriptToken(true, word, start, at, true, null, 0, before == '.'));
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
                open.Add((c == '(' && wordLast ? lastWord : null, 0));
            }
            else if (c is ')' or ']' or '}' && open.Count > 0)
            {
                open.RemoveAt(open.Count - 1);
            }
            else if (c == ',' && open.Count > 0)
            {
                open[^1] = (open[^1].Call, open[^1].Argument + 1);
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

    /// <summary>The strings a script gives straight to a call by one of some names, at the argument each takes the name in.</summary>
    public static IEnumerable<GodotScriptToken> GivenTo(IReadOnlyList<GodotScriptToken> tokens, IReadOnlyDictionary<string, int[]> calls) =>
        tokens.Where(t => !t.Word && t.Call is { } call && calls.TryGetValue(call, out int[]? arguments) && arguments.Contains(t.Argument));

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

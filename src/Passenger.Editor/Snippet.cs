using System.Text;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A template's variable as it goes in: its value there, and whether Tab stops at it to be typed over (one not
/// editable is filled in and passed over).</summary>
public readonly record struct SnippetVariable(string Name, string Value, bool Stops = true);

/// <summary>A live template made ready to insert: its text, each variable's places in it in the order Tab visits
/// them, and where the caret ends up (or what is selected at the end, as try...catch's selects its catch's body).</summary>
public sealed record Snippet(string Text, IReadOnlyList<IReadOnlyList<TextRange>> Fields, TextRange End)
{
    /// <summary>The live template syntax: <c>$NAME$</c> a variable (its places one field, each filled with its value),
    /// <c>$END$</c> where the caret goes after the last, <c>$SELSTART$</c> and <c>$SELEND$</c> what is selected there
    /// instead, <c>$SELECTION$</c> the text the template wraps, <c>$$</c> a dollar. Tab visits the variables in the
    /// order given, then the rest in the order they appear. Lines after the first take the indentation of the line it
    /// goes on, a tab its unit, as do the wrapped text's after its first but those <paramref name="asWritten"/> counts.</summary>
    public static Snippet Parse(string template, IReadOnlyList<SnippetVariable> variables, string indent, string unit, string selection = "",
        IReadOnlySet<int>? asWritten = null)
    {
        StringBuilder text = new();
        Dictionary<string, List<TextRange>> places = new(StringComparer.Ordinal);
        Dictionary<string, SnippetVariable> given = [];
        foreach (SnippetVariable variable in variables)
        {
            given.TryAdd(variable.Name, variable);
        }
        List<string> order = [.. given.Keys];
        int end = -1;
        int selectedFrom = -1;
        int selectedTo = -1;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            int close = c == '$' ? template.IndexOf('$', i + 1) : -1;
            if (close == i + 1)
            {
                text.Append('$');
                i = close;
            }
            else if (close > i)
            {
                string name = template[(i + 1)..close];
                switch (name)
                {
                    case "END":
                        end = text.Length;
                        break;
                    case "SELSTART":
                        selectedFrom = text.Length;
                        break;
                    case "SELEND":
                        selectedTo = text.Length;
                        break;
                    case "SELECTION":
                        AppendWrapped(text, selection, indent, asWritten);
                        break;
                    default:
                        string value = given.TryGetValue(name, out SnippetVariable variable) ? variable.Value : string.Empty;
                        if (!places.TryGetValue(name, out List<TextRange>? list))
                        {
                            places[name] = list = [];
                            if (!given.ContainsKey(name))
                            {
                                order.Add(name);
                            }
                        }
                        list.Add(new TextRange(text.Length, value.Length));
                        text.Append(value);
                        break;
                }
                i = close;
            }
            else if (c == '\n')
            {
                text.Append('\n').Append(indent);
            }
            else if (c == '\t')
            {
                text.Append(unit);
            }
            else
            {
                text.Append(c);
            }
        }
        TextRange last = selectedFrom >= 0 && selectedTo >= selectedFrom ? TextRange.FromBounds(selectedFrom, selectedTo)
            : new TextRange(end < 0 ? text.Length : end, 0);
        IEnumerable<string> stops = order.Where(n => places.ContainsKey(n) && (!given.TryGetValue(n, out SnippetVariable v) || v.Stops));
        return new Snippet(text.ToString(), [.. stops.Select(n => (IReadOnlyList<TextRange>)places[n])], last);
    }

    /// <summary>The language server protocol's snippet syntax, as a completion's text gives it: <c>$1</c> and
    /// <c>${1:placeholder}</c> each a stop (a number's places one field, Tab visiting them by number), <c>${1|a,b|}</c> its
    /// first choice, <c>$0</c> where the caret ends, a variable its default or nothing, and a backslash escaping
    /// <c>$</c>, <c>}</c>, <c>\</c>, <c>,</c> and <c>|</c>.</summary>
    public static Snippet FromProtocol(string snippet)
    {
        StringBuilder text = new();
        SortedDictionary<int, List<TextRange>> stops = [];
        int at = 0;
        Read(null);
        TextRange end = stops.Remove(0, out List<TextRange>? final) ? final[0] : new TextRange(text.Length, 0);
        return new Snippet(text.ToString(), [.. stops.Values], end);

        // Reads to the end, or to the '}' closing a placeholder, which it passes.
        void Read(char? until)
        {
            while (at < snippet.Length)
            {
                char c = snippet[at];
                if (c == '\\' && at + 1 < snippet.Length && snippet[at + 1] is '$' or '}' or '\\' or ',' or '|')
                {
                    text.Append(snippet[at + 1]);
                    at += 2;
                }
                else if (until is { } close && c == close)
                {
                    at++;
                    return;
                }
                else if (c == '$' && at + 1 < snippet.Length && (char.IsAsciiDigit(snippet[at + 1]) || snippet[at + 1] == '{' || IsName(snippet[at + 1])))
                {
                    at++;
                    Placeholder();
                }
                else
                {
                    text.Append(c);
                    at++;
                }
            }
        }

        // After a '$': a number or a name, bare or in braces with what follows it.
        void Placeholder()
        {
            bool braced = snippet[at] == '{';
            if (braced)
            {
                at++;
            }
            int from = at;
            while (at < snippet.Length && (char.IsAsciiDigit(snippet[at]) || IsName(snippet[at])))
            {
                at++;
            }
            string name = snippet[from..at];
            int start = text.Length;
            if (braced && at < snippet.Length)
            {
                if (snippet[at] == ':')
                {
                    at++;
                    Read('}');
                }
                else if (snippet[at] == '|')
                {
                    int choices = snippet.IndexOf("|}", at + 1, StringComparison.Ordinal);
                    string first = snippet[(at + 1)..(choices < 0 ? snippet.Length : choices)].Split(',')[0];
                    text.Append(first);
                    at = choices < 0 ? snippet.Length : choices + 2;
                }
                else if (snippet[at] == '}')
                {
                    at++;
                }
            }
            if (int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
            {
                if (!stops.TryGetValue(number, out List<TextRange>? places))
                {
                    stops[number] = places = [];
                }
                places.Add(TextRange.FromBounds(start, text.Length));
            }
        }

        static bool IsName(char c) => char.IsAsciiLetter(c) || c == '_';
    }

    /// <summary>The wrapped text, its lines after the first indented as the template's line it starts on, which on the
    /// template's first line is the indentation of the line it goes on; an empty line, and one counted as written (by its
    /// place among the wrapped text's lines), stays as it is.</summary>
    private static void AppendWrapped(StringBuilder text, string selection, string indent, IReadOnlySet<int>? asWritten)
    {
        int lineStart = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '\n')
            {
                lineStart = i + 1;
                break;
            }
        }
        int leading = lineStart;
        while (leading < text.Length && text[leading] is ' ' or '\t')
        {
            leading++;
        }
        string lineIndent = (lineStart == 0 ? indent : string.Empty) + text.ToString(lineStart, leading - lineStart);
        string[] lines = selection.Split('\n');
        text.Append(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            text.Append('\n');
            if (lines[i].Length > 0 && asWritten?.Contains(i) != true)
            {
                text.Append(lineIndent);
            }
            text.Append(lines[i]);
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Workspace.GodotShader;

/// <summary>The statements and declarations of the code Godot's compiler is given, found by brackets and semicolons
/// alone, so a compile that stopped at an error can go on past the statement it was in: each a span of the code
/// (a compound one, an <c>if</c> with its <c>else</c> or a function, whole, beside the ones in its braces; a <c>case</c>'s
/// label apart from what it holds), never across an include's marker line. Pure.</summary>
internal static partial class GodotShaderStatements
{
    internal readonly record struct Span(int Start, int End);

    /// <summary>The characters of a condition's last line <see cref="WithoutCondition"/> writes in, the longest word it leaves.</summary>
    private const int ConditionRoom = 4;

    /// <summary>The code with room for <see cref="WithoutCondition"/> in every condition: spaces before its ')' where its last
    /// line is too short, which Godot's compiler reads past, so a change of a condition after it moves no offset of this
    /// code's and no line of any.</summary>
    public static string WithRoomInConditions(string code)
    {
        StringBuilder roomy = new(code.Length);
        Stack<int> opens = new();
        int copied = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (IsMarkerLine(code, i))
            {
                int next = code.IndexOf('\n', i);
                i = next < 0 ? code.Length : next;
            }
            else if (c == '"')
            {
                int close = code.IndexOf('"', i + 1);
                i = close < 0 ? code.Length : close;
            }
            else if (c == '(')
            {
                opens.Push(i);
            }
            else if (c == ')' && opens.TryPop(out int open) && ConditionWord(code, 0, open) is not null && LastLineRoom(code, open, i) is var room and < ConditionRoom)
            {
                roomy.Append(code, copied, i - copied).Append(' ', ConditionRoom - room);
                copied = i;
            }
        }
        return roomy.Append(code, copied, code.Length - copied).ToString();
    }

    /// <summary>Every statement's span, in the order each ends.</summary>
    public static List<Span> Of(string code)
    {
        List<Span> spans = [];
        Stack<(int Owner, int Open)> braces = new();
        int start = -1;
        int parens = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (IsMarkerLine(code, i))
            {
                Close(i);
                int next = code.IndexOf('\n', i);
                i = next < 0 ? code.Length : next;
                continue;
            }
            if (c == '"')
            {
                start = start < 0 ? i : start;
                int close = code.IndexOf('"', i + 1);
                i = close < 0 ? code.Length : close;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            start = start < 0 ? i : start;
            switch (c)
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    parens = Math.Max(0, parens - 1);
                    break;
                case ';' when parens == 0:
                case ':' when parens == 0 && (StartsWord(code, start, "case") || StartsWord(code, start, "default")):
                    spans.Add(new Span(start, i + 1));
                    start = -1;
                    break;
                case '{' when parens == 0:
                    braces.Push((start < 0 ? i : start, i));
                    start = -1;
                    break;
                case '}':
                    Close(i);
                    if (braces.Count == 0)
                    {
                        break;
                    }
                    (int owner, _) = braces.Pop();
                    if (GoesOn(code, owner, i + 1))
                    {
                        start = owner;
                    }
                    else
                    {
                        spans.Add(new Span(owner, i + 1));
                    }
                    parens = 0;
                    break;
                default:
                    break;
            }
        }
        Close(code.Length);
        return spans;

        void Close(int at)
        {
            if (start >= 0 && start < at)
            {
                spans.Add(new Span(start, at));
            }
            start = -1;
        }
    }

    /// <summary>The span to take out for an error whose token begins at <paramref name="offset"/>: the smallest holding it,
    /// or ending just before the bracket or end it was read at (a statement left unended).</summary>
    public static Span? At(IReadOnlyList<Span> spans, string code, int offset)
    {
        bool endRead = offset >= code.Length || code[offset] == '}';
        Span? best = null;
        foreach (Span span in spans)
        {
            bool holds = span.Start <= offset && offset < span.End || endRead && span.End == offset && span.Start < offset;
            if (holds && (best is not { } known || span.End - span.Start < known.End - known.Start))
            {
                best = span;
            }
        }
        if (best is null)
        {
            foreach (Span span in spans)
            {
                if (span.Start >= offset)
                {
                    return span;
                }
            }
        }
        return best;
    }

    /// <summary>The smallest span holding another whole, larger than it; null for none.</summary>
    public static Span? Around(IReadOnlyList<Span> spans, Span inner)
    {
        Span? best = null;
        foreach (Span span in spans)
        {
            bool holds = span.Start <= inner.Start && inner.End <= span.End && span != inner;
            if (holds && (best is not { } known || span.End - span.Start < known.End - known.Start))
            {
                best = span;
            }
        }
        return best;
    }

    /// <summary>The code with a span's text but its line breaks made spaces, so every line keeps its place.</summary>
    public static string Blanked(string code, Span span) =>
        string.Concat(code.AsSpan(0, span.Start), new string([.. code[span.Start..span.End].Select(c => c == '\n' ? '\n' : ' ')]), code.AsSpan(span.End));

    /// <summary>A local variable's declaration with its initializer taken out (<c>float a = …;</c> as <c>float a;</c>), so
    /// the variable stays declared; null where the span is no such declaration.</summary>
    public static string? WithoutInitializer(string code, Span span)
    {
        string text = code[span.Start..span.End];
        Match declared = Declaration().Match(text);
        if (!declared.Success || !text.EndsWith(';') || NoType.Contains(declared.Groups["type"].Value))
        {
            return null;
        }
        int equals = span.Start + declared.Length - 1;
        return Blanked(code, new Span(equals, span.End - 1));
    }

    /// <summary>The code with the condition of the <c>if</c>, <c>while</c> or <c>switch</c> holding an offset made one that
    /// reads (<c>true</c>, a switch's <c>0</c>), written at the end of its last line, so what the statement holds is still
    /// read and every other character keeps its place, with whether it was a switch's; null where no condition holds it, or
    /// none has its room (<see cref="WithRoomInConditions"/>).</summary>
    public static (string Code, bool OfSwitch)? WithoutCondition(string code, Span span, int offset)
    {
        for (int open = Opening(code, span.Start, offset); open > span.Start; open = Opening(code, span.Start, open))
        {
            if (ConditionWord(code, span.Start, open) is not { } keyword)
            {
                continue;
            }
            int close = Closing(code, open);
            if (close < 0 || close > span.End)
            {
                return null;
            }
            string kept = keyword == "switch" ? "0" : "true";
            if (LastLineRoom(code, open, close) < kept.Length)
            {
                return null;
            }
            string blank = Blanked(code, new Span(open + 1, close));
            return (string.Concat(blank.AsSpan(0, close - kept.Length), kept, blank.AsSpan(close)), keyword == "switch");
        }
        return null;
    }

    /// <summary>The word of the <c>if</c>, <c>while</c> or <c>switch</c> whose condition a '(' opens, back to a start; null
    /// for any other bracket.</summary>
    private static string? ConditionWord(string code, int start, int open)
    {
        int end = open;
        while (end > start && char.IsWhiteSpace(code[end - 1]))
        {
            end--;
        }
        int begin = end;
        while (begin > start && IsWordChar(code[begin - 1]))
        {
            begin--;
        }
        string word = code[begin..end];
        return word is "if" or "while" or "switch" ? word : null;
    }

    /// <summary>The characters between brackets on the line of their ')'.</summary>
    private static int LastLineRoom(string code, int open, int close) => close - Math.Max(open, code.LastIndexOf('\n', close - 1, close - open - 1)) - 1;

    /// <summary>Whether an include's marker line starts at an offset.</summary>
    private static bool IsMarkerLine(string code, int at) =>
        (at == 0 || code[at - 1] == '\n') && (code.AsSpan(at).StartsWith("@@>") || code.AsSpan(at).StartsWith("@@<"));

    /// <summary>The unmatched '(' before an offset, back to a start; -1 for none.</summary>
    private static int Opening(string code, int start, int offset)
    {
        int depth = 0;
        for (int at = offset - 1; at >= start; at--)
        {
            if (code[at] == ')')
            {
                depth++;
            }
            else if (code[at] == '(' && depth-- == 0)
            {
                return at;
            }
        }
        return -1;
    }

    /// <summary>The ')' matching a '('; -1 for none.</summary>
    private static int Closing(string code, int open)
    {
        int depth = 0;
        for (int at = open; at < code.Length; at++)
        {
            if (code[at] == '(')
            {
                depth++;
            }
            else if (code[at] == ')' && --depth == 0)
            {
                return at;
            }
        }
        return -1;
    }

    /// <summary>The names a span's text declares, as far as reading it alone tells: a struct's, a variable's, a function's
    /// or a parameter's after its type, and each after a comma at its top.</summary>
    public static IEnumerable<string> NamesDeclared(string text)
    {
        foreach (Match match in Struct().Matches(text))
        {
            yield return match.Groups[1].Value;
        }
        foreach (Match match in Typed().Matches(text))
        {
            if (!NoType.Contains(match.Groups["type"].Value))
            {
                yield return match.Groups["name"].Value;
            }
        }
        foreach (Match match in AfterComma().Matches(text))
        {
            yield return match.Groups[1].Value;
        }
    }

    /// <summary>Whether a compound statement closed before <paramref name="after"/> goes on: an <c>else</c>, a
    /// <c>do</c>'s <c>while</c>, or the <c>;</c>, <c>,</c> or <c>)</c> after a struct's or an array's braces.</summary>
    private static bool GoesOn(string code, int owner, int after)
    {
        int at = after;
        while (at < code.Length && char.IsWhiteSpace(code[at]))
        {
            at++;
        }
        if (at >= code.Length)
        {
            return false;
        }
        if (code[at] is ';' or ',' or ')')
        {
            return true;
        }
        return StartsWord(code, at, "else") || code.AsSpan(at).StartsWith("while") && StartsWord(code, owner, "do");
    }

    /// <summary>Whether a word stands at an offset, whole.</summary>
    private static bool StartsWord(string code, int at, string word) =>
        code.AsSpan(at).StartsWith(word) && (at + word.Length >= code.Length || !IsWordChar(code[at + word.Length]));

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Words a declaration's pattern reads in a type's place that are none.</summary>
    private static readonly HashSet<string> NoType = new(StringComparer.Ordinal)
    {
        "return", "const", "uniform", "varying", "instance", "global", "if", "else", "while", "for", "switch", "case", "do",
        "discard", "break", "continue", "in", "out", "inout", "flat", "smooth", "group_uniforms", "render_mode", "stencil_mode",
        "shader_type", "struct",
    };

    [GeneratedRegex(@"^\s*(?:(?:lowp|mediump|highp)\s+)?(?<type>[A-Za-z_]\w*)\s+[A-Za-z_]\w*\s*(?:\[[^\]]*\])?\s*=")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\bstruct\s+([A-Za-z_]\w*)")]
    private static partial Regex Struct();

    [GeneratedRegex(@"\b(?<type>[A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s+(?<name>[A-Za-z_]\w*)\s*(?=[=;,\[(:)])")]
    private static partial Regex Typed();

    [GeneratedRegex(@",\s*([A-Za-z_]\w*)\s*(?=[=;,\[])")]
    private static partial Regex AfterComma();
}

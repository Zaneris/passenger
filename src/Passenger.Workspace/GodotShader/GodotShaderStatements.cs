using System.Text.RegularExpressions;

namespace Passenger.Workspace.GodotShader;

/// <summary>The statements and declarations of the code Godot's compiler is given, found by brackets and semicolons
/// alone, so a compile that stopped at an error can go on past the statement it was in: each a span of the code
/// (a compound one, an <c>if</c> with its <c>else</c> or a function, whole, beside the ones in its braces), never
/// across an include's marker line. Pure.</summary>
internal static partial class GodotShaderStatements
{
    internal readonly record struct Span(int Start, int End);

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
            if ((i == 0 || code[i - 1] == '\n') && (code.AsSpan(i).StartsWith("@@>") || code.AsSpan(i).StartsWith("@@<")))
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
    /// reads (<c>true</c>, a switch's <c>0</c>), so what the statement holds is still read; null where no condition holds it.</summary>
    public static string? WithoutCondition(string code, Span span, int offset)
    {
        for (int open = Opening(code, span.Start, offset); open > span.Start; open = Opening(code, span.Start, open))
        {
            int word = open;
            while (word > span.Start && char.IsWhiteSpace(code[word - 1]))
            {
                word--;
            }
            int begin = word;
            while (begin > span.Start && IsWordChar(code[begin - 1]))
            {
                begin--;
            }
            string keyword = code[begin..word];
            if (keyword is not ("if" or "while" or "switch"))
            {
                continue;
            }
            int close = Closing(code, open);
            if (close < 0 || close > span.End)
            {
                return null;
            }
            string kept = keyword == "switch" ? "0" : "true";
            Span inside = new(open + 1, close);
            string blank = Blanked(code, inside);
            return string.Concat(blank.AsSpan(0, open + 1), kept, blank.AsSpan(open + 1 + kept.Length));
        }
        return null;
    }

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
        ReadOnlySpan<char> rest = code.AsSpan(at);
        if (rest.StartsWith("else") && (rest.Length == 4 || !IsWordChar(rest[4])))
        {
            return true;
        }
        return rest.StartsWith("while") && code.AsSpan(owner).StartsWith("do") && (owner + 2 >= code.Length || !IsWordChar(code[owner + 2]));
    }

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

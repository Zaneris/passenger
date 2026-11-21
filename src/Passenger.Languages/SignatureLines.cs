using Passenger.Editor;

namespace Passenger.Languages;

/// <summary>A signature on one line laid out a parameter a line, as a card shows one too long for its width: broken after
/// the parameter list's opening bracket and after each parameter's comma, each parameter indented, the closing bracket
/// and what follows it kept after the last. A name's list of parameters is the first bracket after a name (a tag in
/// brackets before the signature is no list), an indexer's after <c>this</c>; commas inside a type's arguments, a nested
/// bracket or a quoted default stay where they are.</summary>
public sealed class SignatureLines
{
    /// <summary>What each parameter's line begins with.</summary>
    public const string Indent = "    ";

    /// <summary>Where the text breaks in the one-line signature, each break's offset and how many spaces it takes there.</summary>
    private readonly (int At, int Spaces)[] breaks;

    private SignatureLines(string text, (int At, int Spaces)[] breaks)
    {
        Text = text;
        this.breaks = breaks;
    }

    /// <summary>The signature laid out a parameter a line.</summary>
    public string Text { get; }

    /// <summary>The signature <paramref name="code"/> laid out a parameter a line; null for code of more than one line, or
    /// with no list of parameters, or an empty one.</summary>
    public static SignatureLines? Of(string code)
    {
        if (code.Contains('\n', StringComparison.Ordinal) || ListStart(code) is not { } open)
        {
            return null;
        }
        List<(int At, int Spaces)> breaks = [(open + 1, 0)];
        int depth = 0;
        char? quote = null;
        for (int i = open + 1; i < code.Length; i++)
        {
            char c = code[i];
            if (quote is { } closing)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == closing)
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
                case '>' when i > 0 && code[i - 1] == '=':
                    break;
                case ')' or ']' or '}' or '>' when depth > 0:
                    depth--;
                    break;
                case ')' or ']':
                    return i == open + 1 ? null : new SignatureLines(Laid(code, breaks), [.. breaks]);
                case ',' when depth == 0:
                    int spaces = 0;
                    while (i + 1 + spaces < code.Length && code[i + 1 + spaces] == ' ')
                    {
                        spaces++;
                    }
                    breaks.Add((i + 1, spaces));
                    break;
            }
        }
        return null;
    }

    /// <summary>Spans of the one-line signature moved to where their text stands once it is laid out.</summary>
    public SyntaxSpan[] Map(IReadOnlyList<SyntaxSpan> spans)
    {
        SyntaxSpan[] moved = new SyntaxSpan[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            int start = Moved(spans[i].Start, end: false);
            moved[i] = new SyntaxSpan(start, Moved(spans[i].Start + spans[i].Length, end: true) - start, spans[i].Kind);
        }
        return moved;
    }

    /// <summary>Where an offset of the one-line signature stands once it is laid out, a span's end at a break before it and
    /// a start after it; one among a break's spaces where they go.</summary>
    private int Moved(int offset, bool end)
    {
        int shift = 0;
        foreach ((int at, int spaces) in breaks)
        {
            if (offset < at || (end && offset == at))
            {
                break;
            }
            shift += 1 + Indent.Length - Math.Min(spaces, offset - at);
        }
        return offset + shift;
    }

    /// <summary>The bracket opening the parameter list: the first '(' straight after a name, a type's arguments or an
    /// array's brackets, or the '[' after <c>this</c>.</summary>
    private static int? ListStart(string code)
    {
        for (int i = 1; i < code.Length; i++)
        {
            char before = code[i - 1];
            if ((code[i] == '(' && (Core.CSharpNames.Continues(before) || before is '>' or ']'))
                || (code[i] == '[' && code.AsSpan(0, i).EndsWith("this", StringComparison.Ordinal)))
            {
                return i;
            }
        }
        return null;
    }

    private static string Laid(string code, List<(int At, int Spaces)> breaks)
    {
        System.Text.StringBuilder laid = new(code.Length + (breaks.Count * (1 + Indent.Length)));
        int from = 0;
        foreach ((int at, int spaces) in breaks)
        {
            laid.Append(code, from, at - from).Append('\n').Append(Indent);
            from = at + spaces;
        }
        return laid.Append(code, from, code.Length - from).ToString();
    }
}

namespace Passenger.Workspace.GDScript;

/// <summary>What a GDScript's caret is at, as completion reads it.</summary>
public enum GDScriptAskedKind
{
    /// <summary>A comment, a number, or a name being declared: nothing is offered.</summary>
    None,

    /// <summary>A name in scope: a local, a member, a global, a keyword.</summary>
    Name,

    /// <summary>A member after a value's dot.</summary>
    Member,

    /// <summary>An annotation, after its <c>@</c>.</summary>
    Annotation,

    /// <summary>A node's path after <c>$</c>, or its unique name after <c>%</c>.</summary>
    NodePath,

    /// <summary>A type: after a declaration's <c>:</c>, after <c>-&gt;</c>, <c>extends</c>, <c>as</c> or <c>is</c>.</summary>
    Type,

    /// <summary>A function's name after <c>func</c> at a class's level, where the engine's virtual functions are overridden.</summary>
    Override,

    /// <summary>The characters of a string.</summary>
    String,
}

/// <summary>A call whose parentheses hold the caret: the function's name and where it starts, its opening parenthesis, the
/// argument the caret is in (from zero), and what it is called on as written before its dot (<c>$Anim</c>, <c>player</c>,
/// <c>get_node("Sprite").material</c>), null for none.</summary>
public sealed record GDScriptCallAt(string Name, int NameStart, int Open, int Argument, string? Receiver);

/// <summary>What the caret of a GDScript is at (<see cref="At"/>), read from its text alone, so it holds while the script
/// is half written: the kind of name typed there and where that name starts (at its <c>@</c>, <c>$</c> or <c>%</c>, or
/// after a string's quote), whether it is at a class's level or a statement's start, and the call whose parentheses hold
/// it. Pure.</summary>
public sealed record GDScriptAsked(GDScriptAskedKind Kind, int Start)
{
    /// <summary>Whether the caret is in a class's body rather than a function's.</summary>
    public bool ClassLevel { get; init; }

    /// <summary>Whether the name typed begins its line's statement, where a statement's keywords go.</summary>
    public bool StatementStart { get; init; }

    /// <summary>A type after <c>-&gt;</c>, where <c>void</c> goes too.</summary>
    public bool Returns { get; init; }

    /// <summary>The innermost call whose parentheses hold the caret; null for none.</summary>
    public GDScriptCallAt? Call { get; init; }

    /// <summary>For a string, where its characters end: its closing quote, else its line's end.</summary>
    public int End { get; init; }

    /// <summary>For a string, whether it is given straight to <see cref="Call"/> as its argument there.</summary>
    public bool Argument { get; init; }

    /// <summary>The words before a name that make what follows a type.</summary>
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal) { "extends", "as", "is" };

    /// <summary>The words before a name that declare it, where nothing is offered.</summary>
    private static readonly HashSet<string> Declaring = new(StringComparer.Ordinal) { "var", "const", "signal", "enum", "class", "class_name", "for" };

    /// <summary>What is at a caret of a script's text.</summary>
    public static GDScriptAsked At(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        List<(char Mark, string? Call, int CallStart, int Argument, int At)> open = [];
        string? lastWord = null;
        int lastWordStart = 0;
        bool wordLast = false;
        int at = 0;
        while (at < caret)
        {
            char c = text[at];
            if (c == '#')
            {
                int end = text.IndexOf('\n', at);
                if (end < 0 || end >= caret)
                {
                    return new GDScriptAsked(GDScriptAskedKind.None, caret);
                }
                at = end;
                continue;
            }
            if (c is '"' or '\'' || c is 'r' or '&' or '^' && at + 1 < text.Length && text[at + 1] is '"' or '\'' && !(at > 0 && IsWordChar(text[at - 1])))
            {
                int quote = c is '"' or '\'' ? at : at + 1;
                (int start, int stop, int next) = Quoted(text, quote, c == 'r');
                bool given = open.Count > 0 && open[^1].Mark == '(' && LastCodeBefore(text, at) is '(' or ',';
                if (caret >= start && caret <= stop)
                {
                    return new GDScriptAsked(GDScriptAskedKind.String, start)
                    {
                        End = stop,
                        Call = CallOf(text, open.Count > 0 ? open[^1] : null),
                        Argument = given,
                    };
                }
                at = next;
                wordLast = false;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = at;
                while (at < text.Length && IsWordChar(text[at]))
                {
                    at++;
                }
                lastWord = text[start..at];
                lastWordStart = start;
                wordLast = true;
                continue;
            }
            if (char.IsDigit(c))
            {
                while (at < text.Length && (IsWordChar(text[at]) || text[at] == '.' && at + 1 < text.Length && char.IsDigit(text[at + 1])))
                {
                    at++;
                }
                wordLast = false;
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                open.Add((c, c == '(' && wordLast ? lastWord : null, lastWordStart, 0, at));
            }
            else if (c is ')' or ']' or '}' && open.Count > 0)
            {
                open.RemoveAt(open.Count - 1);
            }
            else if (c == ',' && open.Count > 0)
            {
                open[^1] = open[^1] with { Argument = open[^1].Argument + 1 };
            }
            if (!char.IsWhiteSpace(c))
            {
                wordLast = false;
            }
            at++;
        }
        GDScriptCallAt? call = CallOf(text, open.Count > 0 ? open[^1] : null);
        int word = caret;
        while (word > 0 && IsWordChar(text[word - 1]))
        {
            word--;
        }
        if (word < caret && char.IsDigit(text[word]))
        {
            return new GDScriptAsked(GDScriptAskedKind.None, caret);
        }
        int path = word;
        while (path > 0 && (IsWordChar(text[path - 1]) || text[path - 1] == '/'))
        {
            path--;
        }
        if (path > 0 && (text[path - 1] == '$' || text[path - 1] == '%' && StartsOperand(text, path - 1)))
        {
            return new GDScriptAsked(GDScriptAskedKind.NodePath, path - 1) { Call = call };
        }
        char before = word > 0 ? text[word - 1] : '\0';
        if (before == '@')
        {
            return new GDScriptAsked(GDScriptAskedKind.Annotation, word - 1) { ClassLevel = ClassLevelAt(text, word), Call = call };
        }
        if (before == '.')
        {
            return word >= 2 && char.IsDigit(text[word - 2]) && NumberEndsAt(text, word - 1)
                ? new GDScriptAsked(GDScriptAskedKind.None, caret)
                : new GDScriptAsked(GDScriptAskedKind.Member, word) { Call = call };
        }
        int gap = word;
        while (gap > 0 && text[gap - 1] is ' ' or '\t')
        {
            gap--;
        }
        int previousEnd = gap;
        int previousStart = previousEnd;
        while (previousStart > 0 && IsWordChar(text[previousStart - 1]))
        {
            previousStart--;
        }
        string previous = text[previousStart..previousEnd];
        char previousChar = gap > 0 ? text[gap - 1] : '\0';
        bool classLevel = ClassLevelAt(text, word);
        if (previousChar == '>' && gap > 1 && text[gap - 2] == '-')
        {
            return new GDScriptAsked(GDScriptAskedKind.Type, word) { Returns = true, Call = call };
        }
        if (TypeWords.Contains(previous) || previousChar == ':' && TypeColon(text, gap - 1, open.Count > 0 ? open[^1] : null))
        {
            return new GDScriptAsked(GDScriptAskedKind.Type, word) { Call = call };
        }
        if (previous == "func")
        {
            return new GDScriptAsked(classLevel ? GDScriptAskedKind.Override : GDScriptAskedKind.None, word) { ClassLevel = classLevel };
        }
        if (Declaring.Contains(previous))
        {
            return new GDScriptAsked(GDScriptAskedKind.None, word);
        }
        int lineStart = word > 0 ? text.LastIndexOf('\n', word - 1) + 1 : 0;
        bool statementStart = text.AsSpan(lineStart, word - lineStart).IsWhiteSpace() || previous == "static" && text.AsSpan(lineStart, previousStart - lineStart).IsWhiteSpace();
        return new GDScriptAsked(GDScriptAskedKind.Name, word) { ClassLevel = classLevel, StatementStart = statementStart, Call = call };
    }

    /// <summary>The annotations Godot takes where a caret is: a class's level's, else a statement's.</summary>
    public static IEnumerable<string> Annotations(bool classLevel) => GDScriptParser.AnnotationNames(classLevel);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>A string from its opening quote: where its characters start and end, and where the code goes on; one left
    /// open ends at its line's end, a tripled one at the text's.</summary>
    private static (int Start, int End, int Next) Quoted(string text, int quote, bool raw)
    {
        char mark = text[quote];
        bool tripled = quote + 2 < text.Length && text[quote + 1] == mark && text[quote + 2] == mark;
        int start = quote + (tripled ? 3 : 1);
        int at = start;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '\\' && at + 1 < text.Length && !(raw && text[at + 1] != mark))
            {
                at += 2;
                continue;
            }
            if (c == mark && (!tripled || at + 2 < text.Length && text[at + 1] == mark && text[at + 2] == mark))
            {
                return (start, at, at + (tripled ? 3 : 1));
            }
            if (c == '\n' && !tripled)
            {
                return (start, at, at);
            }
            at++;
        }
        return (start, at, at);
    }

    /// <summary>The last character before an offset that is not a space, on its line or the ones before.</summary>
    private static char LastCodeBefore(string text, int at)
    {
        while (at > 0 && char.IsWhiteSpace(text[at - 1]))
        {
            at--;
        }
        return at > 0 ? text[at - 1] : '\0';
    }

    /// <summary>Whether a <c>%</c> starts an operand (a unique node's name) rather than taking a remainder: nothing that
    /// ends a value comes before it on its line.</summary>
    private static bool StartsOperand(string text, int sign)
    {
        int at = sign;
        while (at > 0 && text[at - 1] is ' ' or '\t')
        {
            at--;
        }
        char before = at > 0 ? text[at - 1] : '\0';
        return !(IsWordChar(before) || before is ')' or ']' or '}' or '"' or '\'');
    }

    /// <summary>Whether a '.' ends a number (<c>1.</c>) rather than a value's name.</summary>
    private static bool NumberEndsAt(string text, int dot)
    {
        int start = dot;
        while (start > 0 && (IsWordChar(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }
        return char.IsDigit(text[start]);
    }

    /// <summary>Whether a ':' gives a type: a variable's or constant's (<c>var speed:</c>), a function's or signal's
    /// parameter's, a typed loop variable's; not a dictionary's key's, a lambda's body's or a block's.</summary>
    private static bool TypeColon(string text, int colon, (char Mark, string? Call, int CallStart, int Argument, int At)? open)
    {
        int nameEnd = colon;
        while (nameEnd > 0 && text[nameEnd - 1] is ' ' or '\t')
        {
            nameEnd--;
        }
        int nameStart = nameEnd;
        while (nameStart > 0 && IsWordChar(text[nameStart - 1]))
        {
            nameStart--;
        }
        if (nameStart == nameEnd)
        {
            return false;
        }
        string before = WordBefore(text, nameStart);
        if (before is "var" or "const" or "for")
        {
            return true;
        }
        return open is { Mark: '(' } bracket && (bracket.Call == "func" || bracket.Call is not null && WordBefore(text, bracket.CallStart) is "func" or "signal");
    }

    /// <summary>The word that ends before an offset, spaces between them passed over; empty for none.</summary>
    private static string WordBefore(string text, int at)
    {
        int end = at;
        while (end > 0 && text[end - 1] is ' ' or '\t')
        {
            end--;
        }
        int start = end;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }
        return text[start..end];
    }

    /// <summary>Whether an offset is in a class's body rather than a function's: on a line not indented, or under an inner
    /// class's header line before any less indented line.</summary>
    private static bool ClassLevelAt(string text, int offset)
    {
        int lineStart = offset > 0 ? text.LastIndexOf('\n', offset - 1) + 1 : 0;
        int indent = Indent(text, lineStart);
        if (indent == 0)
        {
            return true;
        }
        for (int above = lineStart - 1; above > 0;)
        {
            int start = text.LastIndexOf('\n', above - 1) + 1;
            int width = Indent(text, start);
            ReadOnlySpan<char> code = text.AsSpan(start + width, above - start - width).Trim();
            above = start - 1;
            if (code.IsEmpty || code[0] == '#' || width >= indent)
            {
                continue;
            }
            return code.StartsWith("class ") || code.StartsWith("class\t");
        }
        return true;
    }

    private static int Indent(string text, int lineStart)
    {
        int at = lineStart;
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }
        return at - lineStart;
    }

    /// <summary>An open bracket as the call it opens: its function's name and receiver; null for no call's.</summary>
    private static GDScriptCallAt? CallOf(string text, (char Mark, string? Call, int CallStart, int Argument, int At)? open)
    {
        if (open is not { Mark: '(', Call: { } name } bracket || name is "if" or "elif" or "while" or "for" or "match" or "return" or "and" or "or" or "not" or "in")
        {
            return null;
        }
        return new GDScriptCallAt(name, bracket.CallStart, bracket.At, bracket.Argument, ReceiverBefore(text, bracket.CallStart));
    }

    /// <summary>What a call is made on, as written before its name's dot: names, node paths and calls back to the start of
    /// the expression; null where no dot comes before the name.</summary>
    private static string? ReceiverBefore(string text, int nameStart)
    {
        if (nameStart == 0 || text[nameStart - 1] != '.')
        {
            return null;
        }
        int end = nameStart - 1;
        int at = end;
        while (at > 0)
        {
            char c = text[at - 1];
            if (IsWordChar(c) || c is '/' or '$' or '%' or '.')
            {
                at--;
            }
            else if (c is ')' or ']' && Opening(text, at - 1) is int opened and >= 0)
            {
                at = opened;
            }
            else if (c is '"' && at >= 2 && text.LastIndexOf('"', at - 2) is int quote and >= 0 && quote > 0 && text[quote - 1] == '$')
            {
                at = quote;
            }
            else
            {
                break;
            }
        }
        return at < end ? text[at..end] : null;
    }

    /// <summary>The bracket a closing one at an offset closes, on its line; -1 for none.</summary>
    private static int Opening(string text, int closing)
    {
        int depth = 0;
        for (int at = closing; at >= 0 && text[at] != '\n'; at--)
        {
            if (text[at] is ')' or ']')
            {
                depth++;
            }
            else if (text[at] is '(' or '[' && --depth == 0)
            {
                return at;
            }
        }
        return -1;
    }
}

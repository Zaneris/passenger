using Passenger.Text;

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
        GDScriptScan scan = new(text);
        while (scan.Next(caret))
        {
            if (scan.Piece == GDScriptPiece.Comment && scan.End >= caret)
            {
                return new GDScriptAsked(GDScriptAskedKind.None, caret);
            }
            if (scan.Piece == GDScriptPiece.String && caret >= scan.TextStart && caret <= scan.TextEnd)
            {
                return new GDScriptAsked(GDScriptAskedKind.String, scan.TextStart)
                {
                    End = scan.TextEnd,
                    Call = CallOf(text, scan.Innermost),
                    Argument = scan.Innermost is { Mark: '(' } && scan.BeginsArgument,
                };
            }
        }
        GDScriptCallAt? call = CallOf(text, scan.Innermost);
        int word = NameStart(text, caret);
        if (word < caret && GDScriptTokenizer.IdentifierStart(text, word) == 0)
        {
            return new GDScriptAsked(GDScriptAskedKind.None, caret);
        }
        int path = word;
        while (path > 0 && (text[path - 1] == '/' ? 1 : GDScriptTokenizer.IdentifierContinueBefore(text, path)) is > 0 and int width)
        {
            path -= width;
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
            return word >= 2 && char.IsAsciiDigit(text[word - 2]) && NumberEndsAt(text, word - 1)
                ? new GDScriptAsked(GDScriptAskedKind.None, caret)
                : new GDScriptAsked(GDScriptAskedKind.Member, word) { Call = call };
        }
        int gap = word;
        while (gap > 0 && text[gap - 1] is ' ' or '\t')
        {
            gap--;
        }
        int previousEnd = gap;
        int previousStart = NameStart(text, previousEnd);
        string previous = text[previousStart..previousEnd];
        char previousChar = gap > 0 ? text[gap - 1] : '\0';
        bool classLevel = ClassLevelAt(text, word);
        if (previousChar == '>' && gap > 1 && text[gap - 2] == '-')
        {
            return new GDScriptAsked(GDScriptAskedKind.Type, word) { Returns = true, Call = call };
        }
        if (TypeWords.Contains(previous) || previousChar == ':' && TypeColon(text, gap - 1, scan.Innermost))
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
        int lineStart = TextNavigation.LineStart(text, word);
        bool statementStart = text.AsSpan(lineStart, word - lineStart).IsWhiteSpace() || previous == "static" && text.AsSpan(lineStart, previousStart - lineStart).IsWhiteSpace();
        return new GDScriptAsked(GDScriptAskedKind.Name, word) { ClassLevel = classLevel, StatementStart = statementStart, Call = call };
    }

    /// <summary>The annotations Godot takes where a caret is: a class's level's, else a statement's.</summary>
    public static IEnumerable<string> Annotations(bool classLevel) => GDScriptParser.AnnotationNames(classLevel);

    /// <summary>Where the run of a name's characters that ends at an offset starts, read back by Godot's identifier rule.</summary>
    private static int NameStart(string text, int end)
    {
        while (end > 0 && GDScriptTokenizer.IdentifierContinueBefore(text, end) is > 0 and int width)
        {
            end -= width;
        }
        return end;
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
        return !(GDScriptTokenizer.IdentifierContinueBefore(text, at) > 0 || before is ')' or ']' or '}' or '"' or '\'');
    }

    /// <summary>Whether a '.' ends a number (<c>1.</c>) rather than a value's name.</summary>
    private static bool NumberEndsAt(string text, int dot)
    {
        int start = dot;
        while (start > 0 && (text[start - 1] == '.' ? 1 : GDScriptTokenizer.IdentifierContinueBefore(text, start)) is > 0 and int width)
        {
            start -= width;
        }
        return char.IsAsciiDigit(text[start]);
    }

    /// <summary>Whether a ':' gives a type: a variable's or constant's (<c>var speed:</c>), a function's or signal's
    /// parameter's, a typed loop variable's; not a dictionary's key's, a lambda's body's or a block's.</summary>
    private static bool TypeColon(string text, int colon, GDScriptBracket? open)
    {
        int nameEnd = colon;
        while (nameEnd > 0 && text[nameEnd - 1] is ' ' or '\t')
        {
            nameEnd--;
        }
        int nameStart = NameStart(text, nameEnd);
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
        return text[NameStart(text, end)..end];
    }

    /// <summary>Whether an offset is in a class's body rather than a function's: on a line not indented, or under an inner
    /// class's header line before any less indented line.</summary>
    private static bool ClassLevelAt(string text, int offset)
    {
        int lineStart = TextNavigation.LineStart(text, offset);
        int indent = TextNavigation.IndentEnd(text, lineStart) - lineStart;
        if (indent == 0)
        {
            return true;
        }
        for (int above = lineStart - 1; above > 0;)
        {
            int start = TextNavigation.LineStart(text, above);
            int width = TextNavigation.IndentEnd(text, start) - start;
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

    /// <summary>An open bracket as the call it opens: its function's name and receiver; null for no call's.</summary>
    private static GDScriptCallAt? CallOf(string text, GDScriptBracket? open)
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
            if ((c is '/' or '$' or '%' or '.' ? 1 : GDScriptTokenizer.IdentifierContinueBefore(text, at)) is > 0 and int width)
            {
                at -= width;
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

using System.Text;
using Passenger.Text;
using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>How a GDScript is laid out: its indentation (Godot's tabs unless its .editorconfig says spaces), the length
/// past which a line is wrapped inside its brackets (none at 0), the blank lines around a function or class and whether a
/// multi-line array, dictionary or enum ends in a comma (<see cref="GDScriptFormatSettings"/>).</summary>
public readonly record struct GDScriptFormatOptions(bool UseTabs = true, int IndentSize = 4, int TabWidth = 4,
    int LineLength = GDScriptFormatSettings.GuideLineLength, int BlankLines = 2, bool TrailingCommas = true)
{
    /// <summary>Godot's own: tabs, four columns wide. Without this, <c>new()</c> would skip the defaults above.</summary>
    public GDScriptFormatOptions()
        : this(true)
    {
    }
}

/// <summary>A GDScript's layout as Godot's GDScript style guide asks for it: the edits that make it so, or why it was left
/// as it is.</summary>
public sealed record GDScriptFormatted(IReadOnlyList<TextEdit> Edits, string Text, string? Refused)
{
    /// <summary>The layout refused because it said something else, for finding why.</summary>
    public string? Attempted { get; init; }
}

/// <summary>Lays a GDScript out as Godot's GDScript style guide does (docs.godotengine.org, Getting started › Scripting ›
/// GDScript › GDScript style guide), changing only the whitespace between its tokens and adding the trailing comma of a
/// multi-line array, dictionary or enum: indentation (Indentation), blank lines (Blank lines), spaces around operators and
/// after commas, none inside brackets or before a call's (Whitespace), trailing commas (Trailing comma), and a line past
/// the length asked wrapped inside its brackets (Line length). Comments and every token are kept as written; the result
/// is read again and refused unless its tokens, comments and tree are the script's own. Pure, for any thread.</summary>
public static partial class GDScriptFormatter
{
    /// <summary>A token of the script as written, with what laying it out needs: the indent level of its statement, its
    /// bracket depth, and whether it is the first on its line.</summary>
    private readonly record struct Laid(GDScriptToken Token, int Level, int Depth, bool First);

    public static GDScriptFormatted Format(string text, GDScriptFormatOptions options)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        if (tree.Errors.Count > 0)
        {
            return new GDScriptFormatted([], text, $"the script does not parse: {tree.Errors[0].Message}");
        }
        if (Scanned(text) is not { } tokens)
        {
            return new GDScriptFormatted([], text, "the script's tokens do not read");
        }
        List<TextEdit> edits = Layout(text, tokens, Blocks(tree, options.BlankLines), options);
        string formatted = Applied(text, edits);
        List<GDScriptToken>? laidTokens = null;
        if (options.LineLength > 0 && Wrapped(formatted, Shifted(tokens, edits), options) is { } wrapped)
        {
            laidTokens = Scanned(wrapped);
            if (laidTokens is null || Aligned(text, tokens, wrapped, laidTokens) is not { } aligned)
            {
                return new GDScriptFormatted([], text, "laying it out would change what it says (its tokens)") { Attempted = wrapped };
            }
            // Its edits make the wrapped text wherever the tokens they keep read the same, which is checked next.
            edits = aligned;
            formatted = wrapped;
        }
        if (Differs(text, tokens, tree, formatted, laidTokens) is { } why)
        {
            return new GDScriptFormatted([], text, $"laying it out would change what it says ({why})") { Attempted = formatted };
        }
        return new GDScriptFormatted(edits, formatted, null);
    }

    /// <summary>Every token of a script, newlines and indents among them, read as Godot's parser has its tokenizer read
    /// them: inside brackets, lines run on. Null where a token is an error.</summary>
    private static List<GDScriptToken>? Scanned(string text)
    {
        GDScriptTokenizer tokenizer = new(text);
        List<GDScriptToken> tokens = [];
        int depth = 0;
        while (true)
        {
            tokenizer.Multiline = depth > 0;
            GDScriptToken token = tokenizer.Scan();
            if (token.Kind == K.Error)
            {
                return null;
            }
            if (token.Kind == K.Eof)
            {
                return tokens;
            }
            depth += token.Kind switch
            {
                K.ParenthesisOpen or K.BracketOpen or K.BraceOpen => 1,
                K.ParenthesisClose or K.BracketClose or K.BraceClose => -1,
                _ => 0,
            };
            depth = Math.Max(0, depth);
            tokens.Add(token);
        }
    }

    private static bool IsVirtual(K kind) => kind is K.Newline or K.Indent or K.Dedent;

    /// <summary>The edits laying a script out; <paramref name="blocks"/> null keeps its blank lines as they are.</summary>
    private static List<TextEdit> Layout(string text, List<GDScriptToken> scanned, Dictionary<int, int>? blocks, GDScriptFormatOptions options)
    {
        int[] lines = LineStarts(text);
        List<Laid> laid = [];
        int level = 0;
        int depth = 0;
        int lastLine = -1;
        foreach (GDScriptToken token in scanned)
        {
            switch (token.Kind)
            {
                case K.Indent:
                    level++;
                    continue;
                case K.Dedent:
                    level = Math.Max(0, level - 1);
                    continue;
                case K.Newline:
                    continue;
            }
            int line = LineOf(lines, token.Start);
            laid.Add(new Laid(token, level, depth, line != lastLine));
            lastLine = LineOf(lines, Math.Max(token.Start, token.End - 1));
            depth += token.Kind switch
            {
                K.ParenthesisOpen or K.BracketOpen or K.BraceOpen => 1,
                K.ParenthesisClose or K.BracketClose or K.BraceClose => -1,
                _ => 0,
            };
            depth = Math.Max(0, depth);
        }
        string unit = options.UseTabs ? "\t" : new string(' ', options.IndentSize);
        Units units = new(unit);
        List<TextEdit> edits = [];
        // What each open bracket is, the line it opened on, and whether a lambda lies inside it.
        List<(K Kind, int Line, int Index)> open = [];
        HashSet<int> lambdaBrackets = LambdaBrackets(laid);
        // Each line's indentation as laid out, by the line it began on in the text.
        Dictionary<int, string> indents = [];
        bool nodePath = false;
        for (int i = 0; i <= laid.Count; i++)
        {
            int gapStart = i == 0 ? 0 : laid[i - 1].Token.End;
            int gapEnd = i == laid.Count ? text.Length : laid[i].Token.Start;
            // Most gaps are empty or a space, read without making a string of each.
            string gap = (gapEnd - gapStart) switch
            {
                0 => string.Empty,
                1 when text[gapStart] == ' ' => " ",
                _ => text[gapStart..gapEnd],
            };
            string made;
            if (i == 0)
            {
                made = Leading(gap, options, unit);
                if (laid.Count > 0)
                {
                    string indent = Indent(laid[0], open, lambdaBrackets, indents, laid, units, gap);
                    indents[LineOf(lines, laid[0].Token.Start)] = indent;
                    made += indent;
                }
            }
            else if (i == laid.Count)
            {
                made = Trailing(gap, options, unit);
            }
            else
            {
                Laid a = laid[i - 1];
                Laid b = laid[i];
                GDScriptToken? c = i + 1 < laid.Count ? laid[i + 1].Token : null;
                GDScriptToken? before = i >= 2 ? laid[i - 2].Token : null;
                if (!gap.Contains('\n'))
                {
                    if (nodePath && gap.Length == 0)
                    {
                        made = gap;
                    }
                    else
                    {
                        nodePath = false;
                        made = Space(before, a.Token, b.Token, c, gap, open.Count > 0 && open[^1].Kind == K.ParenthesisOpen);
                    }
                }
                else
                {
                    nodePath = false;
                    string indent = Indent(b, open, lambdaBrackets, indents, laid, units, gap);
                    indents[LineOf(lines, b.Token.Start)] = indent;
                    made = Lines(gap, options, unit, BlankRule(b, blocks, open.Count > 0), indent);
                    if (options.TrailingCommas && b.Token.Kind is K.BracketClose or K.BraceClose && open.Count > 0 && open[^1].Index != i - 1
                        && a.Token.Kind != K.Comma && !lambdaBrackets.Contains(open[^1].Index) && Continued(gap) is false
                        && !(open[^1].Kind == K.BracketOpen && open[^1].Index > 0 && Subscripts(laid[open[^1].Index - 1].Token)))
                    {
                        made = "," + made;
                    }
                }
                nodePath |= b.Token.Kind == K.Dollar || b.Token.Kind == K.Percent && !a.Token.CanPrecedeBinaryOperator;
            }
            if (made != gap)
            {
                edits.Add(new TextEdit(gapStart, gap.Length, made));
            }
            if (i < laid.Count)
            {
                GDScriptToken token = laid[i].Token;
                if (token.Kind is K.ParenthesisOpen or K.BracketOpen or K.BraceOpen)
                {
                    open.Add((token.Kind, LineOf(lines, token.Start), i));
                }
                else if (token.Kind is K.ParenthesisClose or K.BracketClose or K.BraceClose && open.Count > 0)
                {
                    open.RemoveAt(open.Count - 1);
                }
            }
        }
        return edits;
    }

    /// <summary>The brackets (by their token's index) inside which a lambda is written, whose body's lines run by their own
    /// indentation: theirs is kept as written.</summary>
    private static HashSet<int> LambdaBrackets(List<Laid> laid)
    {
        HashSet<int> marked = [];
        List<int> open = [];
        for (int i = 0; i < laid.Count; i++)
        {
            switch (laid[i].Token.Kind)
            {
                case K.ParenthesisOpen or K.BracketOpen or K.BraceOpen:
                    open.Add(i);
                    break;
                case K.ParenthesisClose or K.BracketClose or K.BraceClose when open.Count > 0:
                    open.RemoveAt(open.Count - 1);
                    break;
                case K.Func:
                    marked.UnionWith(open);
                    break;
            }
        }
        return marked;
    }

    /// <summary>The blank lines a member's first token wants before it, by its offset: two around a function or class at the
    /// script's top level (Blank lines: "Surround functions and class definitions with two blank lines"), one or two around
    /// one in an inner class (negative: at least one, at most two); <paramref name="most"/> in place of two.</summary>
    private static Dictionary<int, int> Blocks(GDScriptTree tree, int most)
    {
        Dictionary<int, int> blocks = [];
        void Members(IReadOnlyList<(GDScriptNode Node, int Offset)> members, bool top)
        {
            for (int k = 0; k < members.Count; k++)
            {
                (GDScriptNode node, int offset) = members[k];
                bool block = node is GDScriptFunction or GDScriptClass;
                bool after = k > 0 && members[k - 1].Node is GDScriptFunction or GDScriptClass;
                // An inner class's first member follows its header, with no blank line it must have.
                if ((block || after) && (top || k > 0))
                {
                    blocks[offset + FirstOf(node)] = top ? most : -most;
                }
                if (node is GDScriptClass inner)
                {
                    Members([.. inner.Members.Select(m => (m, offset))], top: false);
                }
            }
        }
        Members([.. tree.Members.Select(m => (m.Node, m.Offset))], top: true);
        return blocks;
    }

    private static int FirstOf(GDScriptNode node) => node.Annotations.Count == 0 ? node.Start : Math.Min(node.Start, node.Annotations.Min(a => a.Start));

    /// <summary>How the blank lines before a token are kept: a member that wants some (<see cref="Blocks"/>), else at most two
    /// at the script's top level, one in a block or inside brackets (Blank lines: "Use one blank line inside functions to
    /// separate logical sections").</summary>
    private static (int Least, int Most) BlankRule(Laid b, Dictionary<int, int>? blocks, bool inBrackets)
    {
        if (blocks is null)
        {
            return (0, int.MaxValue);
        }
        if (!inBrackets && b.First && blocks.TryGetValue(b.Token.Start, out int wanted))
        {
            return wanted > 0 ? (wanted, wanted) : (1, -wanted);
        }
        return (0, !inBrackets && b.Level == 0 ? 2 : 1);
    }

    /// <summary>Whether a gap holds a line continuation (a backslash outside a comment), which keeps it as written.</summary>
    private static bool Continued(string gap)
    {
        bool comment = false;
        foreach (char ch in gap)
        {
            if (ch == '\n')
            {
                comment = false;
            }
            else if (ch == '#')
            {
                comment = true;
            }
            else if (ch == '\\' && !comment)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A gap between two tokens on separate lines laid out: the first line's comment kept (its trailing spaces
    /// dropped), each blank run between held to the rule, the rule's least before the comments that lead the token's
    /// line, each comment line's indentation in the file's indent, and the token's line's indentation.</summary>
    private static string Lines(string gap, GDScriptFormatOptions options, string unit, (int Least, int Most) blanks, string indent)
    {
        if (Continued(gap))
        {
            return gap;
        }
        string[] parts = gap.Split('\n');
        StringBuilder made = new();
        made.Append(Comment(parts[0], first: true, options, unit));
        // The lines between: blank or a comment's.
        List<string?> between = [.. parts[1..^1].Select(p => p.Trim().Length == 0 ? null : Comment(p, first: false, options, unit))];
        int lead = between.Count;
        while (lead > 0 && between[lead - 1] is not null)
        {
            lead--;
        }
        int blank = 0;
        for (int k = 0; k < between.Count; k++)
        {
            if (between[k] is not { } comment)
            {
                blank++;
                continue;
            }
            int keep = k == lead ? Math.Clamp(blank, blanks.Least, blanks.Most) : Math.Min(blank, blanks.Most);
            for (int n = 0; n < keep; n++)
            {
                made.Append('\n');
            }
            blank = 0;
            made.Append('\n').Append(comment);
        }
        int last = lead == between.Count ? Math.Clamp(blank, blanks.Least, blanks.Most) : Math.Min(blank, blanks.Most);
        for (int n = 0; n < last; n++)
        {
            made.Append('\n');
        }
        made.Append('\n').Append(indent);
        return made.ToString();
    }

    /// <summary>A line's comment: after a token, its spaces before the <c>#</c> kept (one at least) and its own end trimmed;
    /// on a line of its own, its indentation in the file's indent. Empty for a line with no comment.</summary>
    private static string Comment(string line, bool first, GDScriptFormatOptions options, string unit)
    {
        int hash = line.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return string.Empty;
        }
        string comment = line[hash..].TrimEnd();
        if (first)
        {
            string before = line[..hash];
            return (before.Length == 0 ? " " : before) + comment;
        }
        return Reindented(line[..hash], options, unit) + comment;
    }

    /// <summary>A comment line's indentation as levels of the file's indent: a tab one level, the indent's width of spaces
    /// one, any spaces left kept.</summary>
    private static string Reindented(string whitespace, GDScriptFormatOptions options, string unit)
    {
        int columns = 0;
        foreach (char ch in whitespace)
        {
            columns += ch == '\t' ? options.TabWidth : 1;
        }
        int width = options.UseTabs ? options.TabWidth : options.IndentSize;
        return string.Concat(Enumerable.Repeat(unit, columns / width)) + new string(' ', columns % width);
    }

    /// <summary>What comes before the first token: no blank lines, the comments kept.</summary>
    private static string Leading(string gap, GDScriptFormatOptions options, string unit)
    {
        StringBuilder made = new();
        string[] parts = gap.Split('\n');
        bool any = false;
        int blank = 0;
        foreach (string part in parts[..^1])
        {
            if (part.Trim().Length == 0)
            {
                blank += any ? 1 : 0;
                continue;
            }
            for (int n = 0; n < Math.Min(blank, 2); n++)
            {
                made.Append('\n');
            }
            blank = 0;
            made.Append(Comment(part, first: false, options, unit)).Append('\n');
            any = true;
        }
        if (any && blank > 0)
        {
            made.Append('\n', Math.Min(blank, 2));
        }
        return made.ToString();
    }

    /// <summary>What comes after the last token: its comments kept, no blank lines at the end, one line break last.</summary>
    private static string Trailing(string gap, GDScriptFormatOptions options, string unit)
    {
        string[] parts = gap.Split('\n');
        StringBuilder made = new(Comment(parts[0], first: true, options, unit));
        int blank = 0;
        foreach (string part in parts.Skip(1))
        {
            if (part.Trim().Length == 0)
            {
                blank++;
                continue;
            }
            made.Append('\n', 1 + Math.Min(blank, 2)).Append(Comment(part, first: false, options, unit));
            blank = 0;
        }
        return made.Append('\n').ToString();
    }

    /// <summary>A token's line's indentation (Indentation): its statement's level, or, inside brackets, that of the line
    /// the innermost opened on, one level more inside an array, dictionary or enum, two inside a parenthesis ("Use 2 indent
    /// levels to distinguish continuation lines from regular code blocks"; arrays, dictionaries and enums one); a bracket
    /// closing on its own line at its opening line's (Line length's multiline statement). Inside brackets a lambda lies
    /// in, as written.</summary>
    private static string Indent(Laid b, List<(K Kind, int Line, int Index)> open, HashSet<int> lambdas, Dictionary<int, string> indents, List<Laid> laid,
        Units units, string gap)
    {
        if (b.Depth == 0)
        {
            return units.Of(b.Level);
        }
        if (open.Any(o => lambdas.Contains(o.Index)))
        {
            int lineStart = gap.LastIndexOf('\n');
            return lineStart < 0 ? gap : gap[(lineStart + 1)..];
        }
        bool closes = b.Token.Kind is K.ParenthesisClose or K.BracketClose or K.BraceClose;
        if (indents.TryGetValue(open[^1].Line, out string? opening))
        {
            return closes ? opening : opening + units.Of(open[^1].Kind == K.ParenthesisOpen ? 2 : 1);
        }
        // A line begun inside a multi-line string: its brackets counted from the statement's level.
        int count = closes ? open.Count - 1 : open.Count;
        int level = laid[open[0].Index].Level;
        int lastLine = -1;
        int weight = 0;
        for (int k = 0; k < count; k++)
        {
            if (open[k].Line != lastLine)
            {
                level += weight;
                lastLine = open[k].Line;
            }
            weight = open[k].Kind == K.ParenthesisOpen ? 2 : 1;
        }
        if (count > 0)
        {
            level += weight;
        }
        return units.Of(level);
    }

    /// <summary>Indentation of each depth in one unit, each made once.</summary>
    private sealed class Units(string unit)
    {
        private readonly List<string> made = [string.Empty];

        public string Of(int levels)
        {
            while (made.Count <= levels)
            {
                made.Add(made[^1] + unit);
            }
            return made[levels];
        }
    }

    /// <summary>Whether a bracket after a token indexes it (<c>a[0]</c>, <c>Array[int]</c>) rather than opening an array.</summary>
    private static bool Subscripts(GDScriptToken a) =>
        a.IsIdentifier || a.Kind is K.Self or K.ParenthesisClose or K.BracketClose or K.BraceClose
        || a.Kind == K.Literal && a.Literal is GDScriptLiteralKind.String or GDScriptLiteralKind.StringName or GDScriptLiteralKind.NodePath;

    /// <summary>The space between two tokens on one line (Whitespace: "Always use one space around operators and after
    /// commas. Also, avoid extra spaces in dictionary references and function calls"): none before a comma, a colon or a
    /// closing bracket, after an opening one, around a dot, after a unary operator or before a call's or subscript's
    /// bracket; one after a comma or a colon, around a binary operator, <c>-&gt;</c> and <c> := </c>; braces and a
    /// parameter's default keep whether they had one, anything else one where it had any.</summary>
    private static string Space(GDScriptToken? before, GDScriptToken a, GDScriptToken b, GDScriptToken? c, string gap, bool inParentheses)
    {
        if (gap.AsSpan().ContainsAnyExcept(' ', '\t'))
        {
            return gap;
        }
        string had = gap.Length > 0 ? " " : string.Empty;
        if (inParentheses && (a.Kind == K.Equal || b.Kind == K.Equal))
        {
            // A parameter's default, which the guide writes either way (Blank lines' example: effect=null).
            return had;
        }
        if (b.Kind == K.Colon)
        {
            return c is { Kind: K.Equal } equal && equal.Start == b.End ? " " : string.Empty;
        }
        if (a.Kind == K.Colon)
        {
            return b.Kind == K.Equal && a.End == b.Start ? string.Empty : " ";
        }
        if (b.Kind is K.Comma or K.Semicolon or K.ParenthesisClose or K.BracketClose)
        {
            return string.Empty;
        }
        if (a.Kind is K.ParenthesisOpen or K.BracketOpen)
        {
            return string.Empty;
        }
        if (b.Kind == K.BraceClose || a.Kind == K.BraceOpen)
        {
            return had;
        }
        if (a.Kind is K.Period or K.PeriodPeriodPeriod or K.Dollar || b.Kind == K.Period)
        {
            return string.Empty;
        }
        if (a.Kind is K.Comma or K.Semicolon)
        {
            return " ";
        }
        if (b.Kind == K.ParenthesisOpen)
        {
            return a.IsIdentifier || a.Kind is K.Self or K.Super or K.Preload or K.Assert or K.Func or K.Annotation or K.ParenthesisClose or K.BracketClose
                or K.Void ? string.Empty : Operated(before, a, b, had);
        }
        if (b.Kind == K.BracketOpen && Subscripts(a))
        {
            return string.Empty;
        }
        if (a.Kind == K.Percent && before is { } owner && !owner.CanPrecedeBinaryOperator)
        {
            // A unique node's name after its percent sign.
            return string.Empty;
        }
        return Operated(before, a, b, had);
    }

    /// <summary>The space around an operator: one around a binary operator, an assignment, <c>-&gt;</c> and a word operator,
    /// none after a unary one; else one where there was any.</summary>
    private static string Operated(GDScriptToken? before, GDScriptToken a, GDScriptToken b, string had)
    {
        if (IsUnary(before, a))
        {
            return a.Kind == K.Not ? " " : string.Empty;
        }
        if (IsBinary(a, before) || IsBinary(b, a))
        {
            return " ";
        }
        return had;
    }

    private static bool IsUnary(GDScriptToken? before, GDScriptToken op) =>
        op.Kind is K.Bang or K.Tilde or K.Not || op.Kind is K.Minus or K.Plus && before is not { CanPrecedeBinaryOperator: true };

    /// <summary>Whether a token is a binary operator where it stands, the token before it given.</summary>
    private static bool IsBinary(GDScriptToken op, GDScriptToken? before) => op.Kind switch
    {
        K.Minus or K.Plus or K.Percent => before is { CanPrecedeBinaryOperator: true },
        K.Star or K.StarStar or K.Slash or K.LessLess or K.GreaterGreater or K.Ampersand or K.Pipe or K.Caret or K.Less or K.LessEqual
            or K.Greater or K.GreaterEqual or K.EqualEqual or K.BangEqual or K.AmpersandAmpersand or K.PipePipe or K.And or K.Or or K.In
            or K.Is or K.As or K.Equal or K.PlusEqual or K.MinusEqual or K.StarEqual or K.StarStarEqual or K.SlashEqual or K.PercentEqual
            or K.LessLessEqual or K.GreaterGreaterEqual or K.AmpersandEqual or K.PipeEqual or K.CaretEqual or K.ForwardArrow => true,
        _ => false,
    };

    private static int[] LineStarts(string text)
    {
        List<int> starts = [0];
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            starts.Add(i + 1);
        }
        return [.. starts];
    }

    private static int LineOf(int[] lines, int offset) => Core.Sorted.FirstAfter<int>(lines, offset) - 1;

    /// <summary>Why a laid out script would say something else than it did: its tokens (the indents and line breaks Godot
    /// reads among them, a trailing comma before a closing bracket aside), its comments in order, or the tree Godot's parser
    /// reads; null where it says the same.</summary>
    public static string? Differs(string was, string now) =>
        Scanned(was) is { } before ? Differs(was, before, GDScriptTree.Parse(was), now, null) : "a token no longer reads";

    private static string? Differs(string was, List<GDScriptToken> before, GDScriptTree treeBefore, string now, List<GDScriptToken>? scanned)
    {
        if ((scanned ?? Scanned(now)) is not { } after)
        {
            return "a token no longer reads";
        }
        int a = 0;
        int b = 0;
        while (true)
        {
            a = Compared(before, a);
            b = Compared(after, b);
            if (a >= before.Count || b >= after.Count)
            {
                if (a < before.Count || b < after.Count)
                {
                    return "its tokens";
                }
                break;
            }
            GDScriptToken x = before[a];
            GDScriptToken y = after[b];
            if (x.Kind != y.Kind || !IsVirtual(x.Kind) && !was.AsSpan(x.Start, x.End - x.Start).SequenceEqual(now.AsSpan(y.Start, y.End - y.Start)))
            {
                return "its tokens";
            }
            a++;
            b++;
        }
        if (!Comments(was, before).SequenceEqual(Comments(now, after)))
        {
            return "its comments";
        }
        GDScriptTree treeAfter = GDScriptTree.Parse(now);
        if (treeAfter.Errors.Count != treeBefore.Errors.Count || !Same(treeBefore, treeAfter))
        {
            return "its tree";
        }
        return null;
    }

    /// <summary>The next token from an index that counts in a comparison: a comma closing a bracket's last item passed over.</summary>
    private static int Compared(List<GDScriptToken> tokens, int at)
    {
        while (at < tokens.Count && tokens[at].Kind == K.Comma && NextReal(tokens, at + 1) is { Kind: K.BracketClose or K.BraceClose })
        {
            at++;
        }
        return at;
    }

    private static GDScriptToken? NextReal(List<GDScriptToken> tokens, int from)
    {
        for (int i = from; i < tokens.Count; i++)
        {
            if (!IsVirtual(tokens[i].Kind))
            {
                return tokens[i];
            }
        }
        return null;
    }

    /// <summary>A script's comments in order, each as written from its <c>#</c>, its end trimmed.</summary>
    private static List<string> Comments(string text, List<GDScriptToken> tokens)
    {
        List<string> comments = [];
        int from = 0;
        foreach (GDScriptToken token in tokens.Where(t => !IsVirtual(t.Kind)).Append(new GDScriptToken(K.Eof, text.Length, text.Length)))
        {
            // Between tokens a # starts a comment to its line's end.
            for (int at = text.IndexOf('#', from, Math.Max(0, token.Start - from)); at >= 0; at = text.IndexOf('#', from, Math.Max(0, token.Start - from)))
            {
                int end = text.IndexOf('\n', at, token.Start - at);
                end = end < 0 ? token.Start : end;
                comments.Add(text.AsSpan(at, end - at).TrimEnd().ToString());
                from = end;
            }
            from = Math.Max(from, token.End);
        }
        return comments;
    }

    /// <summary>A text with edits in order, none overlapping, made.</summary>
    private static string Applied(string text, IReadOnlyList<TextEdit> edits)
    {
        StringBuilder made = new(text.Length + edits.Count * 4);
        int from = 0;
        foreach (TextEdit edit in edits)
        {
            made.Append(text, from, edit.Start - from).Append(edit.Text);
            from = edit.Start + edit.Length;
        }
        return made.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>Whether two trees are alike but for places: each node's kind and what it names or holds, its annotations
    /// and children in order.</summary>
    public static bool Same(GDScriptTree x, GDScriptTree y)
    {
        if (!Node(x.Class, y.Class) || x.Members.Count != y.Members.Count)
        {
            return false;
        }
        for (int i = 0; i < x.Members.Count; i++)
        {
            if (!Node(x.Members[i].Node, y.Members[i].Node))
            {
                return false;
            }
        }
        return true;

        static bool Node(GDScriptNode a, GDScriptNode b)
        {
            if (a.GetType() != b.GetType() || !Alike(a, b) || a.Annotations.Count != b.Annotations.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Annotations.Count; i++)
            {
                if (!Node(a.Annotations[i], b.Annotations[i]))
                {
                    return false;
                }
            }
            using IEnumerator<GDScriptNode> left = a.Children.GetEnumerator();
            using IEnumerator<GDScriptNode> right = b.Children.GetEnumerator();
            while (true)
            {
                bool more = left.MoveNext();
                if (more != right.MoveNext())
                {
                    return false;
                }
                if (!more)
                {
                    return true;
                }
                if (!Node(left.Current, right.Current))
                {
                    return false;
                }
            }
        }
    }

    /// <summary>Whether two nodes of one kind name or hold the same, compared without making words of either.</summary>
    private static bool Alike(GDScriptNode a, GDScriptNode b) => (a, b) switch
    {
        (GDScriptIdentifier x, GDScriptIdentifier y) => x.Name == y.Name,
        (GDScriptLiteral x, GDScriptLiteral y) => x.Kind == y.Kind && Equals(x.Value, y.Value),
        (GDScriptBinary x, GDScriptBinary y) => x.Operator == y.Operator,
        (GDScriptUnary x, GDScriptUnary y) => x.Operator == y.Operator,
        (GDScriptAssignment x, GDScriptAssignment y) => x.Operator == y.Operator,
        (GDScriptAnnotation x, GDScriptAnnotation y) => x.Name == y.Name,
        (GDScriptGetNode x, GDScriptGetNode y) => x.Path == y.Path,
        (GDScriptCall x, GDScriptCall y) => x.FunctionName == y.FunctionName,
        (GDScriptAssignable x, GDScriptAssignable y) => x.Infers == y.Infers,
        (GDScriptFunction x, GDScriptFunction y) => x.IsStatic == y.IsStatic && x.IsAbstract == y.IsAbstract && x.IsLambda == y.IsLambda,
        _ => true,
    };
}

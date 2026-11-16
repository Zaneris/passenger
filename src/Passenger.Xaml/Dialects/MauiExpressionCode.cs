using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.Xaml.Dialects;

/// <summary>The C# of one of MAUI's expressions in XAML as its source generator reads it, from the attribute's value as the
/// file writes it, in the generator's steps: what stands between its braces (after <c>{=</c> for an explicit one), trimmed,
/// its character references read; the operator words MAUI takes for what XML would escape (<c>AND</c>, <c>OR</c>,
/// <c>LT</c>, <c>GT</c>, <c>LTE</c>, <c>GTE</c>, each between spaces) as C#'s operators; a conditional written straight
/// into an interpolation's hole put in parentheses; and each single-quoted string a double-quoted one. Each step keeps
/// where each character of its text came from, so a place in the code is a place in the value. A value still being typed
/// may lack its closing brace.</summary>
public sealed class MauiExpressionCode
{
    private static readonly (string Word, string Operator)[] Words = [("AND", "&&"), ("OR", "||"), ("LTE", "<="), ("GTE", ">="), ("LT", "<"), ("GT", ">")];

    /// <summary>For each character of the code, and its end, where what it stands for starts in the value, and where that
    /// ends. The code may take the value's parts in another order (an attached property's read), so neither is sorted.</summary>
    private readonly int[] written;
    private readonly int[] writtenEnd;

    private MauiExpressionCode(string code, int[] written, int[] writtenEnd)
    {
        Code = code;
        this.written = written;
        this.writtenEnd = writtenEnd;
        Start = written.Min();
    }

    public string Code { get; }

    /// <summary>Where in the value the code starts: the first place any of it is written.</summary>
    public int Start { get; }

    /// <summary>Where in the value the code ends.</summary>
    public int End => written[^1];

    /// <summary>Where in the value a place of the code is written.</summary>
    public int WrittenAt(int code) => written[Math.Clamp(code, 0, written.Length - 1)];

    /// <summary>Where in the value a stretch of the code is written: from the first place any of its characters stands for
    /// to the end of the last, however the code orders them; an empty stretch at its place.</summary>
    public (int Start, int End) WrittenOver(int start, int end)
    {
        start = Math.Clamp(start, 0, written.Length - 1);
        end = Math.Clamp(end, start, written.Length - 1);
        if (end == start)
        {
            return (written[start], written[start]);
        }
        (int first, int last) = (written[start], writtenEnd[start]);
        for (int k = start + 1; k < end; k++)
        {
            (first, last) = (Math.Min(first, written[k]), Math.Max(last, writtenEnd[k]));
        }
        return (first, last);
    }

    /// <summary>The place in the code a place in the value comes to: before the character written there, or after the one
    /// written just before it; where the code takes those two apart, beside the one that is a name's, as a caret at a
    /// name's edge is in the name. Else before the character written first after it, else the code's end.</summary>
    public int CodeAt(int value)
    {
        // Looked through in turn: the code's places are in the value's order only where it reorders nothing.
        int at = -1;
        int after = -1;
        int next = -1;
        for (int c = 0; c < written.Length - 1; c++)
        {
            if (written[c] == value && at < 0)
            {
                at = c;
            }
            else if (writtenEnd[c] == value && written[c] < value)
            {
                after = c;
            }
            if (written[c] > value && (next < 0 || written[c] < written[next]))
            {
                next = c;
            }
        }
        if (at >= 0 && (after < 0 || after + 1 == at || SyntaxFacts.IsIdentifierPartCharacter(Code[at]) || !SyntaxFacts.IsIdentifierPartCharacter(Code[after])))
        {
            return at;
        }
        return after >= 0 ? after + 1 : next >= 0 ? next : Code.Length;
    }

    /// <summary>The code of a value that opens with a brace (a markup extension's or an expression's), as written between
    /// its quotes, a type named by its XAML prefix (<c>local:Helper</c>) written as <paramref name="types"/> names it in C#
    /// (<c>global::App.Helper</c>) and an attached property read from an element (<c>label.(Grid.Row)</c>) as its getter's
    /// call (<c>Grid.GetRow(label)</c>), as the generator writes both; null for any other value, and for <c>{}</c>, which
    /// escapes a brace.</summary>
    public static MauiExpressionCode? Of(string raw, IReadOnlyDictionary<string, string>? types = null)
    {
        Step decoded = Decoded(raw);
        string text = decoded.Text;
        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        if (start >= text.Length || text[start] != '{' || (start + 1 < text.Length && text[start + 1] == '}'))
        {
            return null;
        }
        start++;
        if (start < text.Length && text[start] == '=')
        {
            start++;
        }
        int end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        if (end > start && text[end - 1] == '}')
        {
            end--;
        }
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        Step body = new(text[start..end], [.. Enumerable.Range(start, end - start + 1)], [.. Enumerable.Range(start + 1, end - start), end]);
        Step operators = Operators(body.Text);
        Step ternaries = Ternaries(operators.Text);
        Step quoted = Quoted(ternaries.Text);
        Step prefixed = Prefixed(quoted.Text, types ?? new Dictionary<string, string>());
        Step code = Then(Then(Then(Then(Then(Then(decoded, body), operators), ternaries), quoted), prefixed), Attached(prefixed.Text));
        return new MauiExpressionCode(code.Text, code.From, code.To);
    }

    /// <summary>The types a code names by a XAML prefix (<c>local:Helper</c>), outside its strings, but <c>x:</c>'s, as the
    /// generator finds them to resolve.</summary>
    public static IReadOnlyList<string> PrefixedNames(string code)
    {
        bool[] literal = Literals(code);
        List<string> found = [];
        for (int i = 0; i < code.Length; i++)
        {
            if (!literal[i] && PrefixedAt(code, i) is { } length && !found.Contains(code.Substring(i, length), StringComparer.Ordinal))
            {
                found.Add(code.Substring(i, length));
            }
        }
        return found;
    }

    /// <summary>Where a name being written at a place of the code starts, where MAUI may bind it to the data type: not
    /// after a dot, in a string or past a name's end; null elsewhere, and in code nested too deeply to read.</summary>
    public static int? BoundStart(string code, int at)
    {
        int start = Math.Clamp(at, 0, code.Length);
        while (start > 0 && SyntaxFacts.IsIdentifierPartCharacter(code[start - 1]))
        {
            start--;
        }
        int before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(code[before]))
        {
            before--;
        }
        if ((before >= 0 && code[before] == '.' && before > 0) || CodeBrackets.Expression(code) is not { } parsed || InString(parsed, start))
        {
            return null;
        }
        return start;
    }

    /// <summary>Whether a place of the code is inside a string's text, not an interpolation's hole.</summary>
    private static bool InString(ExpressionSyntax parsed, int at)
    {
        if (at <= 0 || at - 1 >= parsed.FullSpan.End)
        {
            return false;
        }
        SyntaxToken token = parsed.FindToken(at - 1);
        return token.Span.Contains(at - 1) && token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.CharacterLiteralToken;
    }

    /// <summary>How long a prefixed type's name starting at a place is (<c>local:Helper</c>), not after an ASCII letter, digit
    /// or '_'; null for none. Its prefix and its type's name are read as the generator's pattern reads them: an ASCII
    /// letter or '_' (an upper case letter for the type's) starts each, and a <see cref="Word"/> character carries it on.</summary>
    private static int? PrefixedAt(string code, int at)
    {
        if ((at > 0 && (char.IsAsciiLetterOrDigit(code[at - 1]) || code[at - 1] == '_')) || !StartsName(code[at]))
        {
            return null;
        }
        int colon = NameEnd(code, at);
        if (colon + 1 >= code.Length || code[colon] != ':' || !char.IsAsciiLetterUpper(code[colon + 1]) || code[at..colon] is "x" or "global")
        {
            return null;
        }
        return NameEnd(code, colon + 1) - at;
    }

    /// <summary>Whether a name in the generator's patterns starts with a character: an ASCII letter or '_'.</summary>
    private static bool StartsName(char c) => char.IsAsciiLetter(c) || c == '_';

    /// <summary>Whether a character carries a name in the generator's patterns on: one .NET's regular expressions read as a
    /// word's (<c>\w</c>), a letter, a non-spacing mark, a decimal digit or connector punctuation.</summary>
    private static bool Word(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;

    /// <summary>Where the name whose first character stands at a place ends, its <see cref="Word"/> characters read on.</summary>
    private static int NameEnd(string text, int at)
    {
        int end = at + 1;
        while (end < text.Length && Word(text[end]))
        {
            end++;
        }
        return end;
    }

    /// <summary>Each prefixed type's name a map names written as the map names it in C#.</summary>
    private static Step Prefixed(string text, IReadOnlyDictionary<string, string> types)
    {
        Made made = new();
        bool[] literal = Literals(text);
        int i = 0;
        while (i < text.Length)
        {
            if (!literal[i] && PrefixedAt(text, i) is { } length && types.TryGetValue(text.Substring(i, length), out string? named))
            {
                // The type's own name keeps its place; what stands for the prefix is the prefix's.
                int colon = text.IndexOf(':', i);
                int own = named.LastIndexOf('.') + 1;
                made.Put(named[..own], i, colon + 1 - i);
                for (int k = colon + 1; k < i + length; k++)
                {
                    made.Put(text[k], k);
                }
                i += length;
                continue;
            }
            made.Put(text[i], i);
            i++;
        }
        return made.Ended(text.Length);
    }

    /// <summary>Each attached property read from an element (<c>label.(Grid.Row)</c>) as its getter's call
    /// (<c>Grid.GetRow(label)</c>), outside strings.</summary>
    private static Step Attached(string text)
    {
        Made made = new();
        bool[] literal = Literals(text);
        int i = 0;
        while (i < text.Length)
        {
            if (!literal[i] && AttachedAt(text, i) is { } read)
            {
                for (int k = read.Type; k < read.Dot; k++)
                {
                    made.Put(text[k], k);
                }
                made.Put(".Get", read.Dot, 1);
                for (int k = read.Dot + 1; k < read.Close; k++)
                {
                    made.Put(text[k], k);
                }
                made.Put('(', read.Open);
                for (int k = i; k < read.Open - 1; k++)
                {
                    made.Put(text[k], k);
                }
                made.Put(')', read.Close);
                i = read.Close + 1;
                continue;
            }
            made.Put(text[i], i);
            i++;
        }
        return made.Ended(text.Length);
    }

    /// <summary>An attached property read from an element starting at a place, not after a <see cref="Word"/> character or a
    /// dot: where its type's name starts, the dot before the property's, and its parentheses; null for none. Its names are
    /// read as the generator's pattern reads them (<see cref="PrefixedAt"/>), and the property's as a name of letters, digits
    /// and '_' alone, as the generator splits one off.</summary>
    private static (int Type, int Dot, int Open, int Close)? AttachedAt(string text, int at)
    {
        if ((at > 0 && (Word(text[at - 1]) || text[at - 1] == '.')) || !StartsName(text[at]))
        {
            return null;
        }
        int end = NameEnd(text, at);
        if (end + 2 >= text.Length || text[end] != '.' || text[end + 1] != '(')
        {
            return null;
        }
        int open = end + 1;
        int type = open + 1;
        int i = text.AsSpan(type).StartsWith("global::") ? type + "global::".Length : type;
        int dot = -1;
        while (true)
        {
            if (i >= text.Length || !StartsName(text[i]))
            {
                return null;
            }
            int name = i;
            i = NameEnd(text, i);
            if (i >= text.Length)
            {
                return null;
            }
            if (text[i] == ')')
            {
                return dot > 0 && Plain(text.AsSpan(name, i - name)) ? (type, dot, open, i) : null;
            }
            if (text[i] != '.')
            {
                return null;
            }
            dot = i;
            i++;
        }
    }

    /// <summary>Whether a name is of letters, digits and '_' alone.</summary>
    private static bool Plain(ReadOnlySpan<char> name)
    {
        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Which characters of a code are a string's or a character's text, not code: an interpolation's holes are code.</summary>
    private static bool[] Literals(string code)
    {
        bool[] literal = new bool[code.Length];
        int i = 0;
        while (i < code.Length)
        {
            if (code[i] is '"' or '\'')
            {
                bool interpolated = i > 0 && (code[i - 1] == '$' || (code[i - 1] == '@' && i > 1 && code[i - 2] == '$'));
                i = Literal(code, i, interpolated, literal);
                continue;
            }
            i++;
        }
        return literal;
    }

    /// <summary>Marks a string opened at a place as text, its holes left code, and says where it ends.</summary>
    private static int Literal(string code, int open, bool interpolated, bool[] literal)
    {
        char quote = code[open];
        literal[open] = true;
        int i = open + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\' && i + 1 < code.Length)
            {
                literal[i] = literal[i + 1] = true;
                i += 2;
                continue;
            }
            if (interpolated && code[i] == '{' && !(i + 1 < code.Length && code[i + 1] == '{'))
            {
                literal[i] = true;
                int end = HoleEnd(code, i + 1, code.Length);
                if (end < code.Length)
                {
                    literal[end] = true;
                }
                i = end + 1;
                continue;
            }
            literal[i] = true;
            if (code[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return i;
    }

    /// <summary>A step's text, and for each of its characters, and its end, where what it stands for starts and ends in the
    /// text before it.</summary>
    private sealed record Step(string Text, int[] From, int[] To);

    /// <summary>The second step's text placed in what the first came from.</summary>
    private static Step Then(Step first, Step second)
    {
        int last = first.From.Length - 1;
        int[] from = new int[second.From.Length];
        int[] to = new int[second.From.Length];
        for (int i = 0; i < from.Length; i++)
        {
            int start = Math.Clamp(second.From[i], 0, last);
            from[i] = first.From[start];
            to[i] = from[i];
            for (int k = start; k < Math.Min(second.To[i], last); k++)
            {
                to[i] = Math.Max(to[i], first.To[k]);
            }
        }
        return new Step(second.Text, from, to);
    }

    /// <summary>A text being made, each character with where what it stands for starts and ends in the text before.</summary>
    private sealed class Made
    {
        private readonly System.Text.StringBuilder text = new();
        private readonly List<int> from = [];
        private readonly List<int> to = [];

        /// <summary>Text standing for the <paramref name="length"/> characters at <paramref name="at"/>.</summary>
        public void Put(string put, int at, int length)
        {
            text.Append(put);
            for (int i = 0; i < put.Length; i++)
            {
                from.Add(at);
                to.Add(at + length);
            }
        }

        public void Put(char put, int at)
        {
            text.Append(put);
            from.Add(at);
            to.Add(at + 1);
        }

        /// <summary>A character standing for nothing written, placed at <paramref name="at"/>.</summary>
        public void Inserted(char put, int at)
        {
            text.Append(put);
            from.Add(at);
            to.Add(at);
        }

        public Step Ended(int end) => new(text.ToString(), [.. from, end], [.. to, end]);
    }

    /// <summary>The value with its character references read.</summary>
    private static Step Decoded(string raw)
    {
        Made made = new();
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '&' && XmlEntities.ReferenceAt(raw, i, out int length) is { } named)
            {
                made.Put(named, i, length);
                i += length;
                continue;
            }
            made.Put(raw[i], i);
            i++;
        }
        return made.Ended(raw.Length);
    }

    /// <summary>The operator words between spaces as C#'s operators, outside strings.</summary>
    private static Step Operators(string text)
    {
        Made made = new();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                (int close, _) = Closing(text, i, text.Length, c);
                for (int k = i; k < close; k++)
                {
                    made.Put(text[k], k);
                }
                i = close;
                continue;
            }
            if (i > 0 && text[i - 1] == ' ' && WordAt(text, i) is { } word)
            {
                made.Put(word.Operator, i, word.Word.Length);
                i += word.Word.Length;
                continue;
            }
            made.Put(c, i);
            i++;
        }
        return made.Ended(text.Length);
    }

    /// <summary>The operator word at a place after a space, followed by one; null for none.</summary>
    private static (string Word, string Operator)? WordAt(string text, int at)
    {
        foreach ((string Word, string Operator) word in Words)
        {
            int after = at + word.Word.Length;
            if (after < text.Length && text[after] == ' ' && string.Compare(text, at, word.Word, 0, word.Word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return word;
            }
        }
        return null;
    }

    /// <summary>Each interpolation's hole whose expression is a conditional at its top put in parentheses, which C# would
    /// otherwise read the conditional's ':' in as a format's start.</summary>
    private static Step Ternaries(string text)
    {
        Made made = new();
        Interpolations(text, 0, text.Length, made);
        return made.Ended(text.Length);
    }

    private static void Interpolations(string text, int from, int to, Made made)
    {
        int i = from;
        while (i < to)
        {
            char c = text[i];
            if (c == '$' && i + 1 < to && text[i + 1] is '\'' or '"')
            {
                char delimiter = text[i + 1];
                made.Put(c, i);
                made.Put(delimiter, i + 1);
                i += 2;
                while (i < to)
                {
                    char at = text[i];
                    if (at == '\\' && i + 1 < to)
                    {
                        made.Put(at, i);
                        made.Put(text[i + 1], i + 1);
                        i += 2;
                        continue;
                    }
                    if (at == delimiter)
                    {
                        made.Put(at, i);
                        i++;
                        break;
                    }
                    if ((at == '{' || at == '}') && i + 1 < to && text[i + 1] == at)
                    {
                        made.Put(at, i);
                        made.Put(at, i + 1);
                        i += 2;
                        continue;
                    }
                    if (at == '{')
                    {
                        int hole = i + 1;
                        int end = HoleEnd(text, hole, to);
                        made.Put('{', i);
                        Hole(text, hole, end, made);
                        if (end < to && text[end] == '}')
                        {
                            made.Put('}', end);
                            i = end + 1;
                        }
                        else
                        {
                            i = end;
                        }
                        continue;
                    }
                    made.Put(at, i);
                    i++;
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                (int close, _) = Closing(text, i, to, c);
                for (int k = i; k < close; k++)
                {
                    made.Put(text[k], k);
                }
                i = close;
                continue;
            }
            made.Put(c, i);
            i++;
        }
    }

    /// <summary>A hole's expression, its own interpolations read in turn, in parentheses where it is a conditional at its top.</summary>
    private static void Hole(string text, int from, int to, Made made)
    {
        int first = from;
        while (first < to && char.IsWhiteSpace(text[first]))
        {
            first++;
        }
        int last = to;
        while (last > first && char.IsWhiteSpace(text[last - 1]))
        {
            last--;
        }
        bool wrapped = TopConditional(text, first, last);
        for (int k = from; k < first; k++)
        {
            made.Put(text[k], k);
        }
        if (wrapped)
        {
            made.Inserted('(', first);
        }
        Interpolations(text, first, last, made);
        if (wrapped)
        {
            made.Inserted(')', last);
        }
        for (int k = last; k < to; k++)
        {
            made.Put(text[k], k);
        }
    }

    /// <summary>Where a hole opened at <paramref name="from"/> ends: its closing brace, past nested ones and strings.</summary>
    private static int HoleEnd(string text, int from, int to)
    {
        int depth = 1;
        int i = from;
        while (i < to)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < to)
            {
                i += 2;
                continue;
            }
            if (c is '\'' or '"')
            {
                i = Closing(text, i, to, c).After;
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
            i++;
        }
        return to;
    }

    /// <summary>Whether an expression holds a conditional's '?' outside its brackets and strings (not '??', '?.' or '?[').</summary>
    private static bool TopConditional(string text, int from, int to)
    {
        int depth = 0;
        int i = from;
        while (i < to)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < to)
            {
                i += 2;
                continue;
            }
            if (c is '\'' or '"')
            {
                i = Closing(text, i, to, c).After;
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == '?' && depth == 0)
            {
                char next = i + 1 < to ? text[i + 1] : '\0';
                if (next == '?')
                {
                    i += 2;
                    continue;
                }
                if (next is not '.' and not '[')
                {
                    return true;
                }
            }
            i++;
        }
        return false;
    }

    /// <summary>Each single-quoted string as a double-quoted one: an escaped single quote unescaped, a bare double quote
    /// escaped, every other escape kept; an unclosed one left as it stands, as the generator leaves it.</summary>
    private static Step Quoted(string text)
    {
        Made made = new();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                (int after, _) = Closing(text, i, text.Length, '"');
                for (int k = i; k < after; k++)
                {
                    made.Put(text[k], k);
                }
                i = after;
                continue;
            }
            if (c != '\'')
            {
                made.Put(c, i);
                i++;
                continue;
            }
            (int close, bool closed) = Closing(text, i, text.Length, '\'');
            if (!closed)
            {
                for (int k = i; k < close; k++)
                {
                    made.Put(text[k], k);
                }
                i = close;
                continue;
            }
            made.Put('"', i);
            int at = i + 1;
            while (at < close - 1)
            {
                if (text[at] == '\\' && at + 1 < close - 1)
                {
                    made.Put(text[at + 1] == '\'' ? "'" : text.Substring(at, 2), at, 2);
                    at += 2;
                    continue;
                }
                made.Put(text[at] == '"' ? "\\\"" : text[at].ToString(), at, 1);
                at++;
            }
            made.Put('"', close - 1);
            i = close;
        }
        return made.Ended(text.Length);
    }

    /// <summary>Just past the quote that closes a string opened at <paramref name="open"/>, its escapes passed over, and
    /// whether one closes it; the end where none does.</summary>
    private static (int After, bool Closed) Closing(string text, int open, int end, char quote)
    {
        int i = open + 1;
        while (i < end)
        {
            if (text[i] == '\\' && i + 1 < end)
            {
                i += 2;
                continue;
            }
            if (text[i] == quote)
            {
                return (i + 1, true);
            }
            i++;
        }
        return (end, false);
    }
}

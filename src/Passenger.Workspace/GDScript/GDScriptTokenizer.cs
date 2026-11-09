using System.Globalization;
using System.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>GDScript's tokens read from a text as Godot's own tokenizer reads them (a port of
/// modules/gdscript/gdscript_tokenizer.cpp, MIT): indentation as Indent and Dedent tokens, a newline token per logical
/// line, none inside brackets once the parser says so, and each error as an Error token with Godot's words. Positions
/// are offsets into the text. It may begin at any clean line start, given the indentation character the file had chosen
/// by then.</summary>
public sealed class GDScriptTokenizer
{
    private const int TabSize = 4;

    private readonly string text;
    private readonly int length;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> names = new HashSet<string>(StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
    private readonly List<GDScriptToken> errors = [];
    private readonly List<int> parens = [];
    private readonly List<List<int>> indentsStack = [];
    private List<int> indents = [];
    private int position;
    private int lineStart;
    private int start;
    private bool lineContinuation;
    private bool multiline;
    private bool pendingNewline;
    private GDScriptToken lastToken;
    private GDScriptToken lastNewline;
    private int pendingIndents;

    public GDScriptTokenizer(string text, int from = 0, char indentChar = '\0')
    {
        this.text = text;
        length = text.Length;
        position = from;
        lineStart = from;
        start = from;
        IndentChar = indentChar;
    }

    /// <summary>The character the file indents with, chosen at its first indented line; '\0' before.</summary>
    public char IndentChar { get; private set; }

    /// <summary>Whether it stands at a line's start with nothing pending: no bracket open, no indentation, not inside an
    /// expression's lines or a continued line, so a scan from here reads as a fresh tokenizer would.</summary>
    public bool Clean => parens.Count == 0 && indents.Count == 0 && pendingIndents == 0 && !multiline && !lineContinuation && errors.Count == 0
        && indentsStack.Count == 0 && !pendingNewline;

    public bool Multiline
    {
        get => multiline;
        set => multiline = value;
    }

    /// <summary>Keeps the indentation as it stands for a lambda's body inside an expression.</summary>
    public void PushExpressionIndentedBlock() => indentsStack.Add([.. indents]);

    public void PopExpressionIndentedBlock()
    {
        if (indentsStack.Count > 0)
        {
            indents = indentsStack[^1];
            indentsStack.RemoveAt(indentsStack.Count - 1);
        }
    }

    private bool AtEnd => position >= length;

    private char Peek(int offset = 0) => position + offset >= 0 && position + offset < length ? text[position + offset] : '\0';

    private char Advance()
    {
        if (AtEnd)
        {
            return '\0';
        }
        position++;
        if (AtEnd)
        {
            // An extra newline and the dedents the parser expects at the end, as if the text ended with one.
            Newline(true);
            CheckIndent();
        }
        return text[position - 1];
    }

    private GDScriptToken Make(GDScriptTokenKind kind, object? value = null, GDScriptLiteralKind literal = GDScriptLiteralKind.None)
    {
        GDScriptToken token = new(kind, start, position, value, literal);
        lastToken = token;
        return token;
    }

    private GDScriptToken MakeError(string message) => Make(GDScriptTokenKind.Error, message);

    private GDScriptToken ErrorAt(string message, int from, int to) => new(GDScriptTokenKind.Error, from, to, message);

    private void PushError(string message) => errors.Add(MakeError(message));

    private void PushError(GDScriptToken error) => errors.Add(error);

    private GDScriptToken PopError()
    {
        GDScriptToken error = errors[^1];
        errors.RemoveAt(errors.Count - 1);
        return error;
    }

    private bool PopParen(char expected)
    {
        if (parens.Count == 0)
        {
            return false;
        }
        char actual = (char)parens[^1];
        parens.RemoveAt(parens.Count - 1);
        return actual == expected;
    }

    private GDScriptToken ParenError(char paren)
    {
        if (parens.Count == 0)
        {
            return MakeError($"Closing \"{paren}\" doesn't have an opening counterpart.");
        }
        GDScriptToken error = MakeError($"Closing \"{paren}\" doesn't match the opening \"{(char)parens[^1]}\".");
        parens.RemoveAt(parens.Count - 1);
        return error;
    }

    private GDScriptToken VcsMarker(char test, GDScriptTokenKind doubled)
    {
        int chars = 2;
        while (Peek(chars - 1) == test)
        {
            chars++;
        }
        if (chars >= 7)
        {
            for (; chars > 1; chars--)
            {
                Advance();
            }
            return Make(GDScriptTokenKind.VcsConflictMarker);
        }
        Advance();
        return Make(doubled);
    }

    private void Newline(bool makeToken)
    {
        if (makeToken && !pendingNewline && !lineContinuation)
        {
            GDScriptToken newline = new(GDScriptTokenKind.Newline, position - 1, position);
            pendingNewline = true;
            lastToken = newline;
            lastNewline = newline;
        }
        lineStart = position;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsHexDigit(char c) => IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsBinaryDigit(char c) => c is '0' or '1';

    /// <summary>How many characters an identifier's first character takes at an offset: 0 where none starts there, 2 for
    /// one outside the Basic Multilingual Plane.</summary>
    private int IdentifierStart(int at) => IdentifierChar(at, start: true);

    private int IdentifierContinue(int at) => IdentifierChar(at, start: false);

    private int IdentifierChar(int at, bool start)
    {
        if (at >= length)
        {
            return 0;
        }
        char c = text[at];
        if (c < 128)
        {
            return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || !start && IsDigit(c) ? 1 : 0;
        }
        int width = char.IsHighSurrogate(c) && at + 1 < length && char.IsLowSurrogate(text[at + 1]) ? 2 : 1;
        UnicodeCategory category = width == 2 ? CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[at + 1])) : CharUnicodeInfo.GetUnicodeCategory(c);
        bool letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
        bool continuing = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation;
        return letter || !start && continuing ? width : 0;
    }

    private void AdvanceBy(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Advance();
        }
    }

    private GDScriptToken Annotation()
    {
        if (IdentifierStart(position) is > 0 and int first)
        {
            AdvanceBy(first);
        }
        else
        {
            PushError("Expected annotation identifier after \"@\".");
        }
        for (int width; (width = IdentifierContinue(position)) > 0;)
        {
            AdvanceBy(width);
        }
        return Make(GDScriptTokenKind.Annotation, Name(start, position));
    }

    private string Name(int from, int to) => names.TryGetValue(text.AsSpan(from, to - from), out string? known) ? known : Add(text[from..to]);

    private string Add(string name)
    {
        names.Add(name);
        return name;
    }

    private GDScriptToken PotentialIdentifier()
    {
        bool ascii = text[position - 1] < 128;
        for (int width; (width = IdentifierContinue(position)) > 0;)
        {
            ascii = ascii && width == 1 && text[position] < 128;
            AdvanceBy(width);
        }
        int count = position - start;
        if (count == 1 && text[start] == '_')
        {
            return Make(GDScriptTokenKind.Underscore, "_");
        }
        string name = Name(start, position);
        if (!ascii || count < 2 || count > 10)
        {
            return Make(GDScriptTokenKind.Identifier, name);
        }
        GDScriptTokenKind kind = Keyword(name);
        if (kind != GDScriptTokenKind.Identifier)
        {
            return Make(kind, name);
        }
        return name switch
        {
            "true" => Make(GDScriptTokenKind.Literal, true, GDScriptLiteralKind.Bool),
            "false" => Make(GDScriptTokenKind.Literal, false, GDScriptLiteralKind.Bool),
            "null" => Make(GDScriptTokenKind.Literal, null, GDScriptLiteralKind.Null),
            _ => Make(GDScriptTokenKind.Identifier, name),
        };
    }

    private static GDScriptTokenKind Keyword(string name) => name switch
    {
        "as" => GDScriptTokenKind.As,
        "and" => GDScriptTokenKind.And,
        "assert" => GDScriptTokenKind.Assert,
        "await" => GDScriptTokenKind.Await,
        "break" => GDScriptTokenKind.Break,
        "breakpoint" => GDScriptTokenKind.Breakpoint,
        "class" => GDScriptTokenKind.Class,
        "class_name" => GDScriptTokenKind.ClassName,
        "const" => GDScriptTokenKind.Const,
        "continue" => GDScriptTokenKind.Continue,
        "elif" => GDScriptTokenKind.Elif,
        "else" => GDScriptTokenKind.Else,
        "enum" => GDScriptTokenKind.Enum,
        "extends" => GDScriptTokenKind.Extends,
        "for" => GDScriptTokenKind.For,
        "func" => GDScriptTokenKind.Func,
        "if" => GDScriptTokenKind.If,
        "in" => GDScriptTokenKind.In,
        "is" => GDScriptTokenKind.Is,
        "match" => GDScriptTokenKind.Match,
        "namespace" => GDScriptTokenKind.Namespace,
        "not" => GDScriptTokenKind.Not,
        "or" => GDScriptTokenKind.Or,
        "pass" => GDScriptTokenKind.Pass,
        "preload" => GDScriptTokenKind.Preload,
        "return" => GDScriptTokenKind.Return,
        "self" => GDScriptTokenKind.Self,
        "signal" => GDScriptTokenKind.Signal,
        "static" => GDScriptTokenKind.Static,
        "super" => GDScriptTokenKind.Super,
        "trait" => GDScriptTokenKind.Trait,
        "var" => GDScriptTokenKind.Var,
        "void" => GDScriptTokenKind.Void,
        "while" => GDScriptTokenKind.While,
        "when" => GDScriptTokenKind.When,
        "yield" => GDScriptTokenKind.Yield,
        "INF" => GDScriptTokenKind.ConstInf,
        "NAN" => GDScriptTokenKind.ConstNan,
        "PI" => GDScriptTokenKind.ConstPi,
        "TAU" => GDScriptTokenKind.ConstTau,
        _ => GDScriptTokenKind.Identifier,
    };

    private GDScriptToken Number()
    {
        int numberBase = 10;
        bool decimalPoint = false;
        bool exponent = false;
        bool failed = false;
        bool needDigits = false;
        Func<char, bool> digit = IsDigit;
        if (Peek(-1) is '+' or '-' && Peek() == '0')
        {
            Advance();
        }
        if (Peek(-1) == '.')
        {
            decimalPoint = true;
        }
        else if (Peek(-1) == '0')
        {
            if (Peek() is 'x' or 'X')
            {
                numberBase = 16;
                digit = IsHexDigit;
                needDigits = true;
                Advance();
            }
            else if (Peek() is 'b' or 'B')
            {
                numberBase = 2;
                digit = IsBinaryDigit;
                needDigits = true;
                Advance();
            }
        }
        if (numberBase != 10 && Peek() == '_')
        {
            PushError(ErrorAt($"Unexpected underscore after \"0{Peek(-1)}\".", position, position + 1));
            failed = true;
        }
        bool underscore = false;
        while (digit(Peek()) || Peek() == '_')
        {
            if (Peek() == '_')
            {
                if (underscore)
                {
                    PushError(ErrorAt("Multiple underscores cannot be adjacent in a numeric literal.", position, position + 1));
                }
                underscore = true;
            }
            else
            {
                needDigits = false;
                underscore = false;
            }
            Advance();
        }
        if (Peek() == '.' && Peek(1) != '.')
        {
            if (numberBase == 10 && !decimalPoint)
            {
                decimalPoint = true;
            }
            else
            {
                PushError(ErrorAt(numberBase switch
                {
                    10 => "Cannot use a decimal point twice in a number.",
                    16 => "Cannot use a decimal point in a hexadecimal number.",
                    _ => "Cannot use a decimal point in a binary number.",
                }, position, position + 1));
                failed = true;
            }
            if (!failed)
            {
                Advance();
                if (Peek() == '_')
                {
                    PushError(ErrorAt("Unexpected underscore after decimal point.", position, position + 1));
                    failed = true;
                }
                Digits();
            }
        }
        if (numberBase == 10 && Peek() is 'e' or 'E')
        {
            exponent = true;
            Advance();
            if (Peek() is '+' or '-')
            {
                Advance();
            }
            if (!IsDigit(Peek()))
            {
                PushError(ErrorAt("Expected exponent value after \"e\".", position, position + 1));
            }
            Digits();
        }
        if (needDigits)
        {
            return ErrorAt($"Expected {(numberBase == 16 ? "hexadecimal" : "binary")} digit after \"0{(numberBase == 16 ? 'x' : 'b')}\".", position, position + 1);
        }
        if (!failed && decimalPoint && Peek() == '.' && Peek(1) != '.')
        {
            PushError(ErrorAt("Cannot use a decimal point twice in a number.", position, position + 1));
        }
        else if (IdentifierContinue(position) > 0)
        {
            PushError("Invalid numeric notation.");
        }
        string number = text[start..position].Replace("_", string.Empty, StringComparison.Ordinal);
        if (numberBase != 10)
        {
            return Make(GDScriptTokenKind.Literal, Based(number, numberBase), GDScriptLiteralKind.Int);
        }
        if (decimalPoint || exponent)
        {
            return Make(GDScriptTokenKind.Literal, double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) ? real : 0.0,
                GDScriptLiteralKind.Float);
        }
        return Make(GDScriptTokenKind.Literal, long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole) ? whole : long.MaxValue,
            GDScriptLiteralKind.Int);

        void Digits()
        {
            bool previous = false;
            while (IsDigit(Peek()) || Peek() == '_')
            {
                if (Peek() == '_')
                {
                    if (previous)
                    {
                        PushError(ErrorAt("Multiple underscores cannot be adjacent in a numeric literal.", position, position + 1));
                    }
                    previous = true;
                }
                else
                {
                    previous = false;
                }
                Advance();
            }
        }
    }

    private static long Based(string number, int numberBase)
    {
        bool negative = number.StartsWith('-');
        ulong value = 0;
        foreach (char c in number.AsSpan(number.IndexOfAny(['x', 'X', 'b', 'B']) + 1))
        {
            value = unchecked(value * (ulong)numberBase + (ulong)(IsDigit(c) ? c - '0' : (c | 0x20) - 'a' + 10));
        }
        return negative ? -(long)value : (long)value;
    }

    private GDScriptToken String()
    {
        bool raw = false;
        GDScriptLiteralKind kind = GDScriptLiteralKind.String;
        switch (Peek(-1))
        {
            case 'r':
                raw = true;
                Advance();
                break;
            case '&':
                kind = GDScriptLiteralKind.StringName;
                Advance();
                break;
            case '^':
                kind = GDScriptLiteralKind.NodePath;
                Advance();
                break;
            default:
                break;
        }
        char quote = Peek(-1);
        bool multilineString = false;
        if (Peek() == quote && Peek(1) == quote)
        {
            multilineString = true;
            Advance();
            Advance();
        }
        StringBuilder result = new();
        int lead = 0;
        int leadAt = 0;
        while (true)
        {
            if (AtEnd)
            {
                return MakeError("Unterminated string.");
            }
            char ch = Peek();
            if (ch is '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
            {
                PushError(ErrorAt(raw ? "Invisible text direction control character present in the string, use regular string literal instead of r-string."
                    : $"Invisible text direction control character present in the string, escape it (\"\\u{(int)ch:x}\") to avoid confusion.", position, position + 1));
            }
            if (ch == '\\')
            {
                Advance();
                if (AtEnd)
                {
                    return MakeError("Unterminated string.");
                }
                if (raw)
                {
                    if (Peek() == quote || Peek() == '\\')
                    {
                        char escapedRaw = Peek();
                        Advance();
                        if (AtEnd)
                        {
                            return MakeError("Unterminated string.");
                        }
                        result.Append('\\').Append(escapedRaw);
                    }
                    else
                    {
                        result.Append('\\');
                    }
                    continue;
                }
                char code = Peek();
                Advance();
                if (AtEnd)
                {
                    return MakeError("Unterminated string.");
                }
                int escaped = 0;
                bool valid = true;
                switch (code)
                {
                    case 'U' or 'u':
                        for (int j = 0, digits = code == 'U' ? 6 : 4; j < digits; j++)
                        {
                            if (AtEnd)
                            {
                                return MakeError("Unterminated string.");
                            }
                            char hex = Peek();
                            if (!IsHexDigit(hex))
                            {
                                PushError(ErrorAt("Invalid hexadecimal digit in unicode escape sequence.", position, position + 1));
                                valid = false;
                                break;
                            }
                            escaped = (escaped << 4) | (IsDigit(hex) ? hex - '0' : (hex | 0x20) - 'a' + 10);
                            Advance();
                        }
                        break;
                    case '\r' when Peek() != '\n':
                        result.Append(ch);
                        Advance();
                        valid = false;
                        break;
                    case '\r' or '\n':
                        if (code == '\n')
                        {
                            Newline(false);
                        }
                        valid = false;
                        break;
                    default:
                        escaped = Escaped(code);
                        if (escaped < 0)
                        {
                            PushError(ErrorAt("Invalid escape in string.", position - 2, position));
                            valid = false;
                        }
                        break;
                }
                if (valid)
                {
                    if ((escaped & 0xfffffc00) == 0xd800)
                    {
                        if (lead == 0)
                        {
                            lead = escaped;
                            leadAt = position - 2;
                            continue;
                        }
                        PushError(ErrorAt("Invalid UTF-16 sequence in string, unpaired lead surrogate.", position - 2, position));
                        valid = false;
                        lead = 0;
                    }
                    else if ((escaped & 0xfffffc00) == 0xdc00)
                    {
                        if (lead == 0)
                        {
                            PushError(ErrorAt("Invalid UTF-16 sequence in string, unpaired trail surrogate.", position - 2, position));
                            valid = false;
                        }
                        else
                        {
                            escaped = (lead << 10) + escaped - ((0xd800 << 10) + 0xdc00 - 0x10000);
                            lead = 0;
                        }
                    }
                    if (lead != 0)
                    {
                        PushError(ErrorAt("Invalid UTF-16 sequence in string, unpaired lead surrogate.", leadAt, leadAt + 2));
                        lead = 0;
                    }
                }
                if (valid)
                {
                    if (escaped is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                    {
                        result.Append(char.ConvertFromUtf32(escaped));
                    }
                }
            }
            else if (ch == quote)
            {
                UnpairedLead();
                Advance();
                if (!multilineString)
                {
                    break;
                }
                if (Peek() == quote && Peek(1) == quote)
                {
                    Advance();
                    Advance();
                    break;
                }
                result.Append(quote);
            }
            else
            {
                UnpairedLead();
                result.Append(ch);
                Advance();
                if (ch == '\n')
                {
                    Newline(false);
                }
            }
        }
        UnpairedLead();
        return Make(GDScriptTokenKind.Literal, result.ToString(), kind);

        void UnpairedLead()
        {
            if (lead != 0)
            {
                PushError(ErrorAt("Invalid UTF-16 sequence in string, unpaired lead surrogate", leadAt, leadAt + 2));
                lead = 0;
            }
        }
    }

    /// <summary>The character an escape of one letter after its backslash stands for; -1 for none.</summary>
    private static int Escaped(char code) => code switch
    {
        'a' => '\a',
        'b' => '\b',
        'f' => '\f',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        'v' => '\v',
        '\'' or '"' or '\\' => code,
        _ => -1,
    };

    /// <summary>A string from its opening quote (single, double or tripled, after any <c>r</c>, <c>&amp;</c> or <c>^</c>): what
    /// it says, its escapes read as <see cref="String"/> reads them, those it refuses left out; where its characters start and
    /// end, and where the code goes on. One left open ends at its line's end, a tripled one at the text's.</summary>
    public static (string Said, int Start, int End, int Next) ReadString(string text, int quote, bool raw)
    {
        StringBuilder said = new();
        (int start, int end, int next) = ReadString(text, quote, raw, said);
        return (said.ToString(), start, end, next);
    }

    /// <summary>Where a string's characters start and end and where the code goes on, read as <see cref="ReadString(string, int, bool)"/>
    /// reads them, without what it says.</summary>
    public static (int Start, int End, int Next) StringBounds(string text, int quote, bool raw) => ReadString(text, quote, raw, null);

    private static (int Start, int End, int Next) ReadString(string text, int quote, bool raw, StringBuilder? said)
    {
        char mark = text[quote];
        bool tripled = quote + 2 < text.Length && text[quote + 1] == mark && text[quote + 2] == mark;
        int start = quote + (tripled ? 3 : 1);
        int lead = 0;
        int at = start;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '\\' && at + 1 < text.Length)
            {
                char code = text[at + 1];
                if (raw)
                {
                    // A raw string keeps its backslash; one before its quote or another backslash takes that one too.
                    int kept = code == mark || code == '\\' ? 2 : 1;
                    said?.Append(text.AsSpan(at, kept));
                    at += kept;
                    continue;
                }
                at += 2;
                int escaped = Escaped(code);
                if (code is 'u' or 'U')
                {
                    escaped = 0;
                    for (int digits = code == 'U' ? 6 : 4; digits > 0 && escaped >= 0; digits--, at++)
                    {
                        escaped = at < text.Length && IsHexDigit(text[at]) ? (escaped << 4) | (IsDigit(text[at]) ? text[at] - '0' : (text[at] | 0x20) - 'a' + 10) : -1;
                    }
                    at -= escaped < 0 ? 1 : 0;
                }
                else if (code == '\r')
                {
                    // Before a lone carriage return the backslash stays and what follows goes; before a line break the break stays.
                    said?.Append(at >= text.Length || text[at] != '\n' ? '\\' : '\n');
                    at++;
                }
                if (escaped is >= 0xD800 and <= 0xDBFF)
                {
                    lead = escaped;
                    continue;
                }
                if (escaped is >= 0xDC00 and <= 0xDFFF && lead != 0)
                {
                    escaped = char.ConvertToUtf32((char)lead, (char)escaped);
                }
                lead = 0;
                if (escaped is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                {
                    said?.Append(char.ConvertFromUtf32(escaped));
                }
                continue;
            }
            lead = 0;
            if (c == mark && (!tripled || at + 2 < text.Length && text[at + 1] == mark && text[at + 2] == mark))
            {
                return (start, at, at + (tripled ? 3 : 1));
            }
            if (c == '\n' && !tripled)
            {
                return (start, at, at);
            }
            said?.Append(c);
            at++;
        }
        return (start, at, at);
    }

    private void CheckIndent()
    {
        if (AtEnd)
        {
            pendingIndents -= indents.Count;
            indents.Clear();
            return;
        }
        while (true)
        {
            char indentChar = Peek();
            int count = 0;
            if (indentChar is not (' ' or '\t' or '\r' or '\n' or '#'))
            {
                if (lineContinuation || multiline)
                {
                    return;
                }
                pendingIndents -= indents.Count;
                indents.Clear();
                return;
            }
            if (SkipEmptyLine())
            {
                continue;
            }
            bool mixed = false;
            while (!AtEnd)
            {
                char space = Peek();
                if (space == '\t')
                {
                    count += TabSize;
                }
                else if (space == ' ')
                {
                    count++;
                }
                else
                {
                    break;
                }
                mixed = mixed || space != indentChar;
                Advance();
            }
            if (AtEnd)
            {
                pendingIndents -= indents.Count;
                indents.Clear();
                return;
            }
            if (SkipEmptyLine())
            {
                continue;
            }
            if (Peek() == '#')
            {
                while (Peek() != '\n' && !AtEnd)
                {
                    Advance();
                }
                if (AtEnd)
                {
                    pendingIndents -= indents.Count;
                    indents.Clear();
                    return;
                }
                Advance();
                Newline(false);
                continue;
            }
            if (mixed && !lineContinuation && !multiline)
            {
                PushError(ErrorAt("Mixed use of tabs and spaces for indentation.", lineStart, position));
            }
            if (lineContinuation || multiline)
            {
                return;
            }
            if (IndentChar == '\0')
            {
                IndentChar = indentChar;
            }
            else if (indentChar != IndentChar)
            {
                PushError(ErrorAt($"Used {IndentName(indentChar)} character for indentation instead of {IndentName(IndentChar)} as used before in the file.", lineStart, position));
            }
            int previous = indents.Count > 0 ? indents[^1] : 0;
            if (count == previous)
            {
                return;
            }
            if (count > previous)
            {
                indents.Add(count);
                pendingIndents++;
            }
            else
            {
                if (indents.Count == 0)
                {
                    PushError("Tokenizer bug: trying to dedent without previous indent.");
                    return;
                }
                while (indents.Count > 0 && indents[^1] > count)
                {
                    indents.RemoveAt(indents.Count - 1);
                    pendingIndents--;
                }
                if (indents.Count > 0 && indents[^1] != count || indents.Count == 0 && count != 0)
                {
                    PushError(ErrorAt("Unindent doesn't match the previous indentation level.", lineStart, position + 1));
                    indents.Add(count);
                }
            }
            return;
        }
    }

    /// <summary>Passes over an empty line's break where one stands, saying a stray carriage return.</summary>
    private bool SkipEmptyLine()
    {
        if (Peek() == '\r')
        {
            Advance();
            if (Peek() != '\n')
            {
                PushError("Stray carriage return character in source code.");
            }
        }
        if (Peek() == '\n')
        {
            Advance();
            Newline(false);
            return true;
        }
        return false;
    }

    private static string IndentName(char c) => c == ' ' ? "space" : c == '\t' ? "tab" : $"\\u{(int)c:x4}";

    private void SkipWhitespace()
    {
        if (pendingIndents != 0)
        {
            return;
        }
        bool beginning = position == lineStart;
        if (beginning)
        {
            CheckIndent();
            return;
        }
        while (true)
        {
            switch (Peek())
            {
                case ' ' or '\t':
                    Advance();
                    break;
                case '\r':
                    Advance();
                    if (Peek() != '\n')
                    {
                        PushError("Stray carriage return character in source code.");
                        return;
                    }
                    break;
                case '\n':
                    Advance();
                    Newline(!beginning);
                    CheckIndent();
                    break;
                case '#':
                    while (Peek() != '\n' && !AtEnd)
                    {
                        Advance();
                    }
                    if (AtEnd)
                    {
                        return;
                    }
                    Advance();
                    Newline(!beginning);
                    CheckIndent();
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>The next token; an error found while reading one comes as an Error token on the scans after it.</summary>
    public GDScriptToken Scan()
    {
        while (true)
        {
            if (errors.Count > 0)
            {
                return PopError();
            }
            SkipWhitespace();
            if (pendingNewline)
            {
                pendingNewline = false;
                if (!multiline)
                {
                    return lastNewline;
                }
            }
            if (errors.Count > 0)
            {
                return PopError();
            }
            start = position;
            if (pendingIndents != 0)
            {
                start = lineStart;
                if (pendingIndents > 0)
                {
                    pendingIndents--;
                    return Make(GDScriptTokenKind.Indent);
                }
                pendingIndents++;
                GDScriptToken dedent = Make(GDScriptTokenKind.Dedent);
                return lastToken = dedent with { End = dedent.End + 1 };
            }
            if (AtEnd)
            {
                return Make(GDScriptTokenKind.Eof);
            }
            char c = Advance();
            if (c != '\\')
            {
                lineContinuation = false;
                return Token(c);
            }
            if (Peek() == '\r')
            {
                if (Peek(1) != '\n')
                {
                    return MakeError("Unexpected carriage return character.");
                }
                Advance();
            }
            if (Peek() != '\n')
            {
                return MakeError("Expected new line after \"\\\".");
            }
            Advance();
            Newline(false);
            lineContinuation = true;
            SkipWhitespace();
        }
    }

    private GDScriptToken Token(char c)
    {
        if (IsDigit(c))
        {
            return Number();
        }
        if (c == 'r' && Peek() is '"' or '\'')
        {
            return String();
        }
        if (IdentifierStart(position - 1) is > 0 and int width)
        {
            AdvanceBy(width - 1);
            return PotentialIdentifier();
        }
        switch (c)
        {
            case '"' or '\'':
                return String();
            case '@':
                return Annotation();
            case '~':
                return Make(GDScriptTokenKind.Tilde);
            case ',':
                return Make(GDScriptTokenKind.Comma);
            case ':':
                return Make(GDScriptTokenKind.Colon);
            case ';':
                return Make(GDScriptTokenKind.Semicolon);
            case '$':
                return Make(GDScriptTokenKind.Dollar);
            case '?':
                return Make(GDScriptTokenKind.QuestionMark);
            case '`':
                return Make(GDScriptTokenKind.Backtick);
            case '(':
                parens.Add('(');
                return Make(GDScriptTokenKind.ParenthesisOpen);
            case '[':
                parens.Add('[');
                return Make(GDScriptTokenKind.BracketOpen);
            case '{':
                parens.Add('{');
                return Make(GDScriptTokenKind.BraceOpen);
            case ')':
                return PopParen('(') ? Make(GDScriptTokenKind.ParenthesisClose) : ParenError(c);
            case ']':
                return PopParen('[') ? Make(GDScriptTokenKind.BracketClose) : ParenError(c);
            case '}':
                return PopParen('{') ? Make(GDScriptTokenKind.BraceClose) : ParenError(c);
            case '!':
                return Followed('=', GDScriptTokenKind.BangEqual, GDScriptTokenKind.Bang);
            case '.':
                if (Peek() == '.')
                {
                    Advance();
                    return Followed('.', GDScriptTokenKind.PeriodPeriodPeriod, GDScriptTokenKind.PeriodPeriod);
                }
                return IsDigit(Peek()) ? Number() : Make(GDScriptTokenKind.Period);
            case '+':
                return Peek() == '=' ? Followed('=', GDScriptTokenKind.PlusEqual, GDScriptTokenKind.Plus)
                    : IsDigit(Peek()) && !lastToken.CanPrecedeBinaryOperator ? Number() : Make(GDScriptTokenKind.Plus);
            case '-':
                if (Peek() == '=')
                {
                    Advance();
                    return Make(GDScriptTokenKind.MinusEqual);
                }
                if (IsDigit(Peek()) && !lastToken.CanPrecedeBinaryOperator)
                {
                    return Number();
                }
                return Followed('>', GDScriptTokenKind.ForwardArrow, GDScriptTokenKind.Minus);
            case '*':
                if (Peek() == '=')
                {
                    Advance();
                    return Make(GDScriptTokenKind.StarEqual);
                }
                if (Peek() == '*')
                {
                    Advance();
                    return Followed('=', GDScriptTokenKind.StarStarEqual, GDScriptTokenKind.StarStar);
                }
                return Make(GDScriptTokenKind.Star);
            case '/':
                return Followed('=', GDScriptTokenKind.SlashEqual, GDScriptTokenKind.Slash);
            case '%':
                return Followed('=', GDScriptTokenKind.PercentEqual, GDScriptTokenKind.Percent);
            case '^':
                return Peek() == '=' ? Followed('=', GDScriptTokenKind.CaretEqual, GDScriptTokenKind.Caret)
                    : Peek() is '"' or '\'' ? String() : Make(GDScriptTokenKind.Caret);
            case '&':
                if (Peek() == '&')
                {
                    Advance();
                    return Make(GDScriptTokenKind.AmpersandAmpersand);
                }
                return Peek() == '=' ? Followed('=', GDScriptTokenKind.AmpersandEqual, GDScriptTokenKind.Ampersand)
                    : Peek() is '"' or '\'' ? String() : Make(GDScriptTokenKind.Ampersand);
            case '|':
                if (Peek() == '|')
                {
                    Advance();
                    return Make(GDScriptTokenKind.PipePipe);
                }
                return Followed('=', GDScriptTokenKind.PipeEqual, GDScriptTokenKind.Pipe);
            case '=':
                return Peek() == '=' ? VcsMarker('=', GDScriptTokenKind.EqualEqual) : Make(GDScriptTokenKind.Equal);
            case '<':
                if (Peek() == '=')
                {
                    Advance();
                    return Make(GDScriptTokenKind.LessEqual);
                }
                if (Peek() == '<')
                {
                    if (Peek(1) == '=')
                    {
                        Advance();
                        Advance();
                        return Make(GDScriptTokenKind.LessLessEqual);
                    }
                    return VcsMarker('<', GDScriptTokenKind.LessLess);
                }
                return Make(GDScriptTokenKind.Less);
            case '>':
                if (Peek() == '=')
                {
                    Advance();
                    return Make(GDScriptTokenKind.GreaterEqual);
                }
                if (Peek() == '>')
                {
                    if (Peek(1) == '=')
                    {
                        Advance();
                        Advance();
                        return Make(GDScriptTokenKind.GreaterGreaterEqual);
                    }
                    return VcsMarker('>', GDScriptTokenKind.GreaterGreater);
                }
                return Make(GDScriptTokenKind.Greater);
            default:
                int point = char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek()) ? char.ConvertToUtf32(c, Peek()) : c;
                if (point > 0xFFFF)
                {
                    Advance();
                }
                if (char.IsWhiteSpace(c) || c is '\u200B' or '\u2060' or '\uFEFF')
                {
                    return MakeError($"Invalid white space character U+{point:X4}.");
                }
                return MakeError($"Invalid character \"{char.ConvertFromUtf32(point is >= 0xD800 and <= 0xDFFF ? 0xFFFD : point)}\" (U+{point:X4}).");
        }
    }

    private GDScriptToken Followed(char next, GDScriptTokenKind doubled, GDScriptTokenKind single)
    {
        if (Peek() == next)
        {
            Advance();
            return Make(doubled);
        }
        return Make(single);
    }
}

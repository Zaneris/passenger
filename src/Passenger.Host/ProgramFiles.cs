using Passenger.Core;

namespace Passenger.Host;

/// <summary>File-based programs (<c>dotnet run app.cs</c>) as their C# file's head tells them, one definition for both ends
/// (the app compiles this file too): a C# file named where a project would be is a program, whose project the SDK makes
/// from its directives (<c>#:package</c>, <c>#:include</c> and the rest), the lines before its first token and any
/// <c>#if</c>, after a <c>#!</c> line.</summary>
public static class ProgramFiles
{
    /// <summary>The most lines read for a head: past them, a file's directives are not looked for.</summary>
    private const int HeadLines = 400;

    /// <summary>Whether a project's path is a program's C# file rather than a project file.</summary>
    public static bool IsProgram(string path) => Path.GetExtension(path.AsSpan()).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The metadata the SDK puts on the reference a program's <c>#:ref</c> makes, naming the C# file referenced: the
    /// program whose project it is.</summary>
    public const string RefMetadata = "FileBasedProgramsFromRefDirective";

    /// <summary>A file's head: its <c>#!</c> line and each <c>#:</c> directive, trimmed, in order; none for a file not
    /// there or that does not read.</summary>
    public static List<string> Head(string path)
    {
        try
        {
            using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return Head(Lines(reader));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Lines(StreamReader reader)
    {
        for (int read = 0; read < HeadLines && reader.ReadLine() is { } line; read++)
        {
            yield return line;
        }
    }

    /// <summary>The head of a file's lines (<see cref="Head(string)"/>): comments and other preprocessor lines pass, the first
    /// token or <c>#if</c> ends it.</summary>
    public static List<string> Head(IEnumerable<string> lines) => [.. HeadAt(lines).Select(h => h.Text)];

    /// <summary>The head of a file's lines as <see cref="Head(IEnumerable{string})"/> finds it, each line trimmed, with its
    /// index among them.</summary>
    public static List<(int Line, string Text)> HeadAt(IEnumerable<string> lines)
    {
        List<(int Line, string Text)> head = [];
        bool comment = false;
        int index = -1;
        foreach (string line in lines)
        {
            index++;
            string text = line.Trim();
            if (index == 0 && text.StartsWith("#!", StringComparison.Ordinal))
            {
                head.Add((index, text));
                continue;
            }
            if (comment)
            {
                int end = text.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }
                comment = false;
                text = text[(end + 2)..].TrimStart();
            }
            while (text.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = text.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    comment = true;
                    break;
                }
                text = text[(end + 2)..].TrimStart();
            }
            if (comment || text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            if (text.StartsWith("#:", StringComparison.Ordinal))
            {
                head.Add((index, text));
                continue;
            }
            if (!text.StartsWith('#') || text.StartsWith("#if", StringComparison.Ordinal))
            {
                break;
            }
        }
        return head;
    }

    /// <summary>The most characters read for a file's first member (<see cref="BeginsWithStatement(string)"/>).</summary>
    private const int StatementChars = 64 * 1024;

    /// <summary>Whether a C# file's code begins with a top-level statement, which makes it a program's entry point as
    /// <c>dotnet run</c> takes it (<see cref="BeginsWithStatement(string, int)"/>); false for a file not there or that does
    /// not read.</summary>
    public static bool BeginsWithStatement(string path)
    {
        try
        {
            using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            char[] buffer = new char[StatementChars];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            return BeginsWithStatement(new string(buffer, 0, read), 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether C# code, from <paramref name="start"/>, begins with a top-level statement: its first member, past
    /// using directives, extern aliases, attributes, modifiers, comments and preprocessor lines, declares no type nor
    /// namespace (which the compiler lets no top-level statement follow). False for code with no member.</summary>
    public static bool BeginsWithStatement(string code, int start)
    {
        int at = start;
        while (Token(code, ref at) is { } token)
        {
            switch (token)
            {
                case "using":
                    if (!UsingDirective(code, ref at))
                    {
                        return true;
                    }
                    break;
                case "global":
                    // global using, else global:: beginning a statement's name.
                    if (Token(code, ref at) != "using" || !UsingDirective(code, ref at))
                    {
                        return true;
                    }
                    break;
                case "extern" when Peek(code, at) == "alias":
                    SkipPast(code, ref at, ';');
                    break;
                case "[":
                    SkipPast(code, ref at, ']');
                    break;
                case "class" or "struct" or "interface" or "enum" or "namespace":
                    return false;
                case "record":
                    return Peek(code, at) is not { } next || !(next is "class" or "struct" || IsWord(next));
                case "delegate":
                    return Peek(code, at) is "(" or "{";
                default:
                    if (!Modifiers.Contains(token))
                    {
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>What may come before a type's declaration, and before a local function's.</summary>
    private static readonly HashSet<string> Modifiers =
    [
        "public", "private", "protected", "internal", "static", "abstract", "sealed", "partial", "unsafe", "readonly", "ref", "file",
        "new", "extern", "async", "volatile", "virtual", "override", "required",
    ];

    /// <summary>Past a <c>using</c> at the start of a member: true, and past its <c>;</c>, for a using directive (a namespace,
    /// a static or unsafe one, an alias); false for a using statement or declaration, which is code.</summary>
    private static bool UsingDirective(string code, ref int at)
    {
        string? next = Token(code, ref at);
        if (next is "static" or "unsafe")
        {
            SkipPast(code, ref at, ';');
            return true;
        }
        if (next is null or "var" || !IsWord(next))
        {
            return false;
        }
        // A name of words joined by . or :: ends a directive at ; or an alias's one word at =, a declaration's at its variable.
        bool single = true;
        bool word = false;
        while (Token(code, ref at) is { } after)
        {
            if (word)
            {
                word = after is ":";
                if (!word && !IsWord(after))
                {
                    return false;
                }
                continue;
            }
            switch (after)
            {
                case ";":
                    return true;
                case "=":
                    SkipPast(code, ref at, ';');
                    return single;
                case "." or ":":
                    single = false;
                    word = true;
                    break;
                case "<":
                    SkipPast(code, ref at, '>');
                    single = false;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    private static bool IsWord(string token) => token.Length > 0 && (CSharpNames.Starts(token[0]) || token[0] == '@');

    private static string? Peek(string code, int at) => Token(code, ref at);

    /// <summary>Moves past the next <paramref name="end"/> outside a string or character literal, its brackets kept in
    /// balance.</summary>
    private static void SkipPast(string code, ref int at, char end)
    {
        char open = end switch { ']' => '[', '>' => '<', _ => '\0' };
        int depth = 0;
        while (Token(code, ref at) is { } token)
        {
            if (token.Length == 1 && token[0] == open)
            {
                depth++;
            }
            else if (token.Length == 1 && token[0] == end && depth-- == 0)
            {
                return;
            }
        }
    }

    /// <summary>The next token from <paramref name="at"/>, moving past it: a word (a keyword or an identifier), a literal,
    /// or one other character; null at the end. Whitespace, comments, preprocessor lines (a <c>#!</c> line too) and a
    /// merge's conflict markers with the lines the compiler reads as no code after them are passed over.</summary>
    private static string? Token(string code, ref int at)
    {
        while (at < code.Length)
        {
            char c = code[at];
            if (char.IsWhiteSpace(c) || c == '\uFEFF')
            {
                at++;
            }
            else if (c == '/' && at + 1 < code.Length && code[at + 1] == '/' || c == '#' && LineStart(code, at))
            {
                while (at < code.Length && code[at] is not ('\n' or '\r'))
                {
                    at++;
                }
            }
            else if (c == '/' && at + 1 < code.Length && code[at + 1] == '*')
            {
                int end = code.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? code.Length : end + 2;
            }
            else if (ConflictMarker(code, at) is { } marker and not '>')
            {
                at = NextLine(code, at);
                while (at < code.Length && Hides(marker, ConflictMarker(code, at)))
                {
                    at = NextLine(code, at);
                }
                // The marker ending a side read as no code is none either; one beginning no such side is read as code.
                if (marker is '|' or '=' && ConflictMarker(code, at) == '>')
                {
                    at = NextLine(code, at);
                }
            }
            else
            {
                break;
            }
        }
        if (at >= code.Length)
        {
            return null;
        }
        int begin = at;
        if (CSharpNames.Starts(code[at]) || char.IsDigit(code[at]) || code[at] == '@' && at + 1 < code.Length && CSharpNames.Starts(code[at + 1]))
        {
            at++;
            while (at < code.Length && CSharpNames.Continues(code[at]))
            {
                at++;
            }
            return code[begin..at];
        }
        if (code[at] is '"' or '\'' || code[at] is '@' or '$' && at + 1 < code.Length && code[at + 1] is '"' or '@' or '$')
        {
            bool verbatim = false;
            while (at < code.Length && code[at] is '@' or '$')
            {
                verbatim |= code[at] == '@';
                at++;
            }
            if (at >= code.Length)
            {
                return code[begin..];
            }
            char quote = code[at++];
            while (at < code.Length && code[at] != quote)
            {
                at += !verbatim && code[at] == '\\' ? 2 : 1;
            }
            at = Math.Min(at + 1, code.Length);
            return code[begin..at];
        }
        at++;
        return code[begin..at];
    }

    /// <summary>The character of a version control conflict marker beginning a line, as the compiler reads one: seven of
    /// <c>&lt;</c>, <c>|</c>, <c>=</c> or <c>&gt;</c> at the line's very start, a space after <c>&lt;</c>'s and
    /// <c>&gt;</c>'s; null for anything else.</summary>
    private static char? ConflictMarker(string code, int at)
    {
        const int Length = 7;
        if (at + Length > code.Length || code[at] is not ('<' or '|' or '=' or '>') || at > 0 && code[at - 1] is not ('\n' or '\r'))
        {
            return null;
        }
        char marker = code[at];
        for (int i = 1; i < Length; i++)
        {
            if (code[at + i] != marker)
            {
                return null;
            }
        }
        return marker is '|' or '=' || at + Length < code.Length && code[at + Length] == ' ' ? marker : null;
    }

    /// <summary>Whether the compiler reads no code in a line marked <paramref name="line"/> (null for none) after a
    /// <paramref name="marker"/> line: the base's side after <c>|||||||</c>, up to a line marked <c>=</c> or <c>&gt;</c>,
    /// and the other side's after <c>=======</c>, up to one marked <c>&gt;</c>.</summary>
    private static bool Hides(char marker, char? line) => marker switch
    {
        '|' => line is not ('=' or '>'),
        '=' => line is not '>',
        _ => false,
    };

    /// <summary>Where the line after the one <paramref name="at"/> is in begins, or the text's end.</summary>
    private static int NextLine(string code, int at)
    {
        int end = code.IndexOfAny(['\n', '\r'], at);
        return end < 0 ? code.Length : end + (code[end] == '\r' && end + 1 < code.Length && code[end + 1] == '\n' ? 2 : 1);
    }

    /// <summary>Whether only whitespace comes before a place on its line.</summary>
    private static bool LineStart(string code, int at)
    {
        for (int i = at - 1; i >= 0 && code[i] is not ('\n' or '\r'); i--)
        {
            if (!char.IsWhiteSpace(code[i]) && code[i] != '\uFEFF')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A directive's kind and its value, as the SDK reads them (<see cref="Read"/>): <c>#:include "my file.cs"</c> is
    /// ("include", "my file.cs"), <c>#:ref lib.cs Aliases=Lib</c> ("ref", "lib.cs"), <c>#:package Humanizer@2.14.1</c>
    /// ("package", "Humanizer@2.14.1"); null for the <c>#!</c> line.</summary>
    public static (string Kind, string Value)? Parse(string directive) => Read(directive) is { } read ? (read.Kind, read.Value) : null;

    /// <summary>A directive's kind, its value and where that value is written in it, as the SDK reads them: the rest after
    /// the kind is split at white space, a part wrapped in double quotes read as a C# string's (white space and escapes in
    /// it), and the parts after the first are its item metadata (<c>Name=Value</c>) where its kind takes them and every one
    /// is that; else, with no quote in it, the rest is one value, white space and all. A value whose quotes the SDK refuses
    /// is empty, written nowhere. Null for the <c>#!</c> line.</summary>
    public static Directive? Read(string directive)
    {
        if (!directive.StartsWith("#:", StringComparison.Ordinal))
        {
            return null;
        }
        int end = directive.Length;
        while (end > 2 && char.IsWhiteSpace(directive[end - 1]))
        {
            end--;
        }
        int at = 2;
        while (at < end && !char.IsWhiteSpace(directive[at]))
        {
            at++;
        }
        string kind = directive[2..at];
        while (at < end && char.IsWhiteSpace(directive[at]))
        {
            at++;
        }
        if (at == end)
        {
            return new Directive(kind, string.Empty, at, 0);
        }
        bool metadata = kind is "package" or "project" or "ref" or "include";
        string rest = directive[at..end];
        if (!rest.Contains('"', StringComparison.Ordinal))
        {
            int space = 0;
            while (space < rest.Length && !char.IsWhiteSpace(rest[space]))
            {
                space++;
            }
            bool split = space < rest.Length && metadata && rest[space..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(IsMetadata);
            return split ? new Directive(kind, rest[..space], at, space) : new Directive(kind, rest, at, rest.Length);
        }
        char? separator = kind switch { "sdk" or "package" => '@', "property" => '=', _ => null };
        return FirstToken(rest, separator, metadata) is ({ } value, int length) ? new Directive(kind, value, at, length) : new Directive(kind, string.Empty, at, 0);
    }

    /// <summary>A directive's kind and its value as <see cref="Read"/> reads them, with where its value is written in the
    /// directive's text (its quotes included).</summary>
    public sealed record Directive(string Kind, string Value, int Start, int Length);

    /// <summary>Whether a part of a directive is item metadata, <c>Name=Value</c>, its name one MSBuild takes for an element.</summary>
    private static bool IsMetadata(string part)
    {
        int equals = part.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || !System.Xml.XmlConvert.IsStartNCNameChar(part[0]))
        {
            return false;
        }
        for (int i = 1; i < equals; i++)
        {
            if (!System.Xml.XmlConvert.IsNCNameChar(part[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The first part of a directive's rest that holds a quote, decoded, and how many of its characters it spans:
    /// a value, or a name and a value either side of the kind's <paramref name="separator"/>, each bare or quoted, white
    /// space allowed around the separator but before a value only where no metadata (<c>Name=</c>) follows instead; null
    /// where a quote is out of place, left open or holds an escape C# refuses.</summary>
    private static (string Value, int Length)? FirstToken(string rest, char? separator, bool metadata)
    {
        System.Text.StringBuilder value = new();
        int at = 0;
        if (!Part(rest, ref at, separator, value))
        {
            return null;
        }
        int after = Skip(rest, at);
        if (separator is not { } between || after >= rest.Length || rest[after] != between)
        {
            return (value.ToString(), at);
        }
        value.Append(between);
        at = after + 1;
        int next = Skip(rest, at);
        if (next > at && metadata && StartsMetadata(rest, next) || next >= rest.Length)
        {
            return (value.ToString(), at);
        }
        at = next;
        return Part(rest, ref at, null, value) ? (value.ToString(), at) : null;

        static int Skip(string text, int at)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            return at;
        }

        static bool StartsMetadata(string text, int at)
        {
            int end = at;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '=')
            {
                end++;
            }
            int equals = Skip(text, end);
            return end > at && equals < text.Length && text[equals] == '=' && IsMetadata(text[at..end] + "=");
        }
    }

    /// <summary>Reads one side of a directive's part from <paramref name="at"/> into <paramref name="value"/>: a C# string
    /// wrapped in double quotes, else the characters up to white space or the <paramref name="separator"/>. False where a
    /// quote stands inside a bare side, a quoted one is followed by anything but white space or the separator, or a quoted
    /// one is left open or holds an escape C# refuses.</summary>
    private static bool Part(string text, ref int at, char? separator, System.Text.StringBuilder value)
    {
        if (at < text.Length && text[at] == '"')
        {
            at++;
            while (true)
            {
                if (at >= text.Length || text[at] is '\r' or '\n')
                {
                    return false;
                }
                char c = text[at++];
                if (c == '"')
                {
                    break;
                }
                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }
                if (!Escape(text, ref at, value))
                {
                    return false;
                }
            }
            return at >= text.Length || char.IsWhiteSpace(text[at]) || text[at] == separator;
        }
        while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] != separator)
        {
            if (text[at] == '"')
            {
                return false;
            }
            value.Append(text[at++]);
        }
        return true;
    }

    /// <summary>Decodes the escape after a backslash in a C# string, as the compiler reads it: a simple escape, <c>\x</c>
    /// with one to four hex digits, <c>\u</c> with four or <c>\U</c> with eight; false for any other.</summary>
    private static bool Escape(string text, ref int at, System.Text.StringBuilder value)
    {
        if (at >= text.Length)
        {
            return false;
        }
        char kind = text[at++];
        char? simple = kind switch
        {
            '\'' => '\'', '"' => '"', '\\' => '\\', '0' => '\0', 'a' => '\a', 'b' => '\b', 'e' => '\e', 'f' => '\f', 'n' => '\n', 'r' => '\r',
            't' => '\t', 'v' => '\v', _ => null,
        };
        if (simple is { } plain)
        {
            value.Append(plain);
            return true;
        }
        (int least, int most) = kind switch { 'x' => (1, 4), 'u' => (4, 4), 'U' => (8, 8), _ => (0, 0) };
        int digits = 0;
        while (digits < most && at + digits < text.Length && char.IsAsciiHexDigit(text[at + digits]))
        {
            digits++;
        }
        if (most == 0 || digits < least
            || !uint.TryParse(text.AsSpan(at, digits), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out uint code)
            || code > 0x10FFFF)
        {
            return false;
        }
        at += digits;
        if (code <= char.MaxValue)
        {
            value.Append((char)code);
        }
        else
        {
            value.Append(char.ConvertFromUtf32((int)code));
        }
        return true;
    }
}

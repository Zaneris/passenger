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
    public static bool IsProgram(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsWord(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] is '_' or '@');

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
    /// or one other character; null at the end. Whitespace, comments and preprocessor lines (a <c>#!</c> line too) are
    /// passed over.</summary>
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
        if (char.IsLetterOrDigit(code[at]) || code[at] == '_' || code[at] == '@' && at + 1 < code.Length && (char.IsLetter(code[at + 1]) || code[at + 1] == '_'))
        {
            at++;
            while (at < code.Length && (char.IsLetterOrDigit(code[at]) || code[at] == '_'))
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

    /// <summary>A directive's kind and its value, as the SDK splits them: <c>#:include "my file.cs"</c> is ("include",
    /// "my file.cs"), a quoted value read as a C# string's; null for the <c>#!</c> line.</summary>
    public static (string Kind, string Value)? Parse(string directive)
    {
        if (!directive.StartsWith("#:", StringComparison.Ordinal))
        {
            return null;
        }
        string rest = directive[2..];
        int space = rest.IndexOfAny([' ', '\t']);
        string kind = space < 0 ? rest : rest[..space];
        string value = space < 0 ? string.Empty : rest[(space + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        return (kind, value);
    }
}

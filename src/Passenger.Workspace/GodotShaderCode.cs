using System.Text;

namespace Passenger.Workspace;

/// <summary>What a shader's code declares.</summary>
public enum GodotShaderDeclared
{
    Uniform,
    Varying,
    Constant,
    Struct,
    Function,
    Define,
    Parameter,
    Local,
}

/// <summary>A name a shader's code declares: what it is, where its name lies, the whole declaration as written (its white
/// space one space each), its type (a function's return type), a uniform's hints as written (<c>hint_range(0.0, 1.0)</c>),
/// and for a parameter or local the function it is declared in and from where it is seen.</summary>
public sealed record GodotShaderDeclaration(GodotShaderDeclared Kind, string Name, int Start, int End, string Written, string? Type)
{
    public string? Hints { get; init; }

    /// <summary>For a uniform, <c>global</c> or <c>instance</c> where written before it.</summary>
    public string? Scope { get; init; }

    public string? Function { get; init; }

    /// <summary>Where a parameter or local is seen from: its function's body, or the end of its declaration.</summary>
    public int SeenFrom { get; init; }

    /// <summary>Where a parameter or local stops being seen: its block's end.</summary>
    public int SeenTo { get; init; } = int.MaxValue;
}

/// <summary>A file a shader's code includes: the path as written, where its characters lie between the quotes, and the
/// line of its <c>#include</c> (from zero).</summary>
public sealed record GodotShaderInclude(string Path, int Start, int End, int Line);

/// <summary>A function a shader's code defines, by where its name and its body (between its braces) lie.</summary>
public sealed record GodotShaderFunction(string Name, int Start, int BodyStart, int BodyEnd);

/// <summary>What is being written at a place of a shader's code.</summary>
public enum GodotShaderSpot
{
    /// <summary>A comment, a string, a number: nothing completes.</summary>
    None,

    /// <summary>Code at the shader's top, outside any function.</summary>
    Top,

    /// <summary>Code inside a function's body.</summary>
    Body,

    /// <summary>A member after a '.': a struct's member or a swizzle.</summary>
    Member,

    RenderModes,

    StencilModes,

    /// <summary>A uniform's hints, after its ':'.</summary>
    Hints,

    /// <summary>The path of an <c>#include</c>, inside its quotes.</summary>
    Include,

    /// <summary>The type after <c>shader_type</c>.</summary>
    ShaderType,
}

/// <summary>Passenger's own reading of a shader of Godot's shading language, its comments passed over: its type, render
/// modes, includes, and the uniforms, varyings, constants, structs, functions and defines it declares, with each
/// function's parameters and locals; what is written at a place (<see cref="SpotAt"/>), and where a name is used. It reads
/// only what lies where Godot's own grammar puts it, so a shader half typed still reads. Pure.</summary>
public sealed class GodotShaderCode
{
    /// <summary>The types the language names by its own words.</summary>
    public static readonly IReadOnlySet<string> BuiltInTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "void", "bool", "bvec2", "bvec3", "bvec4", "int", "ivec2", "ivec3", "ivec4", "uint", "uvec2", "uvec3", "uvec4", "float", "vec2", "vec3", "vec4",
        "mat2", "mat3", "mat4", "sampler2D", "isampler2D", "usampler2D", "sampler2DArray", "isampler2DArray", "usampler2DArray", "sampler3D", "isampler3D",
        "usampler3D", "samplerCube", "samplerCubeArray", "samplerExternalOES",
    };

    /// <summary>The words of the language that are neither types nor names.</summary>
    public static readonly IReadOnlyList<string> Keywords =
    [
        "shader_type", "render_mode", "stencil_mode", "uniform", "group_uniforms", "varying", "const", "struct", "global", "instance", "flat", "smooth",
        "lowp", "mediump", "highp", "in", "out", "inout", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return",
        "discard", "true", "false",
    ];

    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal) { "lowp", "mediump", "highp", "flat", "smooth", "const", "in", "out", "inout" };

    private readonly string text;
    private readonly List<Token> tokens;
    private readonly List<(int Start, int End, GodotShaderSpot Spot)> spans = [];

    private GodotShaderCode(string text, List<Token> tokens)
    {
        this.text = text;
        this.tokens = tokens;
    }

    /// <summary>The shader type it names (<c>spatial</c>); null where it names none (an include).</summary>
    public string? Type { get; private set; }

    public IReadOnlyList<string> RenderModes { get; private set; } = [];

    public IReadOnlyList<GodotShaderInclude> Includes { get; private set; } = [];

    public IReadOnlyList<GodotShaderDeclaration> Declarations { get; private set; } = [];

    public IReadOnlyList<GodotShaderFunction> Functions { get; private set; } = [];

    public static GodotShaderCode Read(string text)
    {
        GodotShaderCode code = new(text, Tokens(text, out List<GodotShaderInclude> includes, out List<GodotShaderDeclaration> defines, out List<(int, int, bool)> skipped));
        code.Includes = includes;
        code.skipped = skipped;
        code.ReadTop(defines);
        return code;
    }

    /// <summary>The code's text between two offsets.</summary>
    public string Text(int start, int end) => text[start..end];

    /// <summary>The function whose body holds an offset; null at the shader's top.</summary>
    public GodotShaderFunction? FunctionAt(int offset) => Functions.FirstOrDefault(f => f.BodyStart < offset && offset <= f.BodyEnd);

    /// <summary>What is being written at an offset: the include, mode, hint or member being typed, else code at the top or
    /// in a body; nothing in a comment, a string or a number.</summary>
    public GodotShaderSpot SpotAt(int offset)
    {
        if (Includes.Any(i => i.Start <= offset && offset <= i.End))
        {
            return GodotShaderSpot.Include;
        }
        int index = TokenBefore(offset);
        if (InSkipped(offset))
        {
            return GodotShaderSpot.None;
        }
        if (index >= 0 && tokens[index] is { } before && before.End >= offset && before.Kind is TokenKind.Number or TokenKind.String)
        {
            return GodotShaderSpot.None;
        }
        // The word being typed is the place's: the token before it says what it is.
        int previous = index >= 0 && tokens[index].Kind == TokenKind.Word && tokens[index].End >= offset ? index - 1 : index;
        if (previous >= 0 && tokens[previous].Is('.'))
        {
            return GodotShaderSpot.Member;
        }
        if (previous >= 0 && tokens[previous].Text == "shader_type")
        {
            return GodotShaderSpot.ShaderType;
        }
        foreach ((int start, int end, GodotShaderSpot spot) in spans)
        {
            if (start < offset && offset <= end)
            {
                return spot;
            }
        }
        return FunctionAt(offset) is null ? GodotShaderSpot.Top : GodotShaderSpot.Body;
    }

    /// <summary>The identifier at an offset (or ending at it), where its characters lie; null where there is none.</summary>
    public (int Start, int End)? WordAt(int offset)
    {
        int index = TokenBefore(offset);
        return index >= 0 && tokens[index] is { Kind: TokenKind.Word } word && word.End >= offset ? (word.Start, word.End) : null;
    }

    /// <summary>Whether the identifier at a place follows a '.' (a member or a swizzle, never a name of its own).</summary>
    public bool AfterDot(int start)
    {
        int index = TokenBefore(start + 1);
        return index > 0 && tokens[index - 1].Is('.');
    }

    /// <summary>Every place an identifier is written as a name (not a member after a '.'), its declarations among them.</summary>
    public IEnumerable<(int Start, int End)> Uses(string name)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Word && tokens[i].Text == name && !(i > 0 && tokens[i - 1].Is('.')))
            {
                yield return (tokens[i].Start, tokens[i].End);
            }
        }
    }

    /// <summary>The names seen at an offset: the shader's own declarations, and the parameters and locals of the function
    /// holding it declared before it.</summary>
    public IEnumerable<GodotShaderDeclaration> SeenAt(int offset) => Declarations.Where(d => d.Kind is not (GodotShaderDeclared.Parameter or GodotShaderDeclared.Local)
        || d.SeenFrom <= offset && offset <= d.SeenTo);

    /// <summary>The declaration of the name at an offset seen there: a parameter or local before the shader's own.</summary>
    public GodotShaderDeclaration? DeclarationOf(string name, int offset) =>
        SeenAt(offset).Where(d => d.Name == name).OrderBy(d => d.Kind is GodotShaderDeclared.Parameter or GodotShaderDeclared.Local ? 0 : 1).FirstOrDefault();

    /// <summary>Whether an offset is inside a comment or a preprocessor line.</summary>
    private bool InSkipped(int offset) =>
        skipped.Any(s => s.Start < offset && (offset < s.End || s.EndInside && offset == s.End));

    /// <summary>The comments and preprocessor lines, each with whether a place at its end is still inside it (a line's end,
    /// a comment never closed).</summary>
    private List<(int Start, int End, bool EndInside)> skipped = [];

    /// <summary>The index of the last token starting before an offset; -1 for none.</summary>
    private int TokenBefore(int offset)
    {
        int low = 0;
        int high = tokens.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (tokens[middle].Start < offset)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    private void ReadTop(List<GodotShaderDeclaration> defines)
    {
        List<GodotShaderDeclaration> declared = [.. defines];
        List<GodotShaderFunction> functions = [];
        List<string> modes = [];
        HashSet<string> structs = [];
        int i = 0;
        while (i < tokens.Count)
        {
            Token token = tokens[i];
            if (token.Is('{'))
            {
                i = Matching(i) + 1;
                continue;
            }
            int statement = i;
            int end = StatementEnd(i);
            switch (token.Text)
            {
                case "shader_type":
                    Type = Word(i + 1)?.Text;
                    break;
                case "render_mode" or "stencil_mode":
                    spans.Add((token.End, end < tokens.Count ? tokens[end].Start : text.Length, token.Text == "render_mode" ? GodotShaderSpot.RenderModes : GodotShaderSpot.StencilModes));
                    if (token.Text == "render_mode")
                    {
                        modes.AddRange(tokens[(i + 1)..Math.Min(end, tokens.Count)].Where(t => t.Kind == TokenKind.Word).Select(t => t.Text));
                    }
                    break;
                case "global" or "instance" or "uniform":
                    ReadUniform(i, end, declared);
                    break;
                case "varying":
                    if (TypeAndName(i + 1, end) is ({ } type, { } name))
                    {
                        declared.Add(Declaration(GodotShaderDeclared.Varying, name, statement, end, type.Text));
                    }
                    break;
                case "const":
                    ReadConstants(i, end, declared);
                    break;
                case "struct":
                    if (Word(i + 1) is { } named)
                    {
                        structs.Add(named.Text);
                        int close = i + 2 < tokens.Count && tokens[i + 2].Is('{') ? Matching(i + 2) : end;
                        end = StatementEnd(close);
                        declared.Add(Declaration(GodotShaderDeclared.Struct, named, statement, end, "struct"));
                    }
                    break;
                default:
                    if (ReadFunction(i, declared, functions) is int after)
                    {
                        i = after;
                        continue;
                    }
                    break;
            }
            i = end + 1;
        }
        foreach (GodotShaderFunction function in functions)
        {
            ReadLocals(function, structs, declared);
        }
        Declarations = declared;
        Functions = functions;
        RenderModes = modes;
    }

    private void ReadUniform(int i, int end, List<GodotShaderDeclaration> declared)
    {
        string? scope = tokens[i].Text is "global" or "instance" ? tokens[i].Text : null;
        int at = scope is null ? i : i + 1;
        if (Word(at)?.Text != "uniform")
        {
            return;
        }
        int colon = IndexOf(at, end, ':');
        int equals = IndexOf(at, end, '=');
        int nameEnd = new[] { colon, equals, end }.Where(x => x >= 0).Min();
        if (TypeAndName(at + 1, nameEnd) is not ({ } type, { } name))
        {
            return;
        }
        string? hints = null;
        if (colon >= 0)
        {
            int hintsEnd = equals > colon ? equals : end;
            spans.Add((tokens[colon].End, hintsEnd < tokens.Count ? tokens[hintsEnd].Start : text.Length, GodotShaderSpot.Hints));
            hints = hintsEnd - 1 > colon ? Collapsed(tokens[colon + 1].Start, tokens[hintsEnd - 1].End) : string.Empty;
        }
        declared.Add(Declaration(GodotShaderDeclared.Uniform, name, i, end, type.Text) with { Hints = hints, Scope = scope });
    }

    private void ReadConstants(int i, int end, List<GodotShaderDeclaration> declared)
    {
        int at = i + 1;
        while (at < end && Qualifiers.Contains(tokens[at].Text))
        {
            at++;
        }
        if (Word(at) is not { } type)
        {
            return;
        }
        // Each name after the type, or after a comma at the statement's own depth.
        int depth = 0;
        bool expectName = true;
        for (int j = at + 1; j < end; j++)
        {
            Token token = tokens[j];
            if (token.Is('(') || token.Is('['))
            {
                depth++;
            }
            else if (token.Is(')') || token.Is(']'))
            {
                depth--;
            }
            else if (depth == 0 && token.Is(','))
            {
                expectName = true;
            }
            else if (expectName && token.Kind == TokenKind.Word)
            {
                declared.Add(Declaration(GodotShaderDeclared.Constant, token, i, end, type.Text));
                expectName = false;
            }
            else if (token.Is('='))
            {
                expectName = false;
            }
        }
    }

    /// <summary>A function at a token (its return type, its name, its parameters and its body), added; the token after it,
    /// or null where none starts there.</summary>
    private int? ReadFunction(int i, List<GodotShaderDeclaration> declared, List<GodotShaderFunction> functions)
    {
        int at = i;
        while (at < tokens.Count && Qualifiers.Contains(tokens[at].Text))
        {
            at++;
        }
        if (Word(at) is not { } type || Word(at + 1) is not { } name || at + 2 >= tokens.Count || !tokens[at + 2].Is('('))
        {
            return null;
        }
        int close = Matching(at + 2);
        int open = close + 1;
        string signature = Collapsed(tokens[i].Start, close < tokens.Count ? tokens[close].End : text.Length);
        if (open >= tokens.Count || !tokens[open].Is('{'))
        {
            declared.Add(new GodotShaderDeclaration(GodotShaderDeclared.Function, name.Text, name.Start, name.End, signature, type.Text));
            return Math.Min(open, tokens.Count) + 1;
        }
        int bodyEnd = Matching(open);
        int bodyEndOffset = bodyEnd < tokens.Count ? tokens[bodyEnd].Start : text.Length;
        declared.Add(new GodotShaderDeclaration(GodotShaderDeclared.Function, name.Text, name.Start, name.End, signature, type.Text));
        functions.Add(new GodotShaderFunction(name.Text, name.Start, tokens[open].End, bodyEndOffset));
        // Each parameter: its type and name, after its qualifiers, split at the list's own commas.
        int from = at + 3;
        int depth = 0;
        for (int j = at + 3; j <= close && j < tokens.Count; j++)
        {
            if (tokens[j].Is('(') || tokens[j].Is('['))
            {
                depth++;
            }
            else if ((tokens[j].Is(')') || tokens[j].Is(']')) && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && (tokens[j].Is(',') || j == close))
            {
                if (TypeAndName(from, j) is ({ } parameterType, { } parameter))
                {
                    declared.Add(new GodotShaderDeclaration(GodotShaderDeclared.Parameter, parameter.Text, parameter.Start, parameter.End,
                        Collapsed(tokens[from].Start, tokens[j - 1].End), parameterType.Text) { Function = name.Text, SeenFrom = tokens[open].End, SeenTo = bodyEndOffset });
                }
                from = j + 1;
            }
        }
        return bodyEnd + 1;
    }

    /// <summary>The locals a function's body declares: a type (the language's or a struct's) then a name, where a statement
    /// or a <c>for</c>'s first part starts.</summary>
    private void ReadLocals(GodotShaderFunction function, HashSet<string> structs, List<GodotShaderDeclaration> declared)
    {
        int first = TokenBefore(function.BodyStart + 1) + 1;
        List<int> blocks = [function.BodyEnd];
        for (int j = first; j < tokens.Count && tokens[j].Start < function.BodyEnd; j++)
        {
            Token token = tokens[j];
            if (token.Is('{'))
            {
                int close = Matching(j);
                blocks.Add(close < tokens.Count ? tokens[close].Start : text.Length);
                continue;
            }
            if (token.Is('}'))
            {
                if (blocks.Count > 1)
                {
                    blocks.RemoveAt(blocks.Count - 1);
                }
                continue;
            }
            if (token.Kind != TokenKind.Word || !(BuiltInTypes.Contains(token.Text) || structs.Contains(token.Text)) || Word(j + 1) is not { } name)
            {
                continue;
            }
            Token? before = j > first ? tokens[j - 1] : null;
            if (before is { } b && !(b.Is(';') || b.Is('{') || b.Is('}') || b.Is('(') || Qualifiers.Contains(b.Text)))
            {
                continue;
            }
            int after = j + 2 < tokens.Count ? j + 2 : -1;
            if (after >= 0 && !(tokens[after].Is('=') || tokens[after].Is(';') || tokens[after].Is(',') || tokens[after].Is('[')))
            {
                continue;
            }
            int end = StatementEnd(j);
            declared.Add(new GodotShaderDeclaration(GodotShaderDeclared.Local, name.Text, name.Start, name.End,
                Collapsed(token.Start, Math.Min(end < tokens.Count ? tokens[end].End : text.Length, name.End + 200)), token.Text)
            {
                Function = function.Name,
                SeenFrom = name.End,
                SeenTo = blocks[^1],
            });
        }
    }

    private GodotShaderDeclaration Declaration(GodotShaderDeclared kind, Token name, int from, int to, string? type) =>
        new(kind, name.Text, name.Start, name.End, Collapsed(tokens[from].Start, to < tokens.Count ? tokens[to].End : text.Length), type);

    /// <summary>A declaration's type and name before a token: the name the last word, the type the word before it.</summary>
    private (Token? Type, Token? Name) TypeAndName(int from, int to)
    {
        int last = -1;
        for (int j = Math.Min(to, tokens.Count) - 1; j >= from; j--)
        {
            if (tokens[j].Is(']'))
            {
                j = OpeningOf(j, from);
                continue;
            }
            if (tokens[j].Kind == TokenKind.Word)
            {
                last = j;
                break;
            }
        }
        if (last < 0)
        {
            return (null, null);
        }
        for (int j = last - 1; j >= from; j--)
        {
            if (tokens[j].Is(']'))
            {
                j = OpeningOf(j, from);
                continue;
            }
            if (tokens[j].Kind == TokenKind.Word && !Qualifiers.Contains(tokens[j].Text))
            {
                return (tokens[j], tokens[last]);
            }
        }
        return (null, null);
    }

    private int OpeningOf(int close, int from)
    {
        int depth = 0;
        for (int j = close; j >= from; j--)
        {
            depth += tokens[j].Is(']') ? 1 : tokens[j].Is('[') ? -1 : 0;
            if (depth == 0)
            {
                return j;
            }
        }
        return from;
    }

    private Token? Word(int index) => index < tokens.Count && tokens[index].Kind == TokenKind.Word ? tokens[index] : null;

    private int IndexOf(int from, int to, char mark)
    {
        for (int j = from; j < to && j < tokens.Count; j++)
        {
            if (tokens[j].Is(mark))
            {
                return j;
            }
        }
        return -1;
    }

    /// <summary>The ';' ending the statement a token starts, at its own depth (a brace closing before it ends it too); the
    /// token count where none does.</summary>
    private int StatementEnd(int from)
    {
        int depth = 0;
        for (int j = from; j < tokens.Count; j++)
        {
            Token token = tokens[j];
            if (token.Is('(') || token.Is('[') || token.Is('{'))
            {
                depth++;
            }
            else if (token.Is(')') || token.Is(']') || token.Is('}'))
            {
                if (--depth < 0)
                {
                    return j - 1 < from ? from : j - 1;
                }
            }
            else if (depth == 0 && token.Is(';'))
            {
                return j;
            }
        }
        return tokens.Count;
    }

    /// <summary>The token closing the bracket a token opens; the token count where none does.</summary>
    private int Matching(int open)
    {
        char opening = tokens[open].Text[0];
        char closing = opening switch { '(' => ')', '[' => ']', _ => '}' };
        int depth = 0;
        for (int j = open; j < tokens.Count; j++)
        {
            if (tokens[j].Is(opening))
            {
                depth++;
            }
            else if (tokens[j].Is(closing) && --depth == 0)
            {
                return j;
            }
        }
        return tokens.Count;
    }

    /// <summary>The text between two offsets, each run of white space and each comment one space.</summary>
    private string Collapsed(int start, int end)
    {
        StringBuilder written = new();
        int first = TokenBefore(start + 1);
        for (int j = Math.Max(0, first); j < tokens.Count && tokens[j].Start < end; j++)
        {
            Token token = tokens[j];
            if (token.Start < start)
            {
                continue;
            }
            if (written.Length > 0 && (j > 0 && tokens[j - 1].End < token.Start))
            {
                written.Append(' ');
            }
            written.Append(text, token.Start, Math.Min(token.End, end) - token.Start);
        }
        return written.ToString();
    }

    private static List<Token> Tokens(string text, out List<GodotShaderInclude> includes, out List<GodotShaderDeclaration> defines,
        out List<(int Start, int End, bool EndInside)> skipped)
    {
        List<Token> tokens = [];
        includes = [];
        defines = [];
        skipped = [];
        int line = 0;
        int at = 0;
        bool lineStart = true;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '\n')
            {
                line++;
                lineStart = true;
                at++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }
            if (c == '/' && at + 1 < text.Length && text[at + 1] == '/')
            {
                int end = text.IndexOf('\n', at) is var n and >= 0 ? n : text.Length;
                skipped.Add((at, end, true));
                at = end;
                continue;
            }
            if (c == '/' && at + 1 < text.Length && text[at + 1] == '*')
            {
                int close = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 2;
                skipped.Add((at, end, close < 0));
                line += text.AsSpan(at, end - at).Count('\n');
                at = end;
                continue;
            }
            if (c == '#' && lineStart)
            {
                int end = Directive(text, at, line, includes, defines);
                skipped.Add((at, end, true));
                line += text.AsSpan(at, end - at).Count('\n');
                at = end;
                continue;
            }
            lineStart = false;
            if (char.IsLetter(c) || c == '_')
            {
                int start = at;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
                {
                    at++;
                }
                tokens.Add(new Token(TokenKind.Word, text[start..at], start, at));
            }
            else if (char.IsAsciiDigit(c) || c == '.' && at + 1 < text.Length && char.IsAsciiDigit(text[at + 1]))
            {
                int start = at;
                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '.' || text[at] is '+' or '-' && text[at - 1] is 'e' or 'E'))
                {
                    at++;
                }
                tokens.Add(new Token(TokenKind.Number, text[start..at], start, at));
            }
            else if (c == '"')
            {
                int close = text.IndexOf('"', at + 1);
                int end = close < 0 || text.IndexOf('\n', at + 1) is var nl and >= 0 && nl < close ? text.IndexOf('\n', at + 1) is var n and >= 0 ? n : text.Length : close + 1;
                tokens.Add(new Token(TokenKind.String, text[at..end], at, end));
                at = end;
            }
            else
            {
                tokens.Add(new Token(TokenKind.Mark, c.ToString(), at, at + 1));
                at++;
            }
        }
        return tokens;
    }

    /// <summary>A preprocessor line from its '#' (its ends joined by '\'), an include or a define it writes added; where
    /// the text goes on after it.</summary>
    private static int Directive(string text, int at, int line, List<GodotShaderInclude> includes, List<GodotShaderDeclaration> defines)
    {
        int end = at;
        while (true)
        {
            int newline = text.IndexOf('\n', end);
            if (newline < 0)
            {
                end = text.Length;
                break;
            }
            end = newline;
            if (newline > 0 && text[newline - 1] == '\\' || newline > 1 && text[newline - 1] == '\r' && text[newline - 2] == '\\')
            {
                end = newline + 1;
                continue;
            }
            break;
        }
        string directive = text[at..end];
        int word = 1;
        while (word < directive.Length && directive[word] is ' ' or '\t')
        {
            word++;
        }
        if (directive.AsSpan(word).StartsWith("include", StringComparison.Ordinal) && directive.IndexOf('"', word) is var open and >= 0)
        {
            int close = directive.IndexOf('"', open + 1);
            int stop = close < 0 ? directive.TrimEnd('\r').Length : close;
            includes.Add(new GodotShaderInclude(directive[(open + 1)..stop], at + open + 1, at + stop, line));
        }
        else if (directive.AsSpan(word).StartsWith("define", StringComparison.Ordinal))
        {
            int name = word + "define".Length;
            while (name < directive.Length && directive[name] is ' ' or '\t')
            {
                name++;
            }
            int nameEnd = name;
            while (nameEnd < directive.Length && (char.IsLetterOrDigit(directive[nameEnd]) || directive[nameEnd] == '_'))
            {
                nameEnd++;
            }
            if (nameEnd > name)
            {
                defines.Add(new GodotShaderDeclaration(GodotShaderDeclared.Define, directive[name..nameEnd], at + name, at + nameEnd,
                    string.Join(' ', directive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), null));
            }
        }
        return end;
    }

    private enum TokenKind
    {
        Word,
        Number,
        String,
        Mark,
    }

    private sealed record Token(TokenKind Kind, string Text, int Start, int End)
    {
        public bool Is(char mark) => Kind == TokenKind.Mark && Text[0] == mark;
    }
}

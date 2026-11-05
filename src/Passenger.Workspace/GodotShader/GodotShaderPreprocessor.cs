using System.Text;

namespace Passenger.Workspace.GodotShader;

/// <summary>A place in a shader's files, as Godot's preprocessor and compiler name it: a file's res:// path and a line
/// (from one).</summary>
public readonly record struct GodotShaderFilePosition(string File, int Line);

/// <summary>A stretch of a file's lines (from one) a branch directive left in or took out of the code.</summary>
public sealed record GodotShaderRegion(string File, int FromLine, int ToLine, bool Enabled);

/// <summary>A file a <c>#include</c> asks for, as the project has it: whether it is there, whether it is a shader include
/// (a <c>.gdshaderinc</c>), and its code.</summary>
public sealed record GodotShaderIncluded(bool Exists, bool IsInclude, string Code);

/// <summary>What Godot's preprocessor made of a shader: the code its compiler is given (includes inlined between
/// <c>@@&gt;path</c> and <c>@@&lt;path</c> lines), or its error with the places it names (the first the line in the
/// shader, the last the line in the include it happened in); the regions of the shader its branches kept and dropped;
/// and the files it included.</summary>
public sealed record GodotShaderPreprocessed(string Code, string? Error, IReadOnlyList<GodotShaderFilePosition> ErrorPositions,
    IReadOnlyList<GodotShaderRegion> Regions, IReadOnlyList<string> Included);

/// <summary>Godot's shader preprocessor (a port of servers/rendering/shader_preprocessor.cpp, MIT): comments stripped,
/// directives obeyed (<c>#define</c> with arguments and <c>##</c>, <c>#undef</c>, <c>#if</c> through Godot's own
/// expression reader, <c>#ifdef</c>, <c>#ifndef</c>, <c>#elif</c>, <c>#else</c>, <c>#endif</c>, <c>#include</c>,
/// <c>#pragma disable_preprocessor</c>, <c>#error</c>), macros expanded at each line's end, the renderer's own defines
/// given, and its first error kept in Godot's words. A Godot before 4.8 reads line continuations as 4.7 did. Pure.</summary>
public sealed class GodotShaderPreprocessor
{
    /// <summary>The first Godot whose line continuations are read one after another only.</summary>
    private static readonly Version Continuations48 = new(4, 8);

    private readonly List<char> output = [];
    private State state = null!;

    private GodotShaderPreprocessor()
    {
    }

    /// <summary>A shader's code as Godot's compiler is given it, read at <paramref name="filename"/> (its res:// path)
    /// under <paramref name="renderingMethod"/>; each include read through <paramref name="include"/> by its res:// path.</summary>
    public static GodotShaderPreprocessed Preprocess(string code, string filename, string renderingMethod, Func<string, GodotShaderIncluded?> include,
        Version? godot = null)
    {
        State state = new(include, godot is not null && godot < Continuations48)
        {
            CurrentFilename = filename,
            SaveRegions = true,
        };
        state.Defines["CURRENT_RENDERER"] = new Define([], renderingMethod switch
        {
            GodotDefinitions.ForwardPlus => "2",
            "mobile" => "1",
            _ => "0",
        }, Builtin: true);
        state.Defines["RENDERER_COMPATIBILITY"] = new Define([], "0", Builtin: true);
        state.Defines["RENDERER_MOBILE"] = new Define([], "1", Builtin: true);
        state.Defines["RENDERER_FORWARD_PLUS"] = new Define([], "2", Builtin: true);
        GodotShaderPreprocessor processor = new();
        bool done = processor.Run(state, code, out string result);
        return new GodotShaderPreprocessed(done ? result : string.Empty, done ? null : state.Error, state.IncludePositions,
            [.. state.Regions.Where(r => r.File == filename).Select(r => new GodotShaderRegion(r.File, r.FromLine, r.ToLine, r.Enabled))],
            [.. state.Includes]);
    }

    private sealed record Define(IReadOnlyList<string> Arguments, string Body, bool Builtin = false);

    private sealed class Branch(bool condition, Branch? parent)
    {
        public List<bool> Conditions { get; } = [condition];

        public Branch? Parent { get; } = parent;

        public bool ElseDefined { get; set; }
    }

    private sealed class Region(string file, int from, bool enabled, Region? parent)
    {
        public string File { get; } = file;

        public int FromLine { get; } = from;

        public int ToLine { get; set; } = -1;

        public bool Enabled { get; } = enabled;

        public Region? Parent { get; } = parent;
    }

    private sealed class State(Func<string, GodotShaderIncluded?> include, bool legacy)
    {
        public Func<string, GodotShaderIncluded?> Include { get; } = include;

        public bool LegacyContinuations { get; } = legacy;

        public SortedDictionary<string, Define> Defines { get; } = new(StringComparer.Ordinal);

        public Branch? CurrentBranch { get; set; }

        public int ConditionDepth { get; set; }

        public HashSet<string> Includes { get; } = new(StringComparer.Ordinal);

        public List<ulong> CyclicIncludeHashes { get; } = [];

        public int IncludeDepth { get; set; }

        public string CurrentFilename { get; set; } = string.Empty;

        public string Error { get; set; } = string.Empty;

        public List<GodotShaderFilePosition> IncludePositions { get; } = [];

        public bool SaveRegions { get; init; }

        public List<Region> Regions { get; } = [];

        public Region? PreviousRegion { get; set; }

        public bool Disabled { get; set; }
    }

    private readonly record struct Token(char Text, int Line);

    /// <summary>The real preprocessor's reader of the code, a character at a time, its line counted from zero.</summary>
    private sealed class Tokenizer(string code, bool legacy)
    {
        private readonly int size = code.Length;

        public int Line { get; set; }

        public int Index { get; set; }

        public List<Token> Generated { get; } = [];

        private char Next() => Index < size ? code[Index++] : '\0';

        public char Peek() => Index < size ? code[Index] : '\0';

        public void GetAndClearGenerated(List<char> output)
        {
            foreach (Token token in Generated)
            {
                output.Add(token.Text);
            }
            Generated.Clear();
        }

        public void Backtrack(char what)
        {
            while (Index >= 0)
            {
                char c = Index < size ? code[Index] : '\0';
                if (c == what)
                {
                    break;
                }
                Index--;
            }
        }

        /// <summary>Line continuations ("\" before a line's end) from where the reader is, each a line break generated.</summary>
        private int ConsumeLineContinuations(int offset)
        {
            int skips = 0;
            if (legacy)
            {
                for (int i = Index + offset; i < size; i++)
                {
                    char c = code[i];
                    if (c == '\\')
                    {
                        if (i + 1 < size && code[i + 1] == '\n')
                        {
                            Generated.Add(new Token('\n', Line));
                            Line++;
                            skips++;
                            i += 2;
                            Index = i;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (!IsWhitespace(c))
                    {
                        break;
                    }
                }
                return skips;
            }
            for (; Index < size; Index++)
            {
                if (code[Index] != '\\')
                {
                    break;
                }
                if (Index + 1 < size && code[Index + 1] == '\n')
                {
                    Generated.Add(new Token('\n', Line));
                    Line++;
                    skips++;
                    Index++;
                }
                else
                {
                    break;
                }
            }
            return skips;
        }

        public List<Token> Advance(char what)
        {
            List<Token> tokens = [];
            while (Index < size)
            {
                char c = code[Index];
                if (legacy)
                {
                    Index++;
                    if (c == '\\' && ConsumeLineContinuations(-1) > 0)
                    {
                        continue;
                    }
                }
                else
                {
                    if (c == '\\' && ConsumeLineContinuations(0) > 0)
                    {
                        continue;
                    }
                    Index++;
                }
                if (c == '\n')
                {
                    Generated.Add(new Token('\n', Line));
                    Line++;
                }
                tokens.Add(new Token(c, Line));
                if (c == what || c == '\0')
                {
                    return tokens;
                }
            }
            return [];
        }

        public void SkipWhitespace()
        {
            while (IsCharSpace(Peek()))
            {
                Next();
            }
        }

        public bool ConsumeEmptyLine() => StripEdges(TokensToString(Advance('\n'))).Length == 0;

        public string GetIdentifier(bool started = false)
        {
            StringBuilder text = new();
            while (true)
            {
                char c = Peek();
                if (c == '\\' && ConsumeLineContinuations(0) > 0)
                {
                    continue;
                }
                if (IsCharEnd(c) || c is '(' or ')' or ',' or ';')
                {
                    break;
                }
                if (IsWhitespace(c) && started)
                {
                    break;
                }
                if (!IsWhitespace(c))
                {
                    started = true;
                }
                char n = Next();
                if (started)
                {
                    text.Append(n);
                }
            }
            string id = text.ToString();
            return IsValidAsciiIdentifier(id) ? id : string.Empty;
        }

        public string PeekIdentifier()
        {
            int original = Index;
            int originalLine = Line;
            string id = GetIdentifier();
            Index = original;
            Line = originalLine;
            return id;
        }

        public Token GetToken()
        {
            while (Index < size)
            {
                char c = code[Index++];
                Token t = new(c, Line);
                switch (c)
                {
                    case ' ':
                    case '\t':
                        SkipWhitespace();
                        return new Token(' ', Line);
                    case '\n':
                        Line++;
                        return t;
                    default:
                        return t;
                }
            }
            return new Token('\0', Line);
        }
    }

    /// <summary>The code without its comments, their line breaks kept, and a block comment left open said.</summary>
    private static (string Stripped, string Error, int ErrorLine) StripComments(string code)
    {
        StringBuilder stripped = new(code.Length);
        int index = 0;
        int line = 0;
        int commentLineOpen = 0;
        int commentsOpen = 0;
        int stringsOpen = 0;
        while (index < code.Length)
        {
            char c = code[index++];
            if (c == '"')
            {
                stringsOpen += stringsOpen <= 0 ? 1 : -1;
                stripped.Append(c);
            }
            else if (c == '/' && stringsOpen == 0)
            {
                char p = Peek();
                if (p == '/')
                {
                    Advance('\n');
                }
                else if (p == '*')
                {
                    index++;
                    commentLineOpen = line;
                    commentsOpen++;
                    while (Advance('*'))
                    {
                        if (Peek() == '/')
                        {
                            commentsOpen--;
                            index++;
                            break;
                        }
                    }
                }
                else
                {
                    stripped.Append(c);
                }
            }
            else if (c == '*' && stringsOpen == 0)
            {
                if (Peek() == '/')
                {
                    commentLineOpen = line;
                    commentsOpen--;
                }
                else
                {
                    stripped.Append(c);
                }
            }
            else
            {
                if (c == '\n')
                {
                    line++;
                }
                stripped.Append(c);
            }
        }
        return (stripped.ToString(), commentsOpen != 0 ? "Block comment mismatch" : string.Empty, commentsOpen != 0 ? commentLineOpen : -1);

        char Peek() => index < code.Length ? code[index] : '\0';

        bool Advance(char what)
        {
            while (index < code.Length)
            {
                char c = code[index++];
                if (c == '\n')
                {
                    line++;
                    stripped.Append('\n');
                }
                if (c == what)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static bool IsCharWord(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsCharSpace(char c) => c is ' ' or '\t';

    private static bool IsCharEnd(char c) => c is '\n' or '\0';

    /// <summary>Godot's is_whitespace: a space, a tab, a line feed, a vertical tab, a form feed, a carriage return.</summary>
    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static bool IsValidAsciiIdentifier(string id) =>
        id.Length > 0 && !char.IsAsciiDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Godot's strip_edges: every character up to a space taken from both ends.</summary>
    private static string StripEdges(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && text[start] <= ' ')
        {
            start++;
        }
        while (end > start && text[end - 1] <= ' ')
        {
            end--;
        }
        return text[start..end];
    }

    private static string TokensToString(List<Token> tokens) => string.Concat(tokens.Select(t => t.Text));

    /// <summary>The code preprocessed into the output, false at its first error, which the state keeps.</summary>
    private bool Run(State given, string code, out string result)
    {
        output.Clear();
        state = given;
        result = string.Empty;
        (string stripped, string stripError, int stripLine) = StripComments(code);
        if (stripError.Length > 0)
        {
            SetError(stripError, stripLine);
            return false;
        }
        ulong hash = Hash64(code);
        state.CyclicIncludeHashes.Add(hash);
        Tokenizer tokenizer = new(stripped, state.LegacyContinuations);
        int lastSize = 0;
        bool symbolsBeforeDirective = false;
        while (true)
        {
            Token t = tokenizer.GetToken();
            if (t.Text == '\0')
            {
                break;
            }
            tokenizer.GetAndClearGenerated(output);
            if (state.Disabled)
            {
                output.Add(t.Text);
                continue;
            }
            if (t.Text == '#')
            {
                if (symbolsBeforeDirective)
                {
                    SetError("Invalid symbols placed before directive.", tokenizer.Line);
                    state.CyclicIncludeHashes.Remove(hash);
                    return false;
                }
                ProcessDirective(tokenizer);
            }
            else
            {
                if (IsCharEnd(t.Text))
                {
                    ExpandOutputMacros(lastSize, tokenizer.Line);
                    lastSize = output.Count;
                    symbolsBeforeDirective = false;
                }
                else if (!IsCharSpace(t.Text))
                {
                    symbolsBeforeDirective = true;
                }
                output.Add(t.Text);
            }
            if (state.Error.Length > 0)
            {
                state.CyclicIncludeHashes.Remove(hash);
                return false;
            }
        }
        state.CyclicIncludeHashes.Remove(hash);
        if (!state.Disabled)
        {
            if (state.ConditionDepth != 0)
            {
                SetError("Unmatched conditional statement.", tokenizer.Line);
                return false;
            }
            ExpandOutputMacros(lastSize, tokenizer.Line);
        }
        result = new string([.. output]);
        return state.Error.Length == 0;
    }

    private void ProcessDirective(Tokenizer tokenizer)
    {
        switch (tokenizer.GetIdentifier(started: true))
        {
            case "if":
                ProcessIf(tokenizer, elif: false);
                break;
            case "ifdef":
                ProcessIfdef(tokenizer, negate: false);
                break;
            case "ifndef":
                ProcessIfdef(tokenizer, negate: true);
                break;
            case "elif":
                ProcessIf(tokenizer, elif: true);
                break;
            case "else":
                ProcessElse(tokenizer);
                break;
            case "endif":
                ProcessEndif(tokenizer);
                break;
            case "error":
                ProcessError(tokenizer);
                break;
            case "define":
                ProcessDefine(tokenizer);
                break;
            case "undef":
                ProcessUndef(tokenizer);
                break;
            case "include":
                ProcessInclude(tokenizer);
                break;
            case "pragma":
                ProcessPragma(tokenizer);
                break;
            default:
                SetError("Unknown directive.", tokenizer.Line);
                break;
        }
    }

    private void ProcessDefine(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        string label = tokenizer.GetIdentifier();
        if (label.Length == 0)
        {
            SetError("Invalid macro name.", line);
            return;
        }
        if (state.Defines.ContainsKey(label))
        {
            SetError("Macro redefinition.", line);
            return;
        }
        List<string> arguments = [];
        if (tokenizer.Peek() == '(')
        {
            tokenizer.GetToken();
            while (true)
            {
                string name = tokenizer.GetIdentifier();
                if (name.Length == 0)
                {
                    SetError("Invalid argument name.", line);
                    return;
                }
                arguments.Add(name);
                tokenizer.SkipWhitespace();
                char next = tokenizer.GetToken().Text;
                if (next == ')')
                {
                    break;
                }
                if (next != ',')
                {
                    SetError("Expected a comma in the macro argument list.", line);
                    return;
                }
            }
        }
        string body = StripEdges(TokensToString(tokenizer.Advance('\n')));
        if (body.StartsWith("##", StringComparison.Ordinal))
        {
            SetError("'##' must not appear at beginning of macro expansion.", line);
            return;
        }
        if (body.EndsWith("##", StringComparison.Ordinal))
        {
            SetError("'##' must not appear at end of macro expansion.", line);
            return;
        }
        state.Defines[label] = new Define(arguments, body);
    }

    /// <summary>An <c>#if</c>, or an <c>#elif</c> of the branch open.</summary>
    private void ProcessIf(Tokenizer tokenizer, bool elif)
    {
        int line = tokenizer.Line;
        if (elif)
        {
            if (state.CurrentBranch is null || state.CurrentBranch.ElseDefined)
            {
                SetError("Unmatched 'elif' directive.", line);
                return;
            }
            if (state.PreviousRegion is not null)
            {
                state.PreviousRegion.ToLine = line - 1;
            }
        }
        string body = StripEdges(TokensToString(tokenizer.Advance('\n')));
        if (body.Length == 0)
        {
            SetError("Missing condition.", line);
            return;
        }
        if (ExpandCondition(body, line) is not { } condition || ExpandMacros(condition, line) is not { } expanded)
        {
            return;
        }
        GodotShaderExpression expression = new(expanded);
        if (expression.Error is { } error)
        {
            SetError(error, line);
            return;
        }
        if (expression.Value() is not { } value)
        {
            SetError(expression.Error ?? "Condition evaluation error.", line);
            return;
        }
        if (!elif)
        {
            StartBranchCondition(tokenizer, value, carryOn: false);
            if (state.SaveRegions)
            {
                AddRegion(line + 1, value, state.PreviousRegion);
            }
            return;
        }
        bool success = !state.CurrentBranch!.Conditions.Any(c => c) && value;
        StartBranchCondition(tokenizer, success, carryOn: true);
        if (state.SaveRegions)
        {
            AddRegion(line + 1, success, state.PreviousRegion?.Parent);
        }
    }

    private void ProcessElse(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        if (state.CurrentBranch is null || state.CurrentBranch.ElseDefined)
        {
            SetError("Unmatched 'else' directive.", line);
            return;
        }
        if (state.PreviousRegion is not null)
        {
            state.PreviousRegion.ToLine = line - 1;
        }
        if (!tokenizer.ConsumeEmptyLine())
        {
            SetError("Invalid 'else' directive.", line);
        }
        bool skip = state.CurrentBranch.Conditions.Any(c => c);
        state.CurrentBranch.ElseDefined = true;
        if (state.SaveRegions)
        {
            AddRegion(line + 1, !skip, state.PreviousRegion?.Parent);
        }
        if (skip)
        {
            NextDirective(tokenizer, ["endif"]);
        }
    }

    private void ProcessEndif(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        state.ConditionDepth--;
        if (state.ConditionDepth < 0)
        {
            SetError("Unmatched 'endif' directive.", line);
            return;
        }
        if (state.PreviousRegion is not null)
        {
            state.PreviousRegion.ToLine = line - 1;
            state.PreviousRegion = state.PreviousRegion.Parent;
        }
        if (!tokenizer.ConsumeEmptyLine())
        {
            SetError("Invalid 'endif' directive.", line);
        }
        state.CurrentBranch = state.CurrentBranch?.Parent;
    }

    private void ProcessError(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        string body = StripEdges(TokensToString(tokenizer.Advance('\n')));
        SetError(body.Length == 0 ? " " : body, line);
        if (!tokenizer.ConsumeEmptyLine())
        {
            SetError("Invalid 'error' directive.", line);
        }
    }

    /// <summary>An <c>#ifdef</c>, or with <paramref name="negate"/> an <c>#ifndef</c>.</summary>
    private void ProcessIfdef(Tokenizer tokenizer, bool negate)
    {
        int line = tokenizer.Line;
        string label = tokenizer.GetIdentifier();
        if (label.Length == 0)
        {
            SetError("Invalid macro name.", line);
            return;
        }
        if (!tokenizer.ConsumeEmptyLine())
        {
            SetError(negate ? "Invalid 'ifndef' directive." : "Invalid 'ifdef' directive.", line);
            return;
        }
        bool success = state.Defines.ContainsKey(label) != negate;
        StartBranchCondition(tokenizer, success, carryOn: false);
        if (state.SaveRegions)
        {
            AddRegion(line + 1, success, state.PreviousRegion);
        }
    }

    private void ProcessInclude(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        tokenizer.Advance('"');
        string path = TokensToString(tokenizer.Advance('"'));
        path = path.Length > 0 ? path[..^1] : path;
        if (path.Length == 0 || !tokenizer.ConsumeEmptyLine())
        {
            SetError("Invalid path.", line);
            return;
        }
        path = GodotPathStrings.SimplifyPath(path);
        if (!GodotPathStrings.IsAbsolutePath(path))
        {
            path = GodotPathStrings.PathJoin(GodotPathStrings.GetBaseDir(state.CurrentFilename), path);
        }
        string realPath = GodotPathStrings.SimplifyPath(path);
        if (state.Include(realPath) is not { Exists: true } included)
        {
            SetError($"Shader include file does not exist: {path}", line);
            return;
        }
        if (!included.IsInclude)
        {
            SetError("Shader include resource type is wrong.", line);
            return;
        }
        if (included.Code.Length > 0 && state.CyclicIncludeHashes.Contains(Hash64(included.Code)))
        {
            SetError($"Cyclic include found: {path}", line);
            return;
        }
        if (!state.Includes.Add(realPath))
        {
            return;
        }
        state.IncludeDepth++;
        if (state.IncludeDepth > 25)
        {
            SetError("Shader max include depth exceeded.", line);
            return;
        }
        string oldFilename = state.CurrentFilename;
        state.CurrentFilename = realPath;
        int conditionDepth = state.ConditionDepth;
        state.ConditionDepth = 0;
        state.IncludePositions.Add(new GodotShaderFilePosition(state.CurrentFilename, line + 1));
        GodotShaderPreprocessor processor = new();
        processor.Run(state, included.Code, out string result);
        AddToOutput($"@@>{realPath}\n");
        AddToOutput(result);
        AddToOutput($"\n@@<{realPath}\n");
        if (state.Error.Length > 0)
        {
            return;
        }
        state.CurrentFilename = oldFilename;
        state.IncludePositions.RemoveAt(state.IncludePositions.Count - 1);
        state.IncludeDepth--;
        state.ConditionDepth = conditionDepth;
    }

    private void ProcessPragma(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        string label = tokenizer.GetIdentifier();
        if (label != "disable_preprocessor")
        {
            SetError("Invalid 'pragma' directive.", line);
            return;
        }
        state.Disabled = true;
        if (!tokenizer.ConsumeEmptyLine())
        {
            SetError("Invalid 'pragma' directive.", line);
        }
    }

    private void ProcessUndef(Tokenizer tokenizer)
    {
        int line = tokenizer.Line;
        string label = tokenizer.GetIdentifier();
        if (label.Length == 0 || !tokenizer.ConsumeEmptyLine())
        {
            SetError("Invalid 'undef' directive.", line);
            return;
        }
        if (state.Defines.TryGetValue(label, out Define? define))
        {
            if (define.Builtin)
            {
                SetError("Cannot use 'undef' on built-in define.", line);
                return;
            }
            state.Defines.Remove(label);
        }
    }

    private void AddRegion(int line, bool enabled, Region? parent)
    {
        Region region = new(state.CurrentFilename, line, enabled, parent);
        state.Regions.Add(region);
        state.PreviousRegion = region;
    }

    private void StartBranchCondition(Tokenizer tokenizer, bool success, bool carryOn)
    {
        if (!carryOn)
        {
            state.ConditionDepth++;
            state.CurrentBranch = new Branch(success, state.CurrentBranch);
        }
        else
        {
            state.CurrentBranch!.Conditions.Add(success);
        }
        if (!success)
        {
            NextDirective(tokenizer, ["elif", "else", "endif"]);
        }
    }

    private void ExpandOutputMacros(int start, int line)
    {
        string text = new([.. output.Skip(start)]);
        if (ExpandMacros(text, line - 1) is not { } expanded)
        {
            return;
        }
        output.RemoveRange(start, output.Count - start);
        AddToOutput(expanded);
    }

    /// <summary>A condition's <c>defined(NAME)</c> and <c>defined NAME</c> made <c>true</c> or <c>false</c>, its brackets
    /// checked first; null at an error.</summary>
    private string? ExpandCondition(string text, int line)
    {
        int open = text.Count(c => c == '(');
        int close = text.Count(c => c == ')');
        if (open > close)
        {
            SetError("Expected a ')'.", line);
            return null;
        }
        if (close > open)
        {
            SetError("Expected a '('.", line);
            return null;
        }
        string result = text;
        int index = 0;
        int indexStart = 0;
        int indexEnd = 0;
        while (FindMatch(result, "defined", ref index, ref indexStart))
        {
            bool openBracket = false;
            bool foundWord = false;
            bool wordCompleted = false;
            StringBuilder name = new();
            int postBracket = -1;
            for (int i = indexStart - 1; i <= result.Length; i++)
            {
                char c = i < result.Length ? result[i] : '\0';
                if (c == '\0')
                {
                    if (foundWord)
                    {
                        wordCompleted = true;
                    }
                    break;
                }
                string s = c.ToString();
                bool space = IsCharSpace(c);
                if (wordCompleted)
                {
                    if (c == ')')
                    {
                        continue;
                    }
                    if (c is '|' or '&')
                    {
                        if (openBracket)
                        {
                            SetError($"Unexpected token: '{s}'.", line);
                            return null;
                        }
                        break;
                    }
                    if (!space)
                    {
                        SetError($"Unexpected token: '{s}'.", line);
                        return null;
                    }
                }
                else if (space)
                {
                    if (foundWord && !openBracket)
                    {
                        indexEnd = i;
                        wordCompleted = true;
                    }
                }
                else if (c == '(')
                {
                    if (openBracket)
                    {
                        SetError($"Unexpected token: '{s}'.", line);
                        return null;
                    }
                    openBracket = true;
                }
                else if (c == ')')
                {
                    if (openBracket)
                    {
                        if (!foundWord)
                        {
                            SetError($"Unexpected token: '{s}'.", line);
                            return null;
                        }
                        openBracket = false;
                        postBracket = i + 1;
                    }
                    else
                    {
                        indexEnd = i;
                    }
                    wordCompleted = true;
                }
                else if (IsCharWord(c))
                {
                    name.Append(c);
                    foundWord = true;
                }
                else
                {
                    SetError($"Unexpected token: '{s}'.", line);
                    return null;
                }
            }
            if (!wordCompleted)
            {
                SetError("Invalid macro name.", line);
                return null;
            }
            if (openBracket)
            {
                SetError("Expected a ')'.", line);
                return null;
            }
            if (postBracket != -1)
            {
                indexEnd = postBracket;
            }
            string body = state.Defines.ContainsKey(name.ToString()) ? "true" : "false";
            string before = result;
            result = result[..index] + body;
            indexStart = result.Length;
            if (indexEnd > 0)
            {
                result += before[Math.Min(indexEnd, before.Length)..];
            }
        }
        return result;
    }

    /// <summary>The text with every macro expanded until none is left, fifty passes at most; null at an error.</summary>
    private string? ExpandMacros(string text, int line)
    {
        string iterative = text;
        int passes = 0;
        bool expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach ((string key, Define define) in state.Defines)
            {
                if (ExpandMacrosOnce(iterative, line, key, define) is { } once)
                {
                    iterative = once;
                    expanded = true;
                }
            }
            passes++;
            if (passes > 50)
            {
                SetError("Macro expansion limit exceeded.", line);
                break;
            }
        }
        return state.Error.Length == 0 ? iterative : null;
    }

    private string? ExpandMacrosOnce(string text, int line, string key, Define define)
    {
        int indexStart = 0;
        int index = 0;
        if (!FindMatch(text, key, ref index, ref indexStart))
        {
            return null;
        }
        string body = define.Body;
        if (define.Arguments.Count == 0)
        {
            body = ConcatenateMacroBody(body);
            return text[..index] + " " + body + " " + text[(index + key.Length)..];
        }
        int argsStart = -1;
        int argsEnd = -1;
        int bracketsOpen = 0;
        List<string> args = [];
        for (int i = indexStart - 1; i < text.Length; i++)
        {
            bool addArgument = false;
            bool reachedEnd = false;
            char c = text[i];
            if (c == '(')
            {
                bracketsOpen++;
                if (bracketsOpen == 1)
                {
                    argsStart = i + 1;
                    argsEnd = -1;
                }
            }
            else if (c == ')')
            {
                bracketsOpen--;
                if (bracketsOpen == 0)
                {
                    argsEnd = i;
                    addArgument = true;
                    reachedEnd = true;
                }
            }
            else if (c == ',' && bracketsOpen == 1)
            {
                argsEnd = i;
                addArgument = true;
            }
            if (addArgument)
            {
                if (argsStart == -1 || argsEnd == -1)
                {
                    SetError("Invalid macro argument list.", line);
                    return null;
                }
                string arg = StripEdges(text[argsStart..argsEnd]);
                if (arg.Length == 0)
                {
                    SetError("Invalid macro argument.", line);
                    return null;
                }
                args.Add(arg);
                argsStart = argsEnd + 1;
            }
            if (reachedEnd)
            {
                break;
            }
        }
        if (args.Count != define.Arguments.Count)
        {
            SetError("Invalid macro argument count.", line);
            return null;
        }
        for (int i = 0; i < args.Count; i++)
        {
            string name = define.Arguments[i];
            int argIndexStart = 0;
            int argIndex = 0;
            while (FindMatch(body, name, ref argIndex, ref argIndexStart))
            {
                body = body[..argIndex] + args[i] + body[(argIndex + name.Length)..];
                argIndexStart = argIndex + args[i].Length + 1;
            }
        }
        body = ConcatenateMacroBody(body);
        return text[..index] + " " + body + " " + text[(argsEnd + 1)..];
    }

    /// <summary>Whether a word stands alone in the text from <paramref name="indexStart"/>, as a regular expression's \b
    /// would find it: where, and where the next search begins.</summary>
    private static bool FindMatch(string text, string value, ref int index, ref int indexStart)
    {
        index = indexStart <= text.Length ? text.IndexOf(value, indexStart, StringComparison.Ordinal) : -1;
        while (index > -1)
        {
            if (index > 0 && IsCharWord(text[index - 1]))
            {
                indexStart = index + 1;
                index = indexStart <= text.Length ? text.IndexOf(value, indexStart, StringComparison.Ordinal) : -1;
                continue;
            }
            if (index + value.Length < text.Length && IsCharWord(text[index + value.Length]))
            {
                indexStart = index + value.Length + 1;
                index = indexStart <= text.Length ? text.IndexOf(value, indexStart, StringComparison.Ordinal) : -1;
                continue;
            }
            indexStart = index + value.Length + 1;
            return true;
        }
        return false;
    }

    /// <summary>A macro's body with each <c>##</c> joining what is either side of it, the spaces beside it dropped.</summary>
    private static string ConcatenateMacroBody(string body)
    {
        int start = body.IndexOf("##", StringComparison.Ordinal);
        while (start > -1)
        {
            int end = start + 2;
            bool concat = true;
            while (end < body.Length && body[end] == '#')
            {
                end++;
                concat = false;
            }
            if (!concat)
            {
                start = body.IndexOf("##", end, StringComparison.Ordinal);
                continue;
            }
            while (end < body.Length && IsCharSpace(body[end]))
            {
                end++;
            }
            while (start >= 1 && IsCharSpace(body[start - 1]))
            {
                start--;
            }
            body = body[..start] + body[end..];
            start = body.IndexOf("##", start, StringComparison.Ordinal);
        }
        return body;
    }

    /// <summary>Skips to the next of these directives at this nesting, left for the main loop to read.</summary>
    private void NextDirective(Tokenizer tokenizer, string[] directives)
    {
        int line = tokenizer.Line;
        int nesting = 0;
        while (true)
        {
            tokenizer.Advance('#');
            string id = tokenizer.PeekIdentifier();
            if (id.Length == 0)
            {
                break;
            }
            if (nesting == 0 && directives.Contains(id))
            {
                tokenizer.Backtrack('#');
                return;
            }
            if (id is "ifdef" or "ifndef" or "if")
            {
                nesting++;
            }
            else if (id == "endif")
            {
                nesting--;
            }
        }
        SetError("Can't find matching branch directive.", line);
    }

    private void AddToOutput(string text) => output.AddRange(text);

    private void SetError(string error, int line)
    {
        if (state.Error.Length == 0)
        {
            state.Error = error;
            state.IncludePositions.Add(new GodotShaderFilePosition(state.CurrentFilename, line + 1));
        }
    }

    /// <summary>Godot's String::hash64 (djb2 over its characters as 64 bits), which tells one include's code from another.</summary>
    private static ulong Hash64(string text)
    {
        ulong hash = 5381;
        foreach (char c in text)
        {
            hash = ((hash << 5) + hash) + c;
        }
        return hash;
    }
}

using System.Globalization;
using System.Text;

namespace Passenger.Workspace.GodotShader;

/// <summary>The tokenizer of Godot's shading language (a port of ShaderLanguage::_get_token in
/// servers/rendering/shader_language.cpp, MIT): numbers checked as Godot checks them, keywords, strings, comments, and
/// the include markers the preprocessor leaves, which move the line count into an include and back.</summary>
public sealed partial class GodotShaderLanguage
{
    /// <summary>The tokens of Godot's shading language, in its order (TokenType).</summary>
    public enum Tk
    {
        Empty,
        Identifier,
        True,
        False,
        FloatConstant,
        IntConstant,
        UintConstant,
        StringConstant,
        TypeVoid,
        TypeBool,
        TypeBvec2,
        TypeBvec3,
        TypeBvec4,
        TypeInt,
        TypeIvec2,
        TypeIvec3,
        TypeIvec4,
        TypeUint,
        TypeUvec2,
        TypeUvec3,
        TypeUvec4,
        TypeFloat,
        TypeVec2,
        TypeVec3,
        TypeVec4,
        TypeMat2,
        TypeMat3,
        TypeMat4,
        TypeSampler2d,
        TypeIsampler2d,
        TypeUsampler2d,
        TypeSampler2darray,
        TypeIsampler2darray,
        TypeUsampler2darray,
        TypeSampler3d,
        TypeIsampler3d,
        TypeUsampler3d,
        TypeSamplercube,
        TypeSamplercubearray,
        TypeSamplerext,
        InterpolationFlat,
        InterpolationSmooth,
        Const,
        Struct,
        PrecisionLow,
        PrecisionMid,
        PrecisionHigh,
        OpEqual,
        OpNotEqual,
        OpLess,
        OpLessEqual,
        OpGreater,
        OpGreaterEqual,
        OpAnd,
        OpOr,
        OpNot,
        OpAdd,
        OpSub,
        OpMul,
        OpDiv,
        OpMod,
        OpShiftLeft,
        OpShiftRight,
        OpAssign,
        OpAssignAdd,
        OpAssignSub,
        OpAssignMul,
        OpAssignDiv,
        OpAssignMod,
        OpAssignShiftLeft,
        OpAssignShiftRight,
        OpAssignBitAnd,
        OpAssignBitOr,
        OpAssignBitXor,
        OpBitAnd,
        OpBitOr,
        OpBitXor,
        OpBitInvert,
        OpIncrement,
        OpDecrement,
        CfIf,
        CfElse,
        CfFor,
        CfWhile,
        CfDo,
        CfSwitch,
        CfCase,
        CfDefault,
        CfBreak,
        CfContinue,
        CfReturn,
        CfDiscard,
        BracketOpen,
        BracketClose,
        CurlyBracketOpen,
        CurlyBracketClose,
        ParenthesisOpen,
        ParenthesisClose,
        Question,
        Comma,
        Colon,
        Semicolon,
        Period,
        Uniform,
        UniformGroup,
        Instance,
        Global,
        Varying,
        ArgIn,
        ArgOut,
        ArgInout,
        RenderMode,
        StencilMode,
        HintDefaultWhiteTexture,
        HintDefaultBlackTexture,
        HintDefaultTransparentTexture,
        HintNormalTexture,
        HintRoughnessNormalTexture,
        HintRoughnessR,
        HintRoughnessG,
        HintRoughnessB,
        HintRoughnessA,
        HintRoughnessGray,
        HintAnisotropyTexture,
        HintSourceColor,
        HintColorConversionDisabled,
        HintRange,
        HintEnum,
        HintInstanceIndex,
        HintScreenTexture,
        HintNormalRoughnessTexture,
        HintDepthTexture,
        HintBlitSource0,
        HintBlitSource1,
        HintBlitSource2,
        HintBlitSource3,
        FilterNearest,
        FilterLinear,
        FilterNearestMipmap,
        FilterLinearMipmap,
        FilterNearestMipmapAnisotropic,
        FilterLinearMipmapAnisotropic,
        RepeatEnable,
        RepeatDisable,
        HintNoStorage,
        HintNoEditor,
        ShaderType,
        Cursor,
        Error,
        Eof,
    }

    /// <summary>A token: its kind, its text (an identifier's, a string's, an error's words), its number and its line.</summary>
    public readonly record struct Token(Tk Type, string Text, double Constant, int Line)
    {
        public bool IsIntegerConstant => Type is Tk.IntConstant or Tk.UintConstant;
    }

    /// <summary>A place to go back to: where the tokenizer reads, and its line.</summary>
    internal readonly record struct TkPos(int CharIdx, int TkLine);

    /// <summary>Godot's token_names, as it lists them (two entries fewer than the tokens, so the last ones name others).</summary>
    private static readonly string[] TokenNames =
    [
        "EMPTY", "IDENTIFIER", "TRUE", "FALSE", "FLOAT_CONSTANT", "INT_CONSTANT", "UINT_CONSTANT", "STRING_CONSTANT",
        "TYPE_VOID", "TYPE_BOOL", "TYPE_BVEC2", "TYPE_BVEC3", "TYPE_BVEC4", "TYPE_INT", "TYPE_IVEC2", "TYPE_IVEC3",
        "TYPE_IVEC4", "TYPE_UINT", "TYPE_UVEC2", "TYPE_UVEC3", "TYPE_UVEC4", "TYPE_FLOAT", "TYPE_VEC2", "TYPE_VEC3",
        "TYPE_VEC4", "TYPE_MAT2", "TYPE_MAT3", "TYPE_MAT4", "TYPE_SAMPLER2D", "TYPE_ISAMPLER2D", "TYPE_USAMPLER2D",
        "TYPE_SAMPLER2DARRAY", "TYPE_ISAMPLER2DARRAY", "TYPE_USAMPLER2DARRAY", "TYPE_SAMPLER3D", "TYPE_ISAMPLER3D",
        "TYPE_USAMPLER3D", "TYPE_SAMPLERCUBE", "TYPE_SAMPLERCUBEARRAY", "TYPE_SAMPLEREXT", "INTERPOLATION_FLAT",
        "INTERPOLATION_SMOOTH", "CONST", "STRUCT", "PRECISION_LOW", "PRECISION_MID", "PRECISION_HIGH", "OP_EQUAL",
        "OP_NOT_EQUAL", "OP_LESS", "OP_LESS_EQUAL", "OP_GREATER", "OP_GREATER_EQUAL", "OP_AND", "OP_OR", "OP_NOT",
        "OP_ADD", "OP_SUB", "OP_MUL", "OP_DIV", "OP_MOD", "OP_SHIFT_LEFT", "OP_SHIFT_RIGHT", "OP_ASSIGN",
        "OP_ASSIGN_ADD", "OP_ASSIGN_SUB", "OP_ASSIGN_MUL", "OP_ASSIGN_DIV", "OP_ASSIGN_MOD", "OP_ASSIGN_SHIFT_LEFT",
        "OP_ASSIGN_SHIFT_RIGHT", "OP_ASSIGN_BIT_AND", "OP_ASSIGN_BIT_OR", "OP_ASSIGN_BIT_XOR", "OP_BIT_AND",
        "OP_BIT_OR", "OP_BIT_XOR", "OP_BIT_INVERT", "OP_INCREMENT", "OP_DECREMENT", "CF_IF", "CF_ELSE", "CF_FOR",
        "CF_WHILE", "CF_DO", "CF_SWITCH", "CF_CASE", "CF_DEFAULT", "CF_BREAK", "CF_CONTINUE", "CF_RETURN",
        "CF_DISCARD", "BRACKET_OPEN", "BRACKET_CLOSE", "CURLY_BRACKET_OPEN", "CURLY_BRACKET_CLOSE",
        "PARENTHESIS_OPEN", "PARENTHESIS_CLOSE", "QUESTION", "COMMA", "COLON", "SEMICOLON", "PERIOD", "UNIFORM",
        "UNIFORM_GROUP", "INSTANCE", "GLOBAL", "VARYING", "ARG_IN", "ARG_OUT", "ARG_INOUT", "RENDER_MODE",
        "HINT_DEFAULT_WHITE_TEXTURE", "HINT_DEFAULT_BLACK_TEXTURE", "HINT_DEFAULT_TRANSPARENT_TEXTURE",
        "HINT_NORMAL_TEXTURE", "HINT_ROUGHNESS_NORMAL_TEXTURE", "HINT_ROUGHNESS_R", "HINT_ROUGHNESS_G",
        "HINT_ROUGHNESS_B", "HINT_ROUGHNESS_A", "HINT_ROUGHNESS_GRAY", "HINT_ANISOTROPY_TEXTURE", "HINT_SOURCE_COLOR",
        "HINT_COLOR_CONVERSION_DISABLED", "HINT_RANGE", "HINT_ENUM", "HINT_INSTANCE_INDEX", "HINT_SCREEN_TEXTURE",
        "HINT_NORMAL_ROUGHNESS_TEXTURE", "HINT_DEPTH_TEXTURE", "HINT_BLIT_SOURCE0", "HINT_BLIT_SOURCE1",
        "HINT_BLIT_SOURCE2", "HINT_BLIT_SOURCE3", "FILTER_NEAREST", "FILTER_LINEAR", "FILTER_NEAREST_MIPMAP",
        "FILTER_LINEAR_MIPMAP", "FILTER_NEAREST_MIPMAP_ANISOTROPIC", "FILTER_LINEAR_MIPMAP_ANISOTROPIC",
        "REPEAT_ENABLE", "REPEAT_DISABLE", "NO_STORAGE", "NO_EDITOR", "SHADER_TYPE", "CURSOR", "ERROR", "EOF",
    ];

    /// <summary>The keyword list: each keyword's token and text, and the shader types and functions it is not offered in.</summary>
    private static readonly (Tk Token, string Text, string[] ExcludedTypes, string[] ExcludedFunctions)[] Keywords =
    [
        (Tk.True, "true", [], []),
        (Tk.False, "false", [], []),
        (Tk.TypeVoid, "void", [], []),
        (Tk.TypeBool, "bool", [], []),
        (Tk.TypeBvec2, "bvec2", [], []),
        (Tk.TypeBvec3, "bvec3", [], []),
        (Tk.TypeBvec4, "bvec4", [], []),
        (Tk.TypeInt, "int", [], []),
        (Tk.TypeIvec2, "ivec2", [], []),
        (Tk.TypeIvec3, "ivec3", [], []),
        (Tk.TypeIvec4, "ivec4", [], []),
        (Tk.TypeUint, "uint", [], []),
        (Tk.TypeUvec2, "uvec2", [], []),
        (Tk.TypeUvec3, "uvec3", [], []),
        (Tk.TypeUvec4, "uvec4", [], []),
        (Tk.TypeFloat, "float", [], []),
        (Tk.TypeVec2, "vec2", [], []),
        (Tk.TypeVec3, "vec3", [], []),
        (Tk.TypeVec4, "vec4", [], []),
        (Tk.TypeMat2, "mat2", [], []),
        (Tk.TypeMat3, "mat3", [], []),
        (Tk.TypeMat4, "mat4", [], []),
        (Tk.TypeSampler2d, "sampler2D", [], []),
        (Tk.TypeIsampler2d, "isampler2D", [], []),
        (Tk.TypeUsampler2d, "usampler2D", [], []),
        (Tk.TypeSampler2darray, "sampler2DArray", [], []),
        (Tk.TypeIsampler2darray, "isampler2DArray", [], []),
        (Tk.TypeUsampler2darray, "usampler2DArray", [], []),
        (Tk.TypeSampler3d, "sampler3D", [], []),
        (Tk.TypeIsampler3d, "isampler3D", [], []),
        (Tk.TypeUsampler3d, "usampler3D", [], []),
        (Tk.TypeSamplercube, "samplerCube", [], []),
        (Tk.TypeSamplercubearray, "samplerCubeArray", [], []),
        (Tk.TypeSamplerext, "samplerExternalOES", [], []),
        (Tk.InterpolationFlat, "flat", [], []),
        (Tk.InterpolationSmooth, "smooth", [], []),
        (Tk.PrecisionLow, "lowp", [], []),
        (Tk.PrecisionMid, "mediump", [], []),
        (Tk.PrecisionHigh, "highp", [], []),
        (Tk.Uniform, "uniform", [], []),
        (Tk.UniformGroup, "group_uniforms", [], []),
        (Tk.Varying, "varying", ["particles", "sky", "fog"], []),
        (Tk.Const, "const", [], []),
        (Tk.Struct, "struct", [], []),
        (Tk.ShaderType, "shader_type", [], []),
        (Tk.RenderMode, "render_mode", [], []),
        (Tk.StencilMode, "stencil_mode", [], []),
        (Tk.Instance, "instance", [], []),
        (Tk.Global, "global", [], []),
        (Tk.CfIf, "if", [], []),
        (Tk.CfElse, "else", [], []),
        (Tk.CfFor, "for", [], []),
        (Tk.CfWhile, "while", [], []),
        (Tk.CfDo, "do", [], []),
        (Tk.CfSwitch, "switch", [], []),
        (Tk.CfCase, "case", [], []),
        (Tk.CfDefault, "default", [], []),
        (Tk.CfBreak, "break", [], []),
        (Tk.CfContinue, "continue", [], []),
        (Tk.CfReturn, "return", [], []),
        (Tk.CfDiscard, "discard", ["particles", "sky", "fog"], ["vertex"]),
        (Tk.ArgIn, "in", [], []),
        (Tk.ArgOut, "out", [], []),
        (Tk.ArgInout, "inout", [], []),
        (Tk.HintSourceColor, "source_color", [], []),
        (Tk.HintColorConversionDisabled, "color_conversion_disabled", [], []),
        (Tk.HintRange, "hint_range", [], []),
        (Tk.HintEnum, "hint_enum", [], []),
        (Tk.HintInstanceIndex, "instance_index", [], []),
        (Tk.HintNormalTexture, "hint_normal", [], []),
        (Tk.HintDefaultWhiteTexture, "hint_default_white", [], []),
        (Tk.HintDefaultBlackTexture, "hint_default_black", [], []),
        (Tk.HintDefaultTransparentTexture, "hint_default_transparent", [], []),
        (Tk.HintAnisotropyTexture, "hint_anisotropy", [], []),
        (Tk.HintRoughnessR, "hint_roughness_r", [], []),
        (Tk.HintRoughnessG, "hint_roughness_g", [], []),
        (Tk.HintRoughnessB, "hint_roughness_b", [], []),
        (Tk.HintRoughnessA, "hint_roughness_a", [], []),
        (Tk.HintRoughnessNormalTexture, "hint_roughness_normal", [], []),
        (Tk.HintRoughnessGray, "hint_roughness_gray", [], []),
        (Tk.HintScreenTexture, "hint_screen_texture", [], []),
        (Tk.HintNormalRoughnessTexture, "hint_normal_roughness_texture", [], []),
        (Tk.HintDepthTexture, "hint_depth_texture", [], []),
        (Tk.HintBlitSource0, "hint_blit_source0", [], []),
        (Tk.HintBlitSource1, "hint_blit_source1", [], []),
        (Tk.HintBlitSource2, "hint_blit_source2", [], []),
        (Tk.HintBlitSource3, "hint_blit_source3", [], []),
        (Tk.FilterNearest, "filter_nearest", [], []),
        (Tk.FilterLinear, "filter_linear", [], []),
        (Tk.FilterNearestMipmap, "filter_nearest_mipmap", [], []),
        (Tk.FilterLinearMipmap, "filter_linear_mipmap", [], []),
        (Tk.FilterNearestMipmapAnisotropic, "filter_nearest_mipmap_anisotropic", [], []),
        (Tk.FilterLinearMipmapAnisotropic, "filter_linear_mipmap_anisotropic", [], []),
        (Tk.RepeatEnable, "repeat_enable", [], []),
        (Tk.RepeatDisable, "repeat_disable", [], []),
        (Tk.HintNoStorage, "no_storage", [], []),
        (Tk.HintNoEditor, "no_editor", [], []),
    ];

    private string code = string.Empty;
    private int charIdx;
    private int tkLine;

    /// <summary>Where the token last read begins in the code, or the place gone back to.</summary>
    private int tokenStart;
    private readonly List<GodotShaderFilePosition> includePositions = [];
    private readonly HashSet<string> includeMarkersHandled = new(StringComparer.Ordinal);

    private TkPos GetTkPos() => new(charIdx, tkLine);

    private void SetTkPos(TkPos position)
    {
        charIdx = position.CharIdx;
        tkLine = position.TkLine;
        tokenStart = position.CharIdx;
    }

    private char GetChar(int offset) => charIdx + offset < code.Length && charIdx + offset >= 0 ? code[charIdx + offset] : '\0';

    private Token MakeToken(Tk type, string text = "")
    {
        if (type == Tk.Error)
        {
            SetError(text);
        }
        return new Token(type, text, 0, tkLine);
    }

    /// <summary>A token's text as Godot words it in a message: its name, with its number or text after it.</summary>
    public static string GetTokenText(Token token)
    {
        string name = (int)token.Type < TokenNames.Length ? TokenNames[(int)token.Type] : string.Empty;
        if (token.IsIntegerConstant || token.Type == Tk.FloatConstant)
        {
            return $"{name}({Rtos(token.Constant)})";
        }
        return token.Type is Tk.Identifier or Tk.Error ? $"{name}({token.Text})" : name;
    }

    /// <summary>A real as Godot's rtos writes it: up to 14 significant digits, no exponent for ordinary sizes.</summary>
    private static string Rtos(double value) => value.ToString("0.##############", CultureInfo.InvariantCulture);

    private static bool IsAsciiIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private Token GetToken()
    {
        while (true)
        {
            charIdx++;
            tokenStart = charIdx - 1;
            char c = GetChar(-1);
            switch (c)
            {
                case '\0':
                    return MakeToken(Tk.Eof);
                case '\t' or '\r' or ' ':
                    continue;
                case '\n':
                    tkLine++;
                    continue;
                case '/':
                    switch (GetChar(0))
                    {
                        case '*':
                            charIdx++;
                            while (true)
                            {
                                if (GetChar(0) == '\0')
                                {
                                    return MakeToken(Tk.Eof);
                                }
                                if (GetChar(0) == '*' && GetChar(1) == '/')
                                {
                                    charIdx += 2;
                                    break;
                                }
                                if (GetChar(0) == '\n')
                                {
                                    tkLine++;
                                }
                                charIdx++;
                            }
                            break;
                        case '/':
                            while (true)
                            {
                                if (GetChar(0) == '\n')
                                {
                                    tkLine++;
                                    charIdx++;
                                    break;
                                }
                                if (GetChar(0) == '\0')
                                {
                                    return MakeToken(Tk.Eof);
                                }
                                charIdx++;
                            }
                            break;
                        case '=':
                            charIdx++;
                            return MakeToken(Tk.OpAssignDiv);
                        default:
                            return MakeToken(Tk.OpDiv);
                    }
                    continue;
                case '=':
                    return Two('=', Tk.OpEqual, Tk.OpAssign);
                case '<':
                    if (GetChar(0) == '=')
                    {
                        charIdx++;
                        return MakeToken(Tk.OpLessEqual);
                    }
                    if (GetChar(0) == '<')
                    {
                        charIdx++;
                        return Two('=', Tk.OpAssignShiftLeft, Tk.OpShiftLeft);
                    }
                    return MakeToken(Tk.OpLess);
                case '>':
                    if (GetChar(0) == '=')
                    {
                        charIdx++;
                        return MakeToken(Tk.OpGreaterEqual);
                    }
                    if (GetChar(0) == '>')
                    {
                        charIdx++;
                        return Two('=', Tk.OpAssignShiftRight, Tk.OpShiftRight);
                    }
                    return MakeToken(Tk.OpGreater);
                case '!':
                    return Two('=', Tk.OpNotEqual, Tk.OpNot);
                case '"':
                    return StringConstant();
                case '{':
                    return MakeToken(Tk.CurlyBracketOpen);
                case '}':
                    return MakeToken(Tk.CurlyBracketClose);
                case '[':
                    return MakeToken(Tk.BracketOpen);
                case ']':
                    return MakeToken(Tk.BracketClose);
                case '(':
                    return MakeToken(Tk.ParenthesisOpen);
                case ')':
                    return MakeToken(Tk.ParenthesisClose);
                case ',':
                    return MakeToken(Tk.Comma);
                case ';':
                    return MakeToken(Tk.Semicolon);
                case '?':
                    return MakeToken(Tk.Question);
                case ':':
                    return MakeToken(Tk.Colon);
                case '^':
                    return Two('=', Tk.OpAssignBitXor, Tk.OpBitXor);
                case '~':
                    return MakeToken(Tk.OpBitInvert);
                case '&':
                    if (GetChar(0) == '=')
                    {
                        charIdx++;
                        return MakeToken(Tk.OpAssignBitAnd);
                    }
                    return Two('&', Tk.OpAnd, Tk.OpBitAnd);
                case '|':
                    if (GetChar(0) == '=')
                    {
                        charIdx++;
                        return MakeToken(Tk.OpAssignBitOr);
                    }
                    return Two('|', Tk.OpOr, Tk.OpBitOr);
                case '*':
                    return Two('=', Tk.OpAssignMul, Tk.OpMul);
                case '+':
                    if (GetChar(0) == '=')
                    {
                        charIdx++;
                        return MakeToken(Tk.OpAssignAdd);
                    }
                    return Two('+', Tk.OpIncrement, Tk.OpAdd);
                case '-':
                    if (GetChar(0) == '=')
                    {
                        charIdx++;
                        return MakeToken(Tk.OpAssignSub);
                    }
                    return Two('-', Tk.OpDecrement, Tk.OpSub);
                case '%':
                    return Two('=', Tk.OpAssignMod, Tk.OpMod);
                case '@':
                    if (IncludeMarker() is { } marker)
                    {
                        return marker;
                    }
                    continue;
                default:
                    charIdx--;
                    return Other();
            }
        }
    }

    /// <summary>The token of two characters where the next is <paramref name="second"/>, else of the first alone.</summary>
    private Token Two(char second, Tk both, Tk one)
    {
        if (GetChar(0) == second)
        {
            charIdx++;
            return MakeToken(both);
        }
        return MakeToken(one);
    }

    private Token StringConstant()
    {
        StringBuilder content = new();
        bool previousBackslash = false;
        while (true)
        {
            bool ended = false;
            char c = GetChar(0);
            if (c == '\0')
            {
                return MakeToken(Tk.Error, "EOF reached before string termination.");
            }
            switch (c)
            {
                case '"':
                    if (previousBackslash)
                    {
                        content.Append('"');
                        previousBackslash = false;
                    }
                    else
                    {
                        ended = true;
                    }
                    break;
                case '\\':
                    if (previousBackslash)
                    {
                        content.Append('\\');
                    }
                    previousBackslash = !previousBackslash;
                    break;
                case '\n':
                    return MakeToken(Tk.Error, "Unexpected end of string.");
                default:
                    if (previousBackslash)
                    {
                        return MakeToken(Tk.Error, "Only \\\" and \\\\ escape characters supported.");
                    }
                    content.Append(c);
                    break;
            }
            charIdx++;
            if (ended)
            {
                break;
            }
        }
        return MakeToken(Tk.StringConstant, content.ToString());
    }

    /// <summary>An include's enter (<c>@@&gt;path</c>) or exit (<c>@@&lt;path</c>) marker read, the line counted in the
    /// include from its start and back in the file including it after; an error token for anything else.</summary>
    private Token? IncludeMarker()
    {
        bool enter = GetChar(0) == '@' && GetChar(1) == '>';
        bool exit = GetChar(0) == '@' && GetChar(1) == '<';
        if (!enter && !exit)
        {
            return MakeToken(Tk.Error, "Invalid include enter/exit hint token (@@> and @@<)");
        }
        charIdx += 2;
        StringBuilder path = new();
        while (GetChar(0) != '\n' && charIdx < code.Length)
        {
            path.Append(GetChar(0));
            charIdx++;
        }
        string include = path.ToString();
        if (enter)
        {
            includePositions[^1] = includePositions[^1] with { Line = tkLine };
            if (includeMarkersHandled.Add(">>" + include))
            {
                tkLine = 0;
                includePositions.Add(new GodotShaderFilePosition(include, 0));
            }
            return null;
        }
        if (includeMarkersHandled.Add("<<" + include))
        {
            if (includePositions.Count == 1)
            {
                return MakeToken(Tk.Error, "Invalid include exit hint @@< without matching enter hint.");
            }
            includePositions.RemoveAt(includePositions.Count - 1);
        }
        tkLine = includePositions[^1].Line - 1;
        return null;
    }

    /// <summary>A number, a period, an identifier or keyword, or an unknown character.</summary>
    private Token Other()
    {
        if (char.IsAsciiDigit(GetChar(0)) || (GetChar(0) == '.' && char.IsAsciiDigit(GetChar(1))))
        {
            return Number();
        }
        if (GetChar(0) == '.')
        {
            charIdx++;
            return MakeToken(Tk.Period);
        }
        if (IsAsciiIdentifierChar(GetChar(0)))
        {
            int start = charIdx;
            while (IsAsciiIdentifierChar(GetChar(0)))
            {
                charIdx++;
            }
            string word = code[start..charIdx];
            foreach ((Tk token, string text, _, _) in Keywords)
            {
                if (word == text && !(Before48 && token is Tk.HintNoStorage or Tk.HintNoEditor))
                {
                    return MakeToken(token);
                }
            }
            return MakeToken(Tk.Identifier, word.Replace("dus_", "_", StringComparison.Ordinal));
        }
        char c = GetChar(0);
        return c > 32 ? MakeToken(Tk.Error, $"Tokenizer: Unknown character #{(int)c}: '{c}'") : MakeToken(Tk.Error, $"Tokenizer: Unknown character #{(int)c}");
    }

    private Token Number()
    {
        const int All = 0, HexaPeriod = 1, Exponent = 2, SignAfterExponent = 3, None = 4;
        bool hexaFound = false, periodFound = false, exponentFound = false, floatSuffixFound = false, uintSuffixFound = false, endSuffixFound = false;
        int lutCase = All;
        StringBuilder text = new();
        int i = 0;
        bool digitAfterExp = false;
        while (true)
        {
            char symbol = char.ToLowerInvariant(GetChar(i));
            bool error = false;
            if (char.IsAsciiDigit(symbol))
            {
                if (exponentFound)
                {
                    digitAfterExp = true;
                }
                if (endSuffixFound)
                {
                    error = true;
                }
            }
            else if (symbol < 0x7F && Suffix(lutCase, symbol))
            {
                if (symbol == 'x')
                {
                    hexaFound = true;
                    lutCase = HexaPeriod;
                }
                else if (symbol == '.')
                {
                    periodFound = true;
                    lutCase = HexaPeriod;
                }
                else if (symbol == 'e' && !hexaFound)
                {
                    exponentFound = true;
                    lutCase = Exponent;
                }
                else if (symbol == 'f' && !hexaFound)
                {
                    if (!periodFound && !exponentFound)
                    {
                        error = true;
                    }
                    floatSuffixFound = true;
                    endSuffixFound = true;
                    lutCase = None;
                }
                else if (symbol == 'u')
                {
                    uintSuffixFound = true;
                    endSuffixFound = true;
                    lutCase = None;
                }
                else if (symbol is '-' or '+')
                {
                    if (exponentFound)
                    {
                        lutCase = SignAfterExponent;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else if (!hexaFound || !char.IsAsciiHexDigit(symbol))
            {
                if (IsAsciiIdentifierChar(symbol))
                {
                    error = true;
                }
                else
                {
                    break;
                }
            }
            if (error)
            {
                return MakeToken(Tk.Error, hexaFound ? "Invalid (hexadecimal) numeric constant"
                    : periodFound || exponentFound || floatSuffixFound ? "Invalid (float) numeric constant"
                    : uintSuffixFound ? "Invalid (unsigned integer) numeric constant" : "Invalid (integer) numeric constant");
            }
            text.Append(symbol);
            i++;
        }
        string str = text.ToString();
        char last = str[^1];
        if (hexaFound)
        {
            if (uintSuffixFound)
            {
                str = str[..^1];
                charIdx++;
            }
            if (str.Length + 1 > 11 || !IsValidHexNumber(str))
            {
                return MakeToken(Tk.Error, "Invalid (hexadecimal) numeric constant");
            }
        }
        else if (periodFound || exponentFound || floatSuffixFound)
        {
            if (exponentFound && (!digitAfterExp || (!char.IsAsciiDigit(last) && last != 'f')))
            {
                return MakeToken(Tk.Error, "Invalid (float) numeric constant");
            }
            if (periodFound)
            {
                if (floatSuffixFound ? last != 'f' : last != '.' && !char.IsAsciiDigit(last))
                {
                    return MakeToken(Tk.Error, "Invalid (float) numeric constant");
                }
            }
            else if (floatSuffixFound && last != 'f')
            {
                return MakeToken(Tk.Error, "Invalid (float) numeric constant");
            }
            if (floatSuffixFound)
            {
                str = str[..^1];
                charIdx++;
            }
            if (!IsValidFloat(str))
            {
                return MakeToken(Tk.Error, "Invalid (float) numeric constant");
            }
        }
        else
        {
            if (uintSuffixFound)
            {
                str = str[..^1];
                charIdx++;
            }
            if (!IsValidInt(str))
            {
                return MakeToken(Tk.Error, uintSuffixFound ? "Invalid (unsigned integer) numeric constant" : "Invalid (integer) numeric constant");
            }
        }
        charIdx += str.Length;
        Tk type = periodFound || exponentFound || floatSuffixFound ? Tk.FloatConstant : uintSuffixFound ? Tk.UintConstant : Tk.IntConstant;
        double constant = hexaFound ? long.Parse(str[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ToFloat(str);
        return new Token(type, string.Empty, constant, tkLine);

        static bool Suffix(int lut, char t) => lut switch
        {
            All => t is '.' or 'x' or 'e' or 'f' or 'u' or '-' or '+',
            HexaPeriod => t is 'e' or 'f' or 'u',
            Exponent => t is 'f' or '-' or '+',
            SignAfterExponent => t == 'f',
            _ => false,
        };
    }

    /// <summary>Godot's String::is_valid_hex_number with its "0x" prefix.</summary>
    private static bool IsValidHexNumber(string text)
    {
        int from = text.Length != 1 && text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        if (text.Length < 3 || text[from] != '0' || text[from + 1] != 'x')
        {
            return false;
        }
        return text[(from + 2)..].All(char.IsAsciiHexDigit);
    }

    /// <summary>Godot's String::is_valid_float.</summary>
    private static bool IsValidFloat(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }
        int from = text[0] is '+' or '-' ? 1 : 0;
        bool exponentFound = false, periodFound = false, signFound = false, exponentValues = false, numbers = false;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                if (exponentFound)
                {
                    exponentValues = true;
                }
                else
                {
                    numbers = true;
                }
            }
            else if (numbers && !exponentFound && c == 'e')
            {
                exponentFound = true;
            }
            else if (!periodFound && !exponentFound && c == '.')
            {
                periodFound = true;
            }
            else if (c is '-' or '+' && exponentFound && !exponentValues && !signFound)
            {
                signFound = true;
            }
            else
            {
                return false;
            }
        }
        return numbers;
    }

    /// <summary>Godot's String::is_valid_int.</summary>
    private static bool IsValidInt(string text) =>
        text.Length > 0 && text[(text.Length != 1 && text[0] is '+' or '-' ? 1 : 0)..].All(char.IsAsciiDigit);

    /// <summary>A number's digits as a double, as Godot's to_float reads them ("1." and ".5" too).</summary>
    private static double ToFloat(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value
            : double.TryParse(text.EndsWith('.') ? text + "0" : text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;

    /// <summary>The next token, the tokenizer left where it was.</summary>
    private Token Peek()
    {
        TkPos before = GetTkPos();
        Token token = GetToken();
        SetTkPos(before);
        return token;
    }

    /// <summary>The token after the next, where it is on the same line.</summary>
    private bool LookupNext(out Token token)
    {
        TkPos before = GetTkPos();
        int line = before.TkLine;
        GetToken();
        Token next = GetToken();
        SetTkPos(before);
        token = next;
        return next.Line == line;
    }
}

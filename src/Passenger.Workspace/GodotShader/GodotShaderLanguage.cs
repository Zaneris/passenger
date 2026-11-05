using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's shading language compiler, ported to C# from servers/rendering/shader_language.cpp and
/// shader_language.h (MIT) with its node tree, its parse and its checks of types, names, calls and assignments, in
/// Godot's words. It compiles the code Godot's preprocessor made as Godot does (<see cref="Compile"/>), stopping at the
/// first error, and gathers Godot's warnings. One compile at a time, on any thread.</summary>
public sealed partial class GodotShaderLanguage
{
    public enum DataType
    {
        Void,
        Bool,
        Bvec2,
        Bvec3,
        Bvec4,
        Int,
        Ivec2,
        Ivec3,
        Ivec4,
        Uint,
        Uvec2,
        Uvec3,
        Uvec4,
        Float,
        Vec2,
        Vec3,
        Vec4,
        Mat2,
        Mat3,
        Mat4,
        Sampler2d,
        Isampler2d,
        Usampler2d,
        Sampler2darray,
        Isampler2darray,
        Usampler2darray,
        Sampler3d,
        Isampler3d,
        Usampler3d,
        Samplercube,
        Samplercubearray,
        Samplerext,
        Struct,
        Max,
    }

    public enum DataPrecision
    {
        Lowp,
        Mediump,
        Highp,
        Default,
    }

    public enum DataInterpolation
    {
        Flat,
        Smooth,
        Default,
    }

    public enum Operator
    {
        Equal,
        NotEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        And,
        Or,
        Not,
        Negate,
        Add,
        Sub,
        Mul,
        Div,
        Mod,
        ShiftLeft,
        ShiftRight,
        Assign,
        AssignAdd,
        AssignSub,
        AssignMul,
        AssignDiv,
        AssignMod,
        AssignShiftLeft,
        AssignShiftRight,
        AssignBitAnd,
        AssignBitOr,
        AssignBitXor,
        BitAnd,
        BitOr,
        BitXor,
        BitInvert,
        Increment,
        Decrement,
        SelectIf,
        SelectElse,
        PostIncrement,
        PostDecrement,
        Call,
        Construct,
        Struct,
        Index,
        Empty,
        Max,
    }

    public enum FlowOperation
    {
        If,
        Return,
        For,
        While,
        Do,
        Break,
        Switch,
        Case,
        Default,
        Continue,
        Discard,
    }

    public enum ArgumentQualifier
    {
        In,
        Out,
        Inout,
    }

    public enum SubClassTag
    {
        Global,
        Array,
    }

    public enum TextureFilter
    {
        Nearest,
        Linear,
        NearestMipmap,
        LinearMipmap,
        NearestMipmapAnisotropic,
        LinearMipmapAnisotropic,
        Default,
    }

    public enum TextureRepeat
    {
        Disable,
        Enable,
        Default,
    }

    private enum IdentifierType
    {
        Function,
        Uniform,
        Varying,
        FunctionArgument,
        LocalVar,
        BuiltinVar,
        Constant,
        Max,
    }

    private enum CompletionType
    {
        None,
        ShaderType,
        RenderMode,
        StencilMode,
        MainFunction,
        Identifier,
        FunctionCall,
        CallArguments,
        Index,
        Struct,
        Hint,
    }

    public enum Err
    {
        Ok,
        Failed,
        ParseError,
        Bug,
    }

    /// <summary>A constant's value, one component of it: Godot's union of a bool, a float, an int and a uint.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct Scalar
    {
        [FieldOffset(0)]
        private uint bits;

        [FieldOffset(0)]
        private float real;

        [FieldOffset(0)]
        private int sint;

        public bool Boolean
        {
            readonly get => (bits & 0xFF) != 0;
            set => bits = value ? 1u : 0u;
        }

        public float Real
        {
            readonly get => real;
            set => real = value;
        }

        public int Sint
        {
            readonly get => sint;
            set => sint = value;
        }

        public uint Uint
        {
            readonly get => bits;
            set => bits = value;
        }
    }

    /// <summary>Where a function that may read built-ins of a stage alone sits: its name and the built-ins and varyings
    /// it reads with their places, and the functions it calls.</summary>
    private sealed class CallInfo
    {
        public string Name { get; set; } = string.Empty;

        public List<(string Name, CallItem Item)> UsesRestrictedItems { get; } = [];

        public List<CallInfo> Calls { get; } = [];
    }

    private enum CallItemType
    {
        Builtin,
        Varying,
    }

    private readonly record struct CallItem(CallItemType Type, TkPos Pos);

    /// <summary>A built-in a function reads: its type, whether it is constant, and a constant's value.</summary>
    public sealed record BuiltInInfo(DataType Type, bool Constant = false, IReadOnlyList<Scalar>? Values = null)
    {
        public List<Scalar> ValuesList => [.. Values ?? []];
    }

    public sealed record StageArgument(string Name, DataType Type);

    /// <summary>A stage function a processor function may call (<c>texture_sdf</c>).</summary>
    public sealed class StageFunctionInfo
    {
        public List<StageArgument> Arguments { get; init; } = [];

        public DataType ReturnType { get; init; } = DataType.Void;

        public string SkipFunction { get; init; } = string.Empty;
    }

    /// <summary>A render or stencil mode: its name and its options.</summary>
    public sealed record ModeInfo(string Name, IReadOnlyList<string> Options);

    /// <summary>A processor function's built-ins and stage functions, and whether it is a main one and may discard.</summary>
    public sealed class FunctionInfo
    {
        public Dictionary<string, BuiltInInfo> BuiltIns { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, StageFunctionInfo> StageFunctions { get; init; } = new(StringComparer.Ordinal);

        public bool CanDiscard { get; init; }

        public bool MainFunction { get; init; }
    }

    /// <summary>What a compile is given: the shader type's functions and modes (none for an include), the shader types,
    /// the type of each global uniform the project declares, and whether the code is an include's.</summary>
    public sealed class ShaderCompileInfo
    {
        public Dictionary<string, FunctionInfo> Functions { get; init; } = new(StringComparer.Ordinal);

        public List<ModeInfo> RenderModes { get; init; } = [];

        public List<ModeInfo> StencilModes { get; init; } = [];

        public HashSet<string> ShaderTypes { get; init; } = new(StringComparer.Ordinal);

        /// <summary>Every shader type's functions, which an include's code is read against.</summary>
        public IReadOnlyList<Dictionary<string, FunctionInfo>> AllFunctions { get; init; } = [];

        /// <summary>Every shader type's render and stencil modes, which an include's are read against.</summary>
        public IReadOnlyList<(List<ModeInfo> Render, List<ModeInfo> Stencil)> AllModes { get; init; } = [];

        public Func<string, DataType>? GlobalShaderUniformType { get; init; }

        public bool IsInclude { get; init; }

        /// <summary>Whether the rendering method is the low end's (Compatibility), which refuses the high end's functions.</summary>
        public bool LowEnd { get; init; }

        /// <summary>The project's rendering method (<c>forward_plus</c>, <c>mobile</c>, <c>gl_compatibility</c>).</summary>
        public string RenderingMethod { get; init; } = "forward_plus";

        /// <summary>Whether the project's shaders are built for XR (<c>xr/shaders/enabled</c>).</summary>
        public bool XrEnabled { get; init; }

        public uint BaseVaryingIndex { get; init; }

        /// <summary>The warnings looked for, none checked where null (Godot's enable_warning_checking and flags).</summary>
        public GodotShaderWarningFlags? Warnings { get; init; }

        /// <summary>The Godot whose compiler is followed where 4.7's read differently (4.8's for null).</summary>
        public Version? Godot { get; init; }
    }

    /// <summary>What a name was found to be: Godot's out parameters of _find_identifier, its constant's values read
    /// only when asked for.</summary>
    private sealed class IdentifierFound
    {
        public DataType DataType { get; set; } = DataType.Void;

        public IdentifierType Type { get; set; } = IdentifierType.Max;

        public bool IsConst { get; set; }

        public int ArraySize { get; set; }

        public string StructName { get; set; } = string.Empty;

        public List<Scalar>? ConstantValues { get; set; }

        public bool WantsValues { get; init; }
    }

    /// <summary>An error the compile met, in Godot's words, on a line of the code compiled, with the places it names
    /// (the first the line in the shader, the last the line in the include it is in) and where in the code its token
    /// begins.</summary>
    public sealed record CompileError(string Message, int Line, IReadOnlyList<GodotShaderFilePosition> Positions, int Offset);

    private const int MaxInstanceUniformIndices = 16;

    private static readonly Version Godot48 = new(4, 8);

    /// <summary>The processor functions a varying's stage is told by.</summary>
    private static readonly (string Fragment, string Vertex, string Light) varyingFunctionNames = ("fragment", "vertex", "light");

    private bool errorSet;
    private string errorStr = string.Empty;
    private int errorLine;
    private int errorOffset;
    private readonly List<CompileError> errors = [];

    private string shaderTypeIdentifier = string.Empty;
    private string currentFunction = string.Empty;
    private bool isConstDecl;
    private string lastName = string.Empty;
    private IdentifierType lastType = IdentifierType.Max;
    private bool isShaderInc;
    private string currentUniformGroupName = string.Empty;
    private uint baseVaryingIndex;
    private Hint currentUniformHint = Hint.None;
    private TextureFilter currentUniformFilter = TextureFilter.Default;
    private TextureRepeat currentUniformRepeat = TextureRepeat.Default;
    private PropertyUsage currentPropertyUsage = PropertyUsage.Default;
    private CompletionType completionType;
    private int completionLine;
    private BlockNode? completionBlock;
    private DataType completionBase;
    private bool completionBaseArray;
    private SubClassTag completionClass = SubClassTag.Global;
    private string completionFunction = string.Empty;
    private string completionStruct = string.Empty;
    private int completionArgument;
    private Dictionary<string, FunctionInfo> stages = new(StringComparer.Ordinal);
    private bool isSupportedFragOnlyFuncs;
    private bool isDiscardSupported;
    private ShaderNode shader = new();
    private readonly Dictionary<string, int> functionOverloadCount = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, CallInfo> callsInfo = new(StringComparer.Ordinal);
    private ShaderCompileInfo compileInfo = new();

    /// <summary>Whether the compile follows a Godot before 4.8, whose compiler knew no property usage hints, converted
    /// only a constant's type in a built-in's call, let a struct take a function's name, and read a
    /// constant's values only where written as values.</summary>
    private bool Before48 => compileInfo.Godot is { } godot && godot < Godot48;

    /// <summary>The shader the last compile made, whole or as far as it read.</summary>
    public ShaderNode Shader => shader;

    /// <summary>Every error the last compile met, the first as Godot gives it.</summary>
    public IReadOnlyList<CompileError> Errors => errors;

    /// <summary>Compiles code its preprocessor made as Godot's ShaderLanguage::compile does: the shader read whole, then
    /// the warnings of what it declared and never used where they are looked for.</summary>
    public Err Compile(string pCode, ShaderCompileInfo pInfo)
    {
        Clear();
        compileInfo = pInfo;
        isShaderInc = pInfo.IsInclude;
        code = pCode;
        baseVaryingIndex = pInfo.BaseVaryingIndex;
        checkWarnings = pInfo.Warnings is not null;
        warningFlags = pInfo.Warnings ?? GodotShaderWarningFlags.None;
        shader = new ShaderNode();
        Err err = ParseShader(pInfo.Functions, pInfo.RenderModes, pInfo.StencilModes, pInfo.ShaderTypes);
        if (checkWarnings)
        {
            CheckWarningAccums();
        }
        if (errorSet)
        {
            errors.Add(new CompileError(errorStr, errorLine, [.. includePositions], errorOffset));
        }
        return err;
    }

    private void Clear()
    {
        currentFunction = string.Empty;
        lastName = string.Empty;
        lastType = IdentifierType.Max;
        currentUniformGroupName = string.Empty;
        currentUniformHint = Hint.None;
        currentUniformFilter = TextureFilter.Default;
        currentUniformRepeat = TextureRepeat.Default;
        currentPropertyUsage = PropertyUsage.Default;
        completionType = CompletionType.None;
        completionBlock = null;
        completionFunction = string.Empty;
        completionClass = SubClassTag.Global;
        completionStruct = string.Empty;
        completionBase = DataType.Void;
        completionBaseArray = false;
        includePositions.Clear();
        includePositions.Add(new GodotShaderFilePosition(string.Empty, 0));
        includeMarkersHandled.Clear();
        callsInfo.Clear();
        functionOverloadCount.Clear();
        ClearWarnings();
        errorLine = 0;
        errorOffset = 0;
        tkLine = 1;
        charIdx = 0;
        tokenStart = 0;
        errorSet = false;
        errorStr = string.Empty;
        errors.Clear();
        isConstDecl = false;
    }

    /// <summary>What is said where a shader goes past the stack its parse has left: a stack that ran out ends the process.</summary>
    internal const string TooDeep = "This is nested too deeply for Passenger to read.";

    private void SetError(string message)
    {
        if (errorSet)
        {
            return;
        }
        errorLine = tkLine;
        errorOffset = tokenStart;
        errorSet = true;
        errorStr = message;
        includePositions[^1] = includePositions[^1] with { Line = tkLine };
    }

    private void SetExpectedError(string what) => SetError($"Expected a '{what}'.");

    private void SetExpectedError(string first, string second) => SetError($"Expected a '{first}' or '{second}'.");

    private void SetExpectedAfterError(string what, string after) => SetError($"Expected a '{what}' after '{after}'.");

    private void SetRedefinitionError(string what) => SetError($"Redefinition of '{what}'.");

    private void SetParsingError() => SetError("Parser bug.");

    /// <summary>Godot's vformat: each %s and %d in turn given the next argument, as text.</summary>
    private static string Format(string format, params object?[] arguments)
    {
        StringBuilder text = new(format.Length + 16);
        int next = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length && format[i + 1] is 's' or 'd' or 'f' or 'i')
            {
                object? argument = next < arguments.Length ? arguments[next++] : null;
                text.Append(argument switch
                {
                    float f => Rtos(f),
                    double d => Rtos(d),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => argument?.ToString(),
                });
                i++;
            }
            else if (format[i] == '%' && i + 1 < format.Length && format[i + 1] == '%')
            {
                text.Append('%');
                i++;
            }
            else
            {
                text.Append(format[i]);
            }
        }
        return text.ToString();
    }

    /// <summary>Godot's String::substr: from a place, a length of text or the rest, clipped to the text.</summary>
    private static string Substr(string text, int from, int length = -1)
    {
        if (from < 0 || from >= text.Length)
        {
            return string.Empty;
        }
        return length < 0 || from + length > text.Length ? text[from..] : text.Substring(from, length);
    }

    /// <summary>Godot's itos.</summary>
    private static string Itos(long value) => value.ToString(CultureInfo.InvariantCulture);
}

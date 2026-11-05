using System.Globalization;

namespace Passenger.Workspace.GodotShader;

/// <summary>A warning Godot's shading language gives (servers/rendering/shader_warnings.h, MIT), in its order.</summary>
public enum GodotShaderWarningCode
{
    FloatComparison,
    UnusedConstant,
    UnusedFunction,
    UnusedStruct,
    UnusedUniform,
    UnusedVarying,
    UnusedLocalVariable,
    FormattingError,
    DeviceLimitExceeded,
    MagicPositionWrite,
    Max,
}

/// <summary>The warnings a compile looks for, one flag each (ShaderWarning::CodeFlags).</summary>
[Flags]
public enum GodotShaderWarningFlags : uint
{
    None = 0,
    FloatComparison = 1,
    UnusedConstant = 2,
    UnusedFunction = 4,
    UnusedStruct = 8,
    UnusedUniform = 16,
    UnusedVarying = 32,
    UnusedLocalVariable = 64,
    FormattingError = 128,
    DeviceLimitExceeded = 256,
    MagicPositionWrite = 512,
}

/// <summary>A warning a compile gave: its code, its line in the code compiled (-1 for none), what it is about and
/// its numbers, said in Godot's words (shader_warnings.cpp, MIT).</summary>
public sealed record GodotShaderWarning(GodotShaderWarningCode Code, int Line, string Subject, IReadOnlyList<int> ExtraArgs)
{
    private static readonly string[] Names =
    [
        "FLOAT_COMPARISON", "UNUSED_CONSTANT", "UNUSED_FUNCTION", "UNUSED_STRUCT", "UNUSED_UNIFORM", "UNUSED_VARYING",
        "UNUSED_LOCAL_VARIABLE", "FORMATTING_ERROR", "DEVICE_LIMIT_EXCEEDED", "MAGIC_POSITION_WRITE",
    ];

    /// <summary>The warning's name, as Godot's editor shows it and its project setting names it (lower case).</summary>
    public string Name => NameOf(Code);

    public string Message => Code switch
    {
        GodotShaderWarningCode.FloatComparison => "Direct floating-point comparison (this may not evaluate to `true` as you expect). Instead, use `abs(a - b) < 0.0001` for an approximate but predictable comparison.",
        GodotShaderWarningCode.UnusedConstant => $"The const '{Subject}' is declared but never used.",
        GodotShaderWarningCode.UnusedFunction => $"The function '{Subject}' is declared but never used.",
        GodotShaderWarningCode.UnusedStruct => $"The struct '{Subject}' is declared but never used.",
        GodotShaderWarningCode.UnusedUniform => $"The uniform '{Subject}' is declared but never used.",
        GodotShaderWarningCode.UnusedVarying => $"The varying '{Subject}' is declared but never used.",
        GodotShaderWarningCode.UnusedLocalVariable => $"The local variable '{Subject}' is declared but never used.",
        GodotShaderWarningCode.FormattingError => Subject,
        GodotShaderWarningCode.DeviceLimitExceeded => string.Format(CultureInfo.InvariantCulture,
            "The total size of the {0} for this shader on this device has been exceeded ({1}/{2}). The shader may not work correctly.",
            Subject, ExtraArgs.Count > 0 ? ExtraArgs[0] : 0, ExtraArgs.Count > 1 ? ExtraArgs[1] : 0),
        GodotShaderWarningCode.MagicPositionWrite => "You are attempting to assign the VERTEX position in model space to the vertex POSITION in clip space. The definition of clip space changed in version 4.3, so if this code was written prior to 4.3, it will not continue to work. Consider specifying the clip space z-component directly i.e. use `vec4(VERTEX.xy, 1.0, 1.0)`.",
        _ => string.Empty,
    };

    public static string NameOf(GodotShaderWarningCode code) => code is >= 0 and < GodotShaderWarningCode.Max ? Names[(int)code] : string.Empty;

    public static GodotShaderWarningFlags FlagOf(GodotShaderWarningCode code) =>
        code is >= 0 and < GodotShaderWarningCode.Max ? (GodotShaderWarningFlags)(1u << (int)code) : GodotShaderWarningFlags.None;
}

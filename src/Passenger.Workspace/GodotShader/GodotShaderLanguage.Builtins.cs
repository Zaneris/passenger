namespace Passenger.Workspace.GodotShader;

/// <summary>Godot's tables of built-in functions (shader_language.cpp, MIT): every overload the carried table holds in
/// the compiler's order, constructors first, then the functions whose arguments are written to, whose argument must be
/// a constant in a range, which only a fragment stage may call, and the constructors a vector's parts may build. Each
/// ends in an entry of no name, as Godot's do, so its loops read as Godot's.</summary>
public sealed partial class GodotShaderLanguage
{
    private sealed record BuiltinFuncDef(string? Name, DataType Rettype, DataType[] Args, SubClassTag Tag, bool HighEnd)
    {
        public const int MaxArgs = 5;
    }

    private sealed record BuiltinFuncOutArgs(string? Name, int[] Arguments)
    {
        public const int MaxArgs = 2;
    }

    private sealed record BuiltinFuncConstArgs(string? Name, int Arg, int Min, int Max);

    private sealed record BuiltinEntry(string? Name);

    // Before the table, which its static initializer reads.
    private static readonly string[] DatatypeNames =
    [
        "void", "bool", "bvec2", "bvec3", "bvec4", "int", "ivec2", "ivec3", "ivec4", "uint", "uvec2", "uvec3", "uvec4",
        "float", "vec2", "vec3", "vec4", "mat2", "mat3", "mat4", "sampler2D", "isampler2D", "usampler2D", "sampler2DArray",
        "isampler2DArray", "usampler2DArray", "sampler3D", "isampler3D", "usampler3D", "samplerCube", "samplerCubeArray",
        "samplerExternalOES", "struct", "invalid",
    ];

    private static readonly BuiltinFuncDef[] builtinFuncDefs =
    [
        .. GodotShaderTable.Carried.BuiltinFunctions.Select(f => new BuiltinFuncDef(f.Name, DataTypeNamed(f.Returns),
            [.. Enumerable.Range(0, BuiltinFuncDef.MaxArgs).Select(i => i < f.Takes.Count ? DataTypeNamed(f.Takes[i]) : DataType.Void)],
            f.Tag == "array" ? SubClassTag.Array : SubClassTag.Global, f.HighEnd)),
        new BuiltinFuncDef(null, DataType.Void, new DataType[BuiltinFuncDef.MaxArgs], SubClassTag.Global, false),
    ];

    /// <summary>The functions the carried table has only on the high end that every Godot before a version has on the low end too.</summary>
    private static readonly Dictionary<string, Version> highEndFrom = new(StringComparer.Ordinal) { ["textureGather"] = new Version(4, 7) };

    private static readonly HashSet<string> globalFuncSet =
        new(builtinFuncDefs.Where(d => d.Name != null && d.Tag == SubClassTag.Global).Select(d => d.Name!), StringComparer.Ordinal);

    private static readonly BuiltinFuncOutArgs[] builtinFuncOutArgs =
    [
        new("modf", [1, -1]),
        new("umulExtended", [2, 3]),
        new("imulExtended", [2, 3]),
        new("uaddCarry", [2, -1]),
        new("usubBorrow", [2, -1]),
        new("ldexp", [1, -1]),
        new("frexp", [1, -1]),
        new(null, [0, -1]),
    ];

    private static readonly BuiltinFuncConstArgs[] builtinFuncConstArgs =
    [
        new("textureGather", 2, 0, 3),
        new(null, 0, 0, 0),
    ];

    private static readonly BuiltinEntry[] fragOnlyFuncDefs =
    [
        new("dFdx"), new("dFdxCoarse"), new("dFdxFine"), new("dFdy"), new("dFdyCoarse"), new("dFdyFine"), new("fwidth"),
        new("fwidthCoarse"), new("fwidthFine"), new("textureQueryLod"), new(null),
    ];

    private static readonly BuiltinEntry[] builtinVectorizedConstructors =
    [
        new("vec2"), new("vec3"), new("vec4"), new("ivec2"), new("ivec3"), new("ivec4"), new("uvec2"), new("uvec3"),
        new("uvec4"), new("bvec2"), new("bvec3"), new("bvec4"), new("mat2"), new("mat3"), new("mat4"), new(null),
    ];

    /// <summary>The type a name of the language names (<c>sampler2D</c>); <see cref="DataType.Max"/> for none.</summary>
    public static DataType DataTypeNamed(string name)
    {
        int index = Array.IndexOf(DatatypeNames, name);
        return index >= 0 && index < (int)DataType.Max ? (DataType)index : DataType.Max;
    }
}

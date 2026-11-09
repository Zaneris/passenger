using System.Collections.Concurrent;
using System.Globalization;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;
using Passenger.Workspace.GodotShader;

namespace Passenger.App.Godot;

/// <summary>A uniform as Godot's compiler reads it: its name, its Variant type, its property hint and the hint's words.</summary>
internal sealed record GodotShaderUniform(string Name, GodotType Type, long Hint, string HintString)
{
    /// <summary>Godot's PROPERTY_HINT_FLAGS.</summary>
    private const long Flags = 6;

    /// <summary>What Godot makes of it, in words: <c>float, in 0.0 to 10.0, step 0.001</c>.</summary>
    public string Described
    {
        get
        {
            string type = GodotVariant.TypeName(Type);
            string[] parts = HintString.Split(',');
            return Hint switch
            {
                GodotHints.Range when parts.Length >= 2 => $"{type}, in {parts[0]} to {parts[1]}{(parts.Length > 2 ? $", step {Step(parts[2])}" : string.Empty)}",
                GodotHints.Enum => $"{type}, one of {string.Join(", ", parts)}",
                GodotHints.ResourceType => $"{HintString} ({type})",
                GodotHints.ColorNoAlpha => $"{type}, without alpha",
                _ => type,
            };
        }
    }

    /// <summary>A uniform the compile read, as Godot's editor shows it (ShaderLanguage::uniform_to_property_info, MIT).</summary>
    public static GodotShaderUniform Of(string name, GodotShaderLanguage.Uniform uniform)
    {
        bool array = uniform.ArraySize > 0;
        bool color = uniform.Hint is GodotShaderLanguage.Hint.SourceColor or GodotShaderLanguage.Hint.ColorConversionDisabled;
        string range = string.Join(',', uniform.HintRange.Select(r => Rtos(r)));
        (GodotType type, long hint, string words) = uniform.Type switch
        {
            GodotShaderLanguage.DataType.Bool => array ? (GodotType.PackedInt32Array, GodotHints.TypeString, $"{(int)GodotType.Int}/{Flags}:On") : (GodotType.Bool, 0L, ""),
            GodotShaderLanguage.DataType.Bvec2 => array ? (GodotType.PackedInt32Array, GodotHints.TypeString, $"{(int)GodotType.Int}/{Flags}:x,y") : (GodotType.Int, Flags, "x,y"),
            GodotShaderLanguage.DataType.Bvec3 => array ? (GodotType.PackedInt32Array, GodotHints.TypeString, $"{(int)GodotType.Int}/{Flags}:x,y,z") : (GodotType.Int, Flags, "x,y,z"),
            GodotShaderLanguage.DataType.Bvec4 => array ? (GodotType.PackedInt32Array, GodotHints.TypeString, $"{(int)GodotType.Int}/{Flags}:x,y,z,w") : (GodotType.Int, Flags, "x,y,z,w"),
            GodotShaderLanguage.DataType.Int or GodotShaderLanguage.DataType.Uint when array => (GodotType.PackedInt32Array, 0L, ""),
            GodotShaderLanguage.DataType.Int or GodotShaderLanguage.DataType.Uint when uniform.Hint == GodotShaderLanguage.Hint.Enum =>
                (GodotType.Int, GodotHints.Enum, string.Join(',', uniform.HintEnumNames)),
            GodotShaderLanguage.DataType.Int or GodotShaderLanguage.DataType.Uint => (GodotType.Int, GodotHints.Range, uniform.Hint == GodotShaderLanguage.Hint.Range ? range
                : uniform.Type == GodotShaderLanguage.DataType.Uint ? $"0,{uint.MaxValue}" : $"{int.MinValue},{int.MaxValue}"),
            GodotShaderLanguage.DataType.Ivec2 or GodotShaderLanguage.DataType.Uvec2 => (array ? GodotType.PackedInt32Array : GodotType.Vector2I, 0L, ""),
            GodotShaderLanguage.DataType.Ivec3 or GodotShaderLanguage.DataType.Uvec3 => (array ? GodotType.PackedInt32Array : GodotType.Vector3I, 0L, ""),
            GodotShaderLanguage.DataType.Ivec4 or GodotShaderLanguage.DataType.Uvec4 => (array ? GodotType.PackedInt32Array : GodotType.Vector4I, 0L, ""),
            GodotShaderLanguage.DataType.Float when array => (GodotType.PackedFloat32Array, 0L, ""),
            GodotShaderLanguage.DataType.Float => (GodotType.Float, uniform.Hint == GodotShaderLanguage.Hint.Range ? GodotHints.Range : 0L, uniform.Hint == GodotShaderLanguage.Hint.Range ? range : ""),
            GodotShaderLanguage.DataType.Vec2 => (array ? GodotType.PackedVector2Array : GodotType.Vector2, 0L, ""),
            GodotShaderLanguage.DataType.Vec3 when color => (array ? GodotType.PackedColorArray : GodotType.Color, GodotHints.ColorNoAlpha, ""),
            GodotShaderLanguage.DataType.Vec3 => (array ? GodotType.PackedVector3Array : GodotType.Vector3, 0L, ""),
            GodotShaderLanguage.DataType.Vec4 when color => (array ? GodotType.PackedColorArray : GodotType.Color, 0L, ""),
            GodotShaderLanguage.DataType.Vec4 => (array ? GodotType.PackedVector4Array : GodotType.Vector4, 0L, ""),
            GodotShaderLanguage.DataType.Mat2 => (array ? GodotType.PackedFloat32Array : GodotType.Transform2D, 0L, ""),
            GodotShaderLanguage.DataType.Mat3 => (array ? GodotType.PackedFloat32Array : GodotType.Basis, 0L, ""),
            GodotShaderLanguage.DataType.Mat4 => (array ? GodotType.PackedFloat32Array : GodotType.Projection, 0L, ""),
            GodotShaderLanguage.DataType.Sampler2d or GodotShaderLanguage.DataType.Isampler2d or GodotShaderLanguage.DataType.Usampler2d => Texture("Texture2D"),
            GodotShaderLanguage.DataType.Sampler2darray or GodotShaderLanguage.DataType.Isampler2darray or GodotShaderLanguage.DataType.Usampler2darray
                or GodotShaderLanguage.DataType.Samplercube or GodotShaderLanguage.DataType.Samplercubearray => Texture("TextureLayered"),
            GodotShaderLanguage.DataType.Sampler3d or GodotShaderLanguage.DataType.Isampler3d or GodotShaderLanguage.DataType.Usampler3d => Texture("Texture3D"),
            GodotShaderLanguage.DataType.Samplerext => Texture("ExternalTexture"),
            _ => (GodotType.Nil, 0L, ""),
        };
        return new GodotShaderUniform(name, type, hint, words);

        (GodotType, long, string) Texture(string kind) => array
            ? (GodotType.Array, GodotHints.ArrayType, $"{(int)GodotType.Object}/{GodotHints.ResourceType}:{kind}") : (GodotType.Object, GodotHints.ResourceType, kind);
    }

    /// <summary>Godot's rtos: 14 digits after the point (fewer past 10, so as many in all), its zeros taken off but one.</summary>
    internal static string Rtos(double value)
    {
        int decimals = 14;
        if (Math.Abs(value) > 10)
        {
            decimals -= (int)Math.Floor(Math.Log10(Math.Abs(value)));
        }
        string written = value.ToString("F" + Math.Max(0, decimals).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).TrimEnd('0');
        return written.EndsWith('.') ? written + "0" : written;
    }

    /// <summary>A step as Godot writes it from a 32-bit float (<c>0.0010000000475</c>), to the digits that tell it.</summary>
    private static string Step(string written) =>
        double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out double step)
            ? ((float)step).ToString("G7", CultureInfo.InvariantCulture) : written;
}

/// <summary>Godot's shader front end for a window's session: Passenger's ports of its preprocessor and compiler
/// (<see cref="GodotShaderCheck"/>), run on the thread pool, with the shading language's table as the Godot found takes
/// it (<see cref="GodotEngineApis.ShaderTableAsync"/>, its once-per-version probe the only Godot it starts) and the
/// project's options as its project.godot sets them; what each check read of a shader's uniforms kept for its card.</summary>
internal sealed class GodotShaderChecker(GodotEngineApis apis)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<GodotShaderUniform>> read = new(StringComparer.Ordinal);

    /// <summary>The carried table of the shading language as the Godot found takes it.</summary>
    public async Task<GodotShaderTable> TableAsync() => (await apis.ShaderTableAsync()).Table;

    /// <summary>What Godot's front end makes of a shader of a project, as its editor checks it.</summary>
    public async Task<GodotShaderChecked> CheckAsync(GodotShaderUnit unit, GodotScenes scenes)
    {
        (GodotShaderTable table, Version? godot) = await apis.ShaderTableAsync();
        string? project = scenes.Texts.GetValueOrDefault(GodotDefinitions.ProjectRes);
        GodotShaderChecked result = await BackgroundWork.Run(() => GodotShaderCheck.Check(unit, table, GodotShaderCheckOptions.Of(project, godot)));
        // A check that read no uniform for an error leaves what the last one read standing until one reads them again.
        if (result.Uniforms.Count > 0 || !result.Messages.Any(m => m.Error))
        {
            read[unit.Res] = [.. result.Uniforms.Select(u => GodotShaderUniform.Of(u.Key, u.Value))];
        }
        return result;
    }

    /// <summary>What the last check of a shader, by its res:// path (a scene's own by its file and id), read of a uniform;
    /// null where it read none of that name.</summary>
    public GodotShaderUniform? UniformOf(string res, string name) =>
        read.TryGetValue(res, out IReadOnlyList<GodotShaderUniform>? uniforms) ? uniforms.FirstOrDefault(u => u.Name == name) : null;
}

namespace Passenger.Workspace;

/// <summary>What a name of a shader's code is.</summary>
public enum GodotShaderNameKind
{
    /// <summary>A built-in a processor function reads (<c>VERTEX</c>), a scope's (<c>TIME</c>) or a constant (<c>PI</c>).</summary>
    BuiltIn,

    /// <summary>A function a processor function may call (<c>texture_sdf</c>).</summary>
    StageFunction,

    /// <summary>One of the language's built-in functions (<c>mix</c>).</summary>
    Function,

    RenderMode,

    StencilMode,

    Hint,

    /// <summary>What the shader's code, or one of its includes, declares.</summary>
    Declared,
}

/// <summary>A name of a shader's code as Godot's language reads it: what it is, the scope offering a built-in (its
/// processor function, <see cref="GodotShaderTable.Global"/> or <see cref="GodotShaderTable.Constants"/>), the built-in, the
/// overloads of a function, or the declaration naming it and the include declaring it (null for the shader's own).</summary>
public sealed record GodotShaderName(GodotShaderNameKind Kind, string Name, int Start, int End)
{
    public string? Scope { get; init; }

    public GodotShaderBuiltIn? BuiltIn { get; init; }

    public IReadOnlyList<GodotShaderCall> Calls { get; init; } = [];

    public GodotShaderDeclaration? Declaration { get; init; }

    public string? Include { get; init; }
}

/// <summary>The name at a place of a shader's code, read against Godot's language as a table gives it and against what the
/// shader and its includes declare: a declaration seen there first, then a built-in of the processor function holding it,
/// a stage function, a built-in function, and in a render mode, stencil mode or hint statement those. Pure.</summary>
public static class GodotShaderNames
{
    public static GodotShaderName? At(GodotShaderTable table, GodotShaderCode code, string? type, int offset, IReadOnlyDictionary<string, string>? includes = null)
    {
        if (code.WordAt(offset) is not ({ } start, { } end))
        {
            return null;
        }
        GodotShaderSpot spot = code.SpotAt(end);
        if (spot is GodotShaderSpot.None or GodotShaderSpot.Member or GodotShaderSpot.Include or GodotShaderSpot.ShaderType)
        {
            return null;
        }
        string name = code.Text(start, end);
        GodotShaderType? kind = table.TypeOf(type);
        switch (spot)
        {
            case GodotShaderSpot.RenderModes:
                return kind?.Modes.Any(m => m.Written.Contains(name)) == true ? new GodotShaderName(GodotShaderNameKind.RenderMode, name, start, end) : null;
            case GodotShaderSpot.StencilModes:
                return kind?.StencilModes.Any(m => m.Written.Contains(name)) == true ? new GodotShaderName(GodotShaderNameKind.StencilMode, name, start, end) : null;
            case GodotShaderSpot.Hints:
                return (type is null ? table.Types.SelectMany(t => table.HintsOf(t.Name)) : table.HintsOf(type)).Contains(name)
                    ? new GodotShaderName(GodotShaderNameKind.Hint, name, start, end) : null;
        }
        if (code.DeclarationOf(name, offset) is { } own)
        {
            return new GodotShaderName(GodotShaderNameKind.Declared, name, start, end) { Declaration = own };
        }
        foreach ((string include, string text) in includes ?? new Dictionary<string, string>())
        {
            if (GodotShaderCode.Read(text).Declarations.FirstOrDefault(d => d.Name == name && d.Kind is not (GodotShaderDeclared.Parameter or GodotShaderDeclared.Local)) is { } declared)
            {
                return new GodotShaderName(GodotShaderNameKind.Declared, name, start, end) { Declaration = declared, Include = include };
            }
        }
        string? function = code.FunctionAt(offset)?.Name;
        IEnumerable<string> types = type is null ? table.Types.Select(t => t.Name) : [type];
        foreach (string shaderType in types)
        {
            if (table.BuiltInsIn(shaderType, function).FirstOrDefault(b => b.BuiltIn.Name == name) is ({ } builtIn, { } scope))
            {
                return new GodotShaderName(GodotShaderNameKind.BuiltIn, name, start, end) { Scope = scope, BuiltIn = builtIn };
            }
            if (function is not null && table.StageFunctionsIn(shaderType, function).Where(s => s.Name == name).ToList() is { Count: > 0 } stage)
            {
                return new GodotShaderName(GodotShaderNameKind.StageFunction, name, start, end) { Scope = function, Calls = stage };
            }
        }
        return table.Functions.Where(f => f.Name == name).ToList() is { Count: > 0 } calls ? new GodotShaderName(GodotShaderNameKind.Function, name, start, end) { Calls = calls } : null;
    }
}

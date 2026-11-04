namespace Passenger.Workspace.GodotShader;

/// <summary>The warnings a compile gives, as Godot's shading language gathers them (shader_language.h's DEBUG_ENABLED
/// part, MIT): those found as the code is read, then, once read, each constant, function, struct, uniform, varying and
/// local variable declared and never used.</summary>
public sealed partial class GodotShaderLanguage
{
    /// <summary>Where a name was declared and whether anything read it since.</summary>
    private sealed class Usage(int declLine = -1)
    {
        public int DeclLine { get; } = declLine;

        public bool Used { get; set; }
    }

    private readonly Dictionary<string, Usage> usedConstants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Usage> usedVaryings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Usage> usedUniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Usage> usedFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Usage> usedStructs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Usage>> usedLocalVars = new(StringComparer.Ordinal);
    private readonly List<GodotShaderWarning> warnings = [];

    private bool checkWarnings;
    private GodotShaderWarningFlags warningFlags;

    /// <summary>The warnings the last compile gave, in the order it found them.</summary>
    public IReadOnlyList<GodotShaderWarning> Warnings => warnings;

    private bool HasWarning(GodotShaderWarningFlags flag) => (warningFlags & flag) != 0;

    private void AddLineWarning(GodotShaderWarningCode pCode, string pSubject = "", IReadOnlyList<int>? pExtraArgs = null) =>
        warnings.Add(new GodotShaderWarning(pCode, tkLine, pSubject, pExtraArgs ?? []));

    private void AddWarning(GodotShaderWarningCode pCode, int pLine, string pSubject = "", IReadOnlyList<int>? pExtraArgs = null) =>
        warnings.Add(new GodotShaderWarning(pCode, pLine, pSubject, pExtraArgs ?? []));

    private void ClearWarnings()
    {
        usedConstants.Clear();
        usedVaryings.Clear();
        usedUniforms.Clear();
        usedFunctions.Clear();
        usedStructs.Clear();
        usedLocalVars.Clear();
        warnings.Clear();
    }

    private void CheckWarningAccums()
    {
        foreach (KeyValuePair<string, Dictionary<string, Usage>> function in usedLocalVars)
        {
            foreach (KeyValuePair<string, Usage> local in function.Value)
            {
                if (!local.Value.Used)
                {
                    AddWarning(GodotShaderWarningCode.UnusedLocalVariable, local.Value.DeclLine, local.Key);
                }
            }
        }
        (GodotShaderWarningCode Code, Dictionary<string, Usage> Used)[] checks =
        [
            (GodotShaderWarningCode.UnusedConstant, usedConstants),
            (GodotShaderWarningCode.UnusedFunction, usedFunctions),
            (GodotShaderWarningCode.UnusedStruct, usedStructs),
            (GodotShaderWarningCode.UnusedUniform, usedUniforms),
            (GodotShaderWarningCode.UnusedVarying, usedVaryings),
        ];
        foreach ((GodotShaderWarningCode code, Dictionary<string, Usage> used) in checks)
        {
            foreach (KeyValuePair<string, Usage> declared in used)
            {
                if (!declared.Value.Used)
                {
                    AddWarning(code, declared.Value.DeclLine, declared.Key);
                }
            }
        }
    }
}

using System.Text.RegularExpressions;

namespace Passenger.Workspace.GodotShader;

/// <summary>How a project's shaders are checked: its rendering method, the Godot that runs it, the warnings it asks for
/// (none where null) and whether they count as errors, whether its shaders are built for XR, and the type of each global
/// uniform it declares (none checked where null).</summary>
public sealed partial record GodotShaderCheckOptions
{
    /// <summary>Every warning Godot's editor looks for by default.</summary>
    public const GodotShaderWarningFlags AllWarnings = (GodotShaderWarningFlags)((1u << (int)GodotShaderWarningCode.Max) - 1);

    public string RenderingMethod { get; init; } = GodotDefinitions.ForwardPlus;

    public Version? Godot { get; init; }

    public GodotShaderWarningFlags? Warnings { get; init; } = AllWarnings;

    public bool WarningsAsErrors { get; init; }

    public bool XrEnabled { get; init; }

    public IReadOnlyDictionary<string, string>? GlobalUniforms { get; init; }

    private const string WarningsKey = "debug/shader_language/warnings/";

    /// <summary>The options a project.godot's text gives, as Godot's editor reads them: its rendering method, each warning
    /// on unless set off, warnings as errors, XR's shaders, and its global uniforms' types in the language's names. A value
    /// Godot would not read leaves that setting as Godot's own.</summary>
    public static GodotShaderCheckOptions Of(string? project, Version? godot)
    {
        if (project is null)
        {
            return new GodotShaderCheckOptions { Godot = godot };
        }
        Dictionary<string, string> values = GodotProject.Values(project);
        GodotShaderWarningFlags flags = GodotShaderWarningFlags.None;
        for (GodotShaderWarningCode code = 0; code < GodotShaderWarningCode.Max; code++)
        {
            if (!values.TryGetValue(WarningsKey + GodotShaderWarning.NameOf(code).ToLowerInvariant(), out string? set) || Truthy(set) != false)
            {
                flags |= GodotShaderWarning.FlagOf(code);
            }
        }
        Dictionary<string, string> globals = new(StringComparer.Ordinal);
        foreach ((string key, string value) in values)
        {
            if (key.StartsWith("shader_globals/", StringComparison.Ordinal) && GlobalType().Match(value) is { Success: true } type)
            {
                globals[key["shader_globals/".Length..]] = type.Groups[1].Value switch
                {
                    "color" or "rect2" => "vec4",
                    "rect2i" => "ivec4",
                    "transform_2d" => "mat3",
                    "transform" => "mat4",
                    string named => named,
                };
            }
        }
        string method = values.GetValueOrDefault("rendering/renderer/rendering_method")?.Trim().Trim('"') ?? string.Empty;
        method = method is "mobile" or "gl_compatibility" ? method : GodotDefinitions.ForwardPlus;
        return new GodotShaderCheckOptions
        {
            RenderingMethod = method,
            Godot = godot,
            Warnings = values.TryGetValue(WarningsKey + "enable", out string? enable) && Truthy(enable) == false ? null : flags,
            WarningsAsErrors = values.TryGetValue(WarningsKey + "treat_warnings_as_errors", out string? asErrors) && Truthy(asErrors) == true,
            XrEnabled = values.TryGetValue("xr/shaders/enabled", out string? xr) && Truthy(xr) == true,
            GlobalUniforms = globals,
        };
    }

    private static bool? Truthy(string value) => value.Trim() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    [GeneratedRegex("\"type\"\\s*:\\s*\"(\\w+)\"")]
    private static partial Regex GlobalType();
}

/// <summary>What Godot's front end made of a shader or an include: its messages, as Godot's editor gives them (a
/// preprocessor's error, every include its branches reach that no file answers beside the first, else every error of the
/// compile and, where it compiled, its warnings by line), the shader type it was read as, and the uniforms it declares as
/// the compile read them.</summary>
public sealed record GodotShaderChecked(IReadOnlyList<GodotShaderMessage> Messages, string ShaderType,
    IReadOnlyList<KeyValuePair<string, GodotShaderLanguage.Uniform>> Uniforms);

/// <summary>Checks a shader or an include as Godot's shader editor validates it (ShaderTextEditor::_validate_script, MIT),
/// with Passenger's ports of its preprocessor and compiler and the language's table as the Godot found takes it, going
/// on past the error Godot stops at to the others: an include compiled alone, with the warnings of what it declares and
/// never uses off as the editor turns them off. Pure.</summary>
public static class GodotShaderCheck
{
    /// <summary>The first Godot whose canvas, particles, sky, fog and blit shaders know the values of PI, TAU and E.</summary>
    private static readonly Version ConstantValues48 = new(4, 8);

    /// <summary>How many errors a shader's check goes on to, past the first.</summary>
    private const int MostErrors = 64;

    /// <summary>The flags Godot's editor clears for an include: it clears codes' numbers as flags, so these three.</summary>
    private const GodotShaderWarningFlags IncludeCleared = GodotShaderWarningFlags.FloatComparison | GodotShaderWarningFlags.UnusedConstant
        | GodotShaderWarningFlags.UnusedFunction;

    public static GodotShaderChecked Check(GodotShaderUnit unit, GodotShaderTable table, GodotShaderCheckOptions options)
    {
        bool isInclude = unit.Res.EndsWith(".gdshaderinc", StringComparison.Ordinal);
        HashSet<string> passed = new(StringComparer.Ordinal);
        string? unanswered = null;
        GodotShaderPreprocessed preprocessed = Preprocessed();
        if (preprocessed.Error is not null)
        {
            List<GodotShaderMessage> stopped = [PreprocessorMessage(unit, preprocessed)];
            while (IsNoInclude(preprocessed) && passed.Add(unanswered!))
            {
                preprocessed = Preprocessed();
                if (IsNoInclude(preprocessed))
                {
                    stopped.Add(PreprocessorMessage(unit, preprocessed));
                }
            }
            return new GodotShaderChecked(stopped, string.Empty, []);
        }

        string shaderType = isInclude ? string.Empty : GodotShaderLanguage.GetShaderType(preprocessed.Code);
        GodotShaderLanguage.ShaderCompileInfo info = InfoFor(table, isInclude ? null : shaderType, options);
        (GodotShaderLanguage language, List<GodotShaderLanguage.CompileError> errors) = Compiled(preprocessed.Code, info);
        List<GodotShaderMessage> messages = [];
        foreach (GodotShaderLanguage.CompileError compiled in errors)
        {
            bool stopped = compiled.Message == GodotShaderLanguage.TooDeep;
            messages.Add(compiled.Positions.Count > 1
                ? new GodotShaderMessage(!stopped, compiled.Positions[^1].File, compiled.Positions[^1].Line, compiled.Message, stopped)
                : new GodotShaderMessage(!stopped, string.Empty, compiled.Line, compiled.Message, stopped));
        }
        if (errors.Count == 0)
        {
            List<GodotShaderWarning> warnings = [.. language.Warnings.OrderBy(w => w.Line)];
            for (int i = 0; i < warnings.Count; i++)
            {
                bool asError = options.WarningsAsErrors && i == 0;
                string text = asError ? $"{warnings[i].Message} Warnings should be fixed to prevent errors." : warnings[i].Message;
                messages.Add(new GodotShaderMessage(asError, string.Empty, warnings[i].Line, text));
            }
        }
        return new GodotShaderChecked(messages, shaderType, [.. language.Shader.Uniforms]);

        GodotShaderPreprocessed Preprocessed() => GodotShaderPreprocessor.Preprocess(unit.Code, unit.Res, options.RenderingMethod, Included, options.Godot);

        // An include passed reads as an empty one, so the preprocessor goes on to the next it reaches that no file answers.
        GodotShaderIncluded? Included(string path)
        {
            if (unit.Includes.TryGetValue(path, out string? text))
            {
                return new GodotShaderIncluded(true, path.EndsWith(".gdshaderinc", StringComparison.Ordinal), text);
            }
            unanswered = path;
            return passed.Contains(path) ? new GodotShaderIncluded(true, true, string.Empty) : null;
        }
    }

    private static bool IsNoInclude(GodotShaderPreprocessed preprocessed) => preprocessed.Error?.StartsWith(GodotShaderUnit.NoInclude, StringComparison.Ordinal) == true;

    /// <summary>A preprocessor's error as a message, at its line of the shader or of the include it happened in.</summary>
    private static GodotShaderMessage PreprocessorMessage(GodotShaderUnit unit, GodotShaderPreprocessed preprocessed)
    {
        string error = preprocessed.Error!;
        GodotShaderFilePosition first = preprocessed.ErrorPositions.Count > 0 ? preprocessed.ErrorPositions[0] : new GodotShaderFilePosition(unit.Res, 1);
        GodotShaderFilePosition last = preprocessed.ErrorPositions.Count > 0 ? preprocessed.ErrorPositions[^1] : first;
        bool stopped = error == GodotShaderLanguage.TooDeep;
        return preprocessed.ErrorPositions.Count > 1
            ? new GodotShaderMessage(!stopped, last.File, last.Line, error, stopped)
            : new GodotShaderMessage(!stopped, string.Empty, first.Line, error, stopped);
    }

    /// <summary>Every error of the code, the first as Godot gives it, then each Godot would give once the statements of
    /// those before were taken out: a condition or a local's initializer first, so the rest is still read, else the
    /// statement whole, else what holds it. What a taken-out declaration would make wrong after it (a use of its name, a function left
    /// with no return, a <c>switch</c> left with no <c>case</c>, labels of another type than its condition taken out) is left unsaid, and
    /// all past where a compile ran out of stack. With the last compile, whose shader is the one read furthest.</summary>
    private static (GodotShaderLanguage Language, List<GodotShaderLanguage.CompileError> Errors) Compiled(string code, GodotShaderLanguage.ShaderCompileInfo info)
    {
        List<GodotShaderLanguage.CompileError> errors = [];
        code = GodotShaderStatements.WithRoomInConditions(code);
        List<GodotShaderStatements.Span> spans = GodotShaderStatements.Of(code);
        HashSet<string> gone = new(StringComparer.Ordinal);
        HashSet<(GodotShaderStatements.Span, int)> softened = [];
        HashSet<GodotShaderStatements.Span> blanked = [];
        bool returnGone = false;
        bool caseGone = false;
        bool switchKept = false;
        GodotShaderLanguage language = new();
        for (int pass = 0; pass < MostErrors; pass++)
        {
            language = new GodotShaderLanguage();
            GodotShaderLanguage.Err compiled = language.Compile(code, info);
            if (language.Errors is [{ Message: GodotShaderLanguage.TooDeep } stopped, ..])
            {
                errors.Add(stopped);
                break;
            }
            if (compiled == GodotShaderLanguage.Err.Ok || language.Errors.Count == 0)
            {
                break;
            }
            GodotShaderLanguage.CompileError error = language.Errors[0];
            bool follows = Quoted(error.Message).Any(gone.Contains)
                || returnGone && error.Message.StartsWith("Expected at least one 'return' statement", StringComparison.Ordinal)
                || caseGone && error.Message.StartsWith("Expected a 'case'", StringComparison.Ordinal)
                || switchKept && error.Message is "Expected an integer constant." or "Expected an unsigned integer constant.";
            if (!follows && !errors.Any(e => e.Message == error.Message && e.Offset == error.Offset))
            {
                errors.Add(error);
            }
            if (GodotShaderStatements.At(spans, code, error.Offset) is not { } span || code.AsSpan(span.Start).TrimStart().StartsWith("shader_type"))
            {
                break;
            }
            while (blanked.Contains(span))
            {
                if (GodotShaderStatements.Around(spans, span) is not { } around)
                {
                    return (language, errors);
                }
                span = around;
            }
            if (softened.Add((span, error.Offset)))
            {
                if (GodotShaderStatements.WithoutCondition(code, span, error.Offset) is ({ } condition, bool ofSwitch))
                {
                    code = condition;
                    switchKept |= ofSwitch;
                    continue;
                }
                if (GodotShaderStatements.WithoutInitializer(code, span) is { } kept)
                {
                    code = kept;
                    continue;
                }
            }
            string text = code[span.Start..span.End];
            gone.UnionWith(GodotShaderStatements.NamesDeclared(text));
            returnGone |= text.Contains("return", StringComparison.Ordinal);
            caseGone |= text.Contains("case", StringComparison.Ordinal) || text.Contains("default", StringComparison.Ordinal);
            blanked.Add(span);
            code = GodotShaderStatements.Blanked(code, span);
        }
        return (language, errors);
    }

    /// <summary>The names a message quotes ('speedd').</summary>
    private static IEnumerable<string> Quoted(string message)
    {
        for (int open = message.IndexOf('\''); open >= 0;)
        {
            int close = message.IndexOf('\'', open + 1);
            if (close < 0)
            {
                yield break;
            }
            yield return message[(open + 1)..close];
            open = message.IndexOf('\'', close + 1);
        }
    }

    /// <summary>What Godot's compiler is given for a shader of a type (an include's, for null): its type's functions and
    /// modes, every type's for an include, and the project's options.</summary>
    public static GodotShaderLanguage.ShaderCompileInfo InfoFor(GodotShaderTable table, string? shaderType, GodotShaderCheckOptions options)
    {
        GodotShaderWarningFlags? warnings = shaderType is null ? options.Warnings & ~IncludeCleared : options.Warnings;
        GodotShaderType? type = shaderType is null ? null : table.TypeOf(shaderType) ?? table.TypeOf("spatial");
        return new GodotShaderLanguage.ShaderCompileInfo
        {
            Functions = type is null ? new(StringComparer.Ordinal) : FunctionsOf(type, options.Godot),
            RenderModes = type is null ? [] : ModesOf(type.Modes),
            StencilModes = type is null ? [] : ModesOf(type.StencilModes),
            ShaderTypes = [.. table.Types.Select(t => t.Name)],
            AllFunctions = [.. table.Types.Select(t => FunctionsOf(t, options.Godot))],
            AllModes = [.. table.Types.Select(t => (ModesOf(t.Modes), ModesOf(t.StencilModes)))],
            GlobalShaderUniformType = options.GlobalUniforms is { } globals
                ? name => globals.TryGetValue(name, out string? typeName) ? GodotShaderLanguage.DataTypeNamed(typeName) : GodotShaderLanguage.DataType.Max
                : null,
            IsInclude = shaderType is null,
            LowEnd = options.RenderingMethod == "gl_compatibility",
            RenderingMethod = options.RenderingMethod,
            XrEnabled = options.XrEnabled,
            Warnings = warnings,
            Godot = options.Godot,
        };
    }

    private static Dictionary<string, GodotShaderLanguage.FunctionInfo> FunctionsOf(GodotShaderType type, Version? godot)
    {
        bool constantValues = type.Name == "spatial" || godot is null || godot >= ConstantValues48;
        Dictionary<string, GodotShaderLanguage.FunctionInfo> functions = new(StringComparer.Ordinal);
        foreach (GodotShaderProcessor processor in type.Functions)
        {
            GodotShaderLanguage.FunctionInfo function = new()
            {
                CanDiscard = processor.Discards,
                MainFunction = processor.Main,
            };
            foreach (GodotShaderBuiltIn builtIn in processor.BuiltIns)
            {
                function.BuiltIns[builtIn.Name] = new GodotShaderLanguage.BuiltInInfo(GodotShaderLanguage.DataTypeNamed(builtIn.Type), builtIn.Constant,
                    constantValues && processor.Name == GodotShaderTable.Constants ? ValueOf(builtIn.Name) : null);
            }
            foreach (GodotShaderCall stage in processor.StageFunctions)
            {
                function.StageFunctions[stage.Name] = new GodotShaderLanguage.StageFunctionInfo
                {
                    Arguments = [.. stage.Arguments.Select(a => new GodotShaderLanguage.StageArgument(a.Name, GodotShaderLanguage.DataTypeNamed(a.Type)))],
                    ReturnType = GodotShaderLanguage.DataTypeNamed(stage.Returns),
                };
            }
            functions[processor.Name] = function;
        }
        return functions;
    }

    /// <summary>The value Godot gives a constant built-in it knows the value of.</summary>
    private static GodotShaderLanguage.Scalar[]? ValueOf(string name) => name switch
    {
        "PI" => [new GodotShaderLanguage.Scalar { Real = MathF.PI }],
        "TAU" => [new GodotShaderLanguage.Scalar { Real = MathF.Tau }],
        "E" => [new GodotShaderLanguage.Scalar { Real = MathF.E }],
        _ => null,
    };

    private static List<GodotShaderLanguage.ModeInfo> ModesOf(IReadOnlyList<GodotShaderMode> modes) =>
        [.. modes.Select(m => new GodotShaderLanguage.ModeInfo(m.Name, m.Options))];
}

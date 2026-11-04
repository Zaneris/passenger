namespace Passenger.Workspace.GodotShader;

/// <summary>How a project's shaders are checked: its rendering method, the Godot that runs it, the warnings it asks for
/// (none where null) and whether they count as errors, whether its shaders are built for XR, and the type of each global
/// uniform it declares (none checked where null).</summary>
public sealed record GodotShaderCheckOptions
{
    /// <summary>Every warning Godot's editor looks for by default.</summary>
    public const GodotShaderWarningFlags AllWarnings = (GodotShaderWarningFlags)((1u << (int)GodotShaderWarningCode.Max) - 1);

    public string RenderingMethod { get; init; } = GodotDefinitions.ForwardPlus;

    public Version? Godot { get; init; }

    public GodotShaderWarningFlags? Warnings { get; init; } = AllWarnings;

    public bool WarningsAsErrors { get; init; }

    public bool XrEnabled { get; init; }

    public IReadOnlyDictionary<string, string>? GlobalUniforms { get; init; }
}

/// <summary>What Godot's front end made of a shader or an include: its messages, as Godot's editor gives them (a
/// preprocessor's error, else every error of the compile and, where it compiled, its warnings by line), the shader type
/// it was read as, and the uniforms it declares as the compile read them.</summary>
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
        GodotShaderPreprocessed preprocessed = GodotShaderPreprocessor.Preprocess(unit.Code, unit.Res, options.RenderingMethod, Included, options.Godot);
        if (preprocessed.Error is { } error)
        {
            GodotShaderFilePosition first = preprocessed.ErrorPositions.Count > 0 ? preprocessed.ErrorPositions[0] : new GodotShaderFilePosition(unit.Res, 1);
            GodotShaderFilePosition last = preprocessed.ErrorPositions.Count > 0 ? preprocessed.ErrorPositions[^1] : first;
            GodotShaderMessage message = preprocessed.ErrorPositions.Count > 1
                ? new GodotShaderMessage(true, last.File, last.Line, error)
                : new GodotShaderMessage(true, string.Empty, first.Line, error);
            return new GodotShaderChecked([message], string.Empty, []);
        }

        string shaderType = isInclude ? string.Empty : GodotShaderLanguage.GetShaderType(preprocessed.Code);
        GodotShaderLanguage.ShaderCompileInfo info = InfoFor(table, isInclude ? null : shaderType, options);
        (GodotShaderLanguage language, List<GodotShaderLanguage.CompileError> errors) = Compiled(preprocessed.Code, info);
        List<GodotShaderMessage> messages = [];
        foreach (GodotShaderLanguage.CompileError compiled in errors)
        {
            messages.Add(compiled.Positions.Count > 1
                ? new GodotShaderMessage(true, compiled.Positions[^1].File, compiled.Positions[^1].Line, compiled.Message)
                : new GodotShaderMessage(true, string.Empty, compiled.Line, compiled.Message));
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

        GodotShaderIncluded? Included(string path) => unit.Includes.TryGetValue(path, out string? text)
            ? new GodotShaderIncluded(true, path.EndsWith(".gdshaderinc", StringComparison.Ordinal), text)
            : null;
    }

    /// <summary>Every error of the code, the first as Godot gives it, then each Godot would give once the statements of
    /// those before were taken out: a condition or a local's initializer first, so the rest is still read, else the
    /// statement whole, else what holds it. What a taken-out declaration would make wrong after it (a use of its name, a function left
    /// with no return) is left unsaid. With the last compile, whose shader is the one read furthest.</summary>
    private static (GodotShaderLanguage Language, List<GodotShaderLanguage.CompileError> Errors) Compiled(string code, GodotShaderLanguage.ShaderCompileInfo info)
    {
        List<GodotShaderLanguage.CompileError> errors = [];
        List<GodotShaderStatements.Span> spans = GodotShaderStatements.Of(code);
        HashSet<string> gone = new(StringComparer.Ordinal);
        HashSet<(GodotShaderStatements.Span, int)> softened = [];
        HashSet<GodotShaderStatements.Span> blanked = [];
        bool returnGone = false;
        GodotShaderLanguage language = new();
        for (int pass = 0; pass < MostErrors; pass++)
        {
            language = new GodotShaderLanguage();
            if (language.Compile(code, info) == GodotShaderLanguage.Err.Ok || language.Errors.Count == 0)
            {
                break;
            }
            GodotShaderLanguage.CompileError error = language.Errors[0];
            bool follows = Quoted(error.Message).Any(gone.Contains)
                || returnGone && error.Message.StartsWith("Expected at least one 'return' statement", StringComparison.Ordinal);
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
            if (softened.Add((span, error.Offset)) && (GodotShaderStatements.WithoutCondition(code, span, error.Offset) ?? GodotShaderStatements.WithoutInitializer(code, span)) is { } kept)
            {
                code = kept;
                continue;
            }
            string text = code[span.Start..span.End];
            gone.UnionWith(GodotShaderStatements.NamesDeclared(text));
            returnGone |= text.Contains("return", StringComparison.Ordinal);
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A built-in of Godot's shading language a processor function reads (or writes, where it is not constant).</summary>
public sealed record GodotShaderBuiltIn(string Name, string Type, bool Constant);

/// <summary>An argument of a function of Godot's shading language.</summary>
public sealed record GodotShaderArgument(string Name, string Type);

/// <summary>A function of Godot's shading language, one overload: a built-in one, or a stage function a processor function
/// may call (<c>texture_sdf</c>).</summary>
public sealed record GodotShaderCall(string Name, string Returns, IReadOnlyList<GodotShaderArgument> Arguments)
{
    /// <summary>As the language declares it: <c>float radians(float degrees)</c>.</summary>
    public string Signature => $"{Returns} {Name}({string.Join(", ", Arguments.Select(a => $"{a.Type} {a.Name}"))})";
}

/// <summary>A shader type's processor function (<c>vertex</c>), or its scopes every function reads (<see cref="GodotShaderTable.Global"/>,
/// <see cref="GodotShaderTable.Constants"/>): the built-ins it offers and the stage functions it may call.</summary>
public sealed record GodotShaderProcessor(string Name, bool Main, IReadOnlyList<GodotShaderBuiltIn> BuiltIns, IReadOnlyList<GodotShaderCall> StageFunctions)
{
    /// <summary>Whether its code may discard.</summary>
    public bool Discards { get; init; }
}

/// <summary>An entry of the language's built-in function table as its compiler holds it, constructors too: what it
/// returns and takes, its tag (<c>global</c>, or <c>array</c> for an array's own) and whether only the high end has it.</summary>
public sealed record GodotShaderBuiltinFunction(string Name, string Returns, IReadOnlyList<string> Takes, string Tag, bool HighEnd);

/// <summary>A render or stencil mode, each of its options written after its name (<c>blend</c>'s <c>blend_mix</c>), or its
/// name alone where it has none.</summary>
public sealed record GodotShaderMode(string Name, IReadOnlyList<string> Options)
{
    public IEnumerable<string> Written => Options.Count == 0 ? [Name] : Options.Select(o => $"{Name}_{o}");
}

/// <summary>A shader type (<c>spatial</c>): its processor functions and scopes, and its render and stencil modes.</summary>
public sealed record GodotShaderType(string Name, IReadOnlyList<GodotShaderProcessor> Functions, IReadOnlyList<GodotShaderMode> Modes, IReadOnlyList<GodotShaderMode> StencilModes)
{
    /// <summary>The processor functions a shader of the type writes.</summary>
    public IEnumerable<GodotShaderProcessor> Mains => Functions.Where(f => f.Main);
}

/// <summary>A tiny shader that compiles only where the Godot it is given takes the candidate its key names.</summary>
public sealed record GodotShaderProbe(string Key, string Code);

/// <summary>Godot's shading language as the table Passenger carries says it (<c>GodotShaderTypes.json</c>, written by
/// <c>just godot-shader-types</c> from Godot's own sources): each shader type's processor functions and their built-ins,
/// render and stencil modes, the uniform hints and the built-in functions. The table only proposes: <see cref="Probes"/>
/// asks the Godot found of each candidate, and <see cref="Without"/> keeps those it took. Pure.</summary>
public sealed class GodotShaderTable
{
    /// <summary>The scope whose built-ins every processor function of a type reads.</summary>
    public const string Global = "global";

    /// <summary>The scope of a type's constants, read anywhere.</summary>
    public const string Constants = "constants";

    private const string Resource = "Passenger.Workspace.GodotShaderTypes.json";

    private static readonly Lazy<GodotShaderTable> carried = new(() =>
    {
        using Stream stream = typeof(GodotShaderTable).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Passenger carries no {Resource}");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return Read(reader.ReadToEnd());
    });

    private GodotShaderTable(string source, string fingerprint, IReadOnlyList<GodotShaderType> types, IReadOnlyDictionary<string, IReadOnlyList<string>> hints,
        IReadOnlyList<GodotShaderCall> functions, IReadOnlyList<GodotShaderBuiltinFunction> builtinFunctions)
    {
        Source = source;
        Fingerprint = fingerprint;
        Types = types;
        HintsByType = hints;
        Functions = functions;
        BuiltinFunctions = builtinFunctions;
    }

    /// <summary>The table as Passenger carries it.</summary>
    public static GodotShaderTable Carried => carried.Value;

    /// <summary>Where its sources were taken from.</summary>
    public string Source { get; }

    /// <summary>What tells this table from another, so what a Godot was asked of one is never taken for another's.</summary>
    public string Fingerprint { get; }

    public IReadOnlyList<GodotShaderType> Types { get; }

    /// <summary>The uniform hints each type takes, by its name.</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> HintsByType { get; }

    /// <summary>The language's built-in functions, every overload, in its order.</summary>
    public IReadOnlyList<GodotShaderCall> Functions { get; }

    /// <summary>The compiler's table of built-in functions, constructors too, in its order, which overloads are tried in.</summary>
    public IReadOnlyList<GodotShaderBuiltinFunction> BuiltinFunctions { get; }

    /// <summary>The table from its JSON.</summary>
    public static GodotShaderTable Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        IReadOnlyList<string> hints = JsonMembers.Texts(root, "hints");
        List<GodotShaderType> types = [.. JsonMembers.Items(root, "types").Select(type => new GodotShaderType(
            JsonMembers.Text(type, "name") ?? string.Empty,
            [.. JsonMembers.Items(type, "functions").Select(f => new GodotShaderProcessor(JsonMembers.Text(f, "name") ?? string.Empty, JsonMembers.Bool(f, "main") == true,
                [.. JsonMembers.Items(f, "builtIns").Select(b => new GodotShaderBuiltIn(JsonMembers.Text(b, "name") ?? string.Empty, JsonMembers.Text(b, "type") ?? string.Empty,
                    JsonMembers.Bool(b, "constant") == true))],
                [.. JsonMembers.Items(f, "stageFunctions").Select(CallOf)])
            {
                Discards = JsonMembers.Bool(f, "discards") == true,
            })],
            Modes(type, "modes"), Modes(type, "stencilModes")))];
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
        return new GodotShaderTable(JsonMembers.Text(root, "source") ?? string.Empty, fingerprint, types,
            types.ToDictionary(t => t.Name, _ => hints, StringComparer.Ordinal), [.. JsonMembers.Items(root, "functions").Select(CallOf)],
            [.. JsonMembers.Items(root, "builtins").Select(BuiltinOf)]);

        static IReadOnlyList<GodotShaderMode> Modes(JsonElement type, string name) =>
            [.. JsonMembers.Items(type, name).Select(m => new GodotShaderMode(JsonMembers.Text(m, "name") ?? string.Empty, JsonMembers.Texts(m, "options")))];

        static GodotShaderBuiltinFunction BuiltinOf(JsonElement entry) => new(JsonMembers.Text(entry, "name") ?? string.Empty,
            JsonMembers.Text(entry, "returns") ?? "void",
            (JsonMembers.Text(entry, "takes") ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries),
            JsonMembers.Text(entry, "tag") ?? "global", JsonMembers.Bool(entry, "highEnd") == true);

        static GodotShaderCall CallOf(JsonElement call) => new(JsonMembers.Text(call, "name") ?? string.Empty, JsonMembers.Text(call, "returns") ?? "void",
            [.. JsonMembers.Items(call, "arguments").Select(a => new GodotShaderArgument(JsonMembers.Text(a, "name") ?? string.Empty, JsonMembers.Text(a, "type") ?? string.Empty))]);
    }

    /// <summary>A shader type by its name; null for one the table does not hold.</summary>
    public GodotShaderType? TypeOf(string? name) => Types.FirstOrDefault(t => t.Name == name);

    /// <summary>The built-ins a processor function of a type reads, each with the scope that offers it: its own, then the
    /// type's global ones and its constants; at a shader's top (no function) its constants alone.</summary>
    public IReadOnlyList<(GodotShaderBuiltIn BuiltIn, string Scope)> BuiltInsIn(string type, string? function)
    {
        if (TypeOf(type) is not { } kind)
        {
            return [];
        }
        string[] scopes = function is null ? [Constants] : [function, Global, Constants];
        return [.. scopes.SelectMany(scope => kind.Functions.Where(f => f.Name == scope).SelectMany(f => f.BuiltIns.Select(b => (b, scope))))
            .DistinctBy(b => b.b.Name)];
    }

    /// <summary>The stage functions a processor function of a type may call.</summary>
    public IReadOnlyList<GodotShaderCall> StageFunctionsIn(string type, string function) =>
        [.. TypeOf(type)?.Functions.Where(f => f.Name == function).SelectMany(f => f.StageFunctions) ?? []];

    /// <summary>The uniform hints a type takes.</summary>
    public IReadOnlyList<string> HintsOf(string type) => HintsByType.TryGetValue(type, out IReadOnlyList<string>? hints) ? hints : [];

    /// <summary>A tiny shader for each candidate the table offers, which compiles only where Godot takes the candidate: a
    /// built-in read in its function (a scope's in the type's first), a stage function called with zero values, each render
    /// and stencil mode, and each hint on a uniform of a type it fits.</summary>
    public IReadOnlyList<GodotShaderProbe> Probes()
    {
        List<GodotShaderProbe> probes = [];
        foreach (GodotShaderType type in Types)
        {
            string first = type.Mains.FirstOrDefault()?.Name ?? "fragment";
            foreach (GodotShaderProcessor function in type.Functions)
            {
                string body = function.Main ? function.Name : first;
                probes.AddRange(function.BuiltIns.Select(b => new GodotShaderProbe(BuiltInKey(type.Name, function.Name, b.Name),
                    $"shader_type {type.Name};\nvoid {body}() {{\n\t{b.Name};\n}}\n")));
                probes.AddRange(function.StageFunctions.Select(s => new GodotShaderProbe(StageKey(type.Name, function.Name, s.Name),
                    $"shader_type {type.Name};\nvoid {body}() {{\n\t{s.Name}({string.Join(", ", s.Arguments.Select(a => ZeroOf(a.Type)))});\n}}\n")));
            }
            probes.AddRange(type.Modes.SelectMany(m => m.Written).Select(m => new GodotShaderProbe(ModeKey(type.Name, m), $"shader_type {type.Name};\nrender_mode {m};\n")));
            probes.AddRange(type.StencilModes.SelectMany(m => m.Written).Select(m => new GodotShaderProbe(StencilKey(type.Name, m), $"shader_type {type.Name};\nstencil_mode {m};\n")));
            probes.AddRange(HintsOf(type.Name).Select(h => new GodotShaderProbe(HintKey(type.Name, h), $"shader_type {type.Name};\n{HintedUniform(h)}\n")));
        }
        return probes;
    }

    /// <summary>The table without the candidates whose probes a Godot refused (by <see cref="GodotShaderProbe.Key"/>).</summary>
    public GodotShaderTable Without(IReadOnlySet<string> refused)
    {
        if (refused.Count == 0)
        {
            return this;
        }
        List<GodotShaderType> types = [.. Types.Select(type => type with
        {
            Functions = [.. type.Functions.Select(f => f with
            {
                BuiltIns = [.. f.BuiltIns.Where(b => !refused.Contains(BuiltInKey(type.Name, f.Name, b.Name)))],
                StageFunctions = [.. f.StageFunctions.Where(s => !refused.Contains(StageKey(type.Name, f.Name, s.Name)))],
            })],
            Modes = Kept(type.Modes, m => ModeKey(type.Name, m)),
            StencilModes = Kept(type.StencilModes, m => StencilKey(type.Name, m)),
        })];
        return new GodotShaderTable(Source, Fingerprint, types,
            types.ToDictionary(t => t.Name, t => (IReadOnlyList<string>)[.. HintsOf(t.Name).Where(h => !refused.Contains(HintKey(t.Name, h)))], StringComparer.Ordinal), Functions,
            BuiltinFunctions);

        IReadOnlyList<GodotShaderMode> Kept(IReadOnlyList<GodotShaderMode> modes, Func<string, string> key) =>
            [.. modes.Where(m => m.Options.Count == 0 ? !refused.Contains(key(m.Name)) : m.Options.Any(o => !refused.Contains(key($"{m.Name}_{o}"))))
                .Select(m => m.Options.Count == 0 ? m : m with { Options = [.. m.Options.Where(o => !refused.Contains(key($"{m.Name}_{o}")))] })];
    }

    internal static string BuiltInKey(string type, string function, string name) => $"built-in:{type}:{function}:{name}";

    internal static string StageKey(string type, string function, string name) => $"stage:{type}:{function}:{name}";

    internal static string ModeKey(string type, string mode) => $"render-mode:{type}:{mode}";

    internal static string StencilKey(string type, string mode) => $"stencil-mode:{type}:{mode}";

    internal static string HintKey(string type, string hint) => $"hint:{type}:{hint}";

    /// <summary>A value of a type of the language, for an argument a probe gives.</summary>
    internal static string ZeroOf(string type) => type switch
    {
        "float" => "0.0",
        "int" => "0",
        "uint" => "0u",
        "bool" => "false",
        _ when type.StartsWith("vec", StringComparison.Ordinal) || type.StartsWith("mat", StringComparison.Ordinal) => $"{type}(0.0)",
        _ when type.StartsWith("ivec", StringComparison.Ordinal) => $"{type}(0)",
        _ when type.StartsWith("uvec", StringComparison.Ordinal) => $"{type}(0u)",
        _ when type.StartsWith("bvec", StringComparison.Ordinal) => $"{type}(false)",
        _ => $"{type}(0)",
    };

    /// <summary>A uniform of a type a hint fits, given the hint.</summary>
    private static string HintedUniform(string hint) => hint switch
    {
        "source_color" => "uniform vec4 probed : source_color;",
        "color_conversion_disabled" => "uniform vec4 probed : source_color, color_conversion_disabled;",
        "hint_range" => "uniform float probed : hint_range(0.0, 1.0);",
        "hint_enum" => "uniform int probed : hint_enum(\"a\");",
        "instance_index" => "instance uniform float probed : instance_index(0);",
        "no_storage" or "no_editor" => $"uniform float probed : {hint};",
        _ => $"uniform sampler2D probed : {hint};",
    };
}

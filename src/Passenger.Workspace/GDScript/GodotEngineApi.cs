using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace.GDScript;

/// <summary>A method or function of Godot's API: what it returns as the API writes a type (<c>"void"</c> for nothing,
/// <c>"enum::Node.ProcessMode"</c>, <c>"typedarray::Node"</c>), its parameters' names and types, how many a call must give,
/// and whether it takes any number more or is called on its class.</summary>
public sealed record GodotApiMethod(string Name, string Returns, IReadOnlyList<(string Name, string Type)> Parameters, int Required, bool Varargs, bool IsStatic)
{
    /// <summary>Whether a script's class overriding it is what the engine calls (<c>_ready</c>), as the dump's is_virtual says.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Whether it leaves the value it is called on as it was, as the dump's is_const says.</summary>
    public bool IsConst { get; init; }

    /// <summary>Whether Godot works a call of it out as it compiles, given constants: a utility function of the dump's math
    /// category, or one of GDScript's own it registers so.</summary>
    public bool Folds { get; init; }

    /// <summary>Each parameter's default as the dump writes it (<c>false</c>, <c>0</c>, <c>&amp;""</c>), null where a call
    /// must give it; empty where none is known.</summary>
    public IReadOnlyList<string?> Defaults { get; init; } = [];
}

/// <summary>An enum of Godot's API with its values by name.</summary>
public sealed record GodotApiEnum(string Name, IReadOnlyDictionary<string, long> Values, bool IsBitfield);

/// <summary>An engine class (<c>Node</c>) or a built-in type (<c>Vector2</c>) of Godot's API: what it extends, and its methods,
/// properties (for a built-in type its members), signals, constants (a built-in type's by their type), enums, and for a
/// built-in type its operators (left, operator, right as the API names them, with what each gives) and what indexing gives.</summary>
public sealed class GodotApiClass
{
    public required string Name { get; init; }

    public string? Inherits { get; init; }

    public bool IsBuiltin { get; init; }

    /// <summary>Whether an engine class can be made (the dump's <c>is_instantiable</c>), which an abstract one cannot.</summary>
    public bool Instantiable { get; init; } = true;

    /// <summary>An engine class's properties read by a getter that have no setter, which Godot holds read-only.</summary>
    public HashSet<string> ReadOnly { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GodotApiMethod> Methods { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GodotApiMethod> Signals { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Constants { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GodotApiEnum> Enums { get; } = new(StringComparer.Ordinal);

    /// <summary>A built-in type's operators: by the operator's name and the right operand's type (empty for a unary one),
    /// what the operation gives.</summary>
    public Dictionary<(string Operator, string Right), string> Operators { get; } = [];

    public string? Indexing { get; init; }

    /// <summary>A built-in type's constructors, each its parameters' names and types, as the dump lists them.</summary>
    public List<IReadOnlyList<(string Name, string Type)>> Constructors { get; } = [];
}

/// <summary>Godot's API as its own <c>--dump-extension-api</c> writes it (<c>extension_api.json</c>): its engine classes, its
/// built-in types, its utility functions with GDScript's own (<c>range</c>, <c>load</c>, <c>len</c>…, which the dump leaves
/// out), its singletons, and its global enums and constants. What GDScript's types are resolved against. Pure.</summary>
public sealed class GodotEngineApi
{
    private GodotEngineApi()
    {
    }

    public Dictionary<string, GodotApiClass> Classes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GodotApiClass> Builtins { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GodotApiMethod> Functions { get; } = new(StringComparer.Ordinal);

    /// <summary>The engine's singletons (<c>Input</c>, <c>OS</c>) by name, each with its class.</summary>
    public Dictionary<string, string> Singletons { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GodotApiEnum> GlobalEnums { get; } = new(StringComparer.Ordinal);

    /// <summary>Each global enum's values by name, with the enum it belongs to (<c>KEY_A</c>: Key).</summary>
    public Dictionary<string, string> GlobalEnumValues { get; } = new(StringComparer.Ordinal);

    public HashSet<string> GlobalConstants { get; } = new(StringComparer.Ordinal);

    /// <summary>The Godot that wrote the dump, as its header says (4.8 for 4.8 beta 2); null where it says none.</summary>
    public Version? Version { get; private set; }

    /// <summary>GDScript's own functions, which no dump of the API holds (modules/gdscript/gdscript_utility_functions.cpp),
    /// each with its parameters as Godot's class reference names them.</summary>
    private static readonly (string Name, string Returns, int Required, bool Varargs, (string Name, string Type)[] Parameters)[] GDScriptFunctions =
    [
        ("convert", "Variant", 2, false, [("what", "Variant"), ("type", "int")]), ("type_exists", "bool", 1, false, [("type", "StringName")]),
        ("char", "String", 1, false, [("code", "int")]), ("ord", "int", 1, false, [("char", "String")]), ("range", "Array", 0, true, []),
        ("load", "Resource", 1, false, [("path", "String")]), ("inst_to_dict", "Dictionary", 1, false, [("instance", "Object")]),
        ("dict_to_inst", "Object", 1, false, [("dictionary", "Dictionary")]), ("Color8", "Color", 3, false, [("r8", "int"), ("g8", "int"), ("b8", "int"), ("a8", "int")]),
        ("print_debug", "void", 0, true, []), ("print_stack", "void", 0, false, []), ("get_stack", "Array", 0, false, []), ("len", "int", 1, false, [("var", "Variant")]),
        ("is_instance_of", "bool", 2, false, [("value", "Variant"), ("type", "Variant")]),
    ];

    /// <summary>GDScript's own functions Godot works out as it compiles.</summary>
    private static readonly HashSet<string> GDScriptFolded = new(StringComparer.Ordinal) { "convert", "type_exists", "char", "ord", "Color8", "len", "is_instance_of" };

    /// <summary>Object's virtual methods a script may override, which no dump of the API lists.</summary>
    private static readonly GodotApiMethod[] ObjectVirtuals =
    [
        new("_notification", "void", [("what", "int")], 1, false, false), new("_to_string", "String", [], 0, false, false),
        new("_get", "Variant", [("property", "StringName")], 1, false, false), new("_set", "bool", [("property", "StringName"), ("value", "Variant")], 2, false, false),
        new("_get_property_list", "Array", [], 0, false, false), new("_property_can_revert", "bool", [("property", "StringName")], 1, false, false),
        new("_property_get_revert", "Variant", [("property", "StringName")], 1, false, false), new("_validate_property", "void", [("property", "Dictionary")], 1, false, false),
        new("_iter_init", "bool", [("iter", "Array")], 1, false, false), new("_iter_next", "bool", [("iter", "Array")], 1, false, false),
        new("_iter_get", "Variant", [("iter", "Variant")], 1, false, false),
    ];

    /// <summary>The API a dump's JSON gives.</summary>
    public static GodotEngineApi Read(ReadOnlySpan<byte> json)
    {
        GodotEngineApi api = new();
        Utf8JsonReader reader = new(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (JsonMembers.Object(root, "header") is { } header && Count(header, "version_major") is int major && Count(header, "version_minor") is int minor)
        {
            api.Version = new Version(major, minor);
        }
        foreach (JsonElement type in Array(root, "builtin_classes"))
        {
            GodotApiClass builtin = new() { Name = Text(type, "name") ?? string.Empty, IsBuiltin = true, Indexing = Text(type, "indexing_return_type") };
            foreach (JsonElement member in Array(type, "members"))
            {
                builtin.Properties[Text(member, "name") ?? string.Empty] = Text(member, "type") ?? "Variant";
            }
            foreach (JsonElement constant in Array(type, "constants"))
            {
                builtin.Constants[Text(constant, "name") ?? string.Empty] = Text(constant, "type") ?? builtin.Name;
            }
            foreach (JsonElement op in Array(type, "operators"))
            {
                builtin.Operators[(Text(op, "name") ?? string.Empty, Text(op, "right_type") ?? string.Empty)] = Text(op, "return_type") ?? "Variant";
            }
            foreach (JsonElement method in Array(type, "methods"))
            {
                GodotApiMethod read = Method(method, Text(method, "return_type"));
                builtin.Methods[read.Name] = read;
            }
            foreach (JsonElement constructor in Array(type, "constructors"))
            {
                builtin.Constructors.Add([.. Array(constructor, "arguments").Select(a => (Text(a, "name") ?? string.Empty, Text(a, "type") ?? "Variant"))]);
            }
            Enums(type, builtin.Enums);
            api.Builtins[builtin.Name] = builtin;
        }
        foreach (JsonElement type in Array(root, "classes"))
        {
            GodotApiClass engine = new() { Name = Text(type, "name") ?? string.Empty, Inherits = Text(type, "inherits"), Instantiable = JsonMembers.Bool(type, "is_instantiable") != false };
            foreach (JsonElement method in Array(type, "methods"))
            {
                GodotApiMethod read = Method(method, type: null);
                engine.Methods[read.Name] = read;
            }
            foreach (JsonElement property in Array(type, "properties"))
            {
                engine.Properties[Text(property, "name") ?? string.Empty] = Text(property, "type") ?? "Variant";
                if (Text(property, "getter") is { Length: > 0 } && Text(property, "setter") is null or "")
                {
                    engine.ReadOnly.Add(Text(property, "name") ?? string.Empty);
                }
            }
            foreach (JsonElement signal in Array(type, "signals"))
            {
                GodotApiMethod read = Method(signal, "void");
                engine.Signals[read.Name] = read;
            }
            foreach (JsonElement constant in Array(type, "constants"))
            {
                engine.Constants[Text(constant, "name") ?? string.Empty] = "int";
            }
            Enums(type, engine.Enums);
            api.Classes[engine.Name] = engine;
        }
        foreach (JsonElement function in Array(root, "utility_functions"))
        {
            GodotApiMethod read = Method(function, Text(function, "return_type") ?? "void") with { Folds = Text(function, "category") == "math" };
            api.Functions[read.Name] = read;
        }
        foreach ((string name, string returns, int required, bool varargs, (string, string)[] parameters) in GDScriptFunctions)
        {
            api.Functions[name] = new GodotApiMethod(name, returns, parameters, required, varargs, false)
            {
                Defaults = [.. parameters.Select((_, i) => i < required ? null : name == "Color8" ? "255" : null)],
                Folds = GDScriptFolded.Contains(name),
            };
        }
        if (api.Classes.GetValueOrDefault("Object") is { } baseObject)
        {
            foreach (GodotApiMethod virtualMethod in ObjectVirtuals)
            {
                // From Godot 4.4 the property list a script gives is an Array of Dictionary.
                GodotApiMethod added = virtualMethod.Name == "_get_property_list" && api.Version >= new Version(4, 4) ? virtualMethod with { Returns = "typedarray::Dictionary" } : virtualMethod;
                baseObject.Methods.TryAdd(added.Name, added with { IsVirtual = true });
            }
            // Godot's ClassDB holds free among Object's virtuals, with no method bind, so no dump lists it.
            baseObject.Methods.TryAdd("free", new GodotApiMethod("free", "void", [], 0, false, false) { IsVirtual = true });
        }
        foreach (JsonElement singleton in Array(root, "singletons"))
        {
            api.Singletons[Text(singleton, "name") ?? string.Empty] = Text(singleton, "type") ?? "Object";
        }
        Enums(root, api.GlobalEnums, "global_enums");
        foreach (GodotApiEnum global in api.GlobalEnums.Values)
        {
            foreach (string value in global.Values.Keys)
            {
                api.GlobalEnumValues[value] = global.Name;
            }
        }
        foreach (JsonElement constant in Array(root, "global_constants"))
        {
            api.GlobalConstants.Add(Text(constant, "name") ?? string.Empty);
        }
        return api;
    }

    /// <summary>An engine class and the classes it extends, itself first; empty for one the API lacks.</summary>
    public IEnumerable<GodotApiClass> Chain(string name)
    {
        for (int depth = 0; depth < 64 && name is not null && Classes.TryGetValue(name, out GodotApiClass? at); depth++)
        {
            yield return at;
            name = at.Inherits!;
        }
    }

    /// <summary>Whether one engine class is another or extends it.</summary>
    public bool Extends(string name, string ancestor) => Chain(name).Any(c => c.Name == ancestor);

    private static GodotApiMethod Method(JsonElement method, string? type)
    {
        List<(string, string)> parameters = [];
        List<string?> defaults = [];
        int required = 0;
        foreach (JsonElement argument in Array(method, "arguments"))
        {
            parameters.Add((Text(argument, "name") ?? string.Empty, Text(argument, "type") ?? "Variant"));
            string? given = JsonMembers.Scalar(argument, "default_value");
            defaults.Add(given);
            required += given is null ? 1 : 0;
        }
        string returns = type ?? (JsonMembers.Object(method, "return_value") is { } value ? Text(value, "type") ?? "Variant" : "void");
        return new GodotApiMethod(Text(method, "name") ?? string.Empty, returns, parameters, required, Flag(method, "is_vararg"), Flag(method, "is_static"))
        {
            IsVirtual = Flag(method, "is_virtual"),
            IsConst = Flag(method, "is_const"),
            Defaults = defaults,
        };
    }

    private static void Enums(JsonElement owner, Dictionary<string, GodotApiEnum> into, string property = "enums")
    {
        foreach (JsonElement written in Array(owner, property))
        {
            Dictionary<string, long> values = new(StringComparer.Ordinal);
            foreach (JsonElement value in Array(written, "values"))
            {
                values[Text(value, "name") ?? string.Empty] = long.TryParse(JsonMembers.Scalar(value, "value"), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long read) ? read : 0;
            }
            string name = Text(written, "name") ?? string.Empty;
            into[name] = new GodotApiEnum(name, values, Flag(written, "is_bitfield"));
        }
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement owner, string property) => JsonMembers.Items(owner, property);

    private static string? Text(JsonElement owner, string property) => JsonMembers.Text(owner, property);

    private static bool Flag(JsonElement owner, string property) => JsonMembers.Bool(owner, property) == true;

    private static int? Count(JsonElement owner, string property) =>
        int.TryParse(JsonMembers.Scalar(owner, property), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int read) ? read : null;
}

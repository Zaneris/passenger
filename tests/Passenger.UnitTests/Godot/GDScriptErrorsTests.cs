using System.Text;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's analyzer's errors as Passenger's analyzer gives them, in Godot's words and on Godot's lines (each script
/// checked against Godot's own server, which says the same, a twice-pushed error once), over a small API of the engine's:
/// a fact for each family ported (names, calls and types not found, values and calls mismatched, operators, casts and type
/// tests, static places, indices, loops and typed collections, members' names, overrides, exports, files lacked and
/// singleton autoloads, untyped overrides' returns, what must be constant, members reaching themselves and read from their
/// classes, assignments to constants and read-only properties, new() refused, @onready refused, calls Godot makes as it
/// reads, getters and setters, bases refused, 4.8's self.free()), Godot 4.8's words where they differ; and none where the
/// class extends one this reading cannot read.</summary>
public class GDScriptErrorsTests
{
    private const string Api = """
        {
          "global_enums": [{ "name": "Side", "values": [{ "name": "SIDE_LEFT", "value": 0 }, { "name": "SIDE_TOP", "value": 1 }] }],
          "global_constants": [],
          "utility_functions": [{ "name": "print", "return_type": "void", "is_vararg": true, "arguments": [] },
            { "name": "floor", "return_type": "Variant", "arguments": [{ "name": "x", "type": "Variant" }] },
            { "name": "snapped", "return_type": "Variant", "arguments": [{ "name": "x", "type": "Variant" }, { "name": "step", "type": "Variant" }] },
            { "name": "lerp", "return_type": "Variant", "arguments": [{ "name": "from", "type": "Variant" }, { "name": "to", "type": "Variant" }, { "name": "weight", "type": "float" }] }],
          "singletons": [{ "name": "Time", "type": "Time" }],
          "builtin_classes": [
            { "name": "Nil" }, { "name": "bool", "operators": [{ "name": "==", "right_type": "bool", "return_type": "bool" }, { "name": "not", "return_type": "bool" }] },
            { "name": "int", "operators": [{ "name": "+", "right_type": "int", "return_type": "int" }, { "name": "==", "right_type": "Variant", "return_type": "bool" },
              { "name": "/", "right_type": "int", "return_type": "int" }, { "name": "<<", "right_type": "int", "return_type": "int" }, { "name": "unary-", "return_type": "int" }] },
            { "name": "float" }, { "name": "String", "operators": [{ "name": "+", "right_type": "String", "return_type": "String" }] }, { "name": "StringName" },
            { "name": "Color", "constants": [{ "name": "RED", "type": "Color" }, { "name": "ALICE_BLUE", "type": "Color" }],
              "constructors": [{ "index": 0 }, { "index": 1, "arguments": [{ "name": "code", "type": "String" }] }] },
            { "name": "Vector3", "members": [{ "name": "x", "type": "float" }, { "name": "y", "type": "float" }, { "name": "z", "type": "float" }],
              "constants": [{ "name": "ZERO", "type": "Vector3" }], "enums": [{ "name": "Axis", "values": [{ "name": "AXIS_X", "value": 0 }] }],
              "methods": [{ "name": "length", "return_type": "float", "is_const": true }], "constructors": [{ "index": 0 }] },
            { "name": "Dictionary", "methods": [{ "name": "clear", "return_type": "void", "is_const": false }, { "name": "size", "return_type": "int", "is_const": true }] },
            { "name": "Array", "methods": [] }, { "name": "Callable", "methods": [{ "name": "call", "return_type": "Variant", "is_vararg": true, "is_const": true }] }, { "name": "Signal", "methods": [] }
          ],
          "classes": [
            { "name": "Object", "methods": [{ "name": "get", "return_value": { "type": "Variant" } }, { "name": "free" }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "_ready", "is_virtual": true }, { "name": "get_name", "return_value": { "type": "StringName" } },
              { "name": "_process", "is_virtual": true, "arguments": [{ "name": "delta", "type": "float" }] }],
              "properties": [{ "name": "name", "type": "StringName" }], "signals": [{ "name": "ready" }],
              "enums": [{ "name": "ProcessMode", "values": [{ "name": "PROCESS_MODE_INHERIT", "value": 0 }] }] },
            { "name": "CharacterBody2D", "inherits": "Node" }, { "name": "Resource", "inherits": "RefCounted" }, { "name": "Time", "inherits": "Object" },
            { "name": "InstancePlaceholder", "inherits": "Node", "is_instantiable": false },
            { "name": "SceneTree", "inherits": "Object", "properties": [{ "name": "root", "type": "Node", "getter": "get_root" },
              { "name": "current_scene", "type": "Node", "setter": "set_current_scene", "getter": "get_current_scene" }] },
            { "name": "Body", "inherits": "Object", "properties": [{ "name": "center", "type": "Vector3", "getter": "get_center" }] }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(Encoding.UTF8.GetBytes(Api));

    /// <summary>The same API as Godot 4.8's dump says it, by its header's version.</summary>
    private static readonly GodotEngineApi Engine48 = GodotEngineApi.Read(Encoding.UTF8.GetBytes("{ \"header\": { \"version_major\": 4, \"version_minor\": 8 }," + Api.TrimStart()[1..]));

    /// <summary>A script's errors as Godot's test runner prints them: its line, then its words; read against the project's
    /// singleton autoloads, where given which paths its files lack, and the engine's API (4.7's by default).</summary>
    private static List<string> Errors(string text, Func<string, GDScriptTree?>? others = null, IReadOnlyDictionary<string, string>? autoloads = null,
        Func<string, bool>? lacks = null, GodotEngineApi? engine = null)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.Empty(tree.Errors);
        GDScriptProject project = new(engine ?? Engine, p => p == "res://main.gd" ? tree : others?.Invoke(p), new Dictionary<string, string>(), autoloads ?? new Dictionary<string, string>(),
            lacks: lacks);
        GDScriptAnalysis analysis = new GDScriptAnalyzer(project).Analyze(tree, "res://main.gd");
        TextSnapshot snapshot = TextSnapshot.From(text);
        return [.. analysis.Findings(GDScriptWarningSettings.Every).Where(f => f.Code is null).Select(f => $"{snapshot.GetLineFromOffset(f.Start) + 1}: {f.Message}")];
    }

    /// <summary>A name, call, type, value or operator Godot would refuse is an error in its words on its
    /// line.</summary>
    [Fact]
    public void Names_calls_and_types_godot_would_refuse_are_errors_in_its_words() => Cases.All(
        (nameof(A_name_or_member_godot_would_not_find_is_an_error_in_its_words_on_its_line),
            A_name_or_member_godot_would_not_find_is_an_error_in_its_words_on_its_line),
        (nameof(A_value_of_a_type_its_target_takes_none_of_and_a_call_given_too_few_or_many_are_errors_in_godots_words_on_its_lines),
            A_value_of_a_type_its_target_takes_none_of_and_a_call_given_too_few_or_many_are_errors_in_godots_words_on_its_lines));

    /// <summary>What must be constant, a preload and a base the project lacks are errors only where Godot would say
    /// so.</summary>
    [Fact]
    public void Constants_preloads_and_bases_are_errors_only_where_godot_says_so() => Cases.All(
        (nameof(A_base_the_project_lacks_is_the_scripts_one_error_as_godot_reads_no_body_past_it),
            A_base_the_project_lacks_is_the_scripts_one_error_as_godot_reads_no_body_past_it),
        (nameof(A_class_extending_a_script_not_read_says_nothing_of_names_it_does_not_find),
            A_class_extending_a_script_not_read_says_nothing_of_names_it_does_not_find),
        (nameof(With_no_engines_api_a_base_no_script_gives_is_never_said_missing_and_the_bodys_own_errors_stand),
            With_no_engines_api_a_base_no_script_gives_is_never_said_missing_and_the_bodys_own_errors_stand));

    private void A_name_or_member_godot_would_not_find_is_an_error_in_its_words_on_its_line()
    {
        const string Script = "extends Node\n\nenum Mode { A, B }\nvar speed := 1.0\n\n\nfunc _ready() -> void:\n"
            + "\tprint(undefined_name)\n\tprint(int)\n\tprint(Side)\n\tprint(Vector3.Axis)\n\tprint(Vector3.NOPE)\n\tprint(Mode.C)\n"
            + "\tvar v := Vector3()\n\tprint(v.w)\n\tprint(KinematicBody2D)\n\tprint(ProcessMode.PROCESS_MODE_INHERIT, Vector3.Axis.AXIS_X, Side.SIDE_LEFT, v.x, Vector3.ZERO)\n";
        Assert.Equal(
        [
            "8: Identifier \"undefined_name\" not declared in the current scope.",
            "9: Builtin type cannot be used as a name on its own.",
            "9: Identifier \"int\" not declared in the current scope.",
            "10: Global enum \"Side\" cannot be used on its own.",
            "11: Type \"Axis\" in base \"Vector3\" cannot be used on its own.",
            "12: Cannot find member \"NOPE\" in base \"Vector3\".",
            "13: Cannot find member \"C\" in base \"main.gd.Mode\".",
            "15: Cannot find member \"w\" in base \"Vector3\".",
            "16: Identifier \"KinematicBody2D\" not declared in the current scope. Did you mean to use \"CharacterBody2D\"?",
        ], Errors(Script));
    }

    private void A_value_of_a_type_its_target_takes_none_of_and_a_call_given_too_few_or_many_are_errors_in_godots_words_on_its_lines()
    {
        const string Script = "extends Node\n\nenum MyEnum { A, B }\nenum Other { X, Y }\nconst LIMIT = 3\nvar count: int = \"three\"\nvar e: MyEnum = Other.X\n"
            + "var typed_float: float = 1\nvar n := null\n\n\nfunc takes(a: int, b: String = \"\") -> int:\n\treturn a\n\n\nfunc nothing() -> void:\n\treturn 1\n\n\n"
            + "func gives() -> int:\n\treturn \"a\"\n\n\nfunc _ready() -> void:\n\tcount = \"four\"\n\tLIMIT = 4\n\tMyEnum.A = 1\n\tvar s: String = 5\n\ttakes()\n"
            + "\ttakes(1, \"b\", 3)\n\ttakes(\"x\")\n\tvar v = nothing()\n\tprint(s, v, typed_float, n)\n\te = Other.Y\n";
        Assert.Equal(
        [
            "6: Cannot assign a value of type \"String\" as \"int\".",
            "6: Cannot assign a value of type String to variable \"count\" with specified type int.",
            "7: Cannot assign a value of type \"main.gd.Other\" as \"main.gd.MyEnum\".",
            "7: Cannot assign a value of type main.gd.Other to variable \"e\" with specified type main.gd.MyEnum.",
            "9: Cannot infer the type of \"n\" variable because the value is \"null\".",
            "17: A void function cannot return a value.",
            "21: Cannot return a value of type \"String\" as \"int\".",
            "21: Cannot return value of type \"String\" because the function return type is \"int\".",
            "25: Cannot assign a value of type \"String\" as \"int\".",
            "25: Value of type \"String\" cannot be assigned to a variable of type \"int\".",
            "26: Cannot assign a new value to a constant.",
            "27: Cannot assign a new value to a constant.",
            "28: Cannot assign a value of type \"int\" as \"String\".",
            "28: Cannot assign a value of type int to variable \"s\" with specified type String.",
            "29: Too few arguments for \"takes()\" call. Expected at least 1 but received 0.",
            "30: Too many arguments for \"takes()\" call. Expected at most 2 but received 3.",
            "31: Cannot pass a value of type \"String\" as \"int\".",
            "31: Invalid argument for \"takes()\" function: argument 1 should be \"int\" but is \"String\".",
            "32: Cannot get return value of call to \"nothing()\" because it returns \"void\".",
            "34: Cannot assign a value of type \"main.gd.Other\" as \"main.gd.MyEnum\".",
            "34: Value of type \"main.gd.Other\" cannot be assigned to a variable of type \"main.gd.MyEnum\".",
        ], Errors(Script));
    }

    /// <summary>A class extending by path a file the project lacks is the one error Godot gives, its body unread; an inner
    /// class's, with its siblings' names, where an inner class's is.</summary>
    private void A_base_the_project_lacks_is_the_scripts_one_error_as_godot_reads_no_body_past_it()
    {
        static bool Lacks(string path) => path != "res://main.gd";
        Assert.Equal(["1: Could not resolve super class path \"res://nowhere.gd\"."],
            Errors("extends \"res://nowhere.gd\"\n\n\nfunc _ready():\n\tprint(undefined)\n", lacks: Lacks));
        Assert.Equal(["4: Could not resolve super class path \"res://gone.gd\".", "8: Class \"Vector3\" hides a built-in type."],
            Errors("extends Node\n\n\nclass Broken:\n\textends \"res://gone.gd\"\n\n\nclass Vector3:\n\tpass\n\n\nfunc _ready():\n\tprint(undefined)\n", lacks: Lacks));
    }

    /// <summary>A class extending a script this reading has not read may have any member: nothing it names is said to be missing.</summary>
    private void A_class_extending_a_script_not_read_says_nothing_of_names_it_does_not_find()
    {
        Assert.Empty(Errors("extends \"res://elsewhere.gd\"\n\n\nfunc _ready() -> void:\n\tprint(from_elsewhere)\n\tmissing()\n\tvar a: Elsewhere\n"));
        Assert.Equal(["5: Identifier \"nowhere\" not declared in the current scope."],
            Errors("extends \"res://base.gd\"\n\n\nfunc _ready() -> void:\n\tprint(nowhere, from_base)\n",
                p => p == "res://base.gd" ? GDScriptTree.Parse("extends Node\n\nvar from_base := 1\n") : null));
    }

    /// <summary>With no engine's API (no Godot found to dump it), a base no script, autoload or scope gives may be an engine
    /// class: none is said missing, and the body's errors that need no engine stand.</summary>
    private void With_no_engines_api_a_base_no_script_gives_is_never_said_missing_and_the_bodys_own_errors_stand()
    {
        const string Script = "extends Node\n\n\nfunc count() -> int:\n\tpass\n";
        GDScriptTree tree = GDScriptTree.Parse(Script);
        GDScriptProject project = new(null, p => p == "res://main.gd" ? tree : null, new Dictionary<string, string>(), new Dictionary<string, string>());
        GDScriptAnalysis analysis = new GDScriptAnalyzer(project).Analyze(tree, "res://main.gd");
        TextSnapshot snapshot = TextSnapshot.From(Script);
        Assert.Equal(["4: Not all code paths return a value."],
            analysis.Findings(GDScriptWarningSettings.Every).Where(f => f.Code is null).Select(f => $"{snapshot.GetLineFromOffset(f.Start) + 1}: {f.Message}"));
    }
}

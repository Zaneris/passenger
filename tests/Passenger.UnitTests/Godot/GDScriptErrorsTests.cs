using System.Text;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's analyzer's errors as Passenger's analyzer gives them, in Godot's words and on Godot's lines (each script
/// checked against Godot 4.7.2's own server, which says the same, a twice-pushed error once), over a small API of the engine's: a name declared nowhere (with Godot 4's rename hint for a Godot 3 name), a built-in type or global
/// enum standing on its own, a member a known base lacks, an engine's enum used as a value, a call of a function not found
/// on self, a built-in value or type, an engine class or an enum, a type not found, a value of a type its target takes
/// none of and a call given too few or too many arguments, an operator, cast or type test given types it refuses, a static
/// place reaching the instance, an index, loop or typed collection Godot refuses, a member's name taken, an override's
/// signature, an export, a file the project lacks preloaded or extended, a class named as a singleton autoload, an untyped
/// override's returns; and none of them where the class extends one this reading cannot read.</summary>
public class GDScriptErrorsTests
{
    private const string Api = """
        {
          "global_enums": [{ "name": "Side", "values": [{ "name": "SIDE_LEFT", "value": 0 }, { "name": "SIDE_TOP", "value": 1 }] }],
          "global_constants": [],
          "utility_functions": [{ "name": "print", "return_type": "void", "is_vararg": true, "arguments": [] }],
          "singletons": [],
          "builtin_classes": [
            { "name": "Nil" }, { "name": "bool", "operators": [{ "name": "==", "right_type": "bool", "return_type": "bool" }, { "name": "not", "return_type": "bool" }] },
            { "name": "int", "operators": [{ "name": "+", "right_type": "int", "return_type": "int" }, { "name": "==", "right_type": "Variant", "return_type": "bool" },
              { "name": "/", "right_type": "int", "return_type": "int" }, { "name": "<<", "right_type": "int", "return_type": "int" }, { "name": "unary-", "return_type": "int" }] },
            { "name": "float" }, { "name": "String", "operators": [{ "name": "+", "right_type": "String", "return_type": "String" }] }, { "name": "StringName" },
            { "name": "Vector3", "members": [{ "name": "x", "type": "float" }, { "name": "y", "type": "float" }, { "name": "z", "type": "float" }],
              "constants": [{ "name": "ZERO", "type": "Vector3" }], "enums": [{ "name": "Axis", "values": [{ "name": "AXIS_X", "value": 0 }] }],
              "methods": [{ "name": "length", "return_type": "float", "is_const": true }], "constructors": [{ "index": 0 }] },
            { "name": "Dictionary", "methods": [{ "name": "clear", "return_type": "void", "is_const": false }, { "name": "size", "return_type": "int", "is_const": true }] },
            { "name": "Array", "methods": [] }, { "name": "Callable", "methods": [{ "name": "call", "return_type": "Variant", "is_vararg": true, "is_const": true }] }, { "name": "Signal", "methods": [] }
          ],
          "classes": [
            { "name": "Object", "methods": [{ "name": "get", "return_value": { "type": "Variant" } }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "_ready", "is_virtual": true }, { "name": "get_name", "return_value": { "type": "StringName" } },
              { "name": "_process", "is_virtual": true, "arguments": [{ "name": "delta", "type": "float" }] }],
              "properties": [{ "name": "name", "type": "StringName" }], "signals": [{ "name": "ready" }],
              "enums": [{ "name": "ProcessMode", "values": [{ "name": "PROCESS_MODE_INHERIT", "value": 0 }] }] },
            { "name": "CharacterBody2D", "inherits": "Node" }, { "name": "Resource", "inherits": "RefCounted" }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(Encoding.UTF8.GetBytes(Api));

    /// <summary>A script's errors as Godot's test runner prints them: its line, then its words; read against the project's
    /// singleton autoloads and, where given, which paths its files lack.</summary>
    private static List<string> Errors(string text, Func<string, GDScriptTree?>? others = null, IReadOnlyDictionary<string, string>? autoloads = null,
        Func<string, bool>? lacks = null)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.Empty(tree.Errors);
        GDScriptProject project = new(Engine, p => p == "res://main.gd" ? tree : others?.Invoke(p), new Dictionary<string, string>(), autoloads ?? new Dictionary<string, string>(),
            lacks: lacks);
        GDScriptAnalysis analysis = new GDScriptAnalyzer(project).Analyze(tree, "res://main.gd");
        TextSnapshot snapshot = TextSnapshot.From(text);
        return [.. analysis.Findings(GDScriptWarningSettings.Every).Where(f => f.Code is null).Select(f => $"{snapshot.GetLineFromOffset(f.Start) + 1}: {f.Message}")];
    }

    [Fact]
    public void A_name_or_member_godot_would_not_find_is_an_error_in_its_words_on_its_line()
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

    [Fact]
    public void A_call_of_a_function_godot_would_not_find_is_an_error_in_its_words_on_its_line()
    {
        const string Script = "extends Node\n\nenum Mode { A, B }\nvar speed := 1.0\nvar react := func(): pass\n\n\nfunc _ready() -> void:\n"
            + "\tmissing()\n\tspeed()\n\treact()\n\tvar v := Vector3()\n\tv.nope()\n\tNode.nope()\n\tMode.clear()\n\tMode.size()\n\tMode.nope()\n"
            + "\tvar local := func(): pass\n\tlocal()\n\tlocal.call()\n\tsuper()\n\tsuper.nope()\n\tprint(v.length(), int(2.5))\n\n\nfunc _process(_delta: float) -> void:\n\tsuper(_delta)\n";
        Assert.Equal(
        [
            "9: Function \"missing()\" not found in base self.",
            "10: Member \"speed\" is not a function.",
            "10: Name \"speed\" called as a function but is a \"float\".",
            "11: Member \"react\" is not a function.",
            "11: Name \"react\" is a Callable. You can call it with \"react.call()\" instead.",
            "13: Function \"nope()\" not found in base Vector3.",
            "13: Cannot find member \"nope\" in base \"Vector3\".",
            "14: Static function \"nope()\" not found in base \"GDScriptNativeClass\".",
            "15: Cannot call non-const Dictionary function \"clear()\" on enum \"Mode\".",
            "17: Enums only have Dictionary built-in methods. Function \"nope()\" does not exist for enum \"Mode\".",
            "17: Static function \"nope()\" not found in base \"main.gd.Mode\".",
            "19: Function \"local()\" not found in base self.",
            "21: Cannot call the parent class' virtual function \"_ready()\" because it hasn't been defined.",
            "22: Function \"nope()\" not found in base Node.",
            "27: Cannot call the parent class' virtual function \"_process()\" because it hasn't been defined.",
        ], Errors(Script));
    }

    [Fact]
    public void A_type_godot_would_not_find_is_an_error_in_its_words_on_its_line()
    {
        const string Script = "extends Node\n\nvar speed := 1.0\nvar a: Missing\nvar b: speed\nvar c: Vector3.Nope\nvar d: Node.Nope\nvar e: ProcessMode\n"
            + "var f: int[int]\nvar g: Array[int, int]\n\n\nfunc _ready() -> void:\n\tvar h: Later\n\tconst Later = Mode\n\tvar i := 1\n\tvar j: i\n\n\nenum Mode { A }\n";
        Assert.Equal(
        [
            "4: Could not find type \"Missing\" in the current scope.",
            "5: \"speed\" is a variable but does not contain a type.",
            "6: Name \"Nope\" is not a nested type of \"Vector3\".",
            "7: Could not find type \"Nope\" in \"Node\".",
            "9: Only arrays and dictionaries can specify collection element types.",
            "10: Typed arrays require exactly one collection element type.",
            "14: Local constant \"Later\" is not resolved at this point.",
            "17: Local variable \"i\" cannot be used as a type.",
        ], Errors(Script));
    }

    [Fact]
    public void A_value_of_a_type_its_target_takes_none_of_and_a_call_given_too_few_or_many_are_errors_in_godots_words_on_its_lines()
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
            "21: Cannot return value of type \"String\" because the function return type is \"int\".",
            "21: Cannot return a value of type \"String\" as \"int\".",
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

    [Fact]
    public void An_operator_cast_or_type_test_given_types_it_refuses_is_an_error_in_godots_words_on_its_line()
    {
        const string Script = "extends Node\n\nenum Mode { A, B }\nconst LIMIT = 3\nvar count: int = 0\nvar label: String = \"\"\nvar flag: bool = false\nvar node: Node\n\n\n"
            + "func _ready() -> void:\n\tprint(true + true)\n\tprint(count + label)\n\tprint(LIMIT + \"x\")\n\tprint(1 / 0)\n\tprint(-1 << 2)\n\tprint(-label)\n"
            + "\tprint(count == null, count + 1, label + \"x\", Mode.A + 1)\n\tprint(node == 1)\n\tflag += \"bar\"\n\tprint(count as Node)\n\tprint(label as int)\n"
            + "\tprint(1 as Array)\n\tprint(Mode.A as String)\n\tprint(count is String)\n\tprint(node is Resource)\n\tprint(LIMIT is float)\n";
        Assert.Equal(
        [
            "12: Invalid operands to operator +, bool and bool.",
            "13: Invalid operands \"int\" and \"String\" for \"+\" operator.",
            "14: Invalid operands to operator +, int and String.",
            "15: Division by zero error in operator /.",
            "16: Invalid operands for bit shifting. Only positive operands are supported. in operator <<.",
            "17: Invalid operand of type \"String\" for unary operator \"unary-\".",
            "19: Invalid operands \"Node\" and \"int\" for \"==\" operator.",
            "20: Invalid operands \"bool\" and \"String\" for assignment operator.",
            "21: Invalid cast. Cannot convert from \"int\" to \"Node\".",
            "23: Cannot cast a value of type \"int\" as \"Array\".",
            "23: Invalid cast. Cannot convert from \"int\" to \"Array\".",
            "24: Cannot cast a value of type \"main.gd.Mode\" as \"String\".",
            "24: Invalid cast. Cannot convert from \"main.gd.Mode\" to \"String\".",
            "25: Expression is of type \"int\" so it can't be of type \"String\".",
            "26: Expression is of type \"Node\" so it can't be of type \"Resource\".",
            "27: Expression is of type \"int\" so it can't be of type \"float\".",
        ], Errors(Script));
    }

    [Fact]
    public void A_static_place_reaching_the_instance_and_an_index_loop_or_typed_array_refused_are_errors_in_godots_words_on_their_lines()
    {
        const string Script = "extends Node\n\nsignal fired\nvar count: int = 0\nvar items: Array[int] = []\nvar label: String = \"\"\nvar flag: bool = false\n"
            + "static var shared := 1\nstatic var from_func = instance_func()\n\n\nfunc instance_func() -> int:\n\treturn 1\n\n\n"
            + "static func static_func() -> void:\n\tinstance_func()\n\tprint(count, fired, shared, name)\n\tprint($Child)\n\n\n"
            + "func _ready() -> void:\n\tVector3.length()\n\tNode.get_name()\n\tprint(items[label])\n\tprint(flag[0])\n\tfor x in true:\n\t\tpass\n"
            + "\tfor x in range():\n\t\tpass\n\tfor x: String in items:\n\t\tpass\n\tvar typed: Array[int] = [1, \"x\"]\n";
        Assert.Equal(
        [
            "9: Cannot call non-static function \"instance_func()\" from a static variable initializer.",
            "17: Cannot call non-static function \"instance_func()\" from the static function \"static_func()\".",
            "18: Cannot access non-static variable \"count\" from the static function \"static_func()\".",
            "18: Cannot access signal \"fired\" from the static function \"static_func()\".",
            "18: Cannot access non-static variable \"name\" from the static function \"static_func()\".",
            "19: Cannot use shorthand \"get_node()\" notation (\"$\") in a static function.",
            "23: Cannot call non-static function \"length()\" on the class \"Vector3\" directly. Make an instance instead.",
            "24: Cannot call non-static function \"get_name()\" on the class \"Node\" directly. Make an instance instead.",
            "25: Invalid index type \"String\" for a base of type \"Array[int]\".",
            "26: Cannot use subscript operator on a base of type \"bool\".",
            "27: Unable to iterate on value of type \"bool\".",
            "29: Invalid call for \"range()\" function. Expected at least 1 argument, none given.",
            "31: Unable to iterate on value of type \"Array[int]\" with variable of type \"String\".",
            "33: Cannot have an element of type \"String\" in an array of type \"Array[int]\".",
            "33: Cannot include a value of type \"String\" as \"int\".",
        ], Errors(Script));
    }

    [Fact]
    public void A_name_taken_an_override_that_does_not_match_and_an_export_godot_refuses_are_errors_in_its_words_on_their_lines()
    {
        const string Base = "extends Node\n\nsignal changed\nvar health := 10\n\n\nfunc take(amount: int) -> int:\n\treturn amount\n";
        const string Script = "extends \"res://base.gd\"\n\nvar changed := 2\nvar name := \"x\"\nvar script: int\nvar PROCESS_MODE_INHERIT := 2\nvar CharacterBody2D := 1\n"
            + "var Vector3 := 2\n@export var exported_node: Node\n@export var exported_object: Object\n@export var nothing\n@export static var static_exported: int\n\n\n"
            + "func take(amount: int, more: int) -> int:\n\treturn amount + more\n\n\nfunc _ready() -> int:\n\treturn 0\n\n\nfunc _process(delta: int) -> void:\n\tpass\n\n\n"
            + "class RefCounted:\n\tpass\n\n\nclass Holder extends Resource:\n\t@export var node: Node\n";
        Assert.Equal(
        [
            "3: The member \"changed\" already exists in parent class res://base.gd.",
            "4: Member \"name\" redefined (original in native class 'Node')",
            "5: Member \"script\" redefined (original in native class 'Node')",
            "6: Member \"PROCESS_MODE_INHERIT\" redefined (original in native class 'Node')",
            "7: The member \"CharacterBody2D\" shadows a native class.",
            "8: The member \"Vector3\" cannot have the same name as a builtin type.",
            "10: Export type can only be built-in, a resource, a node, or an enum.",
            "11: Cannot use simple \"@export\" annotation with variable without type or initializer, since type can't be inferred.",
            "12: Annotation \"@export\" cannot be applied to a static variable.",
            "15: The function signature doesn't match the parent. Parent signature is \"take(int) -> int\".",
            "19: The function signature doesn't match the parent. Parent signature is \"_ready() -> void\".",
            "23: The function signature doesn't match the parent. Parent signature is \"_process(float) -> void\".",
            "27: Class \"RefCounted\" hides a native class.",
            "32: Node export is only supported in Node-derived classes, but the current class inherits \"Resource\".",
        ], Errors(Script, p => p == "res://base.gd" ? GDScriptTree.Parse(Base) : null));
    }

    /// <summary>An override of no return type written returns what the function it overrides does (void, a type, the
    /// engine's property list an Array), each return checked against it; a constructor's return type, a rest parameter's
    /// type and a return of nothing where an int is asked for are refused as Godot refuses them.</summary>
    [Fact]
    public void An_untyped_override_returns_its_parents_type_and_constructors_and_rest_parameters_are_held_to_godots_rules()
    {
        const string Script = "extends Node\n\n\nclass A:\n\tfunc typed() -> int:\n\t\treturn 1\n\n\tfunc voided() -> void:\n\t\tpass\n\n"
            + "\tfunc variant() -> Variant:\n\t\treturn null\n\n\nclass B extends A:\n\tfunc typed():\n\t\treturn \"s\"\n\n\tfunc voided():\n\t\treturn 1\n\n"
            + "\tfunc variant():\n\t\tpass\n\n\nstatic func _static_init() -> int:\n\tpass\n\n\nfunc _init() -> int:\n\tpass\n\n\nfunc _ready():\n\treturn 5\n\n\n"
            + "func _get_property_list():\n\treturn 0\n\n\nfunc empty() -> int:\n\treturn\n\n\nfunc many(...args: int) -> void:\n\tpass\n\n\n"
            + "func typed_many(...args: Array[int]) -> void:\n\tpass\n";
        Assert.Equal(
        [
            "17: Cannot return value of type \"String\" because the function return type is \"int\".",
            "17: Cannot return a value of type \"String\" as \"int\".",
            "20: A void function cannot return a value.",
            "22: Not all code paths return a value.",
            "26: Static constructor cannot have an explicit return type.",
            "30: Constructor cannot have an explicit return type.",
            "35: A void function cannot return a value.",
            "39: Cannot return value of type \"int\" because the function return type is \"Array\".",
            "39: Cannot return a value of type \"int\" as \"Array\".",
            "43: Cannot return value of type \"null\" because the function return type is \"int\".",
            "46: The rest parameter type must be \"Array\", but \"int\" is specified.",
            "50: Typed arrays are currently not supported for the rest parameter.",
        ], Errors(Script));
    }

    /// <summary>A file the project's files, listed whole, lack is said where a script preloads it, and a class named as a
    /// singleton autoload is said to hide it; with no list, nothing is lacked.</summary>
    [Fact]
    public void A_preloaded_file_the_project_lacks_and_a_class_named_as_a_singleton_are_errors_in_godots_words_on_their_lines()
    {
        const string Script = "class_name Hub\nextends Node\n\nconst Missing = preload(\"res://missing.tscn\")\nconst Present = preload(\"res://present.png\")\n\n\n"
            + "class Game:\n\tpass\n\n\nclass Quiet:\n\tpass\n\n\nfunc _ready() -> void:\n\tprint(Missing, Present, preload(\"gone.tres\"))\n";
        Dictionary<string, string> singletons = new() { ["Hub"] = "res://hub.gd", ["Game"] = "res://game.gd" };
        static bool Lacks(string path) => path is not ("res://main.gd" or "res://present.png" or "res://hub.gd" or "res://game.gd");
        Assert.Equal(
        [
            "1: Class \"Hub\" hides an autoload singleton.",
            "4: Preload file \"res://missing.tscn\" does not exist.",
            "8: Class \"Game\" hides an autoload singleton.",
            "17: Preload file \"res://gone.tres\" does not exist.",
        ], Errors(Script, autoloads: singletons, lacks: Lacks));
        Assert.Equal(["1: Class \"Hub\" hides an autoload singleton.", "8: Class \"Game\" hides an autoload singleton."], Errors(Script, autoloads: singletons));
    }

    /// <summary>A class extending by path a file the project lacks is the one error Godot gives, its body unread; an inner
    /// class's, with its siblings' names, where an inner class's is.</summary>
    [Fact]
    public void A_base_the_project_lacks_is_the_scripts_one_error_as_godot_reads_no_body_past_it()
    {
        static bool Lacks(string path) => path != "res://main.gd";
        Assert.Equal(["1: Could not resolve super class path \"res://nowhere.gd\"."],
            Errors("extends \"res://nowhere.gd\"\n\n\nfunc _ready():\n\tprint(undefined)\n", lacks: Lacks));
        Assert.Equal(["4: Could not resolve super class path \"res://gone.gd\".", "8: Class \"Vector3\" hides a built-in type."],
            Errors("extends Node\n\n\nclass Broken:\n\textends \"res://gone.gd\"\n\n\nclass Vector3:\n\tpass\n\n\nfunc _ready():\n\tprint(undefined)\n", lacks: Lacks));
    }

    /// <summary>A class extending a script this reading has not read may have any member: nothing it names is said to be missing.</summary>
    [Fact]
    public void A_class_extending_a_script_not_read_says_nothing_of_names_it_does_not_find()
    {
        Assert.Empty(Errors("extends \"res://elsewhere.gd\"\n\n\nfunc _ready() -> void:\n\tprint(from_elsewhere)\n\tmissing()\n\tvar a: Elsewhere\n"));
        Assert.Equal(["5: Identifier \"nowhere\" not declared in the current scope."],
            Errors("extends \"res://base.gd\"\n\n\nfunc _ready() -> void:\n\tprint(nowhere, from_base)\n",
                p => p == "res://base.gd" ? GDScriptTree.Parse("extends Node\n\nvar from_base := 1\n") : null));
    }
}

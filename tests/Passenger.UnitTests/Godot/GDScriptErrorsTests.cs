using System.Text;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's analyzer's errors as Passenger's analyzer gives them, in Godot's words and on Godot's lines (each script
/// checked against Godot 4.7.2's own server, which says the same, a twice-pushed error once), over a small API of the engine's: a name declared nowhere (with Godot 4's rename hint for a Godot 3 name), a built-in type or global
/// enum standing on its own, a member a known base lacks, an engine's enum used as a value, a call of a function not found
/// on self, a built-in value or type, an engine class or an enum, a type not found, a value of a type its target takes
/// none of and a call given too few or too many arguments; and none of them where the class extends one this reading
/// cannot read.</summary>
public class GDScriptErrorsTests
{
    private const string Api = """
        {
          "global_enums": [{ "name": "Side", "values": [{ "name": "SIDE_LEFT", "value": 0 }, { "name": "SIDE_TOP", "value": 1 }] }],
          "global_constants": [],
          "utility_functions": [{ "name": "print", "return_type": "void", "is_vararg": true, "arguments": [] }],
          "singletons": [],
          "builtin_classes": [
            { "name": "Nil" }, { "name": "bool" }, { "name": "int" }, { "name": "float" }, { "name": "String" }, { "name": "StringName" },
            { "name": "Vector3", "members": [{ "name": "x", "type": "float" }, { "name": "y", "type": "float" }, { "name": "z", "type": "float" }],
              "constants": [{ "name": "ZERO", "type": "Vector3" }], "enums": [{ "name": "Axis", "values": [{ "name": "AXIS_X", "value": 0 }] }],
              "methods": [{ "name": "length", "return_type": "float", "is_const": true }], "constructors": [{ "index": 0 }] },
            { "name": "Dictionary", "methods": [{ "name": "clear", "return_type": "void", "is_const": false }, { "name": "size", "return_type": "int", "is_const": true }] },
            { "name": "Array", "methods": [] }, { "name": "Callable", "methods": [{ "name": "call", "return_type": "Variant", "is_vararg": true, "is_const": true }] }, { "name": "Signal", "methods": [] }
          ],
          "classes": [
            { "name": "Object", "methods": [{ "name": "get", "return_value": { "type": "Variant" } }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "_ready", "is_virtual": true },
              { "name": "_process", "is_virtual": true, "arguments": [{ "name": "delta", "type": "float" }] }],
              "properties": [{ "name": "name", "type": "StringName" }], "enums": [{ "name": "ProcessMode", "values": [{ "name": "PROCESS_MODE_INHERIT", "value": 0 }] }] },
            { "name": "CharacterBody2D", "inherits": "Node" }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(Encoding.UTF8.GetBytes(Api));

    /// <summary>A script's errors as Godot's test runner prints them: its line, then its words.</summary>
    private static List<string> Errors(string text, Func<string, GDScriptTree?>? others = null)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.Empty(tree.Errors);
        GDScriptProject project = new(Engine, p => p == "res://main.gd" ? tree : others?.Invoke(p), new Dictionary<string, string>(), new Dictionary<string, string>());
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

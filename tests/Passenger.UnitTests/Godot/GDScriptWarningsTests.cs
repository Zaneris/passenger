using System.Text;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's warnings and errors in a GDScript as Passenger's analyzer gives them: on Godot's own test scripts
/// (modules/gdscript/tests/scripts/analyzer/warnings and parser/warnings) the lines and words its expected outputs say, every
/// warning on as Godot's test runner sets them; a project's settings, a <c>@warning_ignore</c> and a region of them followed;
/// and each fix's edits.</summary>
public class GDScriptWarningsTests
{
    private const string Api = """
        {
          "global_enums": [], "global_constants": [],
          "utility_functions": [{ "name": "print", "return_type": "void", "is_vararg": true, "arguments": [] }, { "name": "randf", "return_type": "float", "arguments": [] }],
          "singletons": [],
          "builtin_classes": [
            { "name": "Nil", "operators": [] }, { "name": "bool", "operators": [] },
            { "name": "int", "operators": [{ "name": "/", "right_type": "int", "return_type": "int" }, { "name": "/", "right_type": "float", "return_type": "float" },
              { "name": "+", "right_type": "int", "return_type": "int" }] },
            { "name": "float", "operators": [{ "name": "/", "right_type": "int", "return_type": "float" }, { "name": "/", "right_type": "float", "return_type": "float" }] },
            { "name": "String", "operators": [], "methods": [{ "name": "num_uint64", "return_type": "String", "is_static": true,
              "arguments": [{ "name": "number", "type": "int" }, { "name": "base", "type": "int", "default_value": "10" }, { "name": "capitalize_hex", "type": "bool", "default_value": "false" }] }] }, { "name": "Array", "methods": [] },
            { "name": "Callable", "methods": [{ "name": "call", "return_type": "Variant", "is_vararg": true, "is_const": true }], "constructors": [{ "index": 0 }, { "index": 1, "arguments": [{ "name": "from", "type": "Callable" }] },
              { "index": 2, "arguments": [{ "name": "object", "type": "Object" }, { "name": "method", "type": "StringName" }] }] },
            { "name": "Signal", "methods": [{ "name": "emit", "return_type": "void" }] },
            { "name": "StringName", "methods": [] },
            { "name": "Vector2", "members": [{ "name": "x", "type": "float" }] }, { "name": "Vector2i", "members": [{ "name": "x", "type": "int" }] },
            { "name": "Dictionary", "methods": [] },
            { "name": "PackedVector2Array", "indexing_return_type": "Vector2", "methods": [{ "name": "size", "return_type": "int", "is_const": true },
              { "name": "clear", "return_type": "void", "is_const": false }] }
          ],
          "classes": [
            { "name": "Object", "methods": [{ "name": "emit_signal", "is_vararg": true, "return_value": { "type": "enum::Error" }, "arguments": [{ "name": "signal", "type": "StringName" }] },
              { "name": "connect", "return_value": { "type": "enum::Error" }, "arguments": [{ "name": "signal", "type": "StringName" }, { "name": "callable", "type": "Callable" }, { "name": "flags", "type": "int", "default_value": "0" }] },
              { "name": "disconnect", "arguments": [{ "name": "signal", "type": "StringName" }, { "name": "callable", "type": "Callable" }] }, { "name": "get", "return_value": { "type": "Variant" } }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "get_node", "return_value": { "type": "Node" } }, { "name": "_ready", "is_virtual": true }],
              "properties": [{ "name": "name", "type": "StringName" }] },
            { "name": "Node2D", "inherits": "Node" }, { "name": "Line2D", "inherits": "Node2D", "properties": [{ "name": "points", "type": "PackedVector2Array" }] }, { "name": "Resource", "inherits": "RefCounted" }, { "name": "Script", "inherits": "Resource" },
            { "name": "GDScript", "inherits": "Script" }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(Encoding.UTF8.GetBytes(Api));

    /// <summary>The same API as Godot 4.8's dump, which says its version in its header.</summary>
    private static readonly GodotEngineApi Engine48 = GodotEngineApi.Read(Encoding.UTF8.GetBytes("{ \"header\": { \"version_major\": 4, \"version_minor\": 8 }," + Api.TrimStart()[1..]));

    /// <summary>Every warning on, but the two of declarations' types unless the script asks for them, as Godot's test runner.</summary>
    private static GDScriptWarningSettings AsTested(string text) => GDScriptWarningSettings.Every with
    {
        Levels = GDScriptWarnings.Names.ToDictionary(n => n, n => n is "UNTYPED_DECLARATION" or "INFERRED_DECLARATION" && !text.Contains($"# enable {n}", StringComparison.Ordinal)
            ? GDScriptWarnLevel.Ignore : GDScriptWarnLevel.Warn, StringComparer.Ordinal),
    };

    private static (GDScriptAnalysis Analysis, IReadOnlyList<GDScriptFinding> Found) Read(string text, GDScriptWarningSettings? settings = null, string path = "res://main.gd",
        GodotEngineApi? engine = null, Func<string, GDScriptTree?>? others = null)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        Dictionary<string, string> classNames = tree.Class.Identifier is { } named ? new() { [named.Name] = path } : [];
        GDScriptProject project = new(engine ?? Engine, p => p == path ? tree : others?.Invoke(p), classNames, new Dictionary<string, string>());
        GDScriptAnalysis analysis = new GDScriptAnalyzer(project).Analyze(tree, path);
        return (analysis, analysis.Findings(settings ?? AsTested(text)));
    }

    private static IEnumerable<string> Lines(string text, IReadOnlyList<GDScriptFinding> found)
    {
        TextSnapshot snapshot = TextSnapshot.From(text);
        return found.Select(f => $"{snapshot.GetLineFromOffset(f.Start) + 1} {f.Code ?? "ERROR"}: {f.Message}");
    }

    [Theory]
    [InlineData("func test():\n\tvar unused = \"not used\"\n\n\tvar _unused = \"not used, but no warning since the variable name starts with an underscore\"\n",
        "2 UNUSED_VARIABLE: The local variable \"unused\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_unused\".")]
    [InlineData("func test():\n\tconst UNUSED = \"not used\"\n\n\tconst _UNUSED = \"not used, but no warning since the constant name starts with an underscore\"\n",
        "2 UNUSED_LOCAL_CONSTANT: The local constant \"UNUSED\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_UNUSED\".")]
    [InlineData("# This should emit a warning since the unused argument is not prefixed with an underscore.\nfunc function_with_unused_argument(p_arg1, p_arg2):\n\tprint(p_arg1)\n\n\n"
        + "# This shouldn't emit a warning since the unused argument is prefixed with an underscore.\nfunc function_with_ignored_unused_argument(p_arg1, _p_arg2):\n\tprint(p_arg1)\n\n\nfunc test():\n\tpass\n",
        "2 UNUSED_PARAMETER: The parameter \"p_arg2\" is never used in the function \"function_with_unused_argument()\". If this is intended, prefix it with an underscore: \"_p_arg2\".")]
    [InlineData("func coroutine() -> void:\n\t@warning_ignore(\"redundant_await\")\n\tawait 0\n\nfunc test():\n\tawait coroutine()\n\tcoroutine()\n",
        "7 MISSING_AWAIT: \"await\" keyword might be desired because the expression is a coroutine.")]
    [InlineData("func test():\n\t# This should emit a warning.\n\tvar __ = 5 / 2\n\n\t# These should not emit warnings.\n\t__ = float(5) / 2\n\t__ = 5 / float(2)\n\t__ = 5.0 / 2\n\t__ = 5 / 2.0\n\t__ = 5.0 / 2.0\n",
        "3 INTEGER_DIVISION: Integer division. Decimal part will be discarded.")]
    [InlineData("func test():\n\t1 if true else 2\n\tprint(1) if true else print(2)\n",
        "2 STANDALONE_TERNARY: Standalone ternary operator (the return value is being discarded).",
        "3 STANDALONE_TERNARY: Standalone ternary operator (the return value is being discarded).")]
    [InlineData("# enable UNTYPED_DECLARATION\n\nfunc test() -> void:\n    pass\n\nfunc regular_function(_arg) -> void:\n    pass\n\nfunc vararg_function(..._var_args) -> void:\n    pass\n",
        "6 UNTYPED_DECLARATION: Parameter \"_arg\" has no static type.", "9 UNTYPED_DECLARATION: Parameter \"_var_args\" has no static type.")]
    [InlineData("# Doesn't produce the warning:\nsignal used_as_first_class_signal()\nsignal used_with_signal_constructor()\nsignal used_with_signal_emit()\nsignal used_with_object_emit_signal()\n"
        + "signal used_with_object_connect()\nsignal used_with_object_disconnect()\nsignal used_with_self_prefix()\n\n# Produce the warning:\nsignal used_with_dynamic_name()\nsignal just_unused()\n"
        + "@warning_ignore(\"unused_signal\")\nsignal unused_but_ignored()\n\nfunc no_exec():\n\tprint(used_as_first_class_signal)\n\tprint(Signal(self, \"used_with_signal_constructor\"))\n"
        + "\tused_with_signal_emit.emit()\n\tprint(emit_signal(\"used_with_object_emit_signal\"))\n\tprint(connect(\"used_with_object_connect\", Callable()))\n"
        + "\tdisconnect(\"used_with_object_disconnect\", Callable())\n\tprint(self.emit_signal(\"used_with_self_prefix\"))\n\n\tvar dynamic_name := \"used_with_dynamic_name\"\n"
        + "\tprint(emit_signal(dynamic_name))\n\nfunc test():\n\tpass\n",
        "11 UNUSED_SIGNAL: The signal \"used_with_dynamic_name\" is declared but never explicitly used in the class.",
        "12 UNUSED_SIGNAL: The signal \"just_unused\" is declared but never explicitly used in the class.")]
    [InlineData("# We don't want to execute it because of errors, just analyze.\nfunc no_exec_test():\n\tvar weak_int = 1\n\tprint(weak_int as Variant) # No warning.\n"
        + "\tprint(weak_int as int)\n\tprint(weak_int as Node)\n\n\tvar weak_node = Node.new()\n\tprint(weak_node as Variant) # No warning.\n\tprint(weak_node as int)\n"
        + "\tprint(weak_node as Node)\n\n\tvar weak_variant = null\n\tprint(weak_variant as Variant) # No warning.\n\tprint(weak_variant as int)\n\tprint(weak_variant as Node)\n\n"
        + "\tvar hard_variant: Variant = null\n\tprint(hard_variant as Variant) # No warning.\n\tprint(hard_variant as int)\n\tprint(hard_variant as Node)\n\nfunc test():\n\tpass\n",
        "5 UNSAFE_CAST: Casting \"Variant\" to \"int\" is unsafe.", "6 UNSAFE_CAST: Casting \"Variant\" to \"Node\" is unsafe.",
        "10 UNSAFE_CAST: Casting \"Variant\" to \"int\" is unsafe.", "11 UNSAFE_CAST: Casting \"Variant\" to \"Node\" is unsafe.",
        "15 UNSAFE_CAST: Casting \"Variant\" to \"int\" is unsafe.", "16 UNSAFE_CAST: Casting \"Variant\" to \"Node\" is unsafe.",
        "20 UNSAFE_CAST: Casting \"Variant\" to \"int\" is unsafe.", "21 UNSAFE_CAST: Casting \"Variant\" to \"Node\" is unsafe.")]
    // analyzer/warnings/unsafe_call_argument.gd to its line 43, its built-in constructors' calls in the row after.
    [InlineData("func variant_func(x: Variant) -> void:\n\tprint(x)\n\nfunc int_func(x: int) -> void:\n\tprint(x)\n\nfunc float_func(x: float) -> void:\n\tprint(x)\n\n"
        + "func node_func(x: Node) -> void:\n\tprint(x)\n\n# We don't want to execute it because of errors, just analyze.\nfunc no_exec_test():\n\tvar variant: Variant = null\n"
        + "\tvar untyped_int = 42\n\tvar untyped_string = \"abc\"\n\tvar variant_int: Variant = 42\n\tvar variant_string: Variant = \"abc\"\n\tvar typed_int: int = 42\n\n"
        + "\tvariant_func(untyped_int) # No warning.\n\tvariant_func(untyped_string) # No warning.\n\tvariant_func(variant_int) # No warning.\n\tvariant_func(variant_string) # No warning.\n"
        + "\tvariant_func(typed_int) # No warning.\n\n\tint_func(untyped_int)\n\tint_func(untyped_string)\n\tint_func(variant_int)\n\tint_func(variant_string)\n"
        + "\tint_func(typed_int) # No warning.\n\n\tfloat_func(untyped_int)\n\tfloat_func(untyped_string)\n\tfloat_func(variant_int)\n\tfloat_func(variant_string)\n"
        + "\tfloat_func(typed_int) # No warning.\n\n\tnode_func(variant)\n\tnode_func(Object.new())\n\tnode_func(Node.new()) # No warning.\n\tnode_func(Node2D.new()) # No warning.\n\nfunc test():\n\tpass\n",
        "28 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.",
        "29 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.",
        "30 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.",
        "31 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.",
        "34 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.",
        "35 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.",
        "36 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.",
        "37 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.",
        "40 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"node_func()\" requires the subtype \"Node\" but the supertype \"Variant\" was provided.",
        "41 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"node_func()\" requires the subtype \"Node\" but the supertype \"Object\" was provided.")]
    [InlineData("func test():\n\tvar variant: Variant = null\n\tprint(Callable(self, \"test\")) # No warning.\n\tprint(Callable(variant, \"test\"))\n\n"
        + "\tprint(Dictionary(variant))\n\tprint(Vector2(variant))\n\tprint(int(variant))\n\tvar typed: int = 1\n\tprint(Vector2(typed, typed)) # No warning.\n",
        "4 UNSAFE_CALL_ARGUMENT: The argument 1 of the constructor \"Callable()\" requires the subtype \"Object\" but the supertype \"Variant\" was provided.",
        "6 UNSAFE_CALL_ARGUMENT: The argument 1 of the constructor \"Dictionary()\" requires the subtype \"Dictionary\" but the supertype \"Variant\" was provided.",
        "7 UNSAFE_CALL_ARGUMENT: The argument 1 of the constructor \"Vector2()\" requires the subtype \"Vector2\" or \"Vector2i\" but the supertype \"Variant\" was provided.",
        "8 UNSAFE_CALL_ARGUMENT: The argument 1 of the constructor \"int()\" requires the subtype \"int\", \"bool\", or \"float\" but the supertype \"Variant\" was provided.")]
    // analyzer/features/allow_void_function_to_return_void.gd's UNSAFE_VOID_RETURN, auto_inferred_type_dont_error.gd's
    // UNSAFE_METHOD_ACCESS on a parameter of no type, and warning_ignore_targets.gd's getter returning a float as an int.
    [InlineData("func side_effect(_x):\n\tpass\n\nfunc typed_side_effect(_x) -> void:\n\tpass\n\nfunc return_side_effect() -> void:\n\treturn side_effect(\"x\")\n\n"
        + "func return_typed() -> void:\n\treturn typed_side_effect(\"x\")\n",
        "8 UNSAFE_VOID_RETURN: The method \"return_side_effect()\" returns \"void\" but it's trying to return a call to \"side_effect()\" that can't be ensured to also be \"void\".")]
    [InlineData("func inferred_parameter(param = null):\n\tif param == null:\n\t\tparam = Node.new()\n\tparam.name = \"Ok\"\n\tprint(param.name)\n\tparam.free()\n\n"
        + "func typed(param: Node):\n\tparam.free()\n",
        "6 UNSAFE_METHOD_ACCESS: The method \"free()\" is not present on the inferred type \"Variant\" (but may be present on a subtype).")]
    [InlineData("@warning_ignore(\"narrowing_conversion\")\nvar i: int = f:\n\tget:\n\t\treturn f\n\nvar f: float\n\nfunc whole() -> int:\n\treturn f\n",
        "4 NARROWING_CONVERSION: Narrowing conversion (float is converted to int and loses precision).",
        "9 NARROWING_CONVERSION: Narrowing conversion (float is converted to int and loses precision).")]
    // UNSAFE_METHOD_ACCESS and UNSAFE_PROPERTY_ACCESS as Godot's analyzer pushes them (reduce_call, reduce_subscript), on an engine class's value.
    [InlineData("func test():\n\tvar node: Node = null\n\tnode.unsafe_method_access()\n\tprint(node.unsafe_property_access)\n\tprint(node.name)\n"
        + "\t@warning_ignore(\"unsafe_method_access\")\n\tnode.ignored()\n",
        "3 UNSAFE_METHOD_ACCESS: The method \"unsafe_method_access()\" is not present on the inferred type \"Node\" (but may be present on a subtype).",
        "4 UNSAFE_PROPERTY_ACCESS: The property \"unsafe_property_access\" is not present on the inferred type \"Node\" (but may be present on a subtype).")]
    // analyzer/warnings/confusable_local_declaration.gd, confusable_local_usage.gd, its _initializer.gd and _loop.gd.
    [InlineData("func test():\n\tif true:\n\t\tvar a = 1\n\t\tprint(a)\n\tvar a = 2\n\tprint(a)\n",
        "3 CONFUSABLE_LOCAL_DECLARATION: The variable \"a\" is declared below in the parent block.")]
    [InlineData("var a = 1\n\nfunc test():\n\tprint(a)\n\tvar a = 2\n\tprint(a)\n",
        "4 CONFUSABLE_LOCAL_USAGE: The identifier \"a\" will be shadowed below in the block.",
        "5 SHADOWED_VARIABLE: The local variable \"a\" is shadowing an already-declared variable at line 1 in the current class.")]
    [InlineData("var a = 1\n\nfunc test():\n\tprint(a)\n\tvar a = a + 1\n\tprint(a)\n",
        "4 CONFUSABLE_LOCAL_USAGE: The identifier \"a\" will be shadowed below in the block.",
        "5 SHADOWED_VARIABLE: The local variable \"a\" is shadowing an already-declared variable at line 1 in the current class.",
        "5 CONFUSABLE_LOCAL_USAGE: The identifier \"a\" will be shadowed below in the block.")]
    [InlineData("var a = 1\n\nfunc test():\n\tfor _i in 3:\n\t\tprint(a)\n\t\tvar a = 2\n\t\tprint(a)\n",
        "5 CONFUSABLE_LOCAL_USAGE: The identifier \"a\" will be shadowed below in the block.",
        "6 SHADOWED_VARIABLE: The local variable \"a\" is shadowing an already-declared variable at line 1 in the current class.")]
    // Object's free, which no dump of the API lists, and a script's constant read through a constant typed GDScript (runtime/features/metatypes.gd).
    [InlineData("class MyClass:\n\tconst TEST = 10\n\nfunc test():\n\tvar node: Node = Node.new()\n\tnode.free()\n\tconst ClassNoMeta: GDScript = MyClass\n\tvar b := ClassNoMeta.TEST\n\tprint(b)\n")]
    // analyzer/warnings/confusable_capture_reassignment.gd.
    [InlineData("var member := 1\n\nfunc test():\n\tvar number := 1\n\tvar string := \"1\"\n\tvar vector := Vector2i(1, 0)\n\tvar array_assign := [1]\n"
        + "\tvar array_index := [1]\n\tvar dictionary := { x = 0 }\n\n\tvar lambda := func ():\n\t\tmember = 2\n\t\tnumber = 2\n\t\tstring += \"2\"\n\t\tvector.x = 2\n"
        + "\t\tarray_assign = [2]\n\t\tarray_index[0] = 2\n\t\tdictionary.x = 2\n\n\t\tprint(member, number, string, vector, array_assign, array_index, dictionary)\n\n"
        + "\tlambda.call()\n\tprint(member, number, string, vector, array_assign, array_index, dictionary)\n",
        "13 CONFUSABLE_CAPTURE_REASSIGNMENT: Reassigning lambda capture does not modify the outer local variable \"number\".",
        "14 CONFUSABLE_CAPTURE_REASSIGNMENT: Reassigning lambda capture does not modify the outer local variable \"string\".",
        "15 CONFUSABLE_CAPTURE_REASSIGNMENT: Reassigning lambda capture does not modify the outer local variable \"vector\".",
        "16 CONFUSABLE_CAPTURE_REASSIGNMENT: Reassigning lambda capture does not modify the outer local variable \"array_assign\".")]
    // analyzer/warnings/confusable_temporary_modification.gd, its native property's half and the custom property's.
    [InlineData("class TestNativeProperty extends Line2D:\n\tfunc _ready() -> void:\n\t\tpoints = PackedVector2Array([Vector2()])\n\t\tpoints[0] = Vector2()\n"
        + "\t\tpoints[0].x = 2.0\n\t\tvar _size := points.size()\n\t\tpoints.clear()\n\t\tvar _clear_callable := points.clear\n\t\tvar base: TestNativeProperty = self\n"
        + "\t\tbase.points[0] = Vector2()\n\t\tbase.points.clear()\n\nclass TestCustomProperty extends Node:\n\tvar points: PackedVector2Array\n\n\tfunc _ready() -> void:\n"
        + "\t\tpoints[0] = Vector2()\n\t\tpoints.clear()\n\t\tvar base: TestCustomProperty = self\n\t\tbase.points.clear()\n\nfunc test():\n\tpass\n",
        "4 CONFUSABLE_TEMPORARY_MODIFICATION: The built-in property \"points\" will not be modified in the assignment chain. Consider using a temporary variable and assigning it back instead, or check the \"Line2D\" class for a specialized API.",
        "5 CONFUSABLE_TEMPORARY_MODIFICATION: The built-in property \"points\" will not be modified in the assignment chain. Consider using a temporary variable and assigning it back instead, or check the \"Line2D\" class for a specialized API.",
        "7 CONFUSABLE_TEMPORARY_MODIFICATION: The built-in property \"points\" will not be modified as a result of calling the method \"clear()\". Consider assigning the desired value to the property instead, or check the \"Line2D\" class for a specialized API.",
        "8 CONFUSABLE_TEMPORARY_MODIFICATION: The built-in property \"points\" will not be modified as a result of calling the method \"clear()\". Consider assigning the desired value to the property instead, or check the \"Line2D\" class for a specialized API.",
        "10 CONFUSABLE_TEMPORARY_MODIFICATION: The built-in property \"points\" will not be modified in the assignment chain. Consider using a temporary variable and assigning it back instead, or check the \"Line2D\" class for a specialized API.",
        "11 CONFUSABLE_TEMPORARY_MODIFICATION: The built-in property \"points\" will not be modified as a result of calling the method \"clear()\". Consider assigning the desired value to the property instead, or check the \"Line2D\" class for a specialized API.")]
    // analyzer/warnings/cast_enum_bad_enum.gd and cast_enum_bad_int.gd, and none where an enum's value is more than a number.
    [InlineData("enum MyEnum { ENUM_VALUE_1, ENUM_VALUE_2 }\nenum MyOtherEnum { OTHER_ENUM_VALUE_1, OTHER_ENUM_VALUE_2, OTHER_ENUM_VALUE_3 }\n\nfunc test():\n"
        + "\tprint(MyOtherEnum.OTHER_ENUM_VALUE_3 as MyEnum)\n\tprint(2 as MyEnum)\n\tprint(1 as MyEnum)\n",
        "5 INT_AS_ENUM_WITHOUT_MATCH: Cannot cast 2 as Enum \"main.gd.MyEnum\": no enum member has matching value.",
        "6 INT_AS_ENUM_WITHOUT_MATCH: Cannot cast 2 as Enum \"main.gd.MyEnum\": no enum member has matching value.")]
    [InlineData("const BASE = 4\nenum Flags { A = BASE, B }\n\nfunc test():\n\tprint(5 as Flags)\n")]
    // parser/warnings/static_called_on_instance.gd, return_value_discarded.gd, narrowing_conversion.gd and enum_assign_int_without_casting.gd.
    [InlineData("class_name TestStaticCalledOnInstance\n\nclass Inner:\n\tstatic func static_func():\n\t\tpass\n\nstatic func static_func():\n\tpass\n\nfunc test():\n"
        + "\tprint(String.num_uint64(8589934592))\n\tvar some_string := String()\n\tprint(some_string.num_uint64(8589934592))\n\n\tTestStaticCalledOnInstance.static_func()\n"
        + "\tstatic_func()\n\tself.static_func()\n\tvar other := TestStaticCalledOnInstance.new()\n\tother.static_func()\n\n\tInner.static_func()\n\tvar inner := Inner.new()\n\tinner.static_func()\n",
        "13 STATIC_CALLED_ON_INSTANCE: The function \"num_uint64()\" is a static function but was called from an instance. Instead, it should be directly called from the type: \"String.num_uint64()\".",
        "19 STATIC_CALLED_ON_INSTANCE: The function \"static_func()\" is a static function but was called from an instance. Instead, it should be directly called from the type: \"TestStaticCalledOnInstance.static_func()\".",
        "23 STATIC_CALLED_ON_INSTANCE: The function \"static_func()\" is a static function but was called from an instance. Instead, it should be directly called from the type: \"Inner.static_func()\".")]
    [InlineData("func i_return_int() -> int:\n\treturn 4\n\n\nfunc test():\n\ti_return_int()\n",
        "6 RETURN_VALUE_DISCARDED: The function \"i_return_int()\" returns a value that will be discarded if not used.")]
    [InlineData("func i_accept_ints_only(_i: int):\n\tpass\n\nfunc test():\n\ti_accept_ints_only(12.345)\n",
        "5 NARROWING_CONVERSION: Narrowing conversion (float is converted to int and loses precision).")]
    [InlineData("enum MyEnum { ENUM_VALUE_1, ENUM_VALUE_2 }\n\nvar class_var: MyEnum = 0\n\nfunc test():\n\tprint(class_var)\n\tclass_var = 1\n\tprint(class_var)\n"
        + "\tvar local_var: MyEnum = 0\n\tprint(local_var)\n\tlocal_var = 1\n\tprint(local_var)\n\tlocal_var = MyEnum.ENUM_VALUE_2\n\tprint(local_var)\n",
        "3 INT_AS_ENUM_WITHOUT_CAST: Integer used when an enum value is expected. If this is intended, cast the integer to the enum type using the \"as\" keyword.",
        "7 INT_AS_ENUM_WITHOUT_CAST: Integer used when an enum value is expected. If this is intended, cast the integer to the enum type using the \"as\" keyword.",
        "9 INT_AS_ENUM_WITHOUT_CAST: Integer used when an enum value is expected. If this is intended, cast the integer to the enum type using the \"as\" keyword.",
        "11 INT_AS_ENUM_WITHOUT_CAST: Integer used when an enum value is expected. If this is intended, cast the integer to the enum type using the \"as\" keyword.")]
    // parser/warnings/standalone_expression.gd's strings a class holds outside its members, an inner class's too; none for what extends or an annotation takes.
    [InlineData("\"\"\"\nNot a comment.\n\"\"\"\n\n\"Nor this.\"\n\nextends Node\n\nclass Inner extends \"res://main.gd\":\n\t\"Inside.\"\n\tvar x := 1\n\n"
        + "@warning_ignore(\"unused_parameter\")\nfunc test(a):\n\tpass\n\n'Still not one...'\n",
        "1 STANDALONE_EXPRESSION: Standalone expression (the line may have no effect).",
        "5 STANDALONE_EXPRESSION: Standalone expression (the line may have no effect).",
        "10 STANDALONE_EXPRESSION: Standalone expression (the line may have no effect).",
        "17 STANDALONE_EXPRESSION: Standalone expression (the line may have no effect).")]
    // A script that does not parse gives its errors alone, as Godot's analyzer never runs on it.
    [InlineData("func test():\n\tvar unused = 1\n\tvar = 2\n", "3 ERROR: Expected variable name after \"var\".")]
    [InlineData("func test():\n\tprint(\"warn\")\n\nfunc get(_property: StringName) -> Variant:\n\treturn null\n",
        "4 NATIVE_METHOD_OVERRIDE: The method \"get()\" overrides a method from native class \"Object\". This won't be called by the engine and may not work as expected.")]
    public void Godots_own_test_scripts_give_the_warnings_their_expected_output_says(string script, params string[] expected)
    {
        (_, IReadOnlyList<GDScriptFinding> found) = Read(script);
        Assert.Equal(expected, Lines(script, found));
    }

    /// <summary>analyzer/warnings/non_tool_extends_tool.gd and its _ignored.gd: a class extending a tool script by path, by
    /// class_name or by an autoload's name, no tool itself; none for a tool script or one ignoring it.</summary>
    [Fact]
    public void A_class_extending_a_tool_script_without_being_one_is_missing_tool_however_it_names_it()
    {
        GDScriptTree tool = GDScriptTree.Parse("@tool\nextends Node\n");
        Func<string, GDScriptTree?> others = p => p == "res://base.gd" ? tool : null;
        string script = "extends \"./base.gd\"\n\nclass InnerClass extends \"./base.gd\":\n\tpass\n\nfunc test():\n\tpass\n";
        string message = "The base class script has the \"@tool\" annotation, but this script does not have it.";
        Assert.Equal([$"1 MISSING_TOOL: {message}", $"3 MISSING_TOOL: {message}"], Lines(script, Read(script, others: others).Found));
        string ignored = "@warning_ignore(\"missing_tool\")\nextends \"./base.gd\"\n\n@warning_ignore(\"missing_tool\")\nclass InnerClass extends \"./base.gd\":\n\tpass\n";
        Assert.Empty(Read(ignored, others: others).Found);
        Assert.Empty(Read("@tool\n" + script, others: others).Found);
    }

    [Fact]
    public void A_virtual_overridden_or_a_method_a_base_script_declares_is_no_native_override_and_onready_casts_are_marked_from_Godot_4_8()
    {
        string script = "extends Node\n\nfunc _ready():\n\tpass\n\nfunc _notification(_what):\n\tpass\n\nfunc get(_property: StringName) -> Variant:\n\treturn null\n";
        Assert.Equal(["9 NATIVE_METHOD_OVERRIDE: The method \"get()\" overrides a method from native class \"Object\". This won't be called by the engine and may not work as expected."],
            Lines(script, Read(script).Found));
        GDScriptTree based = GDScriptTree.Parse("extends Node\n\nfunc get(_property: StringName) -> Variant:\n\treturn 1\n");
        string derived = "extends \"res://base.gd\"\n\nfunc get(_property: StringName) -> Variant:\n\treturn null\n";
        Assert.Empty(Read(derived, others: p => p == "res://base.gd" ? based : null).Found);
        string casts = "extends Node\n\n@onready var untyped_cast = $Node as Node2D\n@onready var inferred_cast := $Node as Node2D\n@onready var explicit_cast: Node2D = $Node as Node2D\n\n"
            + "@onready var untyped_direct = $Node\n@onready var inferred_direct := $Node\n@onready var explicit_direct: Node2D = $Node\n@onready var unique := %Unique as Node2D\n";
        string message = "\"as\" will silently return \"null\" if the type of \"{0}\" is wrong. Prefer assigning the node directly to an explicitly typed variable to get an error in such cases.";
        Assert.Equal(new[] { 3, 4, 5 }.Select(l => $"{l} ONREADY_WITH_CAST: {string.Format(System.Globalization.CultureInfo.InvariantCulture, message, "$Node")}")
            .Append($"10 ONREADY_WITH_CAST: {string.Format(System.Globalization.CultureInfo.InvariantCulture, message, "%%Unique")}"), Lines(casts, Read(casts, engine: Engine48).Found));
        Assert.Empty(Read(casts).Found);
    }

    [Fact]
    public void Code_after_a_return_a_function_that_does_not_always_return_and_an_await_of_nothing_are_marked_as_Godot_marks_them()
    {
        string script = "func f() -> int:\n\tif randf() > 0.5:\n\t\treturn 1\n\nfunc g() -> int:\n\tif true:\n\t\treturn 1\n\telse:\n\t\treturn 2\n\tprint(\"never\")\n\n"
            + "func h():\n\tawait 1\n\tvar x\n\tprint(x)\n\tx += 1\n";
        (_, IReadOnlyList<GDScriptFinding> found) = Read(script);
        Assert.Equal(["1 ERROR: Not all code paths return a value.", "10 UNREACHABLE_CODE: Unreachable code (statement after return) in function \"g()\".",
            "13 REDUNDANT_AWAIT: \"await\" keyword is unnecessary because the expression isn't a coroutine nor a signal.",
            "15 UNASSIGNED_VARIABLE: The variable \"x\" is used before being assigned a value.",
            "16 UNASSIGNED_VARIABLE_OP_ASSIGN: The variable \"x\" is modified with the compound-assignment operator \"+=\" but was not previously initialized."], Lines(script, found));
    }

    [Fact]
    public void A_projects_settings_and_the_scripts_annotations_decide_which_warnings_are_given_and_how()
    {
        string script = "func test():\n\tvar unused = 1\n\t@warning_ignore(\"unused_variable\")\n\tvar ignored = 2\n\t@warning_ignore_start(\"integer_division\")\n\tprint(5 / 2)\n"
            + "\t@warning_ignore_restore(\"integer_division\")\n\tprint(7 / 2)\n";
        (GDScriptAnalysis analysis, IReadOnlyList<GDScriptFinding> found) = Read(script, GDScriptWarningSettings.Default);
        Assert.Equal(["2 UNUSED_VARIABLE", "8 INTEGER_DIVISION"], Lines(script, found).Select(l => l[..l.IndexOf(':')]));
        GDScriptWarningSettings strict = GDScriptWarningSettings.Of("[debug]\n\ngdscript/warnings/unused_variable=2\ngdscript/warnings/integer_division=0\n");
        IReadOnlyList<GDScriptFinding> errors = analysis.Findings(strict);
        Assert.Equal(["2 UNUSED_VARIABLE: The local variable \"unused\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_unused\". (Warning treated as error.)"],
            Lines(script, errors));
        Assert.True(errors[0].IsError, "A warning the project makes an error is said as a warning");
        Assert.Empty(analysis.Findings(GDScriptWarningSettings.Of("[debug]\n\ngdscript/warnings/enable=false\n")));
        Assert.True(GDScriptWarningSettings.Default.Silences("res://addons/gut/test.gd"), "An addon's script gives warnings by default, as Godot's never does");
        Assert.False(GDScriptWarningSettings.Of("[debug]\n\ngdscript/warnings/exclude_addons=false\n").Silences("res://addons/gut/test.gd"), "exclude_addons=false still silences addons");
        GDScriptWarningSettings rules = GDScriptWarningSettings.Of("[debug]\n\ngdscript/warnings/directory_rules={\n\"res://addons\": 1,\n\"res://addons/mine\": 0\n}\n");
        Assert.Equal((true, false), (rules.Silences("res://addons/gut/test.gd"), rules.Silences("res://addons/mine/tool.gd")));
    }

    [Fact]
    public void Each_fix_makes_its_edits_as_one()
    {
        string script = "func test(unused_param):\n\tvar total = 5\n\ttotal = 6\n\tvar inferred := 2.5\n\tprint(inferred)\n\tcoroutine()\n\nfunc coroutine() -> int:\n\tawait test(1)\n\tprint(\"a\")\n";
        GDScriptWarningSettings settings = GDScriptWarningSettings.Every;
        (GDScriptAnalysis analysis, IReadOnlyList<GDScriptFinding> found) = Read(script, settings);
        string Fixed(string code, string about, string? title = null)
        {
            GDScriptFinding finding = found.First(f => (f.Code == code || f.Code is null && f.Message.Contains(code, StringComparison.Ordinal)) && f.Message.Contains(about, StringComparison.Ordinal));
            GDScriptFix fix = Assert.Single(GDScriptFixes.Of(analysis, finding));
            if (title is not null)
            {
                Assert.Equal(title, fix.Title);
            }
            return TextSnapshot.From(script).Apply(fix.Edits).ToString();
        }
        Assert.Contains("\tvar _total = 5\n\t_total = 6\n", Fixed("UNUSED_VARIABLE", "total", "Prefix 'total' with an underscore"), StringComparison.Ordinal);
        Assert.StartsWith("func test(_unused_param):", Fixed("UNUSED_PARAMETER", "unused_param"), StringComparison.Ordinal);
        Assert.Contains("\tvar inferred: float = 2.5\n", Fixed("INFERRED_DECLARATION", "inferred", "Add type annotation ': float'"), StringComparison.Ordinal);
        Assert.Contains("\tvar total: int = 5\n", Fixed("UNTYPED_DECLARATION", "Variable \"total\""), StringComparison.Ordinal);
        Assert.Contains("\tawait coroutine()\n", Fixed("MISSING_AWAIT", "await", "Add 'await'"), StringComparison.Ordinal);
        Assert.EndsWith("\tprint(\"a\")\n\treturn 0\n", Fixed("Not all code paths", "return", "Add 'return 0'"), StringComparison.Ordinal);
    }
}

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
          "utility_functions": [{ "name": "print", "return_type": "void", "is_vararg": true, "arguments": [] }, { "name": "randf", "return_type": "float", "arguments": [] },
            { "name": "prints", "return_type": "void", "is_vararg": true, "arguments": [] }],
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
              { "name": "disconnect", "arguments": [{ "name": "signal", "type": "StringName" }, { "name": "callable", "type": "Callable" }] }, { "name": "get", "return_value": { "type": "Variant" },
                "arguments": [{ "name": "property", "type": "StringName" }] }] },
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

    /// <summary>Godot's own test scripts give the warnings their expected output says.</summary>
    [Fact]
    public void Godots_own_test_scripts_give_their_warnings() => Cases.All(
        (nameof(Godots_own_test_scripts_give_the_warnings_their_expected_output_says),
            Godots_own_test_scripts_give_the_warnings_their_expected_output_says));

    /// <summary>Warnings are marked where Godot marks them, as the settings and annotations say, and each fix is one
    /// edit.</summary>
    [Fact]
    public void Warnings_are_marked_and_fixed_as_godot_marks_them() => Cases.All(
        (nameof(A_projects_settings_and_the_scripts_annotations_decide_which_warnings_are_given_and_how),
            A_projects_settings_and_the_scripts_annotations_decide_which_warnings_are_given_and_how),
        (nameof(Each_fix_makes_its_edits_as_one), Each_fix_makes_its_edits_as_one));

    private void Godots_own_test_scripts_give_the_warnings_their_expected_output_says() => Cases.Each<(string Script, string[] Expected)>(
    [
        ("func test():\n\tvar unused = \"not used\"\n\n\tvar _unused = \"not used, but no warning since the variable name starts with an underscore\"\n", ["2 UNUSED_VARIABLE: The local variable \"unused\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_unused\"."]),
        ("func coroutine() -> void:\n\t@warning_ignore(\"redundant_await\")\n\tawait 0\n\nfunc test():\n\tawait coroutine()\n\tcoroutine()\n", ["7 MISSING_AWAIT: \"await\" keyword might be desired because the expression is a coroutine."]),
        ("func test():\n\t1 if true else 2\n\tprint(1) if true else print(2)\n", ["2 STANDALONE_TERNARY: Standalone ternary operator (the return value is being discarded).", "3 STANDALONE_TERNARY: Standalone ternary operator (the return value is being discarded)."]),
        // analyzer/warnings/unsafe_call_argument.gd to its line 43, its built-in constructors' calls in the row after.
        ("func variant_func(x: Variant) -> void:\n\tprint(x)\n\nfunc int_func(x: int) -> void:\n\tprint(x)\n\nfunc float_func(x: float) -> void:\n\tprint(x)\n\n"
                + "func node_func(x: Node) -> void:\n\tprint(x)\n\n# We don't want to execute it because of errors, just analyze.\nfunc no_exec_test():\n\tvar variant: Variant = null\n"
                + "\tvar untyped_int = 42\n\tvar untyped_string = \"abc\"\n\tvar variant_int: Variant = 42\n\tvar variant_string: Variant = \"abc\"\n\tvar typed_int: int = 42\n\n"
                + "\tvariant_func(untyped_int) # No warning.\n\tvariant_func(untyped_string) # No warning.\n\tvariant_func(variant_int) # No warning.\n\tvariant_func(variant_string) # No warning.\n"
                + "\tvariant_func(typed_int) # No warning.\n\n\tint_func(untyped_int)\n\tint_func(untyped_string)\n\tint_func(variant_int)\n\tint_func(variant_string)\n"
                + "\tint_func(typed_int) # No warning.\n\n\tfloat_func(untyped_int)\n\tfloat_func(untyped_string)\n\tfloat_func(variant_int)\n\tfloat_func(variant_string)\n"
                + "\tfloat_func(typed_int) # No warning.\n\n\tnode_func(variant)\n\tnode_func(Object.new())\n\tnode_func(Node.new()) # No warning.\n\tnode_func(Node2D.new()) # No warning.\n\nfunc test():\n\tpass\n", ["28 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.", "29 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.", "30 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.", "31 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"int_func()\" requires the subtype \"int\" but the supertype \"Variant\" was provided.", "34 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.", "35 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.", "36 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.", "37 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"float_func()\" requires the subtype \"float\" but the supertype \"Variant\" was provided.", "40 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"node_func()\" requires the subtype \"Node\" but the supertype \"Variant\" was provided.", "41 UNSAFE_CALL_ARGUMENT: The argument 1 of the function \"node_func()\" requires the subtype \"Node\" but the supertype \"Object\" was provided."]),
        // UNSAFE_METHOD_ACCESS and UNSAFE_PROPERTY_ACCESS as Godot's analyzer pushes them (reduce_call, reduce_subscript), on an engine class's value.
        ("func test():\n\tvar node: Node = null\n\tnode.unsafe_method_access()\n\tprint(node.unsafe_property_access)\n\tprint(node.name)\n"
                + "\t@warning_ignore(\"unsafe_method_access\")\n\tnode.ignored()\n", ["3 UNSAFE_METHOD_ACCESS: The method \"unsafe_method_access()\" is not present on the inferred type \"Node\" (but may be present on a subtype).", "4 UNSAFE_PROPERTY_ACCESS: The property \"unsafe_property_access\" is not present on the inferred type \"Node\" (but may be present on a subtype)."]),
        ("var a = 1\n\nfunc test():\n\tprint(a)\n\tvar a = 2\n\tprint(a)\n", ["4 CONFUSABLE_LOCAL_USAGE: The identifier \"a\" will be shadowed below in the block.", "5 SHADOWED_VARIABLE: The local variable \"a\" is shadowing an already-declared variable at line 1 in the current class."]),
        // analyzer/warnings/cast_enum_bad_enum.gd and cast_enum_bad_int.gd, and none where an enum's value is more than a number.
        ("enum MyEnum { ENUM_VALUE_1, ENUM_VALUE_2 }\nenum MyOtherEnum { OTHER_ENUM_VALUE_1, OTHER_ENUM_VALUE_2, OTHER_ENUM_VALUE_3 }\n\nfunc test():\n"
                + "\tprint(MyOtherEnum.OTHER_ENUM_VALUE_3 as MyEnum)\n\tprint(2 as MyEnum)\n\tprint(1 as MyEnum)\n", ["5 INT_AS_ENUM_WITHOUT_MATCH: Cannot cast 2 as Enum \"main.gd.MyEnum\": no enum member has matching value.", "6 INT_AS_ENUM_WITHOUT_MATCH: Cannot cast 2 as Enum \"main.gd.MyEnum\": no enum member has matching value."]),
        ("func i_return_int() -> int:\n\treturn 4\n\n\nfunc test():\n\ti_return_int()\n", ["6 RETURN_VALUE_DISCARDED: The function \"i_return_int()\" returns a value that will be discarded if not used."]),
        ("func i_accept_ints_only(_i: int):\n\tpass\n\nfunc test():\n\ti_accept_ints_only(12.345)\n", ["5 NARROWING_CONVERSION: Narrowing conversion (float is converted to int and loses precision)."]),
        // parser/warnings/confusable_identifier.gd: a name with a Greek omicron, declared and in a $ path.
        ("extends Node\n\nfunc test():\n\tvar port = 0 # Only latin characters.\n\tvar pοrt = 1 # The \"ο\" is Greek omicron.\n\n\tprints(port, pοrt)\n"
                + "\n# Do not call this since nodes aren't in the tree. It is just a parser check.\nfunc nodes():\n\tvar _node1 = $port # Only latin characters.\n\tvar _node2 = $pοrt # The \"ο\" is Greek omicron.\n", ["5 CONFUSABLE_IDENTIFIER: The identifier \"pοrt\" has misleading characters and might be confused with something else.", "12 CONFUSABLE_IDENTIFIER: The identifier \"pοrt\" has misleading characters and might be confused with something else."]),
    ], row => Godots_own_test_scripts_give_the_warnings_their_expected_output_says(row.Script, row.Expected));

    private void Godots_own_test_scripts_give_the_warnings_their_expected_output_says(string script, params string[] expected)
    {
        (_, IReadOnlyList<GDScriptFinding> found) = Read(script);
        Assert.Equal(expected, Lines(script, found));
    }

    private void A_projects_settings_and_the_scripts_annotations_decide_which_warnings_are_given_and_how()
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

    private void Each_fix_makes_its_edits_as_one()
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

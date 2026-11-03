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
          "utility_functions": [{ "name": "print", "return_type": "void", "arguments": [] }],
          "singletons": [],
          "builtin_classes": [
            { "name": "Nil", "operators": [] }, { "name": "bool", "operators": [] },
            { "name": "int", "operators": [{ "name": "/", "right_type": "int", "return_type": "int" }, { "name": "/", "right_type": "float", "return_type": "float" },
              { "name": "+", "right_type": "int", "return_type": "int" }] },
            { "name": "float", "operators": [{ "name": "/", "right_type": "int", "return_type": "float" }, { "name": "/", "right_type": "float", "return_type": "float" }] },
            { "name": "String", "operators": [] }, { "name": "Array", "methods": [] }, { "name": "Callable", "methods": [] },
            { "name": "Signal", "methods": [{ "name": "emit", "return_type": "void" }] }
          ],
          "classes": [
            { "name": "Object", "methods": [{ "name": "emit_signal", "return_value": { "type": "enum::Error" } }, { "name": "connect", "return_value": { "type": "enum::Error" } },
              { "name": "disconnect" }, { "name": "get", "return_value": { "type": "Variant" } }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "get_node", "return_value": { "type": "Node" } }, { "name": "_ready", "is_virtual": true }],
              "properties": [{ "name": "name", "type": "StringName" }] },
            { "name": "Node2D", "inherits": "Node" }, { "name": "Resource", "inherits": "RefCounted" }, { "name": "Script", "inherits": "Resource" },
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
        GDScriptProject project = new(engine ?? Engine, p => p == path ? tree : others?.Invoke(p), new Dictionary<string, string>(), new Dictionary<string, string>());
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
    // analyzer/warnings/unsafe_call_argument.gd to its line 43, its constructors' calls after it left out.
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
    [InlineData("func test():\n\tprint(\"warn\")\n\nfunc get(_property: StringName) -> Variant:\n\treturn null\n",
        "4 NATIVE_METHOD_OVERRIDE: The method \"get()\" overrides a method from native class \"Object\". This won't be called by the engine and may not work as expected.")]
    public void Godots_own_test_scripts_give_the_warnings_their_expected_output_says(string script, params string[] expected)
    {
        (_, IReadOnlyList<GDScriptFinding> found) = Read(script);
        Assert.Equal(expected, Lines(script, found));
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

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
              { "name": "disconnect" }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "get_node", "return_value": { "type": "Node" } }], "properties": [{ "name": "name", "type": "StringName" }] }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(Encoding.UTF8.GetBytes(Api));

    /// <summary>Every warning on, but the two of declarations' types unless the script asks for them, as Godot's test runner.</summary>
    private static GDScriptWarningSettings AsTested(string text) => GDScriptWarningSettings.Every with
    {
        Levels = GDScriptWarnings.Names.ToDictionary(n => n, n => n is "UNTYPED_DECLARATION" or "INFERRED_DECLARATION" && !text.Contains($"# enable {n}", StringComparison.Ordinal)
            ? GDScriptWarnLevel.Ignore : GDScriptWarnLevel.Warn, StringComparer.Ordinal),
    };

    private static (GDScriptAnalysis Analysis, IReadOnlyList<GDScriptFinding> Found) Read(string text, GDScriptWarningSettings? settings = null, string path = "res://main.gd")
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        GDScriptProject project = new(Engine, p => p == path ? tree : null, new Dictionary<string, string>(), new Dictionary<string, string>());
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
    public void Godots_own_test_scripts_give_the_warnings_their_expected_output_says(string script, params string[] expected)
    {
        (_, IReadOnlyList<GDScriptFinding> found) = Read(script);
        Assert.Equal(expected, Lines(script, found));
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

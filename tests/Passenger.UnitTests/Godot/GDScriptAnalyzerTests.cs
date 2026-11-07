using System.Globalization;
using System.Text;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>A GDScript's types as Passenger's analyzer reads them against a small engine API of Godot's dump's form: each
/// member's as Godot's server writes it in its symbols, each expression's through the engine's classes, its built-in types
/// and operators, the project's other scripts and its autoloads, and the types inferred where none is written.</summary>
public class GDScriptAnalyzerTests
{
    private const string Api = """
        {
          "global_enums": [{ "name": "Error", "is_bitfield": false, "values": [{ "name": "OK", "value": 0 }, { "name": "FAILED", "value": 1 }] }],
          "global_constants": [],
          "utility_functions": [{ "name": "sin", "return_type": "float", "arguments": [{ "name": "angle", "type": "float" }] }],
          "singletons": [{ "name": "Input", "type": "Input" }],
          "builtin_classes": [
            { "name": "Nil", "operators": [] },
            { "name": "bool", "operators": [] },
            { "name": "int", "operators": [{ "name": "*", "right_type": "int", "return_type": "int" }, { "name": "*", "right_type": "float", "return_type": "float" },
              { "name": "+", "right_type": "int", "return_type": "int" }, { "name": "<", "right_type": "int", "return_type": "bool" }] },
            { "name": "float", "operators": [{ "name": "*", "right_type": "float", "return_type": "float" }] },
            { "name": "String", "methods": [{ "name": "length", "return_type": "int" }], "operators": [{ "name": "+", "right_type": "String", "return_type": "String" }] },
            { "name": "Vector2", "members": [{ "name": "x", "type": "float" }, { "name": "y", "type": "float" }], "indexing_return_type": "float",
              "operators": [{ "name": "*", "right_type": "float", "return_type": "Vector2" }], "methods": [{ "name": "length", "return_type": "float" }] },
            { "name": "Array", "indexing_return_type": "Variant", "methods": [{ "name": "size", "return_type": "int" }, { "name": "front", "return_type": "Variant" }] },
            { "name": "Dictionary", "methods": [] },
            { "name": "StringName", "methods": [] },
            { "name": "Callable", "methods": [] },
            { "name": "Signal", "methods": [] }
          ],
          "classes": [
            { "name": "Object", "methods": [{ "name": "get_class", "return_value": { "type": "String" } }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "enums": [{ "name": "ProcessMode", "values": [{ "name": "PROCESS_MODE_INHERIT", "value": 0 }] }],
              "methods": [{ "name": "get_tree", "return_value": { "type": "SceneTree" } }, { "name": "_ready", "is_virtual": true },
                { "name": "print_orphan_nodes", "is_static": true },
                { "name": "get_children", "return_value": { "type": "typedarray::Node" } }, { "name": "get_node", "return_value": { "type": "Node" },
                  "arguments": [{ "name": "path", "type": "NodePath" }] }],
              "properties": [{ "name": "name", "type": "StringName" }, { "name": "process_mode", "type": "enum::Node.ProcessMode" }],
              "signals": [{ "name": "ready" }] },
            { "name": "Node2D", "inherits": "Node", "properties": [{ "name": "position", "type": "Vector2" }] },
            { "name": "CanvasItem", "inherits": "Node", "is_instantiable": false },
            { "name": "SceneTree", "inherits": "Object", "methods": [{ "name": "change_scene_to_file", "return_value": { "type": "enum::Error" } }],
              "properties": [{ "name": "root", "type": "Node" }] },
            { "name": "Input", "inherits": "Object", "methods": [{ "name": "is_action_pressed", "return_value": { "type": "bool" } }] },
            { "name": "Resource", "inherits": "RefCounted" },
            { "name": "PackedScene", "inherits": "Resource" }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(Encoding.UTF8.GetBytes(Api));

    /// <summary>The members of a script as Godot's server writes them, each script of a small project its own.</summary>
    private static (GDScriptAnalysis Analysis, GDScriptAnalyzer Analyzer) Analyze(string main, Dictionary<string, string>? others = null, Dictionary<string, string>? autoloads = null)
    {
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal) { ["res://main.gd"] = GDScriptTree.Parse(main) };
        foreach ((string path, string text) in others ?? [])
        {
            trees[path] = GDScriptTree.Parse(text);
        }
        Dictionary<string, string> classNames = trees.Where(t => t.Value.Class.Identifier is not null).ToDictionary(t => t.Value.Class.Identifier!.Name, t => t.Key, StringComparer.Ordinal);
        GDScriptAnalyzer analyzer = new(new GDScriptProject(Engine, path => trees.GetValueOrDefault(path), classNames, autoloads ?? []));
        return (analyzer.Analyze(trees["res://main.gd"], "res://main.gd"), analyzer);
    }

    /// <summary>Members whose values read one another, however long the chain and however each reads the next, are read to
    /// an end with no error said of them, where a stack that ran out would end the IDE.</summary>
    [Theory]
    [InlineData("var x{0} = x{1}\n", "var x{0} = 1\n")]
    [InlineData("var x{0} := x{1}\n", "var x{0} := 1\n")]
    [InlineData("const X{0} = X{1}\n", "const X{0} = 1\n")]
    [InlineData("var x{0} = x{1} + 1\n", "var x{0} = 1\n")]
    [InlineData("var x{0} = [x{1}]\n", "var x{0} = 1\n")]
    public void A_chain_of_members_thousands_long_comes_to_an_end_with_no_error(string link, string last)
    {
        const int Links = 3_000;
        string script = "extends Node\n\n" + string.Concat(Enumerable.Range(0, Links).Select(i => string.Format(CultureInfo.InvariantCulture, link, i, i + 1)))
            + string.Format(CultureInfo.InvariantCulture, last, Links);
        (GDScriptAnalysis analysis, _) = Analyze(script);
        Assert.Empty(analysis.Findings(GDScriptWarningSettings.Of(null)).Where(f => f.IsError).Select(f => f.Message));
    }

    /// <summary>A valid script nested past what Passenger reads is marked only where reading stopped, with a note: a name
    /// declared after it and a mistake past it say nothing, as nothing can be said of what went unread.</summary>
    [Fact]
    public void A_script_read_in_part_has_a_note_where_reading_stopped_and_nothing_called_wrong()
    {
        string deep = "var deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\n";
        (GDScriptAnalysis analysis, _) = Analyze($"extends Node\n\nvar early: int = later\n{deep}var later := 1\nvar wrong: String = 1 +\n");
        GDScriptFinding only = Assert.Single(analysis.Findings(GDScriptWarningSettings.Of(null)));
        Assert.True(only.IsNote && !only.IsError, $"Reading stopped was said as {only.Code ?? "an error"}: {only.Message}");
        Assert.Equal(analysis.Tree!.Stopped!.Value.Start, only.Start);
    }

    /// <summary>An error the parser met before reading stopped is the script's own and stays, the note after it.</summary>
    [Fact]
    public void A_parse_error_before_reading_stopped_stands_beside_its_note()
    {
        string deep = "var deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\n";
        (GDScriptAnalysis analysis, _) = Analyze($"extends Node\n\nvar = 1\n{deep}");
        Assert.Equal([true, false], analysis.Findings(GDScriptWarningSettings.Of(null)).Select(f => f.IsError));
    }

    [Fact]
    public void A_node_path_is_typed_as_the_scenes_running_the_script_give_its_node_else_as_a_node()
    {
        GDScriptTree main = GDScriptTree.Parse("extends Node\n\nfunc f():\n\tvar a := $Body\n\tvar b := get_node(\"Body\")\n\tvar c := $Gone\n\tvar d := $Mover\n");
        GDScriptTree mover = GDScriptTree.Parse("class_name Mover\nextends Node2D\n");
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal) { ["res://main.gd"] = main, ["res://mover.gd"] = mover };
        GDScriptAnalyzer analyzer = new(new GDScriptProject(Engine, path => trees.GetValueOrDefault(path), new Dictionary<string, string> { ["Mover"] = "res://mover.gd" },
            new Dictionary<string, string>(), nodes: (script, path) => (script, path) switch
            {
                ("res://main.gd", "Body") => new GDScriptNodeAt("Node2D", null),
                ("res://main.gd", "Mover") => new GDScriptNodeAt("Node2D", "res://mover.gd"),
                _ => null,
            }));
        GDScriptAnalysis analysis = analyzer.Analyze(main, "res://main.gd");
        Assert.Equal([": Node2D", ": Node2D", ": Node", ": Mover"], analysis.Inferred().Select(i => $": {i.Type}"));
    }

    private static string[] Details(GDScriptAnalysis analysis) => [.. analysis.Tree!.Members.Select(m => analysis.Detail(m.Node)).OfType<string>()];

    [Fact]
    public void A_scripts_members_are_typed_as_godots_server_writes_them()
    {
        (GDScriptAnalysis analysis, _) = Analyze("extends Node2D\n\nconst LIMIT = 3\nvar speed: float = 2.0\nvar count := 0\nvar loose = 1\n"
            + "@export var label := \"hi\"\nvar where := position\nvar tree := get_tree()\nvar kids := get_children()\n\n"
            + "func _ready():\n\tpass\n\nfunc hit(amount: int, by = 2) -> bool:\n\treturn amount < by\n\nfunc _init():\n\tpass\n\nfunc untyped(a):\n\treturn a\n");
        Assert.Equal(["const LIMIT: int", "var speed: float", "var count: int", "var loose", "@export var label: String", "var where: Vector2",
            "var tree: SceneTree", "var kids: Array[Node]", "func _ready() -> void", "func hit(amount: int, by) -> bool", "func _init() -> res://main.gd",
            "func untyped(a)"], Details(analysis));
    }

    /// <summary>The member types Passenger and Godot's server once told apart: an unnamed enum's value is of Godot's
    /// anonymous enum, a preload of a constant's path is the script it loads, and _static_init says no return.</summary>
    [Fact]
    public void An_unnamed_enums_value_a_preload_of_a_constants_path_and_static_init_are_typed_as_godots_server_types_them()
    {
        (GDScriptAnalysis analysis, _) = Analyze("extends Node\n\nenum { PAST, FUTURE }\nconst OTHER_PATH := \"res://other.gd\"\nconst OTHER := preload(OTHER_PATH)\n"
            + "var type := PAST\n\nstatic func _static_init() -> void:\n\tpass\n", new Dictionary<string, string> { ["res://other.gd"] = "extends Node\n" });
        Assert.Equal(["const OTHER_PATH: String", "const OTHER: res://other.gd", "var type: main.gd.<anonymous enum>", "func _static_init()"],
            Details(analysis).Where(d => !d.StartsWith("enum", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_expressions_type_is_read_through_the_engines_classes_built_in_types_and_operators()
    {
        (GDScriptAnalysis analysis, _) = Analyze("extends Node2D\n\nvar items: Array[int] = []\nvar state := State.IDLE\nenum State { IDLE, RUN }\n\n"
            + "func f():\n\tvar a := 2 * 3\n\tvar b := 2 * 1.5\n\tvar c := position.x\n\tvar d := position * 2.0\n\tvar e := items[0]\n\tvar g := $Child\n"
            + "\tvar h := Input.is_action_pressed(\"jump\")\n\tvar i := get_tree().change_scene_to_file(\"x\")\n\tvar j := \"a\" + \"b\"\n\tvar k := sin(1.0)\n"
            + "\tvar l := Vector2(1, 2).length()\n\tvar m := process_mode\n\tvar n := items.size() < 3\n\tvar o := \"word\".length()\n\tfor each in items:\n\t\tvar p := each\n\t\tpass\n");
        Dictionary<string, string> types = Locals(analysis);
        Assert.Equal("int", types["a"]);
        Assert.Equal("float", types["b"]);
        Assert.Equal("float", types["c"]);
        Assert.Equal("Vector2", types["d"]);
        Assert.Equal("int", types["e"]);
        Assert.Equal("Node", types["g"]);
        Assert.Equal("bool", types["h"]);
        Assert.Equal("Error", types["i"]);
        Assert.Equal("String", types["j"]);
        Assert.Equal("float", types["k"]);
        Assert.Equal("float", types["l"]);
        Assert.Equal("Node.ProcessMode", types["m"]);
        Assert.Equal("bool", types["n"]);
        Assert.Equal("int", types["o"]);
        Assert.Equal("int", types["p"]);
        Assert.Equal("var state: main.gd.State", analysis.Detail(analysis.Tree!.Members[1].Node));
    }

    [Fact]
    public void A_class_takes_its_members_through_extends_by_path_and_class_name_across_the_projects_scripts_and_its_autoloads()
    {
        (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(
            "extends \"res://enemy.gd\"\n\nvar boss := Boss.new()\nvar loot := Game.score\nvar inner := Shape.new()\nvar hp := health\n\nclass Shape:\n\tvar sides := 3\n\n"
            + "func f():\n\tvar s := inner.sides\n\tvar w := boss.weight()\n\tvar t := tree_of()\n\tpass\n",
            new() { ["res://enemy.gd"] = "class_name Enemy\nextends Node\nvar health := 10\nfunc tree_of() -> SceneTree:\n\treturn get_tree()\n",
                ["res://boss.gd"] = "class_name Boss\nextends Enemy\nfunc weight() -> float:\n\treturn 1.0\n",
                ["res://game.gd"] = "extends Node\nvar score := 0\n" },
            new() { ["Game"] = "res://game.gd" });
        Assert.Equal(["var boss: Boss", "var loot: int", "var inner: Shape", "var hp: int"], Details(analysis).Take(4));
        Dictionary<string, string> types = Locals(analysis);
        Assert.Equal(("int", "float", "SceneTree"), (types["s"], types["w"], types["t"]));
        IReadOnlyList<GDScriptMemberInfo> members = analyzer.Members(GDScriptAnalyzer.ScriptClass(analysis.Tree!, "res://main.gd"));
        Assert.Contains(members, m => m.Name == "health" && m.Declaration?.Script == "res://enemy.gd");
        Assert.Contains(members, m => m.Name == "get_tree" && m.Declaration?.EngineClass == "Node");
    }

    [Fact]
    public void The_types_inferred_where_none_is_written_are_given_after_their_names()
    {
        string text = "extends Node\n\nvar count := 0\nvar named: String = \"x\"\n\nfunc f(by = 1.5, times := 2, plain):\n\tvar total := count * times\n\tvar loose = 3\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        Assert.Equal([("count", ": int"), ("by", ": float"), ("times", ": int"), ("total", ": int")],
            analysis.Inferred().Select(i => (Before(text, i.Offset), $": {i.Type}")));

        static string Before(string text, int offset) => text[..offset].Split([' ', '\t', '(', ','])[^1];
    }

    [Fact]
    public void A_name_is_declared_where_its_script_or_another_declares_it()
    {
        string text = "extends Node\n\nvar count := 0\n\nfunc f(by := 1):\n\tvar local := by\n\treturn count + local + helper()\n\nfunc helper() -> int:\n\treturn 1\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        Assert.Equal(text.IndexOf("var count", StringComparison.Ordinal) + 4, DeclaredAt(text.IndexOf("count +", StringComparison.Ordinal)));
        Assert.Equal(text.IndexOf("var local", StringComparison.Ordinal) + 4, DeclaredAt(text.IndexOf("local +", StringComparison.Ordinal)));
        Assert.Equal(text.IndexOf("by := 1", StringComparison.Ordinal), DeclaredAt(text.IndexOf("by\n", StringComparison.Ordinal)));
        Assert.Equal(text.IndexOf("func helper", StringComparison.Ordinal) + 5, DeclaredAt(text.IndexOf("helper()", StringComparison.Ordinal)));
        Assert.Equal(["local", "by"], analysis.LocalsAt(text.IndexOf("return count", StringComparison.Ordinal)).Select(l => l.Name));

        int? DeclaredAt(int offset) => analysis.NodeAt(offset + 1) is ({ } node, _) && node is GDScriptIdentifier name ? analysis.DeclarationOf(name)?.Start : null;
    }

    [Fact]
    public void A_declarations_card_names_what_it_declares_a_class_name_a_class_and_an_enums_value_a_constant()
    {
        string text = "class_name Hero\nextends Node\n\nenum Mood { CALM }\nvar count := 0\n\n\nfunc f() -> void:\n\tpass\n\n\nclass Inner:\n\tenum Kind { ONE }\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        GDScriptTree tree = analysis.Tree!;
        Assert.Equal("class", GDScriptSignature.KindAt(tree, text.IndexOf("Hero", StringComparison.Ordinal), GDScriptType.Native("Node") with { Kind = GDScriptTypeKind.Class, Meta = true }));
        Assert.Equal("const", GDScriptSignature.KindAt(tree, text.IndexOf("CALM", StringComparison.Ordinal), new GDScriptType(GDScriptTypeKind.Enum, "Mood")));
        Assert.Equal("const", GDScriptSignature.KindAt(tree, text.IndexOf("ONE", StringComparison.Ordinal), new GDScriptType(GDScriptTypeKind.Enum, "Kind")));
        Assert.Equal("var", GDScriptSignature.KindAt(tree, text.IndexOf("count", StringComparison.Ordinal), GDScriptType.Builtin("int")));
        Assert.Equal("func", GDScriptSignature.KindAt(tree, text.IndexOf("f()", StringComparison.Ordinal), GDScriptType.Builtin("Callable")));
        Assert.Equal("enum", GDScriptSignature.KindAt(tree, text.IndexOf("Mood", StringComparison.Ordinal), new GDScriptType(GDScriptTypeKind.Enum, "Mood") { Meta = true }));
    }

    [Fact]
    public void A_scripts_own_class_name_and_its_base_by_name_are_declared_where_their_classes_are()
    {
        string text = "class_name Hero\nextends Base\n\n\nfunc f() -> void:\n\tpass\n";
        const string Base = "class_name Base\nextends Node\n";
        (GDScriptAnalysis analysis, _) = Analyze(text, new Dictionary<string, string> { ["res://base.gd"] = Base });
        int hero = text.IndexOf("Hero", StringComparison.Ordinal);
        Assert.True(DeclaredAt(hero) is (var at, "res://main.gd") && at == hero, "Hero, the script's own class_name, is not declared where it stands");
        Assert.True(DeclaredAt(text.IndexOf("Base", StringComparison.Ordinal)) is (var based, "res://base.gd") && based == Base.IndexOf("Base", StringComparison.Ordinal),
            "The script's base by name, Base, is not declared at base.gd's class_name");

        (int, string?)? DeclaredAt(int offset) => analysis.NodeAt(offset + 1) is ({ } node, _) && node is GDScriptIdentifier name && analysis.DeclarationOf(name) is { } declared
            ? (declared.Start, declared.Script) : null;
    }

    [Fact]
    public void An_enums_value_read_off_its_enum_is_declared_where_the_enum_gives_it()
    {
        string text = "extends Node\n\nenum Mood { CALM, ANGRY }\n\n\nfunc f() -> int:\n\treturn Mood.ANGRY + Inner.Kind.TWO\n\n\nclass Inner:\n\tenum Kind { ONE, TWO }\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        Assert.Equal(text.IndexOf("ANGRY }", StringComparison.Ordinal), DeclaredAt(text.IndexOf("ANGRY +", StringComparison.Ordinal)));
        Assert.Equal(text.IndexOf("TWO }", StringComparison.Ordinal), DeclaredAt(text.IndexOf("TWO\n", StringComparison.Ordinal)));

        int? DeclaredAt(int offset) => analysis.NodeAt(offset + 1) is ({ } node, _) && node is GDScriptIdentifier name ? analysis.DeclarationOf(name)?.Start : null;
    }

    [Fact]
    public void A_loop_variables_and_a_pattern_binds_name_is_declared_where_it_stands_as_a_locals_is()
    {
        string text = "extends Node\n\n\nfunc f(items: Array) -> void:\n\tfor item in items:\n\t\tmatch item:\n\t\t\t[var first, ..]:\n\t\t\t\tprint(first, item)\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        foreach (string name in new[] { "item in", "first," })
        {
            int at = text.IndexOf(name, StringComparison.Ordinal);
            Assert.True(DeclaredAt(at) == at, $"{name.Split(' ', ',')[0]} is not declared where it stands, so Go to Declaration on it goes nowhere");
        }

        int? DeclaredAt(int offset) => analysis.NodeAt(offset + 1) is ({ } node, _) && node is GDScriptIdentifier name ? analysis.DeclarationOf(name)?.Start : null;
    }

    [Fact]
    public void A_signals_classs_enums_and_enum_values_name_is_declared_where_it_stands_as_a_variables_is()
    {
        string text = "extends Node\n\nsignal hit(amount: int)\nenum Mood { CALM, ANGRY }\nenum { LOOSE }\n\n\nclass Inner:\n\tsignal inner_hit\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        foreach (string name in new[] { "hit(", "Mood", "CALM", "ANGRY", "LOOSE", "Inner", "inner_hit" })
        {
            int at = text.IndexOf(name, StringComparison.Ordinal);
            Assert.True(DeclaredAt(at) == at, $"{name.TrimEnd('(')} is not declared where it stands, so Go to Declaration on it goes nowhere");
        }

        int? DeclaredAt(int offset) => analysis.NodeAt(offset + 1) is ({ } node, _) && node is GDScriptIdentifier name ? analysis.DeclarationOf(name)?.Start : null;
    }

    /// <summary>Changes one after another join into one span of the text before them, the same edit a whole comparison finds.</summary>
    [Fact]
    public void Changes_one_after_another_join_into_the_one_edit_of_the_text_before_them()
    {
        Random random = new(1196);
        string text = "func f():\n\tvar a := 1\n\tvar b := a * 2\n";
        for (int round = 0; round < 200; round++)
        {
            string now = text;
            GDScriptTextChange? joined = null;
            for (int step = 0; step < 3; step++)
            {
                int at = random.Next(now.Length + 1);
                int length = random.Next(3) == 0 ? Math.Min(random.Next(1, 4), now.Length - at) : 0;
                TextEdit edit = new(at, length, random.Next(2) == 0 ? "x" : string.Empty);
                now = string.Concat(now.AsSpan(0, edit.Start), edit.Text, now.AsSpan(edit.End));
                GDScriptTextChange change = GDScriptTextChange.Of([edit]);
                joined = joined is { } before ? before.Then(change) : change;
            }
            TextEdit covering = joined!.Value.Edit(now);
            Assert.True(joined.Value.Fits(text.Length, now.Length), $"A joined change does not fit the texts in round {round}");
            Assert.Equal(now, string.Concat(text.AsSpan(0, covering.Start), covering.Text, text.AsSpan(covering.End)));
            Assert.Equal(Shape(GDScriptTree.Parse(now)), Shape(GDScriptTree.Parse(text).Reparse(now, [covering])));
        }

        static string Shape(GDScriptTree tree) => string.Join("\n", tree.Members.Select(m => $"{m.Start}-{m.End}").Concat(tree.Errors.Select(e => $"{e.Start} {e.Message}")));
    }

    [Fact]
    public void Each_name_is_coloured_as_what_it_is_where_it_is_declared_and_where_it_is_read()
    {
        string script = "class_name Hero\nextends Node2D\n\nsignal hit(amount)\nenum Mood { CALM, ANGRY }\nconst LIMIT = 3\nvar health := 10\n\n\n"
            + "func heal(by: int) -> void:\n\tvar gained := by * LIMIT\n\thealth += gained\n\thit.emit(gained)\n\tvar mood := Mood.ANGRY\n\tposition.x = sin(1.0)\n\tprint(mood, Hero, get_tree(), Vector2(1, 2))\n";
        (GDScriptAnalysis analysis, _) = Analyze(script);
        Dictionary<string, HashSet<GDScriptColour>> colours = [];
        foreach (GDScriptColoured coloured in analysis.Colours())
        {
            string name = script.Substring(coloured.Start, coloured.Length);
            (colours.TryGetValue(name, out HashSet<GDScriptColour>? kinds) ? kinds : colours[name] = []).Add(coloured.Colour);
        }
        Assert.Equal(
        [
            "ANGRY EnumMember", "CALM EnumMember", "Hero Class", "LIMIT Constant", "Mood Enum", "Node2D Class", "Vector2 Struct", "amount Parameter", "by Parameter", "gained Local",
            "get_tree Method", "heal Method", "health Field", "hit Event", "mood Local", "position Property", "sin Method", "x Property",
        ], colours.Where(c => c.Key is not ("emit" or "print")).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {string.Join("/", c.Value)}"));
    }

    /// <summary>What Godot 4.7.2 accepts read off a class by its name: its static variables and functions, constants, enums,
    /// an unnamed enum's values and inner classes; it rejects a variable, a function or a signal only an instance has. Its
    /// list says the same, a class built on it inheriting the same, and an instance's holds all of them.</summary>
    [Fact]
    public void A_class_read_by_its_name_lists_what_it_has_itself_and_nothing_only_an_instance_has()
    {
        const string Worker = "class_name Worker\nextends Node\n\nsignal hit\nenum Mood { CALM }\nenum { LOOSE }\nconst LIMIT := 3\nstatic var shared := 2\nvar field := 1\n\n"
            + "class Inner:\n\tvar held := 1\n\tstatic func make() -> int:\n\t\treturn 1\n\n"
            + "func work() -> int:\n\treturn 1\n\nstatic func count() -> int:\n\treturn 2\n";
        const string Boss = "class_name Boss\nextends Worker\n\nfunc manage() -> int:\n\treturn 4\n\nstatic func lead() -> int:\n\treturn 5\n";
        string text = "extends Node\n\nfunc f():\n\tprint(Worker.LIMIT, Boss.LIMIT)\n";
        (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text, new() { ["res://worker.gd"] = Worker, ["res://boss.gd"] = Boss });

        HashSet<string> worker = Names(TypeBefore(".LIMIT"));
        Assert.Superset(new HashSet<string> { "count", "shared", "LIMIT", "Mood", "LOOSE", "Inner" }, worker);
        Assert.Empty(worker.Intersect(["field", "work", "hit", "get_tree", "name", "ready", "get_class"]));
        HashSet<string> inner = Names(analyzer.Member(TypeBefore(".LIMIT"), "Inner")!.Type);
        Assert.Contains("make", inner);
        Assert.DoesNotContain("held", inner);
        HashSet<string> boss = Names(TypeBefore(".LIMIT", last: true));
        Assert.Superset(new HashSet<string> { "lead", "count", "LIMIT" }, boss);
        Assert.Empty(boss.Intersect(["manage", "work"]));
        Assert.Superset(new HashSet<string> { "field", "work", "hit", "count", "LIMIT", "get_tree", "name" }, Names(TypeBefore(".LIMIT").Instance));

        HashSet<string> Names(GDScriptType type) => [.. analyzer.Members(type).Select(m => m.Name)];

        GDScriptType TypeBefore(string after, bool last = false)
        {
            int dot = last ? text.LastIndexOf(after, StringComparison.Ordinal) : text.IndexOf(after, StringComparison.Ordinal);
            GDScriptType type = TypeBeforeDot(analysis, dot);
            Assert.True(type.Meta, $"What comes before {after} read as {type}, no class");
            return type;
        }
    }

    /// <summary>What stands before a dot reads as the member it is however many dots came first, as a list opened there needs:
    /// typed on the dot alone, with nothing after it yet, or before a name already written. Godot 4.7.2 compiled each name
    /// listed there, and of a class read by its name rejected those left out.</summary>
    [Theory]
    [InlineData("print(Worker.Inner.|)", "Inner", "make DEEP Deepest", "held work field")]
    [InlineData("print(Worker.Inner.Deepest.|)", "Deepest", "low BOTTOM", "make DEEP held")]
    [InlineData("print(buddy.buddy.|)", "Worker", "field work hit LIMIT count Mood", "held make")]
    [InlineData("print(self.buddy.|)", "Worker", "field work hit LIMIT count", "held make")]
    [InlineData("print(buddy.inner_held.|)", "Inner", "held make DEEP", "field work")]
    [InlineData("print(buddy.buddy.inner_held.|)", "Inner", "held make DEEP", "field work")]
    [InlineData("print(self.spot.|)", "Vector2", "x y length", "field")]
    [InlineData("print(get_tree().root.|)", "Node", "name get_tree ready", "field")]
    [InlineData("print(Worker.Mood.|)", "Mood", "CALM", "LIMIT")]
    public void A_member_read_off_a_member_lists_its_own_members_after_its_dot(string written, string type, string listed, string left)
    {
        const string Worker = "class_name Worker\nextends Node\n\nsignal hit\nenum Mood { CALM }\nconst LIMIT := 3\nvar field := 1\nvar buddy: Worker\nvar inner_held: Inner\n\n"
            + "class Inner:\n\tvar held := 1\n\tconst DEEP := 2\n\tstatic func make() -> int:\n\t\treturn 1\n\tclass Deepest:\n\t\tconst BOTTOM := 3\n\t\tstatic func low() -> int:\n\t\t\treturn 2\n\n"
            + "func work() -> int:\n\treturn 1\n\nstatic func count() -> int:\n\treturn 2\n";
        const string Head = "extends Node2D\n\nvar buddy: Worker\nvar spot: Vector2\n\nfunc f():\n\t";
        foreach (string after in (string[])[string.Empty, "zz"])
        {
            string text = Head + written.Replace("|", after, StringComparison.Ordinal) + "\n";
            (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text, new() { ["res://worker.gd"] = Worker });
            int dot = Head.Length + written.IndexOf('|', StringComparison.Ordinal) - 1;
            GDScriptType owner = TypeBeforeDot(analysis, dot);
            Assert.True(owner.Name == type, $"What stands before the dot of {written} read as {owner}, not {type}: the list there stays empty");
            HashSet<string> names = [.. analyzer.Members(owner).Select(m => m.Name)];
            Assert.Superset(new HashSet<string>(listed.Split(' ')), names);
            Assert.Empty(names.Intersect(left.Split(' ')));
        }
    }

    /// <summary>A dot after a closing bracket reads what the bracket closes, never the last thing written inside it, and a group's
    /// parentheses read as what they hold; with nothing after the dot yet, or a name. Godot 4.7.2 compiled each name listed.</summary>
    [Theory]
    [InlineData("get_node(\"Body\").|", "Node", "name get_tree", "length")]
    [InlineData("buddy.twin(spot).|", "Worker", "field work", "x length")]
    [InlineData("buddy.twin(spot).spot.|", "Vector2", "x length", "field")]
    [InlineData("get_node(\"Body\").get_node(\"Other\").|", "Node", "name get_tree", "length")]
    [InlineData("twin(buddy.twin(spot)).|", "Worker", "field work", "x")]
    [InlineData("arr[index].|", "Node", "name get_tree", "length")]
    [InlineData("arr[0].|", "Node", "name get_tree", "length")]
    [InlineData("get_children()[0].|", "Node", "name get_tree", "length")]
    [InlineData("table[spot].|", "Node", "name get_tree", "length x")]
    [InlineData("Vector2(1, 2).|", "Vector2", "x y length", "name")]
    [InlineData("[1].|", "Array", "size front", "length")]
    [InlineData("(Worker.Inner).|", "Inner", "make", "field held")]
    [InlineData("(buddy).|", "Worker", "field work", "x")]
    [InlineData("(buddy.spot).|", "Vector2", "x length", "field")]
    [InlineData("(buddy.twin(spot)).|", "Worker", "field work", "x")]
    [InlineData("((buddy)).spot.|", "Vector2", "x length", "field")]
    public void A_dot_after_a_closing_bracket_reads_what_the_bracket_closes(string written, string type, string listed, string left)
    {
        const string Worker = "class_name Worker\nextends Node\n\nvar field := 1\nvar spot: Vector2\n\nclass Inner:\n\tvar held := 1\n\tstatic func make() -> int:\n\t\treturn 1\n\n"
            + "func work() -> int:\n\treturn 1\n\nfunc twin(_with) -> Worker:\n\treturn self\n";
        const string Head = "extends Node\n\nvar buddy: Worker\nvar spot: Vector2\nvar arr: Array[Node]\nvar table: Dictionary[Vector2, Node]\nvar index := 0\n\n"
            + "func twin(_with) -> Worker:\n\treturn buddy\n\nfunc f():\n\t";
        foreach (string place in (string[])["print(|)", "|", "var kept = |", "return |", "if |:\n\t\tpass"])
        {
            foreach (string after in (string[])[string.Empty, "zz"])
            {
                string line = place.Replace("|", written.Replace("|", after, StringComparison.Ordinal), StringComparison.Ordinal);
                string text = $"{Head}{line}\n";
                (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text, new() { ["res://worker.gd"] = Worker });
                int dot = Head.Length + place.IndexOf('|', StringComparison.Ordinal) + written.IndexOf('|', StringComparison.Ordinal) - 1;
                GDScriptType owner = TypeBeforeDot(analysis, dot);
                Assert.True(owner.Name == type, $"What stands before the dot of {line} read as {owner}, not {type}");
                HashSet<string> names = [.. analyzer.Members(owner).Select(m => m.Name)];
                Assert.Superset(new HashSet<string>(listed.Split(' ')), names);
                Assert.Empty(names.Intersect(left.Split(' ')));
            }
        }
    }

    /// <summary>What a call is made on reads through the brackets before its dot, its own parenthesis open or closed, as Parameter
    /// Info and the strings a call takes read it; a signal read off a value keeps the name <c>emit</c> is called on.</summary>
    [Theory]
    [InlineData("get_node(\"Body\").get_node(|", "Node")]
    [InlineData("buddy.twin(spot).twin(|)", "Worker")]
    [InlineData("arr[0].get_node(|)", "Node")]
    [InlineData("(buddy).twin(|)", "Worker")]
    [InlineData("buddy.hit.emit(|)", "Signal")]
    public void What_a_call_is_made_on_reads_through_the_brackets_before_its_dot(string written, string type)
    {
        const string Head = "extends Node\n\nvar buddy: Worker\nvar spot: Vector2\nvar arr: Array[Node]\n\nfunc f():\n\t";
        string text = Head + written.Replace("|", string.Empty, StringComparison.Ordinal) + "\n";
        (GDScriptAnalysis analysis, _) = Analyze(text, new() { ["res://worker.gd"] = "class_name Worker\nextends Node\n\nsignal hit\n\nfunc twin(_with) -> Worker:\n\treturn self\n" });
        GDScriptCallAt? call = GDScriptAsked.At(text, Head.Length + written.IndexOf('|', StringComparison.Ordinal)).Call;
        Assert.NotNull(call);
        Assert.Equal(type, TypeBeforeDot(analysis, call.NameStart - 1).Name);
    }

    /// <summary>A function read off a member is a Callable before its dot, and a name a type gives nothing stays no type.</summary>
    [Theory]
    [InlineData("print(self.work.|)", "Callable")]
    [InlineData("print(self.buddy.work.|)", "Callable")]
    [InlineData("print(self.buddy.gone.|)", "Variant")]
    public void What_a_member_read_off_a_member_is_stands_before_its_dot(string written, string type)
    {
        const string Head = "class_name Worker\nextends Node\n\nvar buddy: Worker\n\nfunc work():\n\tpass\n\nfunc f():\n\t";
        string text = Head + written.Replace("|", string.Empty, StringComparison.Ordinal) + "\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        int dot = Head.Length + written.IndexOf('|', StringComparison.Ordinal) - 1;
        Assert.Equal(type, TypeBeforeDot(analysis, dot).Name);
    }

    /// <summary><c>new</c> follows a class read by its name, by any path to it, wherever Godot 4.7.2 compiled that call, and
    /// nowhere it refused one: an abstract class, one built on an abstract engine class, a singleton, an enum, a built-in type.
    /// A value of a class makes nothing, though Godot leaves that to the running game.</summary>
    [Theory]
    [InlineData("Worker", true)]
    [InlineData("Worker.Inner", true)]
    [InlineData("Worker.Inner.Deepest", true)]
    [InlineData("Boss", true)]
    [InlineData("Heir", true)]
    [InlineData("Next", true)]
    [InlineData("Loaded", true)]
    [InlineData("Loaded.Inner", true)]
    [InlineData("kept", true)]
    [InlineData("Node", true)]
    [InlineData("Node2D", true)]
    [InlineData("Shape.Whole", true)]
    [InlineData("Shape", false)]
    [InlineData("Shape.Part", false)]
    [InlineData("CanvasItem", false)]
    [InlineData("Drawn", false)]
    [InlineData("Input", false)]
    [InlineData("Mood", false)]
    [InlineData("Worker.Mood", false)]
    [InlineData("Vector2", false)]
    [InlineData("int", false)]
    [InlineData("self", false)]
    [InlineData("buddy", false)]
    [InlineData("Worker.new()", false)]
    public void New_follows_a_class_by_its_name_wherever_godot_lets_one_be_made(string written, bool listed)
    {
        const string Head = "extends Node\n\nenum Mood { CALM }\nconst Loaded = preload(\"res://worker.gd\")\nvar buddy: Worker\n\nclass Next:\n\tpass\n\nfunc f():\n\tvar kept := Worker\n\t";
        foreach (string after in (string[])[string.Empty, "zz"])
        {
            string text = $"{Head}print({written}.{after})\n";
            (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text, Classes);
            GDScriptType owner = TypeBeforeDot(analysis, Head.Length + "print(".Length + written.Length);
            Assert.True(analyzer.Members(owner).Any(m => m.Name == "new") == listed, $"new {(listed ? "missing from" : "offered on")} {written}, read as {owner}");
        }
    }

    /// <summary>A class's <c>new</c> gives a value of the class, the <c>_init</c> of the nearest class writing one saying what it takes.</summary>
    [Fact]
    public void New_gives_an_instance_and_is_declared_by_the_constructor_its_classes_write()
    {
        string text = "extends Node\n\nfunc f():\n\tprint(Worker.LIMIT, Boss.LIMIT, Heir.LIMIT, Node.LIMIT)\n";
        (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text, Classes);
        GDScriptMemberInfo NewOf(string name) => analyzer.Members(TypeBeforeDot(analysis, text.IndexOf($"{name}.LIMIT", StringComparison.Ordinal) + name.Length)).Single(m => m.Name == "new");

        GDScriptMemberInfo plain = NewOf("Worker");
        Assert.Equal(("func", "Worker", false, null), (plain.Kind, plain.Type.ToString(), plain.Type.Meta, plain.Declaration));
        Assert.Equal(("Node", (GDScriptDeclaration?)null), (NewOf("Node").Type.ToString(), NewOf("Node").Declaration));
        foreach (string name in (string[])["Boss", "Heir"])
        {
            GDScriptDeclaration? written = NewOf(name).Declaration;
            Assert.Equal(("res://boss.gd", Classes["res://boss.gd"].IndexOf("_init", StringComparison.Ordinal)), (written?.Script, written?.Start));
        }
    }

    /// <summary>A small project's classes to make: plain, with inner classes, with a constructor, inheriting one, abstract, and
    /// built on an abstract engine class.</summary>
    private static readonly Dictionary<string, string> Classes = new(StringComparer.Ordinal)
    {
        ["res://worker.gd"] = "class_name Worker\nextends Node\n\nenum Mood { CALM }\nconst LIMIT := 3\n\nclass Inner:\n\tclass Deepest:\n\t\tpass\n",
        ["res://boss.gd"] = "class_name Boss\nextends Worker\n\nfunc _init(rank: int) -> void:\n\tpass\n",
        ["res://heir.gd"] = "class_name Heir\nextends Boss\n",
        ["res://shape.gd"] = "@abstract\nclass_name Shape\nextends RefCounted\n\n@abstract class Part:\n\tpass\n\nclass Whole extends Part:\n\tpass\n",
        ["res://drawn.gd"] = "class_name Drawn\nextends CanvasItem\n",
    };

    /// <summary>The type of what stands before a dot, found as a list opened after it finds it.</summary>
    private static GDScriptType TypeBeforeDot(GDScriptAnalysis analysis, int dot) => analysis.ValueBefore(dot) is ({ } node, _) ? analysis.TypeOf(node) : GDScriptType.Variant;

    /// <summary>The locals in scope where a name is typed (the bar), the nearest first, whether the name is typed yet or, on a line
    /// of its own, the caret only stands at its indentation. Each was compiled by Godot 4.7.2 with every name of its script
    /// typed there: it accepted those listed and rejected the rest.</summary>
    [Theory]
    [InlineData("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\t|\n", "a")]
    [InlineData("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\t\t|\n", "hidden a")]
    [InlineData("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\t|\n\tprint(a)\n", "a")]
    [InlineData("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\telse:\n\t\t|\n", "a")]
    [InlineData("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\telif |:\n\t\tpass\n", "a")]
    [InlineData("func f(a, b):\n\tif a:\n\t\tvar hidden := 1\n\telif b:\n\t\tvar shown := 2\n\t\t|\n", "shown b a")]
    [InlineData("func f(a, b):\n\tif a:\n\t\tpass\n\telif b:\n\t\tvar shown := 2\n\t|\n", "b a")]
    [InlineData("func f(a):\n\twhile a:\n\t\tvar hidden := 1\n\t|\n", "a")]
    [InlineData("func f(a):\n\tvar top := 1\n\tif a:\n\t\tvar mid := 2\n\t\tif a:\n\t\t\tvar low := 3\n\t\t|\n", "mid top a")]
    [InlineData("func f():\n\t|\n\tvar later := 1\n", "")]
    [InlineData("func f():\n\tvar me := |\n", "")]
    [InlineData("func f():\n\tconst ME = |\n", "")]
    [InlineData("func f():\n\tvar me = func(): return |\n", "")]
    [InlineData("func f(a):\n\tif a:\n\t\tconst KEPT = 1\n\t\t|\n", "KEPT a")]
    [InlineData("func f():\n\tfor i in 3:\n\t\t|\n", "i")]
    [InlineData("func f():\n\tfor i in 3:\n\t\tpass\n\t|\n", "")]
    [InlineData("func f():\n\tfor i in |:\n\t\tpass\n", "")]
    [InlineData("func f(x):\n\tmatch x:\n\t\tvar b:\n\t\t\t|\n", "b x")]
    [InlineData("func f(x):\n\tmatch x:\n\t\tvar b when |:\n\t\t\tpass\n", "b x")]
    [InlineData("func f(x):\n\tmatch x:\n\t\tvar b:\n\t\t\tpass\n\t\t2:\n\t\t\t|\n", "x")]
    [InlineData("func f(x):\n\tmatch x:\n\t\tvar b:\n\t\t\tpass\n\t|\n", "x")]
    [InlineData("func f():\n\tvar a := 1\n\tvar g = func(p):\n\t\tvar inner = p\n\t\t|\n\tvar later := 2\n", "inner p a")]
    [InlineData("func f():\n\tvar g = func(p):\n\t\tvar inner = p\n\t|\n", "g")]
    [InlineData("func f():\n\tprint(func(p): return p, |)\n", "")]
    [InlineData("func f(a, b = |):\n\tpass\n", "a")]
    [InlineData("var v: int:\n\tset(value):\n\t\t|\n\tget:\n\t\treturn 1\n", "value")]
    [InlineData("var v: int:\n\tset(value):\n\t\tpass\n\tget:\n\t\treturn |\n", "")]
    [InlineData("func f(a):\n\tvar x = a\n\n\nfunc g(b):\n\t|\n", "b")]
    [InlineData("class In:\n\tfunc f(a):\n\t\tvar x = a\n\tfunc g(b):\n\t\t|\n", "b")]
    [InlineData("func f(a):\n\tif a: var hidden := 1\n\t|\n", "a")]
    [InlineData("func f(a):\n\tpass\n\n\n|\n", "")]
    [InlineData("func f():\n\tvar a := 1; print(|)\n", "a")]

    public void Only_the_locals_whose_scope_holds_the_caret_are_in_scope_there(string written, string expected)
    {
        string[] names = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const string Head = "extends Node\n\n";
        int mark = written.IndexOf('|', StringComparison.Ordinal);
        string typed = Head + written.Replace("|", "zz", StringComparison.Ordinal);
        (GDScriptAnalysis analysis, _) = Analyze(typed);
        Assert.Equal(names, analysis.LocalsAt(Head.Length + mark + 2).Select(l => l.Name));

        int lineStart = written.LastIndexOf('\n', mark) + 1;
        if (written[lineStart..mark].Trim('\t').Length == 0 && written[mark + 1] == '\n')
        {
            (GDScriptAnalysis blank, _) = Analyze(Head + written.Replace("|", string.Empty, StringComparison.Ordinal));
            Assert.Equal(names, blank.LocalsAt(Head.Length + mark).Select(l => l.Name));
        }
    }

    /// <summary>A name the script's own text gives a type carries it to the list: a loop's variable its element's, a pattern's
    /// bind none, a local its value's.</summary>
    [Fact]
    public void A_local_in_scope_says_its_type_and_where_it_is_declared()
    {
        string text = "extends Node\n\nfunc f(items: Array[Node], x):\n\tfor item in items:\n\t\tvar count := 1\n\t\tmatch x:\n\t\t\tvar b:\n\t\t\t\tzz\n";
        (GDScriptAnalysis analysis, _) = Analyze(text);
        IReadOnlyList<GDScriptMemberInfo> locals = analysis.LocalsAt(text.IndexOf("zz", StringComparison.Ordinal) + 2);
        Assert.Equal(["b Variant", "count int", "item Node", "x Variant", "items Array[Node]"], locals.Select(l => $"{l.Name} {l.Type}"));
        Assert.Equal(text.IndexOf("item in", StringComparison.Ordinal), locals.Single(l => l.Name == "item").Declaration!.Start);
        Assert.Equal(text.IndexOf("count :=", StringComparison.Ordinal), locals.Single(l => l.Name == "count").Declaration!.Start);
    }

    /// <summary>A function an edit above it moved, its nodes kept by the tree parsed again, has the same locals in scope.</summary>
    [Fact]
    public void A_function_an_edit_moved_keeps_its_scopes()
    {
        string text = "extends Node\n\nvar a := 1\n\nfunc f(p):\n\tif p:\n\t\tvar hidden = 1\n\t\tyy\n\tzz\n";
        TextEdit edit = new(text.IndexOf('1', StringComparison.Ordinal), 1, "2000");
        string moved = TextSnapshot.From(text).Apply([edit]).ToString();
        GDScriptTree tree = GDScriptTree.Parse(text).Reparse(moved, edit);
        GDScriptAnalysis analysis = new GDScriptAnalyzer(new GDScriptProject(Engine, _ => tree, new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal))).Analyze(tree, "res://main.gd");
        Assert.Equal(["hidden", "p"], analysis.LocalsAt(moved.IndexOf("yy", StringComparison.Ordinal) + 2).Select(l => l.Name));
        Assert.Equal(["p"], analysis.LocalsAt(moved.IndexOf("zz", StringComparison.Ordinal) + 2).Select(l => l.Name));
    }

    /// <summary>Godot 4.7.2 lets an inner class's code name its own members, wherever declared, and the constants, enums and
    /// classes of the classes around it, and rejects their variables, functions and signals, static ones too.</summary>
    [Fact]
    public void An_inner_classs_code_finds_its_own_members_and_what_the_classes_around_it_declare_for_all()
    {
        string text = "extends Node\n\nsignal rung\nenum Mood { CALM }\nconst LIMIT := 3\nstatic var shared := 2\nvar outer_field := 1\n\n"
            + "func outer_work():\n\tpass\n\nstatic func outer_static():\n\tpass\n\nclass Next:\n\tpass\n\n"
            + "class Inner:\n\tvar own := 1\n\n\tclass Deep:\n\t\tconst DEEP := 1\n\t\tfunc deep_work():\n\t\t\tzz\n\n\tfunc work():\n\t\tyy\n\n\tvar later := 2\n";
        (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text);
        HashSet<string> Names(int at) => [.. analyzer.MembersInScope(analysis.Tree!, analysis.Path, analysis.ClassAt(at), analysis.StaticAt(at)).Select(m => m.Name)];

        int work = text.IndexOf("yy", StringComparison.Ordinal);
        Assert.Equal("Inner", analysis.ClassAt(work)?.Identifier?.Name);
        HashSet<string> inner = Names(work);
        Assert.Superset(new HashSet<string> { "own", "later", "work", "Deep", "LIMIT", "Mood", "Next", "Inner" }, inner);
        Assert.Empty(inner.Intersect(["rung", "shared", "outer_field", "outer_work", "outer_static", "name", "get_tree", "DEEP"]));

        int deep = text.IndexOf("zz", StringComparison.Ordinal);
        Assert.Equal("Deep", analysis.ClassAt(deep)?.Identifier?.Name);
        HashSet<string> deepest = Names(deep);
        Assert.Superset(new HashSet<string> { "DEEP", "deep_work", "LIMIT", "Deep", "Inner" }, deepest);
        Assert.Empty(deepest.Intersect(["own", "later", "work", "outer_field"]));

        int outer = text.IndexOf("pass", StringComparison.Ordinal);
        Assert.Null(analysis.ClassAt(outer));
        Assert.Superset(new HashSet<string> { "outer_field", "outer_work", "rung", "shared", "LIMIT", "name", "get_tree" }, Names(outer));
    }

    /// <summary>The class's names a place finds, whether a name is typed there, nothing yet, or, on a line of its own, only its
    /// indentation. Godot 4.7.2 compiled every name of the script at each: in a static place only what needs no instance, and
    /// <c>new()</c>; a member variable's value takes all of them, as Godot's analyzer does, though its completion shows it none.</summary>
    [Theory]
    [InlineData("static func f():\n\t|\n", true)]
    [InlineData("static func f():\n\tprint(|)\n", true)]
    [InlineData("static func f(a):\n\tif a:\n\t\t|\n", true)]
    [InlineData("static func f(a):\n\tif a:\n\t\tpass\n\t|\n", true)]
    [InlineData("static func f():\n\tvar g = func(): print(|)\n", true)]
    [InlineData("static func f():\n\tvar g = func():\n\t\tvar h = func():\n\t\t\t|\n", true)]
    [InlineData("static func f():\n\tvar local = |\n", true)]
    [InlineData("static func f(a = |):\n\tpass\n", true)]
    [InlineData("static func _static_init():\n\t|\n", true)]
    [InlineData("static var v = |\n", true)]
    [InlineData("static var v = func(): print(|)\n", true)]
    [InlineData("static var v: int:\n\tset(value):\n\t\t|\n", true)]
    [InlineData("static var v: int:\n\tget:\n\t\tprint(|)\n\t\treturn 1\n", true)]
    [InlineData("func f():\n\t|\n", false)]
    [InlineData("func f():\n\tvar g = func(): print(|)\n", false)]
    [InlineData("func f(a = |):\n\tpass\n", false)]
    [InlineData("var v = |\n", false)]
    [InlineData("var v = func(): print(|)\n", false)]
    [InlineData("var v: int:\n\tset(value):\n\t\t|\n", false)]
    [InlineData("var v: int:\n\tget:\n\t\tprint(|)\n\t\treturn 1\n", false)]
    [InlineData("static func g():\n\tpass\n\n\nfunc f():\n\t|\n", false)]
    [InlineData("static var w = 1\n\n\nfunc f():\n\t|\n", false)]
    public void A_static_place_finds_only_what_needs_no_instance(string written, bool isStatic)
    {
        const string Head = "extends Node\n\nsignal rung\nenum Mood { CALM }\nenum { LOOSE }\nconst LIMIT := 3\nstatic var shared := 2\nvar field := 1\n\n"
            + "func work():\n\tpass\n\nstatic func tool_fn():\n\tpass\n\nclass Next:\n\tpass\n\n";
        string[] always = ["shared", "tool_fn", "LIMIT", "Mood", "LOOSE", "Next", "print_orphan_nodes"];
        string[] ofAnInstance = ["field", "work", "rung", "name", "get_tree", "ready"];
        int mark = written.IndexOf('|', StringComparison.Ordinal);
        foreach (string typed in (string[])["zz", string.Empty])
        {
            (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(Head + written.Replace("|", typed, StringComparison.Ordinal));
            int caret = Head.Length + mark + typed.Length;
            Assert.True(analysis.StaticAt(caret) == isStatic, $"\"{written}\" with \"{typed}\" typed read as {(isStatic ? "no static place" : "a static place")}");
            HashSet<string> names = [.. analyzer.MembersInScope(analysis.Tree!, analysis.Path, analysis.ClassAt(caret), analysis.StaticAt(caret)).Select(m => m.Name)];
            Assert.Superset(new HashSet<string>(always), names);
            Assert.Equal(isStatic ? [] : ofAnInstance.Order(), names.Intersect(ofAnInstance).Order());
            Assert.Equal(isStatic, names.Contains("new"));
        }
    }

    /// <summary>An inner class's static function finds what its own class has as a class and what the classes around it declare
    /// for all, as Godot 4.7.2 compiled them, and a static function of a class Godot refuses to make has no <c>new</c>.</summary>
    [Fact]
    public void An_inner_classs_static_function_finds_what_needs_no_instance_of_it()
    {
        string text = "extends Node\n\nconst LIMIT := 3\nvar field := 1\nstatic var shared := 2\n\nclass In:\n\tvar own := 1\n\tstatic var own_shared := 2\n\tconst OWN := 3\n\tsignal own_rung\n"
            + "\tfunc own_work():\n\t\tpass\n\tstatic func own_tool():\n\t\tpass\n\tstatic func f():\n\t\tzz\n\tfunc g():\n\t\tyy\n";
        (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(text);
        HashSet<string> Names(string mark)
        {
            int at = text.IndexOf(mark, StringComparison.Ordinal) + mark.Length;
            return [.. analyzer.MembersInScope(analysis.Tree!, analysis.Path, analysis.ClassAt(at), analysis.StaticAt(at)).Select(m => m.Name)];
        }

        HashSet<string> inStatic = Names("zz");
        Assert.Superset(new HashSet<string> { "own_shared", "OWN", "own_tool", "LIMIT", "In", "new" }, inStatic);
        Assert.Empty(inStatic.Intersect(["own", "own_rung", "own_work", "field", "shared"]));
        Assert.Superset(new HashSet<string> { "own", "own_rung", "own_work", "own_shared", "own_tool", "OWN", "LIMIT" }, Names("yy"));

        foreach (string head in (string[])["@abstract\nextends Node\n", "extends CanvasItem\n"])
        {
            string made = $"{head}\nstatic func f():\n\tzz\n";
            (GDScriptAnalysis held, GDScriptAnalyzer holder) = Analyze(made);
            int caret = made.IndexOf("zz", StringComparison.Ordinal) + 2;
            Assert.DoesNotContain("new", holder.MembersInScope(held.Tree!, held.Path, held.ClassAt(caret), held.StaticAt(caret)).Select(m => m.Name));
        }
    }

    private static Dictionary<string, string> Locals(GDScriptAnalysis analysis) =>
        analysis.Tree!.Members.SelectMany(m => analysis.LocalsAt(m.End - 1)).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First().Type.ToString(), StringComparer.Ordinal);
}

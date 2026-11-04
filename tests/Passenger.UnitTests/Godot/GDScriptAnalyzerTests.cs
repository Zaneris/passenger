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
                { "name": "get_children", "return_value": { "type": "typedarray::Node" } }, { "name": "get_node", "return_value": { "type": "Node" },
                  "arguments": [{ "name": "path", "type": "NodePath" }] }],
              "properties": [{ "name": "name", "type": "StringName" }, { "name": "process_mode", "type": "enum::Node.ProcessMode" }],
              "signals": [{ "name": "ready" }] },
            { "name": "Node2D", "inherits": "Node", "properties": [{ "name": "position", "type": "Vector2" }] },
            { "name": "SceneTree", "inherits": "Object", "methods": [{ "name": "change_scene_to_file", "return_value": { "type": "enum::Error" } }] },
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
            + "\tvar l := Vector2(1, 2).length()\n\tvar m := process_mode\n\tvar n := items.size() < 3\n\tvar o := \"word\".length()\n\tfor each in items:\n\t\tvar p := each\n");
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
            + "func f():\n\tvar s := inner.sides\n\tvar w := boss.weight()\n\tvar t := tree_of()\n",
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

    private static Dictionary<string, string> Locals(GDScriptAnalysis analysis) =>
        analysis.Tree!.Members.SelectMany(m => analysis.LocalsAt(m.End - 1)).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First().Type.ToString(), StringComparer.Ordinal);
}

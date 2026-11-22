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
          "utility_functions": [{ "name": "sin", "return_type": "float", "category": "math", "arguments": [{ "name": "angle", "type": "float" }] },
            { "name": "absi", "return_type": "int", "category": "math", "arguments": [{ "name": "x", "type": "int" }] },
            { "name": "abs", "return_type": "Variant", "category": "math", "arguments": [{ "name": "x", "type": "Variant" }] },
            { "name": "randi", "return_type": "int", "category": "random" }, { "name": "str", "return_type": "String", "category": "general" }],
          "singletons": [{ "name": "Input", "type": "Input" }],
          "builtin_classes": [
            { "name": "Nil", "operators": [] },
            { "name": "bool", "operators": [] },
            { "name": "int", "operators": [{ "name": "*", "right_type": "int", "return_type": "int" }, { "name": "*", "right_type": "float", "return_type": "float" },
              { "name": "+", "right_type": "int", "return_type": "int" }, { "name": "<", "right_type": "int", "return_type": "bool" }] },
            { "name": "float", "operators": [{ "name": "*", "right_type": "float", "return_type": "float" }] },
            { "name": "String", "methods": [{ "name": "length", "return_type": "int" }], "operators": [{ "name": "+", "right_type": "String", "return_type": "String" }] },
            { "name": "Vector2", "members": [{ "name": "x", "type": "float" }, { "name": "y", "type": "float" }], "indexing_return_type": "float",
              "constants": [{ "name": "ZERO", "type": "Vector2" }], "enums": [{ "name": "Axis", "values": [{ "name": "AXIS_X", "value": 0 }] }],
              "operators": [{ "name": "*", "right_type": "float", "return_type": "Vector2" }], "methods": [{ "name": "length", "return_type": "float" }] },
            { "name": "Array", "indexing_return_type": "Variant", "methods": [{ "name": "size", "return_type": "int" }, { "name": "front", "return_type": "Variant" }] },
            { "name": "Dictionary", "methods": [{ "name": "keys", "return_type": "Array", "is_const": true }, { "name": "size", "return_type": "int", "is_const": true },
              { "name": "has", "return_type": "bool", "is_const": true, "arguments": [{ "name": "key", "type": "Variant" }] }, { "name": "clear" }] },
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
            { "name": "Input", "inherits": "Object", "methods": [{ "name": "is_action_pressed", "return_value": { "type": "bool" } }],
              "enums": [{ "name": "MouseMode", "values": [{ "name": "MOUSE_MODE_VISIBLE", "value": 0 }] }] },
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

    /// <summary>A script's members and expressions are typed as Godot's server types them, through its bases, scenes
    /// and the engine.</summary>
    [Fact]
    public void Members_and_expressions_are_typed_as_godots_server_types_them() => Cases.All(
        (nameof(A_chain_of_members_thousands_long_comes_to_an_end_with_no_error), A_chain_of_members_thousands_long_comes_to_an_end_with_no_error),
        (nameof(A_classs_base_by_a_relative_path_is_the_script_Godot_loads), A_classs_base_by_a_relative_path_is_the_script_Godot_loads),
        (nameof(A_script_read_in_part_has_a_note_where_reading_stopped_and_nothing_called_wrong),
            A_script_read_in_part_has_a_note_where_reading_stopped_and_nothing_called_wrong),
        (nameof(A_node_path_is_typed_as_the_scenes_running_the_script_give_its_node_else_as_a_node),
            A_node_path_is_typed_as_the_scenes_running_the_script_give_its_node_else_as_a_node),
        (nameof(A_scripts_members_are_typed_as_godots_server_writes_them), A_scripts_members_are_typed_as_godots_server_writes_them),
        (nameof(An_expressions_type_is_read_through_the_engines_classes_built_in_types_and_operators),
            An_expressions_type_is_read_through_the_engines_classes_built_in_types_and_operators),
        (nameof(A_class_takes_its_members_through_extends_by_path_and_class_name_across_the_projects_scripts_and_its_autoloads),
            A_class_takes_its_members_through_extends_by_path_and_class_name_across_the_projects_scripts_and_its_autoloads));

    /// <summary>A name leads to where Godot declares it, its card says what it declares, and it is coloured as what it
    /// is.</summary>
    [Fact]
    public void A_name_is_declared_and_coloured_where_godot_declares_it() => Cases.All(
        (nameof(A_name_is_declared_where_its_script_or_another_declares_it), A_name_is_declared_where_its_script_or_another_declares_it),
        (nameof(Changes_one_after_another_join_into_the_one_edit_of_the_text_before_them),
            Changes_one_after_another_join_into_the_one_edit_of_the_text_before_them),
        (nameof(Each_name_is_coloured_as_what_it_is_where_it_is_declared_and_where_it_is_read),
            Each_name_is_coloured_as_what_it_is_where_it_is_declared_and_where_it_is_read));

    /// <summary>After a dot, completion lists the members of what stands before it, read through brackets and
    /// calls.</summary>
    [Fact]
    public void A_dot_lists_the_members_of_what_stands_before_it() => Cases.All(
        (nameof(A_member_read_off_a_member_lists_its_own_members_after_its_dot), A_member_read_off_a_member_lists_its_own_members_after_its_dot),
        (nameof(A_dot_after_a_closing_bracket_reads_what_the_bracket_closes), A_dot_after_a_closing_bracket_reads_what_the_bracket_closes));

    /// <summary>New follows the classes Godot lets one make, and an enum's values follow what Godot finds them
    /// on.</summary>
    [Fact]
    public void New_and_enums_list_what_godot_finds_on_them() => Cases.All(
        (nameof(New_follows_a_class_by_its_name_wherever_godot_lets_one_be_made), New_follows_a_class_by_its_name_wherever_godot_lets_one_be_made));

    /// <summary>Completion offers the locals whose scope holds the caret, an inner class's code what its classes
    /// declare.</summary>
    [Fact]
    public void Locals_in_scope_are_those_whose_scope_holds_the_caret() => Cases.All(
        (nameof(Only_the_locals_whose_scope_holds_the_caret_are_in_scope_there), Only_the_locals_whose_scope_holds_the_caret_are_in_scope_there));

    /// <summary>A static place, a constant, an enum value and a node path find only what Godot compiles
    /// there.</summary>
    [Fact]
    public void A_static_or_constant_place_finds_only_what_godot_allows_there() => Cases.All(
        (nameof(A_static_place_finds_only_what_needs_no_instance), A_static_place_finds_only_what_needs_no_instance),
        (nameof(A_constants_value_finds_what_godot_folds), A_constants_value_finds_what_godot_folds),
        (nameof(A_node_path_is_asked_for_only_where_godot_compiles_one), A_node_path_is_asked_for_only_where_godot_compiles_one));

    /// <summary>Members whose values read one another, however long the chain and however each reads the next, are read to
    /// an end with no error said of them, where a stack that ran out would end the IDE.</summary>
    private void A_chain_of_members_thousands_long_comes_to_an_end_with_no_error() => Cases.Each<(string Link, string Last)>(
    [
        ("var x{0} = x{1}\n", "var x{0} = 1\n"),
        ("var x{0} := x{1}\n", "var x{0} := 1\n"),
        ("const X{0} = X{1}\n", "const X{0} = 1\n"),
        ("var x{0} = x{1} + 1\n", "var x{0} = 1\n"),
        ("var x{0} = [x{1}]\n", "var x{0} = 1\n"),
    ], row => A_chain_of_members_thousands_long_comes_to_an_end_with_no_error(row.Link, row.Last));

    private void A_chain_of_members_thousands_long_comes_to_an_end_with_no_error(string link, string last)
    {
        const int Links = 3_000;
        string script = "extends Node\n\n" + string.Concat(Enumerable.Range(0, Links).Select(i => string.Format(CultureInfo.InvariantCulture, link, i, i + 1)))
            + string.Format(CultureInfo.InvariantCulture, last, Links);
        (GDScriptAnalysis analysis, _) = Analyze(script);
        Assert.Empty(analysis.Findings(GDScriptWarningSettings.Of(null)).Where(f => f.IsError).Select(f => f.Message));
    }

    /// <summary>Godot takes an extends path from the script's folder, a '..' past res:// kept, so a script at the top
    /// extending "../base.gd" extends a file outside the project, never res://base.gd.</summary>
    private void A_classs_base_by_a_relative_path_is_the_script_Godot_loads()
    {
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal)
        {
            ["res://base.gd"] = GDScriptTree.Parse("extends Node\n"),
            ["res://sub/main.gd"] = GDScriptTree.Parse("extends \"../base.gd\"\n"),
            ["res://top.gd"] = GDScriptTree.Parse("extends \"../base.gd\"\n"),
        };
        GDScriptAnalyzer analyzer = new(new GDScriptProject(Engine, path => trees.GetValueOrDefault(path), new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)));

        Assert.Equal("res://base.gd", analyzer.BaseOf(GDScriptAnalyzer.ScriptClass(trees["res://sub/main.gd"], "res://sub/main.gd"))?.Script);
        GDScriptType? outside = analyzer.BaseOf(GDScriptAnalyzer.ScriptClass(trees["res://top.gd"], "res://top.gd"));
        Assert.True(outside is { Script: null }, $"A script extending a file outside the project was given {outside?.Script}'s class, which Godot never loads for it");
    }

    /// <summary>A valid script nested past what Passenger reads is marked only where reading stopped, with a note: a name
    /// declared after it and a mistake past it say nothing, as nothing can be said of what went unread.</summary>
    private void A_script_read_in_part_has_a_note_where_reading_stopped_and_nothing_called_wrong()
    {
        string deep = "var deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\n";
        (GDScriptAnalysis analysis, _) = Analyze($"extends Node\n\nvar early: int = later\n{deep}var later := 1\nvar wrong: String = 1 +\n");
        GDScriptFinding only = Assert.Single(analysis.Findings(GDScriptWarningSettings.Of(null)));
        Assert.True(only.IsNote && !only.IsError, $"Reading stopped was said as {only.Code ?? "an error"}: {only.Message}");
        Assert.Equal(analysis.Tree!.Stopped!.Value.Start, only.Start);
    }

    private void A_node_path_is_typed_as_the_scenes_running_the_script_give_its_node_else_as_a_node()
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

    private void A_scripts_members_are_typed_as_godots_server_writes_them()
    {
        (GDScriptAnalysis analysis, _) = Analyze("extends Node2D\n\nconst LIMIT = 3\nvar speed: float = 2.0\nvar count := 0\nvar loose = 1\n"
            + "@export var label := \"hi\"\nvar where := position\nvar tree := get_tree()\nvar kids := get_children()\n\n"
            + "func _ready():\n\tpass\n\nfunc hit(amount: int, by = 2) -> bool:\n\treturn amount < by\n\nfunc _init():\n\tpass\n\nfunc untyped(a):\n\treturn a\n");
        Assert.Equal(["const LIMIT: int", "var speed: float", "var count: int", "var loose", "@export var label: String", "var where: Vector2",
            "var tree: SceneTree", "var kids: Array[Node]", "func _ready() -> void", "func hit(amount: int, by) -> bool", "func _init() -> res://main.gd",
            "func untyped(a)"], Details(analysis));
    }

    private void An_expressions_type_is_read_through_the_engines_classes_built_in_types_and_operators()
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

    private void A_class_takes_its_members_through_extends_by_path_and_class_name_across_the_projects_scripts_and_its_autoloads()
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

    private void A_name_is_declared_where_its_script_or_another_declares_it()
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

    /// <summary>Changes one after another join into one span of the text before them, the same edit a whole comparison finds.</summary>
    private void Changes_one_after_another_join_into_the_one_edit_of_the_text_before_them()
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

    private void Each_name_is_coloured_as_what_it_is_where_it_is_declared_and_where_it_is_read()
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

    /// <summary>What stands before a dot reads as the member it is however many dots came first, as a list opened there needs:
    /// typed on the dot alone, with nothing after it yet, or before a name already written. Godot 4.7.2 compiled each name
    /// listed there, and of a class read by its name rejected those left out.</summary>
    private void A_member_read_off_a_member_lists_its_own_members_after_its_dot() => Cases.Each<(string Written, string Type, string Listed, string Left)>(
    [
        ("print(Worker.Inner.|)", "Inner", "make DEEP Deepest", "held work field"),
        ("print(self.buddy.|)", "Worker", "field work hit LIMIT count", "held make"),
        ("print(self.spot.|)", "Vector2", "x y length", "field"),
        ("print(Worker.Mood.|)", "Mood", "CALM", "LIMIT"),
    ], row => A_member_read_off_a_member_lists_its_own_members_after_its_dot(row.Written, row.Type, row.Listed, row.Left));

    private void A_member_read_off_a_member_lists_its_own_members_after_its_dot(string written, string type, string listed, string left)
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
    private void A_dot_after_a_closing_bracket_reads_what_the_bracket_closes() => Cases.Each<(string Written, string Type, string Listed, string Left)>(
    [
        ("get_node(\"Body\").|", "Node", "name get_tree", "length"),
        ("arr[index].|", "Node", "name get_tree", "length"),
        ("[1].|", "Array", "size front", "length"),
        ("((buddy)).spot.|", "Vector2", "x length", "field"),
    ], row => A_dot_after_a_closing_bracket_reads_what_the_bracket_closes(row.Written, row.Type, row.Listed, row.Left));

    private void A_dot_after_a_closing_bracket_reads_what_the_bracket_closes(string written, string type, string listed, string left)
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

    /// <summary><c>new</c> follows a class read by its name, by any path to it, wherever Godot 4.7.2 compiled that call, and
    /// nowhere it refused one: an abstract class, one built on an abstract engine class, a singleton, an enum, a built-in type.
    /// A value of a class makes nothing, though Godot leaves that to the running game.</summary>
    private void New_follows_a_class_by_its_name_wherever_godot_lets_one_be_made() => Cases.Each<(string Written, bool Listed)>(
    [
        ("Worker", true),
        ("Worker.Inner.Deepest", true),
        ("Loaded.Inner", true),
        ("Shape.Whole", true),
        ("Shape", false),
        ("CanvasItem", false),
        ("Worker.new()", false),
    ], row => New_follows_a_class_by_its_name_wherever_godot_lets_one_be_made(row.Written, row.Listed));

    private void New_follows_a_class_by_its_name_wherever_godot_lets_one_be_made(string written, bool listed)
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
    private void Only_the_locals_whose_scope_holds_the_caret_are_in_scope_there() => Cases.Each<(string Written, string Expected)>(
    [
        ("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\t|\n", "a"),
        ("func f(a):\n\tif a:\n\t\tvar hidden := 1\n\t\t|\n", "hidden a"),
        ("func f(a, b):\n\tif a:\n\t\tvar hidden := 1\n\telif b:\n\t\tvar shown := 2\n\t\t|\n", "shown b a"),
        ("func f():\n\tvar me := |\n", ""),
        ("func f():\n\tfor i in 3:\n\t\t|\n", "i"),
        ("func f(x):\n\tmatch x:\n\t\tvar b:\n\t\t\t|\n", "b x"),
        ("func f():\n\tvar a := 1\n\tvar g = func(p):\n\t\tvar inner = p\n\t\t|\n\tvar later := 2\n", "inner p a"),
        ("var v: int:\n\tset(value):\n\t\t|\n\tget:\n\t\treturn 1\n", "value"),
        ("func f():\n\tvar a := 1; print(|)\n", "a"),
    ], row => Only_the_locals_whose_scope_holds_the_caret_are_in_scope_there(row.Written, row.Expected));

    private void Only_the_locals_whose_scope_holds_the_caret_are_in_scope_there(string written, string expected)
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

    /// <summary>The class's names a place finds, whether a name is typed there, nothing yet, or, on a line of its own, only its
    /// indentation. Godot 4.7.2 compiled every name of the script at each: in a static place only what needs no instance, and
    /// <c>new()</c>; a member variable's value takes all of them, as Godot's analyzer does, though its completion shows it none.</summary>
    private void A_static_place_finds_only_what_needs_no_instance() => Cases.Each<(string Written, bool IsStatic)>(
    [
        ("static func f():\n\t|\n", true),
        ("static func f():\n\tvar g = func(): print(|)\n", true),
        ("static var v = |\n", true),
        ("func f():\n\t|\n", false),
        ("var v = func(): print(|)\n", false),
        ("static var w = 1\n\n\nfunc f():\n\t|\n", false),
    ], row => A_static_place_finds_only_what_needs_no_instance(row.Written, row.IsStatic));

    private void A_static_place_finds_only_what_needs_no_instance(string written, bool isStatic)
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

    /// <summary>A constant's value and an enum's take what Godot folds: whether a name is typed there or nothing yet, none of the
    /// script's variables, functions and signals, nor the engine's, and of the locals only constants, an enum's value only what
    /// holds an integer. Godot 4.7.2 compiled every name of the script at each (gen9), a class or enum before a further dot.</summary>
    private void A_constants_value_finds_what_godot_folds() => Cases.Each<(string Written, GDScriptConstantPlace Place)>(
    [
        ("const C = |\n", GDScriptConstantPlace.Value),
        ("func f(p):\n\tvar v := 1\n\tconst K := 2\n\tconst C = |\n", GDScriptConstantPlace.Value),
        ("enum { X = | }\n", GDScriptConstantPlace.Integer),
        ("enum { X = int(1) + | }\n", GDScriptConstantPlace.Integer),
        ("enum { X = int(|) }\n", GDScriptConstantPlace.Value),
        ("var v = |\n", GDScriptConstantPlace.None),
        ("func f(p):\n\tvar v := 1\n\tconst K := 2\n\tprint(|)\n", GDScriptConstantPlace.None),
    ], row => A_constants_value_finds_what_godot_folds(row.Written, row.Place));

    private void A_constants_value_finds_what_godot_folds(string written, GDScriptConstantPlace place)
    {
        const string Head = "extends Node\n\nsignal rung\nenum Mood { CALM }\nenum { LOOSE }\nconst LIMIT := 3\nconst TEXT := \"x\"\nconst Klass = preload(\"res://worker.gd\")\nstatic var shared := 2\nvar field := 1\n\n"
            + "func work():\n\treturn 1\n\nstatic func tool_fn():\n\treturn 1\n\nclass Next:\n\tconst K := 1\n\n";
        string[] constants = ["LIMIT", "Mood", "LOOSE", "Next", "Klass", "PROCESS_MODE_INHERIT"];
        string[] running = ["field", "shared", "work", "tool_fn", "rung", "name", "get_tree", "ready", "print_orphan_nodes", "new"];
        int mark = written.IndexOf('|', StringComparison.Ordinal);
        foreach (string typed in (string[])["zz", string.Empty])
        {
            (GDScriptAnalysis analysis, GDScriptAnalyzer analyzer) = Analyze(Head + written.Replace("|", typed, StringComparison.Ordinal), Classes);
            int caret = Head.Length + mark + typed.Length;
            GDScriptConstantPlace found = analysis.ConstantAt(caret);
            Assert.True(found == place, $"\"{written}\" with \"{typed}\" typed read as {found}, not {place}");
            HashSet<string> locals = [.. analysis.LocalsAt(caret).Where(m => found.Takes(m)).Select(l => l.Name)];
            HashSet<string> names = [.. analyzer.MembersInScope(analysis.Tree!, analysis.Path, analysis.ClassAt(caret), analysis.StaticAt(caret)).Where(m => found.Takes(m)).Select(m => m.Name)];
            Assert.Superset(new HashSet<string>(constants), names);
            Assert.Equal(place != GDScriptConstantPlace.Integer, names.Contains("TEXT"));
            if (place != GDScriptConstantPlace.None)
            {
                Assert.Empty(names.Intersect(running));
                Assert.Empty(locals.Intersect(["p", "v"]));
                Assert.Equal(written.Contains("const K", StringComparison.Ordinal), locals.Contains("K"));
            }
            else
            {
                Assert.Contains("field", names);
                Assert.Equal(written.Contains("(p)", StringComparison.Ordinal), locals.Contains("p"));
            }
        }
    }

    /// <summary>A node path is asked for only where Godot 4.7.2 compiled <c>$</c> and <c>%</c>, as each is typed: never in a static
    /// place, whose names are still asked for.</summary>
    private void A_node_path_is_asked_for_only_where_godot_compiles_one() => Cases.Each<(string Written, bool IsStatic)>(
    [
        ("static func f():\n\t|\n", true),
        ("static func _static_init():\n\t|\n", true),
        ("func f():\n\tprint(|)\n", false),
        ("static func g():\n\tpass\n\n\nfunc f():\n\t|\n", false),
    ], row => A_node_path_is_asked_for_only_where_godot_compiles_one(row.Written, row.IsStatic));

    private void A_node_path_is_asked_for_only_where_godot_compiles_one(string written, bool isStatic)
    {
        const string Head = "extends Node\n\n";
        int mark = written.IndexOf('|', StringComparison.Ordinal);
        foreach (string typed in (string[])["$", "%", "$Bo", "%Bo", "$Body/Ch"])
        {
            GDScriptAsked asked = Asked(typed);
            GDScriptAskedKind expected = isStatic ? GDScriptAskedKind.None : GDScriptAskedKind.NodePath;
            Assert.True(asked.Kind == expected, $"{typed} in \"{written}\" asked for {asked.Kind}, not {expected}");
        }
        Assert.Equal(GDScriptAskedKind.Name, Asked("zz").Kind);

        GDScriptAsked Asked(string typed)
        {
            (GDScriptAnalysis analysis, _) = Analyze(Head + written.Replace("|", typed, StringComparison.Ordinal));
            return analysis.AskedAt(Head.Length + mark + typed.Length);
        }
    }

    private static Dictionary<string, string> Locals(GDScriptAnalysis analysis) =>
        analysis.Tree!.Members.SelectMany(m => analysis.LocalsAt(m.End - 1)).GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First().Type.ToString(), StringComparer.Ordinal);
}

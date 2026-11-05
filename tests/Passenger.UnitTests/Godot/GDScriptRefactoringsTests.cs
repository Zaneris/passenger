using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>GDScript's refactorings over Passenger's reading of a project's scripts: each one's edits, every script it
/// touches still parsing clean, and each refused, naming the place, where it cannot see every use or would change what the
/// code does.</summary>
public class GDScriptRefactoringsTests
{
    /// <summary>A small engine API of Godot's dump's form: a few node classes, one beside another.</summary>
    private const string Api = """
        {
          "global_enums": [], "global_constants": [], "utility_functions": [], "singletons": [],
          "builtin_classes": [{ "name": "String", "methods": [] }, { "name": "StringName", "methods": [] },
            { "name": "Callable", "methods": [{ "name": "call", "is_const": true, "is_vararg": true, "return_type": "Variant" }] },
            { "name": "Vector2", "methods": [{ "name": "length", "is_const": true, "return_type": "float" }, { "name": "from_angle", "is_static": true, "return_type": "Vector2" }] },
            { "name": "Array", "indexing_return_type": "Variant", "methods": [{ "name": "size", "is_const": true, "return_type": "int" }, { "name": "pop_back", "return_type": "Variant" },
              { "name": "map", "is_const": true, "return_type": "Array", "arguments": [{ "name": "method", "type": "Callable" }] }] },
            { "name": "Dictionary", "indexing_return_type": "Variant", "methods": [] }],
          "classes": [
            { "name": "Object", "methods": [{ "name": "call", "is_vararg": true, "return_value": { "type": "Variant" }, "arguments": [{ "name": "method", "type": "StringName" }] },
              { "name": "call_deferred", "is_vararg": true, "return_value": { "type": "Variant" }, "arguments": [{ "name": "method", "type": "StringName" }] }] },
            { "name": "RefCounted", "inherits": "Object" },
            { "name": "Node", "inherits": "Object", "methods": [{ "name": "_ready", "is_virtual": true }, { "name": "_get_configuration_warnings", "is_virtual": true },
              { "name": "get_parent", "is_const": true, "return_value": { "type": "Node" } }, { "name": "add_child", "arguments": [{ "name": "node", "type": "Node" }] }] },
            { "name": "Node2D", "inherits": "Node", "methods": [{ "name": "_draw_shape", "is_virtual": true }] },
            { "name": "Sprite2D", "inherits": "Node2D" },
            { "name": "Timer", "inherits": "Node" }
          ]
        }
        """;

    private static readonly GodotEngineApi Engine = GodotEngineApi.Read(System.Text.Encoding.UTF8.GetBytes(Api));

    /// <summary>Scripts read as a project, each class_name naming its script.</summary>
    private static IReadOnlyList<GDScriptRead> Read(params (string Script, string Text)[] scripts) => ReadWith(null, scripts);

    /// <summary>Scripts read as a project against an engine's API.</summary>
    private static IReadOnlyList<GDScriptRead> ReadWith(GodotEngineApi? engine, params (string Script, string Text)[] scripts)
    {
        Dictionary<string, GDScriptTree> trees = scripts.ToDictionary(s => s.Script, s => GDScriptTree.Parse(s.Text), StringComparer.Ordinal);
        Dictionary<string, string> classNames = trees.Where(t => t.Value.Class.Identifier is not null).ToDictionary(t => t.Value.Class.Identifier!.Name, t => t.Key, StringComparer.Ordinal);
        GDScriptAnalyzer analyzer = new(new GDScriptProject(engine, p => trees.GetValueOrDefault(p), classNames, new Dictionary<string, string>()));
        return [.. scripts.Select(s => new GDScriptRead(s.Script, analyzer.Analyze(trees[s.Script], s.Script)))];
    }

    private static GDScriptAnalysis One(string text) => Read(("res://main.gd", text))[0].Analysis;

    /// <summary>A script's text after a refactoring, which must parse clean.</summary>
    private static string After(GDScriptRefactored made, string script, string text)
    {
        Assert.Null(made.Refused);
        string after = made.Scripts.FirstOrDefault(s => s.Script == script) is { } edits ? TextSnapshot.From(text).Apply(edits.Edits).ToString() : text;
        Assert.True(GDScriptTree.Parse(after).Errors.Count == 0, $"{script} no longer parses after the refactoring:\n{after}");
        return after;
    }

    private static TextRange Over(string text, string selected, int nth = 0)
    {
        int at = -1;
        for (int i = 0; i <= nth; i++)
        {
            at = text.IndexOf(selected, at + 1, StringComparison.Ordinal);
        }
        return new TextRange(at, selected.Length);
    }

    [Fact]
    public void An_inner_classs_member_is_no_local_so_its_rename_and_uses_are_left_to_the_whole_project()
    {
        string text = "extends Node\n\n\nfunc _ready() -> void:\n\tvar h := Helper.new()\n\th.count += 1\n\tprint(h.count)\n\n\n"
            + "class Helper:\n\tenum Kind { ONE, TWO }\n\tsignal bumped\n\tvar count := 0\n\n\tfunc bump(by := 1) -> void:\n\t\tvar step := by\n\t\tcount += step\n";
        GDScriptRead read = Read(("res://main.gd", text))[0];
        foreach (string member in new[] { "count := 0", "bump(by", "bumped\n", "ONE", "count += step" })
        {
            int at = text.IndexOf(member, StringComparison.Ordinal) + 1;
            Assert.True(GDScriptRefactorings.RenameLocal(read, at, "renamed") is null, $"Renaming {member} renames it as a local of its inner class alone");
            Assert.True(GDScriptRefactorings.LocalPlacesOf(read, at) is null, $"Find Usages of {member} finds it as a local of its inner class alone");
        }
        string renamed = After(GDScriptRefactorings.RenameLocal(read, text.IndexOf("step :=", StringComparison.Ordinal) + 1, "amount")!, "res://main.gd", text);
        Assert.Contains("\t\tvar amount := by\n\t\tcount += amount\n", renamed);
    }

    [Fact]
    public void An_enums_value_and_an_inner_classs_member_are_found_and_renamed_where_their_scope_takes_them()
    {
        string text = "extends Node\n\nenum Mood { CALM, ANGRY }\nenum { LOW, HIGH }\n\nvar helper := Helper.new()\n\n\nfunc _ready() -> void:\n\tvar m := Mood.CALM\n"
            + "\thelper.count += 1\n\tprint(m == Mood.ANGRY, LOW, Helper.Kind.ONE, helper.bump())\n\n\nclass Helper:\n\tenum Kind { ONE, TWO }\n\tvar count := 0\n\n"
            + "\tfunc bump() -> int:\n\t\tcount += 1\n\t\treturn Kind.TWO\n\n\nclass Sub extends Helper:\n\tfunc bump() -> int:\n\t\treturn 2\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptTarget At(string written, int into = 0) => GDScriptRefactorings.DeclarationAt(scripts, scripts[0], text.IndexOf(written, StringComparison.Ordinal) + into)!;
        GDScriptTarget count = At("count += 1\n\tprint");
        Assert.Equal(3, GDScriptRefactorings.PlacesOf(scripts, count).Places.Count);
        string renamed = After(GDScriptRefactorings.Rename(scripts, count, "total"), "res://main.gd", text);
        Assert.True(renamed.Contains("\thelper.total += 1\n", StringComparison.Ordinal) && renamed.Contains("\tvar total := 0\n", StringComparison.Ordinal)
            && renamed.Contains("\t\ttotal += 1\n", StringComparison.Ordinal), $"Helper's count should be renamed where it is declared, read off a Helper and read in its class:\n{renamed}");
        renamed = After(GDScriptRefactorings.Rename(scripts, At("CALM, ANGRY"), "STILL"), "res://main.gd", text);
        Assert.True(renamed.Contains("enum Mood { STILL, ANGRY }", StringComparison.Ordinal) && renamed.Contains("var m := Mood.STILL", StringComparison.Ordinal), renamed);
        renamed = After(GDScriptRefactorings.Rename(scripts, At("Kind.ONE", 5), "FIRST"), "res://main.gd", text);
        Assert.True(renamed.Contains("enum Kind { FIRST, TWO }", StringComparison.Ordinal) && renamed.Contains("Helper.Kind.FIRST", StringComparison.Ordinal), renamed);
        Assert.Contains("already has a member 'HIGH'", GDScriptRefactorings.Rename(scripts, At("LOW, HIGH"), "HIGH").Refused);
        Assert.Contains("already has a member 'helper'", GDScriptRefactorings.Rename(scripts, At("LOW, HIGH"), "helper").Refused);
        Assert.Null(GDScriptRefactorings.Rename(scripts, At("CALM, ANGRY"), "LOW").Refused);
        Assert.Contains("the inner class Sub declares a 'bump' too", GDScriptRefactorings.Rename(scripts, At("bump() -> int:\n\t\tcount"), "poke").Refused);
    }

    [Fact]
    public void A_callables_own_call_names_no_method_so_a_rename_goes_on_but_an_objects_call_by_a_name_read_at_run_time_refuses_it()
    {
        const string Helper = "class_name Helper\nextends Node\n\n\nfunc clear(after: float) -> void:\n\tprint(after)\n";
        const string User = "extends Node\n\nvar helper := Helper.new()\n\n\nfunc _ready() -> void:\n\thelper.clear.call_deferred(0.2)\n"
            + "\tvar later := func(): helper.clear(0.1)\n\tlater.call()\n\tcall_deferred(\"queue_free\")\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://helper.gd", Helper), ("res://user.gd", User));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], Helper.IndexOf("clear", StringComparison.Ordinal))!;
        GDScriptRefactored renamed = GDScriptRefactorings.Rename(scripts, target, "wipe");
        Assert.Contains("\thelper.wipe.call_deferred(0.2)\n", After(renamed, "res://user.gd", User));
        Assert.Contains("helper.wipe(0.1)", After(renamed, "res://user.gd", User));
        const string Dynamic = User + "\n\nfunc by_name(method: String) -> void:\n\thelper.call(method)\n";
        IReadOnlyList<GDScriptRead> unsure = Read(("res://helper.gd", Helper), ("res://user.gd", Dynamic));
        Assert.Contains("names a method by what it reads at run time",
            GDScriptRefactorings.Rename(unsure, GDScriptRefactorings.MemberAt(unsure, unsure[0], Helper.IndexOf("clear", StringComparison.Ordinal))!, "wipe").Refused);
    }

    [Fact]
    public void A_call_by_a_name_holds_back_a_rename_only_where_its_object_may_run_the_functions_class()
    {
        const string Helper = "class_name Helper\nextends Node2D\n\n\nfunc clear() -> void:\n\tpass\n";
        const string Other = "class_name Other\nextends Node2D\n";
        const string User = "extends Node\n\nvar other := Other.new()\nvar timer := Timer.new()\nvar sprite := Sprite2D.new()\n\n\n"
            + "func by_name(method: String) -> void:\n\tother.call(method)\n\ttimer.call_deferred(method)\n\tcall(method)\n\ttimer.clear()\n"
            + "\ttimer.call(\"clear\")\n\tsprite.call(\"clear\")\n";
        IReadOnlyList<GDScriptRead> scripts = ReadWith(Engine, ("res://helper.gd", Helper), ("res://other.gd", Other), ("res://user.gd", User));
        GDScriptRefactored renamed = GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], Helper.IndexOf("clear", StringComparison.Ordinal))!, "wipe");
        Assert.True(renamed.Refused is null, $"A call by a name on another script's class, on an engine class Helper is not built on, and on the user's own class held back the rename: {renamed.Refused}");
        string after = After(renamed, "res://user.gd", User);
        Assert.True(after.Contains("\ttimer.call(\"clear\")\n\tsprite.call(\"wipe\")\n", StringComparison.Ordinal),
            $"A string on a Timer, which can't run Helper, should stay, and one on a Sprite2D, which may, should follow the rename:\n{after}");
        foreach (string unsure in new[] { "var node: Node\n\n\nfunc on_node(method: String) -> void:\n\tnode.call(method)\n",
            "var anything\n\n\nfunc on_anything(method: String) -> void:\n\tanything.call(method)\n", "var node: Node\n\n\nfunc on_node() -> void:\n\tnode.clear()\n" })
        {
            IReadOnlyList<GDScriptRead> held = ReadWith(Engine, ("res://helper.gd", Helper), ("res://other.gd", Other), ("res://user.gd", User + "\n\n" + unsure));
            GDScriptRefactored refused = GDScriptRefactorings.Rename(held, GDScriptRefactorings.MemberAt(held, held[0], Helper.IndexOf("clear", StringComparison.Ordinal))!, "wipe");
            Assert.True(refused.Places.Count == 1 && refused.Places[0].Script == "res://user.gd" && refused.Unseen,
                $"A call on a Node, which may run Helper, or on a value of no type, and only that, should hold back the rename:\n{unsure}\n{refused.Refused}");
            GDScriptRefactored anyway = GDScriptRefactorings.Rename(held, GDScriptRefactorings.MemberAt(held, held[0], Helper.IndexOf("clear", StringComparison.Ordinal))!, "wipe", anyway: true);
            Assert.Contains("func wipe() -> void:", After(anyway, "res://helper.gd", Helper));
            Assert.Equal(refused.Places, anyway.Places);
        }
    }

    [Fact]
    public void A_member_a_string_names_for_get_set_or_is_connected_is_found_renamed_and_holds_back_a_safe_delete()
    {
        string text = "extends Node\n\nsignal done\nvar score := 5\n\n\nfunc cb():\n\tpass\n\n\nfunc _ready():\n\tprint(get(\"score\"), self.get(\"score\"))\n\tset(\"score\", 1)\n"
            + "\tset_deferred(\"score\", 2)\n\tprint(get_indexed(\"score:x\"), self[\"score\"], \"score\" in self)\n\tcreate_tween().tween_property(self, \"score\", 1, 1.0)\n"
            + "\tprint(is_connected(\"done\", cb), Signal(self, \"done\"), get_signal_connection_list(\"done\"))\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptTarget score = GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("score", StringComparison.Ordinal))!;
        GDScriptTarget done = GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("done", StringComparison.Ordinal))!;
        string renamed = After(GDScriptRefactorings.Rename(scripts, score, "points"), "res://main.gd", text);
        Assert.Equal(text.Replace("score", "points"), renamed);
        Assert.Equal(text.Replace("done", "finished"), After(GDScriptRefactorings.Rename(scripts, done, "finished"), "res://main.gd", text));
        Assert.Equal(9, GDScriptRefactorings.PlacesOf(scripts, score).Places.Count);
        Assert.Contains("names it in a string", GDScriptRefactorings.SafeDelete(scripts, score, []).Refused);
        Assert.Contains("names it in a string", GDScriptRefactorings.SafeDelete(scripts, done, []).Refused);
        // A signal taken as a value can't follow a change of what it passes; one only asked about can.
        Assert.Contains("takes it as a value", GDScriptRefactorings.ChangeSignalSignature(scripts, done, "done", [new GDScriptParameterChange(-1, "n", string.Empty, string.Empty, "1")], [], []).Refused);
    }

    [Fact]
    public void A_string_on_a_value_that_may_not_be_the_members_class_leaves_a_rename_to_the_person_or_alone()
    {
        const string Scored = "class_name Scored\nextends Node2D\n\nvar score := 5\n\n\nfunc die():\n\tpass\n";
        (string? Refused, bool Unseen, string User) Renamed(string body, string name = "score", string to = "points")
        {
            string user = $"extends Node\n\nvar typed: Scored\nvar other: Timer\nvar known := {{}}\n\n\nfunc go(data, map: Dictionary, key):\n{body}";
            IReadOnlyList<GDScriptRead> scripts = ReadWith(Engine, ("res://scored.gd", Scored), ("res://user.gd", user));
            GDScriptRefactored made = GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], Scored.IndexOf(name, StringComparison.Ordinal))!, to);
            return (made.Refused, made.Unseen, made.Refused is null ? After(made, "res://user.gd", user) : string.Empty);
        }
        // On its own class it is the member, on a dictionary or an engine class beside it a name of theirs.
        Assert.Contains("\tprint(typed.get(\"points\"))\n", Renamed("\tprint(typed.get(\"score\"))\n").User);
        Assert.Contains("\tprint(map.get(\"score\"), known.get(\"score\"), map[\"score\"], \"score\" in map)\n", Renamed("\tprint(map.get(\"score\"), known.get(\"score\"), map[\"score\"], \"score\" in map)\n").User);
        Assert.Contains("\tprint(other.get(\"score\"))\n", Renamed("\tprint(other.get(\"score\"))\n").User);
        // On a value of no type read it may be a dictionary's key, so the person says, and every other string of no such name goes on.
        foreach (string read in new[] { "\tprint(data.get(\"score\"))\n", "\tprint(data[\"score\"])\n", "\tprint(\"score\" in data)\n" })
        {
            (string? refused, bool unseen, _) = Renamed(read);
            Assert.True(unseen && refused?.Contains("may name it, on a value whose type is not read", StringComparison.Ordinal) == true, $"{read}: {refused}");
        }
        Assert.Null(Renamed("\tprint(data.get(\"scorer\"), data.get(key))\n").Refused);
        Assert.Contains("names a property by what it reads at run time", Renamed("\tprint(typed.get(key))\n").Refused);
        Assert.Contains("names it for the nodes of a group", Renamed("\tget_tree().call_group(\"enemies\", \"die\")\n", "die", "fall").Refused);
        Assert.Contains("names a signal by what it reads at run time", SignalRefused("\temit_signal(key)\n"));

        static string? SignalRefused(string body)
        {
            string text = $"extends Node\n\nsignal done\n\n\nfunc go(key):\n{body}";
            IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
            return GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("done", StringComparison.Ordinal))!, "finished").Refused;
        }
    }

    [Fact]
    public void Renaming_an_override_renames_the_function_it_overrides_and_every_override_of_that_but_never_an_engines()
    {
        const string Base = "class_name Tool\nextends Node2D\n\n\nfunc draw_move() -> void:\n\tpass\n\n\nfunc _draw_shape() -> void:\n\tpass\n";
        const string Pencil = "extends Tool\n\n\nfunc draw_move() -> void:\n\tsuper()\n";
        const string Eraser = "extends \"res://base.gd\"\n\n\nfunc draw_move() -> void:\n\tsuper.draw_move()\n";
        const string Lone = "class_name Lone\nextends Node2D\n\n\nfunc draw_move() -> void:\n\tpass\n";
        const string User = "extends Node\n\nvar tool: Tool\nvar pencil := preload(\"res://pencil.gd\").new()\nvar lone := Lone.new()\n\n\n"
            + "func go() -> void:\n\ttool.draw_move()\n\tpencil.draw_move()\n\tlone.draw_move()\n";
        (string, string)[] project = [("res://base.gd", Base), ("res://pencil.gd", Pencil), ("res://eraser.gd", Eraser), ("res://lone.gd", Lone), ("res://user.gd", User)];
        IReadOnlyList<GDScriptRead> scripts = ReadWith(Engine, project);
        GDScriptRefactored renamed = GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[1], Pencil.IndexOf("draw_move", StringComparison.Ordinal))!, "move_to");
        Assert.Contains("\nfunc move_to() -> void:\n\tpass\n", After(renamed, "res://base.gd", Base));
        Assert.Contains("\nfunc move_to() -> void:\n\tsuper()\n", After(renamed, "res://pencil.gd", Pencil));
        Assert.Contains("\nfunc move_to() -> void:\n\tsuper.move_to()\n", After(renamed, "res://eraser.gd", Eraser));
        string user = After(renamed, "res://user.gd", User);
        Assert.True(user.Contains("\ttool.move_to()\n\tpencil.move_to()\n\tlone.draw_move()\n", StringComparison.Ordinal),
            $"The calls of the base's and the override's should follow, and Lone's own draw_move, no override of them, stay:\n{user}");
        Assert.DoesNotContain(renamed.Scripts, s => s.Script == "res://lone.gd");

        IReadOnlyList<GDScriptRead> taken = ReadWith(Engine, [.. project, ("res://bucket.gd", "extends Tool\n\n\nfunc move_to() -> void:\n\tpass\n")]);
        Assert.Contains("res://bucket.gd declares a 'move_to' too",
            GDScriptRefactorings.Rename(taken, GDScriptRefactorings.MemberAt(taken, taken[1], Pencil.IndexOf("draw_move", StringComparison.Ordinal))!, "move_to").Refused);
        Assert.Contains("overrides Node2D's own, which Godot calls by its name",
            GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], Base.IndexOf("_draw_shape", StringComparison.Ordinal))!, "shape").Refused);
    }

    [Fact]
    public void An_enum_named_as_a_type_is_declared_where_the_enum_is_and_renamed_there_as_where_code_reads_it()
    {
        const string Helper = "class_name Helper\nextends Node\n\nenum Mood { CALM, ANGRY }\n\nvar mood: Mood = Mood.CALM\n\n\n"
            + "func feel(m: Mood) -> Mood:\n\tvar moods: Array[Mood] = [m]\n\treturn moods[0] as Mood\n";
        const string User = "extends Node\n\nvar seen: Helper.Mood\n\n\nfunc _ready() -> void:\n\tseen = Helper.Mood.ANGRY\n\tprint(seen is Helper.Mood)\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://helper.gd", Helper), ("res://user.gd", User));
        int typed = User.IndexOf("Helper.Mood", StringComparison.Ordinal) + "Helper.".Length + 1;
        Assert.True(scripts[1].Analysis.NodeAt(typed) is ({ } node, _) && node is GDScriptIdentifier { Name: "Mood" } named
            && scripts[1].Analysis.DeclarationOf(named) is { Script: "res://helper.gd" } declared && declared.Start == Helper.IndexOf("Mood", StringComparison.Ordinal),
            "Go to Declaration on Helper.Mood written as a type should go to helper.gd's enum Mood");
        GDScriptRefactored renamed = GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], Helper.IndexOf("Mood", StringComparison.Ordinal))!, "Feeling");
        Assert.Equal("class_name Helper\nextends Node\n\nenum Feeling { CALM, ANGRY }\n\nvar mood: Feeling = Feeling.CALM\n\n\n"
            + "func feel(m: Feeling) -> Feeling:\n\tvar moods: Array[Feeling] = [m]\n\treturn moods[0] as Feeling\n", After(renamed, "res://helper.gd", Helper));
        Assert.Equal("extends Node\n\nvar seen: Helper.Feeling\n\n\nfunc _ready() -> void:\n\tseen = Helper.Feeling.ANGRY\n\tprint(seen is Helper.Feeling)\n",
            After(renamed, "res://user.gd", User));
    }

    [Fact]
    public void Renaming_an_inner_class_renames_it_where_a_type_or_a_base_names_it_as_where_code_does()
    {
        string text = "extends Node\n\nvar inner: Inner\nvar many: Array[Inner] = []\n\n\nfunc _ready() -> void:\n\tvar x: Variant = inner\n"
            + "\tprint(x is Inner, x as Inner, Inner.new(), many)\n\n\nclass Inner:\n\tpass\n\n\nclass Sub extends Inner:\n\tpass\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("class Inner", StringComparison.Ordinal) + 7)!;
        Assert.Equal(text.Replace("Inner", "Inside", StringComparison.Ordinal), After(GDScriptRefactorings.Rename(scripts, target, "Inside"), "res://main.gd", text));
    }

    [Fact]
    public void Renaming_a_function_a_property_names_as_its_setter_or_getter_renames_it_there_too()
    {
        string text = "extends Node\n\nvar active := true:\n\tset = set_active, get = is_active\n\n\nfunc set_active(value: bool) -> void:\n\tactive = value\n\n\n"
            + "func is_active() -> bool:\n\treturn active\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptRefactored setter = GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("func set_active", StringComparison.Ordinal) + 6)!, "turn");
        Assert.Contains("\tset = turn, get = is_active\n", After(setter, "res://main.gd", text));
        GDScriptRefactored getter = GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("func is_active", StringComparison.Ordinal) + 6)!, "on");
        Assert.Contains("\tset = set_active, get = on\n", After(getter, "res://main.gd", text));
    }

    [Fact]
    public void Renaming_a_member_is_refused_where_a_use_would_read_a_local_of_the_new_name()
    {
        string? Renamed(string body, string to = "health")
        {
            string text = $"var hp := 5\n\n\nfunc go(list):\n{body}";
            IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
            return GDScriptRefactorings.Rename(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("hp", StringComparison.Ordinal))!, to).Refused;
        }
        Assert.Contains("'health' would read the local, not the member", Renamed("\tvar health := 9\n\tprint(hp)\n"));
        Assert.Contains("reads it where a local 'health' stands", Renamed("\tvar health := 9\n\thp = 1\n"));
        Assert.Contains("would read the local", Renamed("\tconst health = 9\n\tprint(hp)\n"));
        Assert.Contains("would read the local", Renamed("\tfor health in list:\n\t\tprint(hp)\n"));
        Assert.Contains("would read the local", Renamed("\tmatch list:\n\t\tvar health:\n\t\t\tprint(hp)\n"));
        Assert.Contains("would read the local", Renamed("\tvar health := 9\n\tvar f := func(): return hp\n"));
        Assert.Contains("would read the local", Renamed("\tvar f := func(health): return hp\n"));
        Assert.Contains("would read the local", Renamed("\tprint(hp)\n", "list"));
        // Where a local of the name stands over no use, and where the name can't be the local's.
        Assert.Null(Renamed("\tprint(hp)\n\tvar health := 9\n\tprint(health)\n"));
        Assert.Null(Renamed("\tif list:\n\t\tvar health := 9\n\t\tprint(health)\n\tprint(hp)\n"));
        Assert.Null(Renamed("\tvar health := 9\n\tprint(self.hp, health)\n"));
        Assert.Null(Renamed("\tvar health := hp\n\tprint(health)\n"));

        const string Own = "var hp := 5\n\n\nfunc go(hurt):\n\thurt()\n\n\nfunc other():\n\tvar health := 1\n\tgo(health)\n";
        IReadOnlyList<GDScriptRead> called = Read(("res://main.gd", Own));
        Assert.Null(GDScriptRefactorings.Rename(called, GDScriptRefactorings.MemberAt(called, called[0], Own.IndexOf("go(hurt", StringComparison.Ordinal))!, "health").Refused);

        // A use in another script, and a property's own setter.
        const string Base = "class_name Base\nextends Node\n\nvar hp := 5\n";
        const string Child = "extends Base\n\n\nfunc go():\n\tvar health := 9\n\tprint(hp, health)\n";
        IReadOnlyList<GDScriptRead> family = Read(("res://base.gd", Base), ("res://child.gd", Child));
        Assert.Contains("res://child.gd", GDScriptRefactorings.Rename(family, GDScriptRefactorings.MemberAt(family, family[0], Base.IndexOf("hp", StringComparison.Ordinal))!, "health").Refused);
        const string Setter = "var hp := 5:\n\tset(value):\n\t\thp = value\n";
        IReadOnlyList<GDScriptRead> set = Read(("res://main.gd", Setter));
        Assert.Contains("would read the local", GDScriptRefactorings.Rename(set, GDScriptRefactorings.MemberAt(set, set[0], 4)!, "value").Refused);
    }

    [Fact]
    public void Inlining_a_function_and_introducing_a_parameter_are_refused_where_a_name_would_read_a_local_in_a_members_place()
    {
        string text = "var hp := 5\n\n\nfunc read_hp():\n\treturn hp\n\n\nfunc go():\n\tvar hp2 := 1\n\tvar other = 2\n\tprint(read_hp(), hp2, other)\n\n\nfunc shadowed():\n\tvar hp := 9\n\tprint(read_hp(), hp)\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptRefactored inlined = GDScriptRefactorings.InlineFunction(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("read_hp", StringComparison.Ordinal))!, []);
        Assert.Contains("'hp' would read the local where it is called", inlined.Refused);
        string step = "var step := 2\n\n\nfunc go(a):\n\tprint(step)\n\treturn a + 10\n";
        Assert.Contains("'step' is read in the function", GDScriptRefactorings.IntroduceParameter(One(step), Over(step, "10"), "step").Refused);
        Assert.Contains("func go(a, size := 10):", After(GDScriptRefactorings.IntroduceParameter(One(step), Over(step, "10"), "size"), "res://main.gd", step));
    }

    [Fact]
    public void Extract_variable_declares_the_expression_before_its_statement_and_reads_it_there()
    {
        // Typed by := where its type is read, as an int literal's is; without the engine's operators, a product's is not.
        string text = "func f(a: int) -> int:\n\treturn a * 2 + 1\n";
        Assert.Equal("func f(a: int) -> int:\n\tvar doubled = a * 2\n\treturn doubled + 1\n",
            After(GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a * 2"), "doubled"), "res://main.gd", text));
        string untyped = "func f(a):\n\tprint(a.size())\n";
        Assert.Equal("func f(a):\n\tvar n = a.size()\n\tprint(n)\n", After(GDScriptRefactorings.ExtractVariable(One(untyped), Over(untyped, "a.size()"), "n"), "res://main.gd", untyped));
        string literal = "func f(a):\n\treturn a + 10\n";
        Assert.Equal("func f(a):\n\tvar step := 10\n\treturn a + step\n", After(GDScriptRefactorings.ExtractVariable(One(literal), Over(literal, "10"), "step"), "res://main.gd", literal));
    }

    [Fact]
    public void Extract_variable_is_refused_where_the_expression_would_run_another_number_of_times()
    {
        string text = "func f(a):\n\twhile a.next():\n\t\tpass\n\tif a and a.ok():\n\t\tpass\n";
        Assert.Contains("loop's condition", GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a.next()"), "n").Refused);
        Assert.Contains("condition before it", GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a.ok()"), "n").Refused);
        Assert.Contains("keyword", GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a.ok()"), "class").Refused);
    }

    [Fact]
    public void Extract_variable_is_refused_where_something_that_runs_before_the_expression_could_tell()
    {
        const string Prelude = "var hp = 5\n\n\nfunc first():\n\thp = 1\n\treturn 1\n\n\nfunc second():\n\treturn 2\n\n\nfunc consume(a, b):\n\tprint(a, b)\n\n\n";
        void Refused(string statement, string selected, string runsFirst)
        {
            string text = Prelude + $"func f(n):\n\t{statement}\n";
            GDScriptRefactored made = GDScriptRefactorings.ExtractVariable(One(text), Over(text, selected, text.Split(selected).Length - 2), "x");
            Assert.True(made.Refused?.Contains($"'{runsFirst}' runs first", StringComparison.Ordinal) == true, $"Extracting {selected} from {statement} runs it ahead of {runsFirst}; it answered: {made.Refused ?? "made"}");
        }
        void Made(string statement, string selected, string extracted)
        {
            string text = Prelude + $"func f(n):\n\t{statement}\n";
            string after = After(GDScriptRefactorings.ExtractVariable(One(text), Over(text, selected, text.Split(selected).Length - 2), "x"), "res://main.gd", text);
            Assert.EndsWith(extracted, after);
        }
        Refused("consume(first(), second())", "second()", "first()");
        Refused("print(first() + second())", "second()", "first()");
        Refused("print([first(), second()])", "second()", "first()");
        Refused("print({first(): second()})", "second()", "first()");
        // A call's arguments run before what it is called on, whichever is written first.
        Refused("first().size(second())", "first()", "second()");
        // What a call may change reads otherwise after it; the plain variable alone is read as the call it is passed to runs.
        Refused("consume(first(), hp)", "hp", "first()");
        Refused("consume(hp, first())", "hp", "first()");
        Refused("consume(hp + 1, first())", "first()", "hp");
        Made("consume(hp, first())", "first()", "\tvar x = first()\n\tconsume(hp, x)\n");
        Made("hp.size(first())", "first()", "\tvar x = first()\n\thp.size(x)\n");
        Made("consume(first(), n + 1)", "n + 1", "\tvar x = n + 1\n\tconsume(first(), x)\n");
        Made("consume(first(), 2)", "2", "\tvar x := 2\n\tconsume(first(), x)\n");
        Made("consume(first(), Vector2(1, 2))", "Vector2(1, 2)", "\tvar x := Vector2(1, 2)\n\tconsume(first(), x)\n");
        Made("consume(first(), abs(n))", "abs(n)", "\tvar x = abs(n)\n\tconsume(first(), x)\n");
        Made("hp = first()", "first()", "\tvar x = first()\n\thp = x\n");
        Made("print(second() if n else 1)", "n", "\tvar x = n\n\tprint(second() if x else 1)\n");
    }

    [Fact]
    public void What_the_engines_api_says_of_a_call_decides_what_it_may_run_ahead_of()
    {
        const string Prelude = "extends Node\n\nstatic var counted = 5\nvar held := 5\n\n\nfunc first():\n\treturn 1\n\n\nfunc consume(a, b):\n\tprint(a, b)\n\n\n";
        string? Refused(string statement, string selected)
        {
            string text = Prelude + $"func f(v: Vector2, list: Array, callback: Callable):\n\t{statement}\n";
            IReadOnlyList<GDScriptRead> scripts = ReadWith(Engine, ("res://main.gd", text));
            return GDScriptRefactorings.ExtractVariable(scripts[0].Analysis, Over(text, selected, text.Split(selected).Length - 2), "x").Refused;
        }
        // Of a value of its own a const method tells nothing, and made of nothing else neither does a static one or a new engine object.
        Assert.Null(Refused("consume(first(), v.length())", "v.length()"));
        Assert.Null(Refused("consume(first(), Vector2.from_angle(1.0))", "Vector2.from_angle(1.0)"));
        Assert.Null(Refused("consume(first(), Timer.new())", "Timer.new()"));
        // A const method of what others share reads it, and one that runs what it is given acts.
        Assert.Contains("'first()' runs first", Refused("consume(first(), list.size())", "list.size()"));
        Assert.Contains("'first()' runs first", Refused("consume(first(), get_parent())", "get_parent()"));
        Assert.Contains("'list.size()' runs first", Refused("consume(list.size(), first())", "first()"));
        Assert.Null(Refused("consume(list.size(), get_parent())", "get_parent()"));
        Assert.Contains("'first()' runs first", Refused("consume(first(), list.pop_back())", "list.pop_back()"));
        Assert.Contains("'first()' runs first", Refused("consume(first(), list.map(callback))", "list.map(callback)"));
        Assert.Contains("'first()' runs first", Refused("consume(first(), callback.call())", "callback.call()"));
        Assert.Contains("'first()' runs first", Refused("consume(first(), add_child(self))", "add_child(self)"));
        // Godot reads an object's variable as the call runs, a static variable or an index where it stands.
        Assert.Null(Refused("consume(held, first())", "first()"));
        Assert.Contains("'counted' runs first", Refused("consume(counted, first())", "first()"));
        Assert.Contains("'list[0]' runs first", Refused("consume(list[0], first())", "first()"));
        Assert.Null(Refused("consume(counted, first())", "counted"));
        Assert.Contains("'first()' runs first", Refused("consume(held, first())", "held"));
    }

    [Fact]
    public void Inline_variable_puts_its_value_at_each_read_in_parentheses_where_it_needs_them()
    {
        string text = "func f(a):\n\tvar b = a + 1\n\tprint(b * 2)\n\tprint(b)\n";
        Assert.Equal("func f(a):\n\tprint((a + 1) * 2)\n\tprint(a + 1)\n", After(GDScriptRefactorings.InlineVariable(One(text), text.IndexOf("b =", StringComparison.Ordinal)), "res://main.gd", text));
    }

    [Fact]
    public void Inline_variable_is_refused_where_its_value_read_anew_would_run_in_a_loop_under_a_branch_or_after_a_call()
    {
        string text = "func compute() -> int:\n\treturn 1\n\n\nfunc a(cond: bool) -> void:\n\tvar x := compute()\n\tif cond:\n\t\tprint(x)\n\n\n"
            + "func b(n: int) -> void:\n\tvar y := n + 1\n\tfor i in 3:\n\t\tprint(y, i)\n\n\nfunc c() -> void:\n\tvar z := compute()\n\tprint(\"before\")\n\tprint(z)\n\n\n"
            + "func d(cond: bool) -> bool:\n\tvar w := compute()\n\treturn cond and w > 0\n\n\nfunc e(n: int) -> void:\n\tvar v := n * 2\n\tif n > 0:\n\t\tprint(v)\n\n\n"
            + "func f() -> int:\n\tvar u := compute()\n\treturn u + 1\n";
        GDScriptAnalysis analysis = One(text);
        Assert.Contains("read anew in a branch", GDScriptRefactorings.InlineVariable(analysis, text.IndexOf("x :=", StringComparison.Ordinal)).Refused);
        Assert.Contains("read anew in a loop", GDScriptRefactorings.InlineVariable(analysis, text.IndexOf("y :=", StringComparison.Ordinal)).Refused);
        Assert.Contains("after a call", GDScriptRefactorings.InlineVariable(analysis, text.IndexOf("z :=", StringComparison.Ordinal)).Refused);
        Assert.Contains("read anew in a branch", GDScriptRefactorings.InlineVariable(analysis, text.IndexOf("w :=", StringComparison.Ordinal)).Refused);
        Assert.Contains("\tif n > 0:\n\t\tprint(n * 2)\n", After(GDScriptRefactorings.InlineVariable(analysis, text.IndexOf("v :=", StringComparison.Ordinal)), "res://main.gd", text));
        Assert.Contains("\treturn compute() + 1\n", After(GDScriptRefactorings.InlineVariable(analysis, text.IndexOf("u :=", StringComparison.Ordinal)), "res://main.gd", text));
    }

    [Fact]
    public void Inline_variable_is_refused_where_it_is_assigned_again_or_what_it_reads_changes()
    {
        string again = "func f(a):\n\tvar b = a\n\tb = 3\n\tprint(b)\n";
        Assert.Contains("assigned again", GDScriptRefactorings.InlineVariable(One(again), again.IndexOf("b =", StringComparison.Ordinal)).Refused);
        string changed = "func f(a):\n\tvar b = a\n\ta = 3\n\tprint(b)\n";
        GDScriptRefactored refused = GDScriptRefactorings.InlineVariable(One(changed), changed.IndexOf("b =", StringComparison.Ordinal));
        Assert.Contains("'a', which its value reads, is assigned", refused.Refused);
        Assert.Single(refused.Places);
    }

    [Fact]
    public void Inline_variable_is_refused_where_what_its_value_reads_through_a_name_changes_before_a_use()
    {
        string Refused(string body)
        {
            string text = $"var hp = 5\n\n\nfunc bump():\n\thp = 100\n\treturn 1\n\n\nfunc f(values, other):\n{body}";
            return GDScriptRefactorings.InlineVariable(One(text), text.IndexOf("old =", StringComparison.Ordinal)).Refused ?? "made";
        }
        Assert.Contains("'values', which its value reads, is changed", Refused("\tvar old = values[0]\n\tvalues[0] = 2\n\tprint(old)\n"));
        Assert.Contains("'values', which its value reads, is changed", Refused("\tvar old = values.size\n\tvalues.size = 2\n\tprint(old)\n"));
        Assert.Contains("'values', which its value reads, is changed", Refused("\tvar old = values[0]\n\tvalues[0] += 2\n\tprint(old)\n"));
        Assert.Contains("'values', which its value reads, is changed", Refused("\tvar old = values.a[0]\n\tvalues.a[1].b = 2\n\tprint(old)\n"));
        // Through another name, or by a call on it.
        Assert.Contains("may be changed before a use", Refused("\tvar old = values[0]\n\tother[0] = 2\n\tprint(old)\n"));
        Assert.Contains("after a call", Refused("\tvar old = values[0]\n\tvalues.clear()\n\tprint(old)\n"));
        Assert.Contains("read anew in a branch", Refused("\tvar old = values[0]\n\tif other:\n\t\tprint(old)\n"));
        // Godot reads a plain variable only as the call it is passed to runs, so inlined it would come after the call beside it.
        Assert.Contains("after a call", Refused("\tvar old = hp\n\tprint(old, bump())\n"));
        // A call moved past a read of what it may change.
        Assert.Contains("'hp' reads what it may change", Refused("\tvar old = bump()\n\tvar seen = hp\n\tprint(old, seen)\n"));
        // An array read twice is one array, its value written twice two.
        Assert.Contains("makes a new one each time", Refused("\tvar old = []\n\told.append(1)\n\tprint(old)\n"));
        Assert.Contains("makes a new one each time", Refused("\tvar old = func(): pass\n\tvalues.connect(old)\n\tvalues.disconnect(old)\n"));

        string kept = "func f(values):\n\tvar old = values[0]\n\tvalues[0] = old + 1\n";
        Assert.Equal("func f(values):\n\tvalues[0] = values[0] + 1\n", After(GDScriptRefactorings.InlineVariable(One(kept), kept.IndexOf("old =", StringComparison.Ordinal)), "res://main.gd", kept));
        string alias = "func f(values):\n\tvar same = values\n\tsame.append(1)\n\tprint(same)\n";
        Assert.Equal("func f(values):\n\tvalues.append(1)\n\tprint(values)\n", After(GDScriptRefactorings.InlineVariable(One(alias), alias.IndexOf("same =", StringComparison.Ordinal)), "res://main.gd", alias));
        string once = "func f():\n\tvar made = [1]\n\tprint(made)\n";
        Assert.Equal("func f():\n\tprint([1])\n", After(GDScriptRefactorings.InlineVariable(One(once), once.IndexOf("made =", StringComparison.Ordinal)), "res://main.gd", once));
    }

    [Fact]
    public void Extract_function_moves_the_statements_into_a_function_after_theirs_with_the_locals_they_read_as_parameters()
    {
        string text = "func f(a: int) -> void:\n\tvar b = a + 1\n\tvar c = b * 2\n\tprint(c)\n\tprint(b)\n";
        Assert.Equal("func f(a: int) -> void:\n\tvar b = a + 1\n\tshow(b)\n\tprint(b)\n\n\nfunc show(b) -> void:\n\tvar c = b * 2\n\tprint(c)\n",
            After(GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var c = b * 2\n\tprint(c)"), "show"), "res://main.gd", text));
        // The one local it sets that is read after it is returned.
        Assert.Equal("func f(a: int) -> void:\n\tvar b = a + 1\n\tvar c = double(b)\n\tprint(c)\n\tprint(b)\n\n\nfunc double(b):\n\tvar c = b * 2\n\treturn c\n",
            After(GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var c = b * 2"), "double"), "res://main.gd", text));
    }

    [Fact]
    public void Extract_function_returns_the_type_the_analyzer_reads_of_the_local_it_returns()
    {
        string text = "func f() -> void:\n\tvar c = 5\n\tprint(c)\n";
        Assert.Equal("func f() -> void:\n\tvar c = make_c()\n\tprint(c)\n\n\nfunc make_c() -> int:\n\tvar c = 5\n\treturn c\n",
            After(GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var c = 5"), "make_c"), "res://main.gd", text));
    }

    [Fact]
    public void Extract_function_is_refused_where_the_statements_return_or_set_two_locals_read_after()
    {
        string text = "func f(a):\n\tvar b = a + 1\n\tvar c = b * 2\n\tprint(c)\n\tprint(b)\n\treturn b\n";
        Assert.Contains("'c' and 'b'", GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var b = a + 1\n\tvar c = b * 2"), "two").Refused?.Replace("'b' and 'c'", "'c' and 'b'"));
        Assert.Contains("return", GDScriptRefactorings.ExtractFunction(One(text), Over(text, "print(b)\n\treturn b"), "out").Refused);
    }

    [Fact]
    public void Introduce_parameter_makes_the_expression_a_parameter_with_it_as_the_default()
    {
        string text = "func f(a):\n\treturn a + 10\n";
        Assert.Equal("func f(a, step := 10):\n\treturn a + step\n", After(GDScriptRefactorings.IntroduceParameter(One(text), Over(text, "10"), "step"), "res://main.gd", text));
        string local = "func f(a):\n\tvar b = 2\n\treturn a + b\n";
        Assert.Contains("reads 'b'", GDScriptRefactorings.IntroduceParameter(One(local), Over(local, "a + b"), "sum").Refused);
    }

    [Fact]
    public void Introduce_parameter_is_refused_for_what_a_default_would_run_at_another_time_or_share_between_rounds()
    {
        string Introduced(string body, string selected)
        {
            string text = $"const LIMIT = 3\nenum Mood {{ CALM }}\nvar hp = 5\n\n\nfunc Trigger():\n\treturn 1\n\n\nfunc go(flag):\n{body}";
            GDScriptRefactored made = GDScriptRefactorings.IntroduceParameter(One(text), Over(text, selected, text.Split(selected).Length - 2), "value");
            return made.Refused ?? After(made, "res://main.gd", text);
        }
        Assert.Contains("read as the function is called", Introduced("\tif flag:\n\t\tprint(Trigger())\n", "Trigger()"));
        Assert.Contains("read as the function is called", Introduced("\tprint(Stranger())\n", "Stranger()"));
        Assert.Contains("read as the function is called", Introduced("\tprint(randi())\n", "randi()"));
        Assert.Contains("it reads 'hp'", Introduced("\thp = 2\n\tprint(hp + 1)\n", "hp + 1"));
        Assert.Contains("it reads 'flag'", Introduced("\tprint(flag.size + 1)\n", "flag.size + 1"));
        Assert.Contains("it reads 'Hud'", Introduced("\tprint(Hud.score + 1)\n", "Hud.score + 1"));
        Assert.Contains("a new one each time round", Introduced("\tfor i in 2:\n\t\tvar a = [1]\n\t\ta.append(2)\n", "[1]"));
        Assert.Contains("a new one each time round", Introduced("\twhile flag:\n\t\tvar a = {}\n\t\ta[1] = 2\n", "{}"));
        Assert.Contains("a new one each time round", Introduced("\tfor i in 2:\n\t\tvar a = PackedInt32Array()\n", "PackedInt32Array()"));
        Assert.Contains("func go(flag, value := [1]):", Introduced("\tvar a = [1]\n\ta.append(2)\n", "[1]"));
        Assert.Contains("func go(flag, value := Vector2(1, 2)):", Introduced("\tif flag:\n\t\tprint(Vector2(1, 2))\n", "Vector2(1, 2)"));
        Assert.Contains("func go(flag, value = abs(-3)):", Introduced("\tfor i in 2:\n\t\tprint(abs(-3))\n", "abs(-3)"));
        Assert.Contains("func go(flag, value := LIMIT):", Introduced("\tprint(LIMIT)\n", "LIMIT"));
        Assert.Contains("Mood.CALM):", Introduced("\tprint(Mood.CALM)\n", "Mood.CALM"));
        Assert.Contains("PI):", Introduced("\tprint(PI)\n", "PI"));
        Assert.Contains("Vector2.ZERO):", Introduced("\tprint(Vector2.ZERO)\n", "Vector2.ZERO"));
    }

    private const string Helper = "class_name Helper\nextends Node\n\n\nfunc twice(x):\n\treturn x * 2\n\n\nfunc run():\n\tprint(twice(3) + 1)\n";
    private const string User = "extends Helper\n\n\nfunc go():\n\tvar y = twice(4 + 1)\n\tprint(y)\n";

    [Fact]
    public void Inline_function_gives_each_call_in_every_script_its_body_and_takes_it_out()
    {
        IReadOnlyList<GDScriptRead> scripts = Read(("res://helper.gd", Helper), ("res://user.gd", User));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], Helper.IndexOf("twice", StringComparison.Ordinal))!;
        GDScriptRefactored made = GDScriptRefactorings.InlineFunction(scripts, target, []);
        Assert.Equal("class_name Helper\nextends Node\n\n\nfunc run():\n\tprint((3 * 2) + 1)\n", After(made, "res://helper.gd", Helper));
        Assert.Equal("extends Helper\n\n\nfunc go():\n\tvar y = (4 + 1) * 2\n\tprint(y)\n", After(made, "res://user.gd", User));
    }

    [Fact]
    public void Inline_function_is_refused_where_the_arguments_would_not_run_as_the_call_ran_them()
    {
        string Inlined(string function, string call)
        {
            string text = $"var hp = 5\n\n\nfunc first():\n\thp = 1\n\treturn 1\n\n\nfunc second():\n\treturn 2\n\n\n{function}\n\n\nfunc go(n):\n\tprint({call})\n";
            IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
            GDScriptRefactored made = GDScriptRefactorings.InlineFunction(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("func subject", StringComparison.Ordinal) + 5)!, []);
            return made.Refused ?? After(made, "res://main.gd", text)[text.IndexOf("func subject", StringComparison.Ordinal)..];
        }
        const string Subtract = "func subject(a, b):\n\treturn b - a";
        Assert.Contains("'first()' would run after 'second()'", Inlined(Subtract, "subject(first(), second())"));
        Assert.Contains("'hp + 0' would run after 'first()'", Inlined(Subtract, "subject(hp + 0, first())"));
        Assert.Contains("'second()' would run after 'first()', which the function runs", Inlined("func subject(a):\n\treturn first() + a", "subject(second())"));
        // Godot reads a plain variable only as what takes it runs, so here after the call beside it, where the argument was read before.
        Assert.Contains("'hp' would run after 'first()', which the function runs", Inlined("func subject(a):\n\treturn a + first()", "subject(hp)"));
        // And as the inlined call's own argument, read before the call beside it, where it was read after.
        Assert.Contains("'first()' would run after 'hp'", Inlined("func subject(a, b):\n\treturn str(a) + b", "subject(hp, first())"));
        Assert.Contains("print(hp + first())", Inlined("func subject(a, b):\n\treturn a + b", "subject(hp, first())"));
        Assert.Contains("'second()' would run only as the function's body says", Inlined("func subject(a, b):\n\treturn a and b", "subject(first(), second())"));
        Assert.Contains("would be read 2 times", Inlined("func subject(a = first()):\n\treturn a + a", "subject()"));
        Assert.Contains("print(first() - second())", Inlined("func subject(a, b):\n\treturn a - b", "subject(first(), second())"));
        Assert.Contains("print(2 - first())", Inlined(Subtract, "subject(first(), 2)"));
        Assert.Contains("print(n - first())", Inlined(Subtract, "subject(first(), n)"));
        Assert.Contains("print(hp + 1)", Inlined("func subject(a):\n\treturn a + 1", "subject(hp)"));
    }

    [Fact]
    public void Safe_delete_takes_out_a_member_nothing_names_and_refuses_one_a_script_or_scene_still_does()
    {
        string text = "extends Node\n\n\nfunc unused():\n\tpass\n\n\nfunc used():\n\tpass\n\n\nfunc _ready():\n\tused()\n\tcall(\"used\")\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptRefactored deleted = GDScriptRefactorings.SafeDelete(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("unused", StringComparison.Ordinal))!, []);
        Assert.Equal("extends Node\n\n\nfunc used():\n\tpass\n\n\nfunc _ready():\n\tused()\n\tcall(\"used\")\n", After(deleted, "res://main.gd", text));
        GDScriptRefactored refused = GDScriptRefactorings.SafeDelete(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("func used", StringComparison.Ordinal) + 5)!,
            [new GDScriptPlace("res://main.tscn", 10, 14, "a connection calls it")]);
        Assert.Contains("still used", refused.Refused);
        Assert.Equal(["a connection calls it", "calls it", "names it in a string"], refused.Places.Select(p => p.What).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Safe_delete_is_refused_for_a_function_only_godot_calls_and_a_value_that_would_no_longer_run()
    {
        string text = "extends Node2D\n\nvar unused = record()\nvar plain = [1, 2]\nstatic var counted = record()\nvar shape = Vector2(1, 2)\n\n\nfunc record():\n\treturn 1\n\n\n"
            + "func _ready():\n\tpass\n\n\nfunc _draw_shape():\n\tpass\n\n\nfunc _iter_init(arg):\n\treturn true\n\n\nfunc _helper():\n\tpass\n";
        IReadOnlyList<GDScriptRead> scripts = ReadWith(Engine, ("res://main.gd", text));
        GDScriptRefactored Deleted(string name) => GDScriptRefactorings.SafeDelete(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf(name, StringComparison.Ordinal))!, []);
        Assert.Contains("'_ready' is Godot's own, which Godot calls by its name", Deleted("_ready").Refused);
        Assert.Contains("'_draw_shape' overrides Node2D's own", Deleted("_draw_shape").Refused);
        Assert.Contains("'_iter_init' is Godot's own", Deleted("_iter_init").Refused);
        Assert.Contains("'unused''s value calls, which would no longer run: record()", Deleted("unused").Refused);
        Assert.Contains("'counted''s value calls", Deleted("counted").Refused);
        Assert.Null(Deleted("plain").Refused);
        Assert.Null(Deleted("shape").Refused);
        Assert.Null(Deleted("_helper").Refused);
        // Without the engine's API its own names still hold.
        IReadOnlyList<GDScriptRead> alone = Read(("res://main.gd", text));
        Assert.Contains("is Godot's own", GDScriptRefactorings.SafeDelete(alone, GDScriptRefactorings.MemberAt(alone, alone[0], text.IndexOf("_ready", StringComparison.Ordinal))!, []).Refused);

        const string Base = "class_name Base\nextends Node\n\n\nfunc hook():\n\tpass\n\n\nfunc run():\n\thook()\n";
        const string Child = "extends Base\n\n\nfunc hook():\n\tprint(\"mine\")\n";
        IReadOnlyList<GDScriptRead> family = Read(("res://base.gd", Base), ("res://child.gd", Child));
        GDScriptRefactored overriding = GDScriptRefactorings.SafeDelete(family, GDScriptRefactorings.MemberAt(family, family[1], Child.IndexOf("hook", StringComparison.Ordinal))!, []);
        Assert.Contains("res://base.gd: declares it too", overriding.Refused);
    }

    private const string Hitter = "extends Node\n\n\nfunc hit(target, amount = 1):\n\tprint(target, amount)\n\n\nfunc run():\n\thit(\"a\")\n\thit(\"b\", 3)\n\tcall(\"hit\", \"c\", 4)\n";

    [Fact]
    public void Change_signature_renames_reorders_and_adds_through_every_call_and_string_naming_it()
    {
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", Hitter));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], Hitter.IndexOf("hit", StringComparison.Ordinal))!;
        Assert.Equal(["target", "amount"], GDScriptRefactorings.ParametersOf(scripts[0], target).Select(p => p.Name));
        GDScriptRefactored made = GDScriptRefactorings.ChangeSignature(scripts, target, "strike",
            [new GDScriptParameterChange(0, "who", string.Empty, string.Empty, string.Empty), new GDScriptParameterChange(-1, "times", "int", string.Empty, "2"),
                new GDScriptParameterChange(1, "amount", string.Empty, "1", string.Empty)], []);
        Assert.Equal("extends Node\n\n\nfunc strike(who, times: int, amount = 1):\n\tprint(who, amount)\n\n\nfunc run():\n\tstrike(\"a\", 2)\n\tstrike(\"b\", 2, 3)\n\tcall(\"strike\", \"c\", 2, 4)\n",
            After(made, "res://main.gd", Hitter));
    }

    [Theory]
    [InlineData("int", true)]
    [InlineData("Node", true)]
    [InlineData("Outer.Inner", true)]
    [InlineData("void", true)]
    [InlineData("Array", true)]
    [InlineData("Array[Node]", true)]
    [InlineData("Array[Outer.Inner]", true)]
    [InlineData("Dictionary[String, int]", true)]
    [InlineData("Dictionary[String,int]", true)]
    [InlineData("Array[", false)]
    [InlineData("Array[Node]]", false)]
    [InlineData("Array[Array[int]]", false)]
    [InlineData("1x", false)]
    [InlineData("int = 3", false)]
    [InlineData("int:\n\tpass\n\n\nfunc g() -> int", false)]
    [InlineData("A.", false)]
    public void A_type_is_what_gdscripts_parser_reads_as_one(string text, bool type) => Assert.Equal(type, GDScriptRefactorings.IsType(text));

    [Fact]
    public void Change_signature_keeps_a_parameter_of_a_typed_collection_as_written()
    {
        string text = "func count(nodes: Array[Node], table: Dictionary[String, int] = {}) -> int:\n\treturn nodes.size() + table.size()\n\n\nfunc go():\n\tprint(count([]))\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], 5)!;
        IReadOnlyList<GDScriptParameterChange> shown = GDScriptRefactorings.ParametersOf(scripts[0], target);
        Assert.Equal(["Array[Node]", "Dictionary[String, int]"], shown.Select(p => p.Type));
        Assert.Contains("func tally(nodes: Array[Node], table: Dictionary[String, int] = {}) -> int:\n", After(GDScriptRefactorings.ChangeSignature(scripts, target, "tally", shown, []), "res://main.gd", text));
    }

    [Fact]
    public void Change_signature_is_refused_where_a_calls_arguments_would_not_run_as_they_did()
    {
        const string Subtract = "var hp = 5\n\n\nfunc first():\n\thp = 1\n\treturn 1\n\n\nfunc second():\n\treturn 2\n\n\nfunc subtract(a, b):\n\treturn a - b\n\n\n";
        GDScriptParameterChange a = new(0, "a", string.Empty, string.Empty, string.Empty);
        GDScriptParameterChange b = new(1, "b", string.Empty, string.Empty, string.Empty);
        string Changed(string call, params GDScriptParameterChange[] parameters)
        {
            string text = $"{Subtract}func go(n):\n\t{call}\n";
            IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", text));
            GDScriptRefactored made = GDScriptRefactorings.ChangeSignature(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], text.IndexOf("func subtract", StringComparison.Ordinal) + 5)!, "subtract", parameters, []);
            return made.Refused ?? After(made, "res://main.gd", text);
        }
        Assert.Contains("passes 'first()' and 'second()', which would run the other way round", Changed("subtract(first(), second())", b, a));
        Assert.Contains("passes 'first()' and 'second()', which would run the other way round", Changed("call(\"subtract\", first(), second())", b, a));
        Assert.Contains("passes 'n.x' and 'first()', which would run the other way round", Changed("subtract(n.x, first())", b, a));
        Assert.Contains("\tsubtract(2, first())\n", Changed("subtract(first(), 2)", b, a));
        Assert.Contains("\tsubtract(n, first())\n", Changed("subtract(first(), n)", b, a));
        // Godot reads a plain variable as the call runs, before or after the call beside it.
        Assert.Contains("\tsubtract(first(), hp)\n", Changed("subtract(hp, first())", b, a));
        string dropped = "func f(a, b):\n\tprint(a)\n\n\nfunc make():\n\treturn 1\n\n\nfunc go(n):\n\tf(n, make())\n";
        IReadOnlyList<GDScriptRead> dropping = Read(("res://main.gd", dropped));
        Assert.Contains("passes 'make()', which would no longer run", GDScriptRefactorings.ChangeSignature(dropping, GDScriptRefactorings.MemberAt(dropping, dropping[0], 5)!, "f", [a], []).Refused);
        string emits = "signal hit(a, b)\n\n\nfunc first():\n\treturn 1\n\n\nfunc second():\n\treturn 2\n\n\nfunc go():\n\thit.emit(first(), second())\n";
        IReadOnlyList<GDScriptRead> emitting = Read(("res://main.gd", emits));
        Assert.Contains("which would run the other way round", GDScriptRefactorings.ChangeSignalSignature(emitting, GDScriptRefactorings.MemberAt(emitting, emitting[0], 8)!, "hit", [b, a], [], []).Refused);
        // A C# call's arguments, read by their text.
        Assert.Contains("passes 'First()' and 'Second()'", GDScriptRefactorings.WhyNotRearranged(["First()", "Second()"], [b, a]));
        Assert.Contains("passes 'hp' and 'Second()'", GDScriptRefactorings.WhyNotRearranged(["hp", "Second()"], [b, a]));
        Assert.Null(GDScriptRefactorings.WhyNotRearranged(["1", "Second()"], [b, a]));
        Assert.Null(GDScriptRefactorings.WhyNotRearranged(["\"a(b)\"", "Second()"], [b, a]));
        Assert.Null(GDScriptRefactorings.WhyNotRearranged(["First()", "Second()"], [a, b]));
    }

    [Fact]
    public void Change_signature_is_refused_where_a_removed_parameter_is_read_or_a_use_is_unseen()
    {
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", Hitter));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], Hitter.IndexOf("hit", StringComparison.Ordinal))!;
        Assert.Contains("'target' is still read", GDScriptRefactorings.ChangeSignature(scripts, target, "hit", [new GDScriptParameterChange(1, "amount", string.Empty, "1", string.Empty)], []).Refused);
        string dynamic = Hitter + "\n\nfunc later(other, method):\n\tother.hit(1)\n\tcall(method)\n";
        IReadOnlyList<GDScriptRead> unseen = Read(("res://main.gd", dynamic));
        GDScriptRefactored refused = GDScriptRefactorings.ChangeSignature(unseen, GDScriptRefactorings.MemberAt(unseen, unseen[0], dynamic.IndexOf("hit", StringComparison.Ordinal))!, "hit",
            [new GDScriptParameterChange(1, "amount", string.Empty, "1", string.Empty), new GDScriptParameterChange(0, "target", string.Empty, "null", string.Empty)], []);
        Assert.Contains("can't follow every use", refused.Refused);
        Assert.Equal(2, refused.Places.Count);
    }

    private const string Signaller = "extends Node\n\nsignal hurt(amount, source)\n\n\nfunc _ready():\n\thurt.connect(on_hurt)\n\tconnect(\"hurt\", Callable(self, \"on_hurt_too\"))\n"
        + "\thurt.emit(3, self)\n\temit_signal(\"hurt\", 4, null)\n\n\nfunc on_hurt(amount, source):\n\tprint(amount)\n\n\nfunc on_hurt_too(a, b):\n\tprint(b)\n\n\nfunc on_scene(x, y):\n\tprint(x)\n";

    [Fact]
    public void Change_signature_of_a_signal_reorders_and_adds_through_every_emit_and_every_handler_connected()
    {
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", Signaller));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], Signaller.IndexOf("hurt", StringComparison.Ordinal))!;
        Assert.Equal(["amount", "source"], GDScriptRefactorings.SignalParametersOf(scripts[0], target).Select(p => p.Name));
        GDScriptRefactored made = GDScriptRefactorings.ChangeSignalSignature(scripts, target, "damaged",
            [new GDScriptParameterChange(1, "source", string.Empty, string.Empty, string.Empty), new GDScriptParameterChange(0, "amount", string.Empty, string.Empty, string.Empty),
                new GDScriptParameterChange(-1, "kind", "String", string.Empty, "\"hit\"")],
            [new GDScriptHandler("res://main.gd", "on_scene", new GDScriptPlace("main.tscn:9", 0, 0, "a scene's connection names it"))], []);
        Assert.Equal("extends Node\n\nsignal damaged(source, amount, kind: String)\n\n\nfunc _ready():\n\tdamaged.connect(on_hurt)\n\tconnect(\"damaged\", Callable(self, \"on_hurt_too\"))\n"
            + "\tdamaged.emit(self, 3, \"hit\")\n\temit_signal(\"damaged\", null, 4, \"hit\")\n\n\nfunc on_hurt(source, amount, kind: String):\n\tprint(amount)\n\n\n"
            + "func on_hurt_too(b, a, kind: String):\n\tprint(b)\n\n\nfunc on_scene(y, x, kind: String):\n\tprint(x)\n", After(made, "res://main.gd", Signaller));
    }

    [Fact]
    public void Change_signature_of_a_signal_is_refused_where_a_handler_can_not_follow()
    {
        string bound = Signaller.Replace("hurt.connect(on_hurt)", "hurt.connect(on_hurt.bind(1))", StringComparison.Ordinal);
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", bound));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], bound.IndexOf("hurt", StringComparison.Ordinal))!;
        IReadOnlyList<GDScriptParameterChange> swapped = [new GDScriptParameterChange(1, "source", string.Empty, string.Empty, string.Empty),
            new GDScriptParameterChange(0, "amount", string.Empty, string.Empty, string.Empty)];
        Assert.Contains("connects a handler it can't follow", GDScriptRefactorings.ChangeSignalSignature(scripts, target, "hurt", swapped, [], []).Refused);
        // A rename alone follows every use as it is.
        Assert.Null(GDScriptRefactorings.ChangeSignalSignature(scripts, target, "harmed",
            [new GDScriptParameterChange(0, "amount", string.Empty, string.Empty, string.Empty), new GDScriptParameterChange(1, "source", string.Empty, string.Empty, string.Empty)], [], []).Refused);
        IReadOnlyList<GDScriptRead> csharp = Read(("res://main.gd", Signaller));
        Assert.Contains("C# connects it", GDScriptRefactorings.ChangeSignalSignature(csharp, GDScriptRefactorings.MemberAt(csharp, csharp[0], Signaller.IndexOf("hurt", StringComparison.Ordinal))!,
            "hurt", swapped, [], [new GDScriptPlace("Main.cs:12", 0, 0, "C# connects it")]).Refused);
    }

    private const string Healer = "extends Node\n\n\nstatic func clamp_health(value):\n\treturn value * 2\n\n\nfunc _ready():\n\tprint(clamp_health(120))\n\tvar f = clamp_health\n";

    [Fact]
    public void Move_takes_a_static_function_to_a_class_named_script_its_calls_and_values_called_by_its_class_name()
    {
        string utils = "class_name Utils\nextends RefCounted\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", Healer), ("res://utils.gd", utils));
        GDScriptRefactored made = GDScriptRefactorings.Move(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], Healer.IndexOf("clamp_health", StringComparison.Ordinal))!,
            "res://utils.gd", new Dictionary<string, string>(), []);
        Assert.Equal("extends Node\n\n\nfunc _ready():\n\tprint(Utils.clamp_health(120))\n\tvar f = Utils.clamp_health\n", After(made, "res://main.gd", Healer));
        Assert.Equal("class_name Utils\nextends RefCounted\n\n\nstatic func clamp_health(value):\n\treturn value * 2\n", After(made, "res://utils.gd", utils));
    }

    [Fact]
    public void Move_takes_a_function_to_an_autoloads_script_or_one_its_class_extends_and_refuses_what_can_not_follow()
    {
        string player = "extends Node\n\nvar score := 0\nvar lives := 3\n\n\nfunc add(points):\n\tscore += points\n\n\nfunc lose():\n\tlives -= 1\n\n\nfunc _ready():\n\tadd(5)\n\tself.add(1)\n";
        string game = "extends Node\n\nvar score := 0\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://player.gd", player), ("res://game.gd", game));
        Dictionary<string, string> autoloads = new() { ["Game"] = "res://game.gd" };
        GDScriptRefactored made = GDScriptRefactorings.Move(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], player.IndexOf("add", StringComparison.Ordinal))!, "res://game.gd", autoloads, []);
        Assert.EndsWith("func _ready():\n\tGame.add(5)\n\tGame.add(1)\n", After(made, "res://player.gd", player), StringComparison.Ordinal);
        Assert.Equal("extends Node\n\nvar score := 0\n\n\nfunc add(points):\n\tscore += points\n", After(made, "res://game.gd", game));
        Assert.Contains("reads 'lives', which res://game.gd lacks",
            GDScriptRefactorings.Move(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], player.IndexOf("lose", StringComparison.Ordinal))!, "res://game.gd", autoloads, []).Refused);
        Assert.Contains("on an object of its own script",
            GDScriptRefactorings.Move(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], player.IndexOf("add", StringComparison.Ordinal))!, "res://game.gd", new Dictionary<string, string>(), []).Refused);

        string based = "class_name Base\nextends Node\n\nvar score := 0\n";
        string child = "extends Base\n\n\nfunc add(points):\n\tscore += points\n\n\nfunc _ready():\n\tadd(5)\n";
        IReadOnlyList<GDScriptRead> family = Read(("res://base.gd", based), ("res://child.gd", child));
        GDScriptRefactored up = GDScriptRefactorings.Move(family, GDScriptRefactorings.MemberAt(family, family[1], child.IndexOf("add", StringComparison.Ordinal))!, "res://base.gd",
            new Dictionary<string, string>(), [new GDScriptPlace("main.tscn:7", 0, 0, "a scene's connection names it")]);
        Assert.Equal("extends Base\n\n\nfunc _ready():\n\tadd(5)\n", After(up, "res://child.gd", child));
        Assert.Equal("class_name Base\nextends Node\n\nvar score := 0\n\n\nfunc add(points):\n\tscore += points\n", After(up, "res://base.gd", based));
    }

    [Fact]
    public void Moving_or_extracting_code_leaves_a_multi_line_strings_lines_as_they_are()
    {
        const string Words = "\"\"\"\n    four spaces\n\ta tab\n\n        eight\n no\"\"\"";
        string spaces = $"static func say():\n    var words = {Words}\n    if words:\n        return (words\n            + {Words})\n";
        string own = $"class_name Own\nextends Node\n\n\n{spaces}";
        const string Tabs = "class_name Other\nextends Node\n\n\nfunc kept():\n\tpass\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://own.gd", own), ("res://other.gd", Tabs));
        GDScriptRefactored moved = GDScriptRefactorings.Move(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], own.IndexOf("say", StringComparison.Ordinal))!, "res://other.gd",
            new Dictionary<string, string>(), []);
        string landed = After(moved, "res://other.gd", Tabs);
        Assert.Contains($"static func say():\n\tvar words = {Words}\n\tif words:\n\t\treturn (words\n\t\t\t+ {Words})\n", landed);

        string nested = $"func f():\n\tif true:\n\t\tvar words = {Words}\n\t\tprint(words)\n";
        string extracted = After(GDScriptRefactorings.ExtractFunction(One(nested), Over(nested, $"var words = {Words}\n\t\tprint(words)"), "speak"), "res://main.gd", nested);
        Assert.EndsWith($"func speak() -> void:\n\tvar words = {Words}\n\tprint(words)\n", extracted);

        // A string's lines are no indentation for the script's unit to be read from.
        string tabbed = $"const WORDS = {Words}\n\n\nstatic func say():\n\tprint(WORDS)\n\tif WORDS:\n\t\tprint(2)\n";
        const string Spaced = "class_name Spaced\nextends Node\n\n\nfunc kept():\n  pass\n";
        IReadOnlyList<GDScriptRead> toSpaces = Read(("res://own.gd", "class_name Own\n" + tabbed.Replace("print(WORDS)", "print(1)").Replace("if WORDS", "if 1")), ("res://other.gd", Spaced));
        GDScriptTarget say = GDScriptRefactorings.MemberAt(toSpaces, toSpaces[0], toSpaces[0].Text.IndexOf("say", StringComparison.Ordinal))!;
        Assert.Contains("static func say():\n  print(1)\n  if 1:\n    print(2)\n", After(GDScriptRefactorings.Move(toSpaces, say, "res://other.gd", new Dictionary<string, string>(), []), "res://other.gd", Spaced));
    }

    [Fact]
    public void Rearranged_gives_a_csharp_calls_arguments_in_the_new_order()
    {
        IReadOnlyList<GDScriptParameterChange> was = [new(0, "target", string.Empty, string.Empty, string.Empty), new(1, "amount", string.Empty, "1", string.Empty)];
        Assert.Equal(["\"b\"", "2", "3"], GDScriptRefactorings.Rearranged(["\"b\"", "3"], was,
            [was[0], new GDScriptParameterChange(-1, "times", "int", string.Empty, "2"), was[1]]));
        Assert.Equal(["\"a\"", "2"], GDScriptRefactorings.Rearranged(["\"a\""], was, [was[0], new GDScriptParameterChange(-1, "times", "int", string.Empty, "2"), was[1]]));
    }

    /// <summary>A change across scripts is refused while one is read only in part, as a use after where reading stopped would
    /// go unseen and keep the old name.</summary>
    [Fact]
    public void No_change_is_made_across_a_script_read_only_in_part()
    {
        static GDScriptRead Read(string path, string text) => new(path, new GDScriptAnalyzer(GDScriptProject.Alone(null)).Analyze(GDScriptTree.Parse(text), path));
        GDScriptRead whole = Read("res://whole.gd", "var a = 1\n");
        GDScriptRead part = Read("res://part.gd", "var deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\nvar uses = a\n");
        Assert.Null(GDScriptRefactorings.WhyNotChanged([whole]));
        Assert.StartsWith("part.gd is nested too deeply", GDScriptRefactorings.WhyNotChanged([whole, part]), StringComparison.Ordinal);
    }
}

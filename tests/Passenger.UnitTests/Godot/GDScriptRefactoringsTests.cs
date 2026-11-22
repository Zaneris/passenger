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

    /// <summary>A rename follows every name Godot reads as the member, and is refused where a reading would
    /// change.</summary>
    [Fact]
    public void A_rename_reaches_every_name_godot_reads_and_refuses_what_it_would_change() => Cases.All(
        (nameof(A_rename_takes_a_name_godot_reads_whole_and_refuses_one_it_reads_as_none),
            A_rename_takes_a_name_godot_reads_whole_and_refuses_one_it_reads_as_none),
        (nameof(A_call_by_a_name_holds_back_a_rename_only_where_its_object_may_run_the_functions_class),
            A_call_by_a_name_holds_back_a_rename_only_where_its_object_may_run_the_functions_class),
        (nameof(A_member_a_string_names_for_get_set_or_is_connected_is_found_renamed_and_holds_back_a_safe_delete),
            A_member_a_string_names_for_get_set_or_is_connected_is_found_renamed_and_holds_back_a_safe_delete),
        (nameof(Renaming_an_override_renames_the_function_it_overrides_and_every_override_of_that_but_never_an_engines),
            Renaming_an_override_renames_the_function_it_overrides_and_every_override_of_that_but_never_an_engines),
        (nameof(Renaming_a_member_is_refused_where_a_use_would_read_a_local_of_the_new_name),
            Renaming_a_member_is_refused_where_a_use_would_read_a_local_of_the_new_name));

    /// <summary>Extract variable and introduce parameter move an expression only where it runs as it did.</summary>
    [Fact]
    public void Extract_and_introduce_refuse_what_would_run_otherwise() => Cases.All(
        (nameof(Extract_variable_declares_the_expression_before_its_statement_and_reads_it_there),
            Extract_variable_declares_the_expression_before_its_statement_and_reads_it_there),
        (nameof(Extract_variable_is_refused_where_the_expression_would_run_another_number_of_times),
            Extract_variable_is_refused_where_the_expression_would_run_another_number_of_times),
        (nameof(Introduce_parameter_makes_the_expression_a_parameter_with_it_as_the_default),
            Introduce_parameter_makes_the_expression_a_parameter_with_it_as_the_default));

    /// <summary>Inline variable and function, extract function and safe delete keep what runs and refuse where they
    /// would not.</summary>
    [Fact]
    public void Inline_extract_function_and_safe_delete_keep_what_runs() => Cases.All(
        (nameof(Inline_variable_puts_its_value_at_each_read_in_parentheses_where_it_needs_them),
            Inline_variable_puts_its_value_at_each_read_in_parentheses_where_it_needs_them),
        (nameof(Inline_variable_is_refused_where_it_is_assigned_again_or_what_it_reads_changes),
            Inline_variable_is_refused_where_it_is_assigned_again_or_what_it_reads_changes),
        (nameof(Extract_function_moves_the_statements_into_a_function_after_theirs_with_the_locals_they_read_as_parameters),
            Extract_function_moves_the_statements_into_a_function_after_theirs_with_the_locals_they_read_as_parameters),
        (nameof(Extract_function_is_refused_where_the_statements_return_or_set_two_locals_read_after),
            Extract_function_is_refused_where_the_statements_return_or_set_two_locals_read_after),
        (nameof(Inline_function_gives_each_call_in_every_script_its_body_and_takes_it_out),
            Inline_function_gives_each_call_in_every_script_its_body_and_takes_it_out),
        (nameof(Inline_function_is_refused_where_the_arguments_would_not_run_as_the_call_ran_them),
            Inline_function_is_refused_where_the_arguments_would_not_run_as_the_call_ran_them),
        (nameof(Safe_delete_takes_out_a_member_nothing_names_and_refuses_one_a_script_or_scene_still_does),
            Safe_delete_takes_out_a_member_nothing_names_and_refuses_one_a_script_or_scene_still_does));

    /// <summary>Change signature and move follow every call, emit, handler and string naming what they
    /// change.</summary>
    [Fact]
    public void Change_signature_and_move_follow_every_call_and_string() => Cases.All(
        (nameof(Change_signature_renames_reorders_and_adds_through_every_call_and_string_naming_it),
            Change_signature_renames_reorders_and_adds_through_every_call_and_string_naming_it),
        (nameof(Change_signature_is_refused_where_a_calls_arguments_would_not_run_as_they_did),
            Change_signature_is_refused_where_a_calls_arguments_would_not_run_as_they_did),
        (nameof(Change_signature_of_a_signal_reorders_and_adds_through_every_emit_and_every_handler_connected),
            Change_signature_of_a_signal_reorders_and_adds_through_every_emit_and_every_handler_connected),
        (nameof(Move_takes_a_static_function_to_a_class_named_script_its_calls_and_values_called_by_its_class_name),
            Move_takes_a_static_function_to_a_class_named_script_its_calls_and_values_called_by_its_class_name),
        (nameof(No_change_is_made_across_a_script_read_only_in_part), No_change_is_made_across_a_script_read_only_in_part));

    /// <summary>A new name is one GDScript declares as Godot's tokenizer reads one: a rename to a name holding marks
    /// (नमस्ते's virama and vowel sign) or a joiner goes through, and one holding a letter Godot reads in no name (U+2E2F)
    /// is refused.</summary>
    private void A_rename_takes_a_name_godot_reads_whole_and_refuses_one_it_reads_as_none()
    {
        const string Text = "extends Node\n\n\nfunc greet() -> void:\n\tpass\n\n\nfunc _ready() -> void:\n\tgreet()\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", Text));
        GDScriptTarget greet = GDScriptRefactorings.MemberAt(scripts, scripts[0], Text.IndexOf("greet", StringComparison.Ordinal))!;
        Assert.Contains("\tनमस्ते()\n", After(GDScriptRefactorings.Rename(scripts, greet, "नमस्ते"), "res://main.gd", Text));
        Assert.Contains("\ta‌b()\n", After(GDScriptRefactorings.Rename(scripts, greet, "a‌b"), "res://main.gd", Text));
        Assert.Equal("'aⸯ' is not a GDScript name", GDScriptRefactorings.Rename(scripts, greet, "aⸯ").Refused);
    }

    private void A_call_by_a_name_holds_back_a_rename_only_where_its_object_may_run_the_functions_class()
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

    private void A_member_a_string_names_for_get_set_or_is_connected_is_found_renamed_and_holds_back_a_safe_delete()
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

    private void Renaming_an_override_renames_the_function_it_overrides_and_every_override_of_that_but_never_an_engines()
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

    private void Renaming_a_member_is_refused_where_a_use_would_read_a_local_of_the_new_name()
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

    private void Extract_variable_declares_the_expression_before_its_statement_and_reads_it_there()
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

    private void Extract_variable_is_refused_where_the_expression_would_run_another_number_of_times()
    {
        string text = "func f(a):\n\twhile a.next():\n\t\tpass\n\tif a and a.ok():\n\t\tpass\n";
        Assert.Contains("loop's condition", GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a.next()"), "n").Refused);
        Assert.Contains("condition before it", GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a.ok()"), "n").Refused);
        Assert.Contains("keyword", GDScriptRefactorings.ExtractVariable(One(text), Over(text, "a.ok()"), "class").Refused);
    }

    private void Inline_variable_puts_its_value_at_each_read_in_parentheses_where_it_needs_them()
    {
        string text = "func f(a):\n\tvar b = a + 1\n\tprint(b * 2)\n\tprint(b)\n";
        Assert.Equal("func f(a):\n\tprint((a + 1) * 2)\n\tprint(a + 1)\n", After(GDScriptRefactorings.InlineVariable(One(text), text.IndexOf("b =", StringComparison.Ordinal)), "res://main.gd", text));
    }

    private void Inline_variable_is_refused_where_it_is_assigned_again_or_what_it_reads_changes()
    {
        string again = "func f(a):\n\tvar b = a\n\tb = 3\n\tprint(b)\n";
        Assert.Contains("assigned again", GDScriptRefactorings.InlineVariable(One(again), again.IndexOf("b =", StringComparison.Ordinal)).Refused);
        string changed = "func f(a):\n\tvar b = a\n\ta = 3\n\tprint(b)\n";
        GDScriptRefactored refused = GDScriptRefactorings.InlineVariable(One(changed), changed.IndexOf("b =", StringComparison.Ordinal));
        Assert.Contains("'a', which its value reads, is assigned", refused.Refused);
        Assert.Single(refused.Places);
    }

    private void Extract_function_moves_the_statements_into_a_function_after_theirs_with_the_locals_they_read_as_parameters()
    {
        string text = "func f(a: int) -> void:\n\tvar b = a + 1\n\tvar c = b * 2\n\tprint(c)\n\tprint(b)\n";
        Assert.Equal("func f(a: int) -> void:\n\tvar b = a + 1\n\tshow(b)\n\tprint(b)\n\n\nfunc show(b) -> void:\n\tvar c = b * 2\n\tprint(c)\n",
            After(GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var c = b * 2\n\tprint(c)"), "show"), "res://main.gd", text));
        // The one local it sets that is read after it is returned.
        Assert.Equal("func f(a: int) -> void:\n\tvar b = a + 1\n\tvar c = double(b)\n\tprint(c)\n\tprint(b)\n\n\nfunc double(b):\n\tvar c = b * 2\n\treturn c\n",
            After(GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var c = b * 2"), "double"), "res://main.gd", text));
    }

    private void Extract_function_is_refused_where_the_statements_return_or_set_two_locals_read_after()
    {
        string text = "func f(a):\n\tvar b = a + 1\n\tvar c = b * 2\n\tprint(c)\n\tprint(b)\n\treturn b\n";
        Assert.Contains("'c' and 'b'", GDScriptRefactorings.ExtractFunction(One(text), Over(text, "var b = a + 1\n\tvar c = b * 2"), "two").Refused?.Replace("'b' and 'c'", "'c' and 'b'"));
        Assert.Contains("return", GDScriptRefactorings.ExtractFunction(One(text), Over(text, "print(b)\n\treturn b"), "out").Refused);
    }

    private void Introduce_parameter_makes_the_expression_a_parameter_with_it_as_the_default()
    {
        string text = "func f(a):\n\treturn a + 10\n";
        Assert.Equal("func f(a, step := 10):\n\treturn a + step\n", After(GDScriptRefactorings.IntroduceParameter(One(text), Over(text, "10"), "step"), "res://main.gd", text));
        string local = "func f(a):\n\tvar b = 2\n\treturn a + b\n";
        Assert.Contains("reads 'b'", GDScriptRefactorings.IntroduceParameter(One(local), Over(local, "a + b"), "sum").Refused);
    }

    private const string Helper = "class_name Helper\nextends Node\n\n\nfunc twice(x):\n\treturn x * 2\n\n\nfunc run():\n\tprint(twice(3) + 1)\n";
    private const string User = "extends Helper\n\n\nfunc go():\n\tvar y = twice(4 + 1)\n\tprint(y)\n";

    private void Inline_function_gives_each_call_in_every_script_its_body_and_takes_it_out()
    {
        IReadOnlyList<GDScriptRead> scripts = Read(("res://helper.gd", Helper), ("res://user.gd", User));
        GDScriptTarget target = GDScriptRefactorings.MemberAt(scripts, scripts[0], Helper.IndexOf("twice", StringComparison.Ordinal))!;
        GDScriptRefactored made = GDScriptRefactorings.InlineFunction(scripts, target, []);
        Assert.Equal("class_name Helper\nextends Node\n\n\nfunc run():\n\tprint((3 * 2) + 1)\n", After(made, "res://helper.gd", Helper));
        Assert.Equal("extends Helper\n\n\nfunc go():\n\tvar y = (4 + 1) * 2\n\tprint(y)\n", After(made, "res://user.gd", User));
    }

    private void Inline_function_is_refused_where_the_arguments_would_not_run_as_the_call_ran_them()
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

    private void Safe_delete_takes_out_a_member_nothing_names_and_refuses_one_a_script_or_scene_still_does()
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

    private const string Hitter = "extends Node\n\n\nfunc hit(target, amount = 1):\n\tprint(target, amount)\n\n\nfunc run():\n\thit(\"a\")\n\thit(\"b\", 3)\n\tcall(\"hit\", \"c\", 4)\n";

    private void Change_signature_renames_reorders_and_adds_through_every_call_and_string_naming_it()
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

    private void Change_signature_is_refused_where_a_calls_arguments_would_not_run_as_they_did()
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

    private const string Signaller = "extends Node\n\nsignal hurt(amount, source)\n\n\nfunc _ready():\n\thurt.connect(on_hurt)\n\tconnect(\"hurt\", Callable(self, \"on_hurt_too\"))\n"
        + "\thurt.emit(3, self)\n\temit_signal(\"hurt\", 4, null)\n\n\nfunc on_hurt(amount, source):\n\tprint(amount)\n\n\nfunc on_hurt_too(a, b):\n\tprint(b)\n\n\nfunc on_scene(x, y):\n\tprint(x)\n";

    private void Change_signature_of_a_signal_reorders_and_adds_through_every_emit_and_every_handler_connected()
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

    private const string Healer = "extends Node\n\n\nstatic func clamp_health(value):\n\treturn value * 2\n\n\nfunc _ready():\n\tprint(clamp_health(120))\n\tvar f = clamp_health\n";

    private void Move_takes_a_static_function_to_a_class_named_script_its_calls_and_values_called_by_its_class_name()
    {
        string utils = "class_name Utils\nextends RefCounted\n";
        IReadOnlyList<GDScriptRead> scripts = Read(("res://main.gd", Healer), ("res://utils.gd", utils));
        GDScriptRefactored made = GDScriptRefactorings.Move(scripts, GDScriptRefactorings.MemberAt(scripts, scripts[0], Healer.IndexOf("clamp_health", StringComparison.Ordinal))!,
            "res://utils.gd", new Dictionary<string, string>(), []);
        Assert.Equal("extends Node\n\n\nfunc _ready():\n\tprint(Utils.clamp_health(120))\n\tvar f = Utils.clamp_health\n", After(made, "res://main.gd", Healer));
        Assert.Equal("class_name Utils\nextends RefCounted\n\n\nstatic func clamp_health(value):\n\treturn value * 2\n", After(made, "res://utils.gd", utils));
    }

    /// <summary>A change across scripts is refused while one is read only in part, as a use after where reading stopped would
    /// go unseen and keep the old name.</summary>
    private void No_change_is_made_across_a_script_read_only_in_part()
    {
        static GDScriptRead Read(string path, string text) => new(path, new GDScriptAnalyzer(GDScriptProject.Alone(null)).Analyze(GDScriptTree.Parse(text), path));
        GDScriptRead whole = Read("res://whole.gd", "var a = 1\n");
        GDScriptRead part = Read("res://part.gd", "var deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + "\nvar uses = a\n");
        Assert.Null(GDScriptRefactorings.WhyNotChanged([whole]));
        Assert.StartsWith("part.gd is nested too deeply", GDScriptRefactorings.WhyNotChanged([whole, part]), StringComparison.Ordinal);
    }
}

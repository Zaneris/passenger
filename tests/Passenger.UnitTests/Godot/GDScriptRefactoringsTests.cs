using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>GDScript's refactorings over Passenger's reading of a project's scripts: each one's edits, every script it
/// touches still parsing clean, and each refused, naming the place, where it cannot see every use or would change what the
/// code does.</summary>
public class GDScriptRefactoringsTests
{
    /// <summary>Scripts read as a project, each class_name naming its script.</summary>
    private static IReadOnlyList<GDScriptRead> Read(params (string Script, string Text)[] scripts)
    {
        Dictionary<string, GDScriptTree> trees = scripts.ToDictionary(s => s.Script, s => GDScriptTree.Parse(s.Text), StringComparer.Ordinal);
        Dictionary<string, string> classNames = trees.Where(t => t.Value.Class.Identifier is not null).ToDictionary(t => t.Value.Class.Identifier!.Name, t => t.Key, StringComparer.Ordinal);
        GDScriptAnalyzer analyzer = new(new GDScriptProject(null, p => trees.GetValueOrDefault(p), classNames, new Dictionary<string, string>()));
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
    public void Inline_variable_puts_its_value_at_each_read_in_parentheses_where_it_needs_them()
    {
        string text = "func f(a):\n\tvar b = a + 1\n\tprint(b * 2)\n\tprint(b)\n";
        Assert.Equal("func f(a):\n\tprint((a + 1) * 2)\n\tprint(a + 1)\n", After(GDScriptRefactorings.InlineVariable(One(text), text.IndexOf("b =", StringComparison.Ordinal)), "res://main.gd", text));
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
    public void Rearranged_gives_a_csharp_calls_arguments_in_the_new_order()
    {
        IReadOnlyList<GDScriptParameterChange> was = [new(0, "target", string.Empty, string.Empty, string.Empty), new(1, "amount", string.Empty, "1", string.Empty)];
        Assert.Equal(["\"b\"", "2", "3"], GDScriptRefactorings.Rearranged(["\"b\"", "3"], was,
            [was[0], new GDScriptParameterChange(-1, "times", "int", string.Empty, "2"), was[1]]));
        Assert.Equal(["\"a\"", "2"], GDScriptRefactorings.Rearranged(["\"a\""], was, [was[0], new GDScriptParameterChange(-1, "times", "int", string.Empty, "2"), was[1]]));
    }
}

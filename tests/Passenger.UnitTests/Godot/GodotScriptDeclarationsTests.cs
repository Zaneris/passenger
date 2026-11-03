using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>What a GDScript file declares, which a scene's connection and a C# string naming a member are read against, and
/// the scripts its class extends in its project.</summary>
public class GodotScriptDeclarationsTests
{
    [Fact]
    public void A_gdscript_functions_signals_variables_and_constants_are_found_where_their_lines_declare_them_outside_inner_classes()
    {
        string text = "extends Node\n\nsignal scored(points: int)\n\n@rpc(\"any_peer\") func _on_scored_more() -> void:\n\tpass\n\n"
            + "static func make() -> Node:\n\treturn null\n\nclass Inner:\n\tfunc _on_scored() -> void:\n\t\tpass\n\nfunc _on_scored(points: int) -> void:\n\tpass\n"
            + "@export var speed := 2.0\nstatic var count := 0\nconst LIMIT = 3\nenum State { IDLE }\n";
        GodotScriptDeclarations read = GodotScriptDeclarations.Read(text);
        TextSnapshot snapshot = TextSnapshot.From(text);
        Assert.Equal(new TextPosition(14, 5), At(GodotScriptKind.Function, "_on_scored"));
        Assert.Equal(new TextPosition(4, 22), At(GodotScriptKind.Function, "_on_scored_more"));
        Assert.Equal(new TextPosition(7, 12), At(GodotScriptKind.Function, "make"));
        Assert.Equal(new TextPosition(2, 7), At(GodotScriptKind.Signal, "scored"));
        Assert.Null(read.Member(GodotScriptKind.Function, "scored"));
        Assert.Null(read.Member(GodotScriptKind.Function, "missing"));
        Assert.Equal(new TextPosition(16, 12), At(GodotScriptKind.Variable, "speed"));
        Assert.Null(read.Member(GodotScriptKind.Variable, "count"));
        Assert.NotNull(read.Member(GodotScriptKind.Constant, "LIMIT"));
        Assert.NotNull(read.Member(GodotScriptKind.Constant, "State"));
        Assert.Equal(("Node", false), (read.Extends, read.ExtendsPath));

        TextPosition? At(GodotScriptKind kind, string name) => read.Member(kind, name) is { } member ? snapshot.GetPosition(member.Start) : null;
    }

    [Fact]
    public void What_a_script_extends_is_read_from_its_extends_or_its_class_name_line()
    {
        Assert.Equal(("res://base.gd", true, null), Read("@tool\nextends \"res://base.gd\" # the base\n"));
        Assert.Equal(("CharacterBody2D", false, "Player"), Read("class_name Player extends CharacterBody2D\n"));
        Assert.Equal(("Enemy", false, "Boss"), Read("class_name Boss\nextends Enemy\n"));
        Assert.Equal(((string?)null, false, (string?)null), Read("func _ready():\n\tpass\n"));

        static (string?, bool, string?) Read(string text) => GodotScriptDeclarations.Read(text) is var read ? (read.Extends, read.ExtendsPath, read.ClassName) : default;
    }

    [Fact]
    public void A_scripts_class_takes_in_the_scripts_it_extends_by_path_and_by_class_name_and_is_whole_where_the_last_extends_the_engines()
    {
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string>
        {
            ["res://actors/boss.gd"] = "extends \"enemy.gd\"\nfunc roar():\n\tpass\n",
            ["res://actors/enemy.gd"] = "class_name Enemy\nextends Actor\nsignal died\nfunc hit():\n\tpass\n",
            ["res://actor.gd"] = "class_name Actor extends CharacterBody2D\nfunc _on_hurt():\n\tpass\n",
            ["res://ghost.gd"] = "extends Spirit.Inner\n",
            ["res://lost.gd"] = "extends \"res://gone.gd\"\n",
        });
        GodotScriptChain boss = scenes.ChainOf("res://actors/boss.gd");
        Assert.Equal(["res://actors/boss.gd", "res://actors/enemy.gd", "res://actor.gd"], boss.Scripts.Select(s => s.Script));
        Assert.True(boss.Whole && boss.Engine == "CharacterBody2D", $"The boss's class is whole on {boss.Engine}");
        Assert.Equal("res://actor.gd", boss.Find(GodotScriptKind.Function, "_on_hurt")?.Script);
        Assert.Equal("res://actors/enemy.gd", boss.Find(GodotScriptKind.Signal, "died")?.Script);
        Assert.Null(boss.Find(GodotScriptKind.Function, "died"));
        Assert.Equal(["roar", "hit", "_on_hurt"], boss.Names(GodotScriptKind.Function));
        Assert.False(scenes.ChainOf("res://ghost.gd").Whole, "A script extending an inner class is taken for whole");
        Assert.False(scenes.ChainOf("res://lost.gd").Whole, "A script extending one not in the project is taken for whole");
        Assert.Equal("RefCounted", new GodotScenes("/game", new Dictionary<string, GodotFile>(), texts: new Dictionary<string, string> { ["res://a.gd"] = "func f():\n\tpass\n" })
            .ChainOf("res://a.gd").Engine);
    }

    /// <summary>A signal's declared parameters stand where the script's own emits give as many, and are not known where one
    /// gives another count (another object's emit of a signal of the same name is not the script's).</summary>
    [Fact]
    public void A_signal_the_scripts_own_emits_give_other_arguments_than_declared_has_its_parameters_unknown()
    {
        GodotScriptDeclarations read = GodotScriptDeclarations.Read(
            "signal updated(zone)\nsignal hit(amount: int)\nsignal named(a, b)\nsignal other(a)\n\nfunc f(offsets):\n"
            + "\tupdated.emit(offsets[1], offsets[2])\n\tself.hit.emit(3)\n\temit_signal(&\"named\", 1)\n\tenemy.other.emit()\n\tother.emit(\"a, b\")\n");
        Assert.Null(read.Member(GodotScriptKind.Signal, "updated")!.Parameters);
        Assert.Equal(["amount: int"], read.Member(GodotScriptKind.Signal, "hit")!.Parameters!);
        Assert.Null(read.Member(GodotScriptKind.Signal, "named")!.Parameters);
        Assert.Equal(["a"], read.Member(GodotScriptKind.Signal, "other")!.Parameters!);
    }

    [Fact]
    public void A_functions_and_a_signals_parameters_are_read_with_how_many_a_call_must_give()
    {
        GodotScriptDeclarations read = GodotScriptDeclarations.Read(
            "signal died\nsignal hit(amount: int, by: Node = null)\nfunc heal(points := 1, to: Array[int] = [1, 2]) -> void:\n\tpass\n"
            + "func take(a, b: Dictionary = {\"x\": 1}):\n\tpass\nfunc open(\n\ta,\n):\n\tpass\n");
        Assert.Null(read.Member(GodotScriptKind.Signal, "died")!.Parameters);
        Assert.Equal(["amount: int", "by: Node = null"], read.Member(GodotScriptKind.Signal, "hit")!.Parameters!);
        Assert.Equal(1, read.Member(GodotScriptKind.Signal, "hit")!.Required);
        Assert.Equal((2, 0), (read.Member(GodotScriptKind.Function, "heal")!.Parameters!.Count, read.Member(GodotScriptKind.Function, "heal")!.Required));
        Assert.Equal((2, 1), (read.Member(GodotScriptKind.Function, "take")!.Parameters!.Count, read.Member(GodotScriptKind.Function, "take")!.Required));
        Assert.Null(read.Member(GodotScriptKind.Function, "open")!.Parameters);
    }

    /// <summary>The script's members are what Godot's parser reads: a rest parameter keeps its dots, code in a string declares
    /// nothing, an unnamed enum's values are each a constant, and an emit over several lines is counted.</summary>
    [Fact]
    public void Members_are_what_Godots_parser_reads_of_the_script_strings_and_rest_parameters_too()
    {
        GodotScriptDeclarations read = GodotScriptDeclarations.Read(
            "signal moved(x)\nenum { NORTH, SOUTH }\nvar template = \"\"\"\nfunc not_a_member():\n\tpass\n\"\"\"\n\n"
            + "func call_all(first, ...rest: Array) -> void:\n\tmoved.emit(\n\t\tfirst, rest\n\t)\n");
        Assert.Equal(["first", "...rest: Array"], read.Member(GodotScriptKind.Function, "call_all")!.Parameters!);
        Assert.Null(read.Member(GodotScriptKind.Function, "not_a_member"));
        Assert.NotNull(read.Member(GodotScriptKind.Constant, "SOUTH"));
        Assert.Null(read.Member(GodotScriptKind.Signal, "moved")!.Parameters);
    }
}

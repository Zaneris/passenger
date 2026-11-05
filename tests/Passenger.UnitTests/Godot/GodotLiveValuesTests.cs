using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Godot;
using Passenger.Text;

namespace Passenger.UnitTests.Godot;

/// <summary>A running game's values in the code, as far as they are pure: the members of a script's class read for it (a C#
/// class's instance fields and auto-properties, a GDScript's top-level vars), the helper's answer read back, a value's
/// words in a hint, and each member's last minute kept a reading a second.</summary>
public class GodotLiveValuesTests
{
    private const string Player = """
        using Godot;

        namespace Game;

        public partial class Player : CharacterBody2D
        {
            private const float Top = 400;

            private static int players;

            private float speed = 312.5f, turn;

            [Export]
            public Vector2 Velocity2 { get; set; }

            public bool Dead => health <= 0;

            public int Health { get => health; set => health = value; }

            private int health = 3;

            public event System.Action? Died;

            public partial class Inner
            {
                private int hidden;
            }
        }

        public partial class Other : Node
        {
            private int notPlayers;
        }

        """;

    private static GodotScriptMembers? CSharp(string text, string path)
    {
        TextSnapshot snapshot = TextSnapshot.From(text);
        return GodotMembers.OfCSharp(CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken), snapshot, path);
    }

    private static int Line(string text, string code) => Array.FindIndex(text.Split('\n'), l => l.Contains(code, StringComparison.Ordinal));

    [Fact]
    public void A_csharp_scripts_members_are_its_instance_fields_and_auto_properties_never_a_getter_of_its_own()
    {
        GodotScriptMembers members = CSharp(Player, "res://actors/Player.cs") ?? throw new InvalidOperationException("Player.cs declares no script class");
        Assert.Equal(("res://actors/Player.cs", "Player", Line(Player, "class Player")), (members.Script, members.Class, members.ClassLine));
        Assert.Equal([("speed", Line(Player, "speed")), ("turn", Line(Player, "speed")), ("Velocity2", Line(Player, "Velocity2")), ("health", Line(Player, "int health"))],
            members.Members.Select(m => (m.Name, m.Line)));
    }

    [Fact]
    public void A_partial_classs_other_file_reads_its_members_for_the_script_named_as_the_class()
    {
        const string Movement = "public partial class Player\n{\n    private int steps;\n}\n";
        GodotScriptMembers? members = CSharp(Movement, "res://actors/Player.Movement.cs");
        Assert.True(members is { Script: "res://actors/Player.cs", Members: [{ Name: "steps", Line: 2 }] }, $"Player.Movement.cs read as {members}");
        Assert.Null(CSharp(Movement, "res://actors/Enemy.cs"));
    }

    [Fact]
    public void A_gdscripts_members_are_its_top_level_vars_annotated_or_not_but_static_ones_and_an_inner_classs()
    {
        const string Script = """
            extends CharacterBody2D
            class_name Hero

            const TOP := 3
            @export var speed := 312.5
            @export_range(0, 10, 0.5) var jump: float = 2
            @onready var label: Label = $Label
            static var heroes := 0
            var _hits = 0

            class Inner:
                var hidden := 1

            func _process(delta):
                var local := 1

            """;
        GodotScriptMembers members = GodotMembers.OfGdScript(TextSnapshot.From(Script), "res://hero.gd");
        Assert.Equal(("res://hero.gd", "Hero", 1), (members.Script, members.Class, members.ClassLine));
        Assert.Equal(["speed", "jump", "label", "_hits"], members.Members.Select(m => m.Name));
        Assert.Equal(Line(Script, "var _hits"), members.Members[^1].Line);
        GodotScriptMembers plain = GodotMembers.OfGdScript(TextSnapshot.From("extends Node\nvar x = 1\n"), "res://things/crate.gd");
        Assert.Equal(("crate", 0), (plain.Class, plain.ClassLine));
    }

    [Fact]
    public void What_reading_a_node_runs_is_each_getter_and_get_of_its_script_and_those_it_extends_by_path_or_by_class_name()
    {
        Dictionary<string, string> files = new()
        {
            ["res://enemy.gd"] = "extends \"base.gd\"\n\nvar hp: int:\n\tget:\n\t\treturn 1\nvar plain := 1\nvar kept := 2: set = _keep\n",
            ["res://base.gd"] = "extends Actor\n\nvar armour: int: get = _armour\nstatic var made: int:\n\tget:\n\t\treturn 1\n\n\nfunc _armour() -> int:\n\treturn 1\n",
            ["res://actor.gd"] = "class_name Actor\nextends Node\n\n\nfunc _get(_property: StringName) -> Variant:\n\treturn null\n\n\nfunc _get_property_list() -> Array:\n\treturn []\n",
            ["res://.godot/global_script_class_cache.cfg"] = "list=[{\n\"base\": &\"Node\",\n\"class\": &\"Actor\",\n\"icon\": \"\",\n\"path\": \"res://actor.gd\"\n}]\n",
        };
        Assert.Equal(["hp", "armour", "made", "_get", "_get_property_list"], GodotMembers.RunByReading("res://enemy.gd", res => files.GetValueOrDefault(res)));
        Assert.Empty(GodotMembers.RunByReading("res://gone.gd", res => files.GetValueOrDefault(res)));
    }

    [Fact]
    public void A_script_extending_itself_is_read_once()
    {
        Assert.Equal(["a"], GodotMembers.RunByReading("res://a.gd", _ => "extends \"a.gd\"\n\nvar a: int:\n\tget:\n\t\treturn 1\n"));
    }

    [Fact]
    public void What_reading_a_node_runs_of_a_csharp_script_is_each_property_with_a_getter_of_the_persons_and_its_Get()
    {
        const string Script = """
            namespace Game;

            public partial class Player : Node
            {
                public int Auto { get; set; }

                public int Bodied { get { return 1; } }

                public int Arrow => 2;

                public int Accessor { get => 3; set { } }

                public int SetOnly { set { } }

                public static int Shared => 4;

                public override Variant _Get(StringName property) => default;

                public class Nested
                {
                    public int Inside => 5;
                }
            }

            public partial class Other : Node
            {
                public int Beside => 6;
            }

            """;
        Assert.Equal(["Bodied", "Arrow", "Accessor", "_Get"], GodotMembers.RunByReading("res://Player.cs", res => res == "res://Player.cs" ? Script : null));
    }

    [Fact]
    public void A_gdscript_member_with_a_getter_of_its_own_is_never_read_however_it_is_written()
    {
        const string Script = """
            extends Node

            var plain := 1
            var set_only := 2:
                set(value):
                    set_only = value
            var named_setter: int: set = _keep
            var inline_body: int:
                get:
                    return plain
            var with_value := 3:
                get:
                    return plain
                set(value):
                    plain = value
            var setter_first := 4:
                set(value):
                    plain = value
                get:
                    return plain
            var named: int: get = _counted
            var both := 5: set = _keep, get = _counted
            @export var exported := 6:
                get: return plain
            var spread := [
                1,
            ]:
                get:
                    return []
            var last := 7

            func _counted() -> int:
                return plain

            func _keep(value: int) -> void:
                plain = value

            """;
        GodotScriptMembers members = GodotMembers.OfGdScript(TextSnapshot.From(Script), "res://getters.gd");
        Assert.Equal(["plain", "set_only", "named_setter", "last"], members.Members.Select(m => m.Name));
        Assert.Equal(Line(Script, "var last"), members.Members[^1].Line);
    }

    [Fact]
    public void A_script_that_does_not_parse_still_has_the_members_around_what_is_wrong()
    {
        const string Script = "extends Node\n\nvar first := 1\n\nfunc _ready(:\n\tpass\n\nvar after := 2\nvar := 3\nvar last := 4\n";
        Assert.Equal(["first", "after", "last"], GodotMembers.OfGdScript(TextSnapshot.From(Script), "res://broken.gd").Members.Select(m => m.Name));
    }

    [Fact]
    public void The_helpers_answer_reads_as_each_scripts_count_node_and_values_a_member_not_held_left_out()
    {
        GodotScriptAsk main = new("res://Main.cs", 0, ["frames", "drift", "Target", "tag", "list"]);
        GodotScriptAsk enemy = new("res://Enemy.cs", 0, ["hits"]);
        List<object?> said =
        [
            new List<object?>
            {
                new List<object?> { 1L, 27917288914L, "Main", new List<object?>
                {
                    new List<object?> { 29L },
                    new List<object?> { new GodotMath(GodotType.Vector2, [1.5, -2]) },
                    new List<object?> { new GodotNodePath(["Tag"], [], false), 2L, "Tag (Node2D)" },
                    new List<object?> { "Tag (Node2D)", 1L },
                    new List<object?>(),
                } },
                new List<object?> { 2L, 0L, "", new List<object?> { new List<object?>() } },
            },
        ];
        IReadOnlyList<GodotScriptRead>? read = GodotScriptRead.Read(said, [main, enemy]);
        Assert.NotNull(read);
        Assert.True(read[0] is { Count: 1, Node: 27917288914UL, NodeName: "Main" }, $"Main's answer read as {read[0]}");
        Assert.Equal(["frames", "drift", "Target", "tag"], read[0].Values.Keys);
        Assert.Equal("frames: 29", GodotLiveWords.Hint("frames", read[0].Values["frames"]));
        Assert.Equal("drift: (1.5, -2)", GodotLiveWords.Hint("drift", read[0].Values["drift"]));
        Assert.Equal("Target: Tag → Tag (Node2D)", GodotLiveWords.Hint("Target", read[0].Values["Target"]));
        Assert.Equal("tag: Tag (Node2D)", GodotLiveWords.Hint("tag", read[0].Values["tag"]));
        Assert.True(read[1] is { Count: 2, Node: 0, Values.Count: 0 }, $"The enemies' answer read as {read[1]}");
        Assert.Null(GodotScriptRead.Read(said, [main]));
        Assert.Null(GodotScriptRead.Read([new List<object?> { "nonsense" }], [main]));
    }

    [Theory]
    [InlineData(312.48736572265625, "312.487")]
    [InlineData(0.10000000149011612, "0.1")]
    [InlineData(-2.0, "-2")]
    [InlineData(0.0, "0")]
    [InlineData(0.000123456, "0.000123")]
    [InlineData(-0.0001, "-0.0001")]
    [InlineData(1234567.25, "1234567.25")]
    public void A_real_number_is_rounded_to_a_thousandth_or_three_figures_when_smaller(double value, string words)
    {
        Assert.Equal(words, GodotLiveWords.Number(value));
    }

    [Fact]
    public void A_hint_quotes_strings_says_a_path_reaching_no_node_and_is_cut_at_its_longest()
    {
        Assert.Equal("name: \"Ada\"", GodotLiveWords.Hint("name", new GodotMemberValue("Ada")));
        Assert.Equal("action: &\"jump\"", GodotLiveWords.Hint("action", new GodotMemberValue(new GodotName("jump"))));
        Assert.Equal("alive: true", GodotLiveWords.Hint("alive", new GodotMemberValue(true)));
        Assert.Equal("cell: (3, -4)", GodotLiveWords.Hint("cell", new GodotMemberValue(new GodotMath(GodotType.Vector2I, [3, -4]))));
        Assert.Equal("target: ../Gone → no node", GodotLiveWords.Hint("target", new GodotMemberValue(new GodotNodePath(["..", "Gone"], [], false), Node: "", IsPath: true)));
        Assert.Equal("scene: null", GodotLiveWords.Hint("scene", new GodotMemberValue(null)));
        string cut = GodotLiveWords.Hint("story", new GodotMemberValue(new string('a', 100)));
        Assert.True(cut.Length == GodotLiveWords.Longest && cut.EndsWith('…'), $"A long string's hint is {cut.Length} long: {cut}");
    }

    [Fact]
    public void A_members_last_minute_keeps_a_reading_a_second_lets_older_ones_go_and_begins_again_for_another_shape()
    {
        GodotLiveHistory history = new();
        for (int second = 0; second < 90; second++)
        {
            history.Add(1, "frames", second * 1000L, (long)(second * 60));
            // A reading soon after takes the last one's place.
            history.Add(1, "frames", (second * 1000L) + 200, (long)((second * 60) + 12));
        }
        IReadOnlyList<(long At, double[] Parts)> kept = history.Of(1, "frames", 89_200);
        Assert.Equal(61, kept.Count);
        Assert.Equal((29_200L, 29 * 60 + 12.0), (kept[0].At, kept[0].Parts[0]));
        Assert.Equal((89_200L, 89 * 60 + 12.0), (kept[^1].At, kept[^1].Parts[0]));

        history.Add(1, "drift", 0, new GodotMath(GodotType.Vector2, [1, 2]));
        history.Add(1, "drift", 1000, new GodotMath(GodotType.Vector3, [1, 2, 3]));
        Assert.True(history.Of(1, "drift", 1000) is [(1000, [1, 2, 3])], "A vector read with another count of parts kept the readings before it");
        history.Add(1, "drift", 2000, "words");
        Assert.Empty(history.Of(1, "drift", 2000));
        Assert.Equal(["x", "y", "z", "w"], GodotLiveWords.PartNames(new GodotMath(GodotType.Vector4, [0, 0, 0, 0])));
        Assert.Equal(["r", "g", "b", "a"], GodotLiveWords.PartNames(new GodotMath(GodotType.Color, [0, 0, 0, 1])));

        history.Forget(200_000);
        Assert.Empty(history.Of(1, "frames", 89_200));
    }
}

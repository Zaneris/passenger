using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>A running game's containers as Passenger reads, sets and keeps them: a typed array and a typed dictionary keep
/// their element types sent back (a typed member refuses an untyped one), packed arrays are written as their own types,
/// an element type is read from Godot's hint words, an untyped item from what is typed, and arrays and dictionaries are
/// written as a scene holds them.</summary>
public class GodotContainersTests
{
    [Fact]
    public void A_typed_array_and_dictionary_read_back_keep_what_they_are_typed_to_hold()
    {
        GodotArray waves = new([1L, 5L]) { Element = GodotElement.Of(GodotType.Int) };
        GodotArray read = Assert.IsType<GodotArray>(GodotVariant.Read(GodotVariant.Write(waves)));
        Assert.Equal(GodotElement.Of(GodotType.Int), read.Element);
        Assert.Equal([1L, 5L], read);

        GodotArray nodes = new([]) { Element = new GodotElement(2, GodotType.Object, "Node2D") };
        Assert.Equal("Node2D", Assert.IsType<GodotArray>(GodotVariant.Read(GodotVariant.Write(nodes))).Element.Name);

        GodotDictionary scores = new([new KeyValuePair<object?, object?>("red", 1L)]) { KeyType = GodotElement.Of(GodotType.String), ValueType = GodotElement.Of(GodotType.Int) };
        GodotDictionary back = Assert.IsType<GodotDictionary>(GodotVariant.Read(GodotVariant.Write(scores)));
        Assert.True(back.KeyType == scores.KeyType && back.ValueType == scores.ValueType && back.Entries.Single().Key as string == "red",
            $"The dictionary came back typed {back.KeyType}, {back.ValueType} with {back}");
    }

    [Fact]
    public void Packed_arrays_are_written_as_their_own_types_and_read_back_whole()
    {
        Assert.Equal(new[] { 3, -4 }, GodotVariant.Read(GodotVariant.Write(new[] { 3, -4 })));
        Assert.Equal(new[] { 1.5f, 2f }, GodotVariant.Read(GodotVariant.Write(new[] { 1.5f, 2f })));
        Assert.Equal(new byte[] { 1, 2, 3 }, GodotVariant.Read(GodotVariant.Write(new byte[] { 1, 2, 3 })));
        GodotPackedMath points = new(GodotType.PackedVector2Array, [new(GodotType.Vector2, [1, 2]), new(GodotType.Vector2, [3, 4])]);
        Assert.Equal(points, GodotVariant.Read(GodotVariant.Write(points)));
        Assert.Equal(GodotType.PackedVector2Array, GodotVariant.TypeOf(points));
        Assert.Equal(new[] { 7, 8 }, GodotVariant.Packed(GodotType.PackedInt32Array, [7L, 8L]));
    }

    [Theory]
    [InlineData(GodotType.PackedVector2Array)]
    [InlineData(GodotType.PackedVector3Array)]
    [InlineData(GodotType.PackedVector4Array)]
    [InlineData(GodotType.PackedColorArray)]
    public void An_empty_packed_array_stays_its_own_type_sent_read_back_and_written_in_a_scene(GodotType type)
    {
        object? empty = GodotVariant.Packed(type, []);
        Assert.Equal(type, GodotVariant.TypeOf(empty));
        byte[] sent = GodotVariant.Write(empty);
        Assert.True(sent[0] == (byte)type, $"The last item removed from a {type} would send a {(GodotType)sent[0]}, which Godot leaves the property unchanged for");
        Assert.Equal(type, GodotVariant.TypeOf(GodotVariant.Read(sent)));
        Assert.Equal($"{type}()", GodotText.Scene(empty));
        Assert.Equal(type, GodotVariant.TypeOf(GodotVariant.Default(type)));
    }

    [Theory]
    [InlineData("C:\\new")]
    [InlineData("say \"hi\"")]
    [InlineData("two\nlines\tand\ra tab")]
    public void A_string_in_a_container_reads_back_as_it_was_from_the_words_shown(string held)
    {
        Assert.Equal(held, GodotVariant.Literal(GodotVariant.Shown(held, quoted: true)));
        Assert.Equal(held, GodotVariant.Guessed(GodotVariant.Shown(held, quoted: true)));
        Assert.Equal(new GodotName(held), GodotVariant.Guessed(GodotVariant.Shown(new GodotName(held), quoted: true)));
        Assert.Equal(held, GodotVariant.Literal(GodotVariant.Shown(new GodotNodePath([held], [], false), quoted: true)));
    }

    [Theory]
    [InlineData("\"\\u00e9\"", "é")]
    [InlineData("\"é\"", "é")]
    [InlineData("\"\\U01F600\"", "😀")]
    [InlineData("\"\\ud83d\\ude00\"", "😀")]
    [InlineData("\"\\a\\b\\f\\n\\r\\t\\v\"", "\a\b\f\n\r\t\v")]
    [InlineData("\"\\'\\\"\\\\\"", "'\"\\")]
    [InlineData("&\"\\u0041\"", "A")]
    public void A_literal_says_what_GDScript_reads_of_every_escape(string typed, string said)
    {
        Assert.Equal(said, GodotVariant.Literal(typed));
    }

    [Theory]
    [InlineData("\"\\q\"")]
    [InlineData("\"\\ud83d\"")]
    [InlineData("\"\\ude00\\ud83d\"")]
    [InlineData("\"\\u00e\"")]
    [InlineData("\"\\U00D83D\"")]
    [InlineData("\"\\U110000\"")]
    public void An_escape_GDScript_refuses_makes_no_literal(string typed)
    {
        Assert.Null(GodotVariant.Literal(typed));
    }

    [Fact]
    public void A_scenes_string_reads_its_escapes_as_Godots_variant_text_does()
    {
        Assert.Equal("q\ba", GodotVariant.OfScene(GodotType.String, "\"\\q\\b\\a\"").Value);
        Assert.Equal("é", GodotVariant.OfScene(GodotType.String, "\"\\u00e9\"").Value);
    }

    [Fact]
    public void Editing_the_words_of_a_path_a_container_shows_leaves_its_backslash_a_backslash()
    {
        string shown = GodotVariant.Shown("C:\\new", quoted: true);
        Assert.Equal("C:\\newer", GodotVariant.Literal(shown.Replace("new", "newer", StringComparison.Ordinal)));
        Assert.Equal("[\"C:\\\\new\", \"a\\\"b\"]", GodotVariant.Shown(new[] { "C:\\new", "a\"b" }));
        Assert.Equal("{ \"C:\\\\new\": \"a\\\"b\" }", GodotVariant.Shown(new GodotDictionary([new("C:\\new", "a\"b")])));
    }

    [Fact]
    public void Two_byte_arrays_of_one_length_differ_by_their_bytes_wherever_they_are_held()
    {
        Assert.True(GodotVariant.Same(new byte[] { 1, 2 }, new byte[] { 1, 2 }), "One byte array held twice would be marked changed");
        Assert.False(GodotVariant.Same(new byte[] { 1, 2 }, new byte[] { 3, 4 }), "A byte changed would be left out of Keep");
        Assert.False(GodotVariant.Same(new GodotArray([new byte[] { 1 }]), new GodotArray([new byte[] { 2 }])), "A byte array in an array");
        Assert.False(GodotVariant.Same(new GodotDictionary([new("a", new byte[] { 1 })]), new GodotDictionary([new("a", new byte[] { 2 })])), "A byte array in a dictionary");
        Assert.False(GodotVariant.Same(new GodotObject("Thing", [new("data", new byte[] { 1 })]), new GodotObject("Thing", [new("data", new byte[] { 2 })])), "A byte array in an object");
        Assert.True(GodotVariant.Same(new GodotArray([1L, "a"]), new GodotArray([1L, "a"])), "Arrays alike");
        Assert.False(GodotVariant.Same(new GodotArray([1L]), new GodotArray([1L, 2L])), "Arrays of different lengths");
        Assert.Equal("[2 bytes]", GodotVariant.Shown(new byte[] { 1, 2 }));
    }

    private static object? NewKey(GodotType type, params object?[] keys)
    {
        Assert.True(GodotVariant.TryNewKey(type, keys, out object? key), $"A {type} dictionary holding {keys.Length} keys has a new key to take");
        return key;
    }

    [Fact]
    public void A_new_key_is_of_the_type_the_dictionary_declares_and_no_key_it_has()
    {
        Assert.Equal(false, NewKey(GodotType.Bool));
        Assert.Equal(true, NewKey(GodotType.Bool, false));
        Assert.False(GodotVariant.TryNewKey(GodotType.Bool, [true, false], out _), "A bool has two keys to give");
        Assert.Equal(0L, NewKey(GodotType.Int));
        Assert.Equal(6L, NewKey(GodotType.Int, 5L, 2L));
        Assert.Equal(0.0, NewKey(GodotType.Float));
        Assert.Equal(2.0, NewKey(GodotType.Float, 1.5, double.PositiveInfinity));
        Assert.Equal("key", NewKey(GodotType.String));
        Assert.Equal("key 3", NewKey(GodotType.String, "key", "key 2"));
        Assert.Equal(new GodotName("key 2"), NewKey(GodotType.StringName, new GodotName("key")));
        Assert.Equal("key", NewKey(GodotType.NodePath)?.ToString());
        Assert.IsType<GodotNodePath>(NewKey(GodotType.NodePath));
        Assert.Equal(new GodotMath(GodotType.Vector2, [0, 0]), NewKey(GodotType.Vector2));
        Assert.Equal(new GodotMath(GodotType.Vector2I, [2, 0]), NewKey(GodotType.Vector2I, new GodotMath(GodotType.Vector2I, [0, 0]), new GodotMath(GodotType.Vector2I, [1, 0])));
        Assert.Equal(new GodotMath(GodotType.Color, [1, 0, 0, 1]), NewKey(GodotType.Color, new GodotMath(GodotType.Color, [0, 0, 0, 1])));
        Assert.Equal(new GodotArray([]), NewKey(GodotType.Array));
        Assert.False(GodotVariant.TryNewKey(GodotType.Array, [new GodotArray([])], out _), "An empty array is the one key an array type gives");
        Assert.Equal(GodotType.PackedVector3Array, GodotVariant.TypeOf(NewKey(GodotType.PackedVector3Array)));
        Assert.False(GodotVariant.TryNewKey(GodotType.Object, [], out _), "An object is no key made without a person");
    }

    [Theory]
    [InlineData("int", GodotType.Int)]
    [InlineData("Vector2i", GodotType.Vector2I)]
    [InlineData("String", GodotType.String)]
    [InlineData("2:", GodotType.Int)]
    [InlineData("24/17:Texture2D", GodotType.Object)]
    [InlineData("Texture2D", GodotType.Object)]
    [InlineData("", GodotType.Nil)]
    public void An_element_type_is_read_from_its_hint_words(string hint, GodotType type) => Assert.Equal(type, GodotHints.TypeNamed(hint));

    [Fact]
    public void Untyped_items_are_read_as_their_text_reads()
    {
        Assert.Equal(5L, GodotVariant.Guessed("5"));
        Assert.Equal(2.5, GodotVariant.Guessed(" 2.5 "));
        Assert.Equal(true, GodotVariant.Guessed("true"));
        Assert.Null(GodotVariant.Guessed("null"));
        Assert.Equal("a b", GodotVariant.Guessed("\"a b\""));
        Assert.Equal(new GodotName("go"), GodotVariant.Guessed("&\"go\""));
        Assert.Equal(new GodotMath(GodotType.Vector3, [1, 2, 3]), GodotVariant.Guessed("(1, 2, 3)"));
        Assert.Equal("plain words", GodotVariant.Guessed("plain words"));
    }

    [Fact]
    public void Arrays_and_dictionaries_are_written_as_a_scene_holds_them()
    {
        Assert.Equal("Array[int]([1, 5])", GodotText.Scene(new GodotArray([1L, 5L]) { Element = GodotElement.Of(GodotType.Int) }));
        Assert.Equal("[1, \"two\"]", GodotText.Scene(new List<object?> { 1L, "two" }));
        Assert.Equal("{\"red\": 1, \"blue\": 7}", GodotText.Scene(new GodotDictionary([new("red", 1L), new("blue", 7L)])));
        Assert.Equal("PackedVector2Array(1, 2, 3, 4)", GodotText.Scene(new GodotPackedMath(GodotType.PackedVector2Array, [new(GodotType.Vector2, [1, 2]), new(GodotType.Vector2, [3, 4])])));
        Assert.Null(GodotText.Scene(new List<object?> { new GodotObjectId(5) }));
    }
}

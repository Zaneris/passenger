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

    [Fact]
    public void Editing_the_words_of_a_path_a_container_shows_leaves_its_backslash_a_backslash()
    {
        string shown = GodotVariant.Shown("C:\\new", quoted: true);
        Assert.Equal("C:\\newer", GodotVariant.Literal(shown.Replace("new", "newer", StringComparison.Ordinal)));
        Assert.Equal("[\"C:\\\\new\", \"a\\\"b\"]", GodotVariant.Shown(new[] { "C:\\new", "a\"b" }));
        Assert.Equal("{ \"C:\\\\new\": \"a\\\"b\" }", GodotVariant.Shown(new GodotDictionary([new("C:\\new", "a\"b")])));
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

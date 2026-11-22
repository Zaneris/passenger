using Passenger.App.Godot;
using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>A running game's containers as Passenger reads, sets and keeps them: a typed array and a typed dictionary keep
/// their element types sent back (a typed member refuses an untyped one), packed arrays are written as their own types,
/// an element type is read from Godot's hint words, an untyped item from what is typed, and arrays and dictionaries are
/// written as a scene holds them.</summary>
public class GodotContainersTests
{
    /// <summary>Typed and packed arrays and dictionaries read back as they were and are written as a scene holds
    /// them.</summary>
    [Fact]
    public void Containers_read_back_and_are_written_as_godot_holds_them() => Cases.All(
        (nameof(A_typed_array_and_dictionary_read_back_keep_what_they_are_typed_to_hold),
            A_typed_array_and_dictionary_read_back_keep_what_they_are_typed_to_hold),
        (nameof(Packed_arrays_are_written_as_their_own_types_and_read_back_whole), Packed_arrays_are_written_as_their_own_types_and_read_back_whole),
        (nameof(Two_byte_arrays_of_one_length_differ_by_their_bytes_wherever_they_are_held),
            Two_byte_arrays_of_one_length_differ_by_their_bytes_wherever_they_are_held),
        (nameof(Arrays_and_dictionaries_are_written_as_a_scene_holds_them), Arrays_and_dictionaries_are_written_as_a_scene_holds_them));

    /// <summary>A string's escapes are read as GDScript and Godot's variant text read them, a path's backslash
    /// kept.</summary>
    [Fact]
    public void Escapes_are_read_as_gdscript_and_godots_variant_text_read_them() => Cases.All(
        (nameof(A_literal_says_what_GDScript_reads_of_every_escape), A_literal_says_what_GDScript_reads_of_every_escape),
        (nameof(A_scenes_string_reads_its_escapes_as_Godots_variant_text_does), A_scenes_string_reads_its_escapes_as_Godots_variant_text_does));

    private void A_typed_array_and_dictionary_read_back_keep_what_they_are_typed_to_hold()
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

    private void Packed_arrays_are_written_as_their_own_types_and_read_back_whole()
    {
        Assert.Equal(new[] { 3, -4 }, GodotVariant.Read(GodotVariant.Write(new[] { 3, -4 })));
        Assert.Equal(new[] { 1.5f, 2f }, GodotVariant.Read(GodotVariant.Write(new[] { 1.5f, 2f })));
        Assert.Equal(new byte[] { 1, 2, 3 }, GodotVariant.Read(GodotVariant.Write(new byte[] { 1, 2, 3 })));
        GodotPackedMath points = new(GodotType.PackedVector2Array, [new(GodotType.Vector2, [1, 2]), new(GodotType.Vector2, [3, 4])]);
        Assert.Equal(points, GodotVariant.Read(GodotVariant.Write(points)));
        Assert.Equal(GodotType.PackedVector2Array, GodotVariant.TypeOf(points));
        Assert.Equal(new[] { 7, 8 }, GodotVariant.Packed(GodotType.PackedInt32Array, [7L, 8L]));
    }

    private void A_literal_says_what_GDScript_reads_of_every_escape() => Cases.Each<(string Typed, string Said)>(
    [
        ("\"\\u00e9\"", "é"),
        ("\"\\U01F600\"", "😀"),
        ("\"\\a\\b\\f\\n\\r\\t\\v\"", "\a\b\f\n\r\t\v"),
        ("&\"\\u0041\"", "A"),
    ], row => A_literal_says_what_GDScript_reads_of_every_escape(row.Typed, row.Said));

    private void A_literal_says_what_GDScript_reads_of_every_escape(string typed, string said)
    {
        Assert.Equal(said, GodotVariant.Literal(typed));
    }

    private void A_scenes_string_reads_its_escapes_as_Godots_variant_text_does()
    {
        Assert.Equal("q\ba", GodotVariant.OfScene(GodotType.String, "\"\\q\\b\\a\"").Value);
        Assert.Equal("é", GodotVariant.OfScene(GodotType.String, "\"\\u00e9\"").Value);
    }

    private void Two_byte_arrays_of_one_length_differ_by_their_bytes_wherever_they_are_held()
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

    private void Arrays_and_dictionaries_are_written_as_a_scene_holds_them()
    {
        Assert.Equal("Array[int]([1, 5])", GodotText.Scene(new GodotArray([1L, 5L]) { Element = GodotElement.Of(GodotType.Int) }));
        Assert.Equal("[1, \"two\"]", GodotText.Scene(new List<object?> { 1L, "two" }));
        Assert.Equal("{\"red\": 1, \"blue\": 7}", GodotText.Scene(new GodotDictionary([new("red", 1L), new("blue", 7L)])));
        Assert.Equal("PackedVector2Array(1, 2, 3, 4)", GodotText.Scene(new GodotPackedMath(GodotType.PackedVector2Array, [new(GodotType.Vector2, [1, 2]), new(GodotType.Vector2, [3, 4])])));
        Assert.Null(GodotText.Scene(new List<object?> { new GodotObjectId(5) }));
    }
}

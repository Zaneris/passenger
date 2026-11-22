using Passenger.Workspace;

namespace Passenger.UnitTests.Godot;

/// <summary>Text a person types for a scene's property, held to what Godot does with it: each value here stood on a node's
/// line between two others in a scene Godot 4.7.2 loaded and instanced, and is among those read only where the scene loaded
/// with the lines after it still read; a value that followed by more text swallowed those lines is refused with those that
/// would not load.</summary>
public class GodotVariantTextTests
{
    private static readonly GodotFile Bare = GodotFile.Parse(string.Empty);

    private static readonly string[] Read =
    [
        "\"\\u00e9\"", "\"\\ud83d\\ude00\"", "\"\\b\\t\\n\\f\\r\"",
        "true", "false", "null", "nil", "inf", "-inf", "inf_neg", "nan", "5", "-5", "1.5", "-1.5", "1e5", "1E+5", "1e", "1.", "#ff0000", "#FF00",
        "\"text\"", "\"a\\\"b\"", "\"a\\qb\"", "\"é\"", "\"\\U01F600\"", "\"😀\"", "&\"name\"", "@\"old\"", "Vector2(1, 2)", "Vector2 (1, 2)",
        "Vector2(inf, nan)", "Vector2(-inf, inf_neg)", "Vector2i(1, 2)", "Vector2i(1.5, 2)", "Rect2(0, 0, 1, 1)", "Rect2i(0, 0, 1, 1)",
        "Vector3(1, 2, 3)", "Vector3i(1, 2, 3)", "Vector4(1, 2, 3, 4)", "Vector4i(1, 2, 3, 4)", "Transform2D(1, 0, 0, 1, 0, 0)",
        "Matrix32(1, 0, 0, 1, 0, 0)", "Plane(0, 1, 0, 0)", "Quaternion(0, 0, 0, 1)", "Quat(0, 0, 0, 1)", "AABB(0, 0, 0, 1, 1, 1)",
        "Rect3(0, 0, 0, 1, 1, 1)", "Basis(1, 0, 0, 0, 1, 0, 0, 0, 1)", "Matrix3(1, 0, 0, 0, 1, 0, 0, 0, 1)",
        "Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0)", "Transform(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0)",
        "Projection(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)", "Color(1, 0, 0, 1)", "NodePath(\"a/b\")", "RID()", "RID(5)", "Signal()",
        "Callable()", "Object(Node,)", "Object(Resource, \"name\": \"x\")", "Object(Resource, \"name\": \"x\",)", "Array[int]([1, 2])",
        "Array[Node]([])", "Array[Resource]([])", "Array[Unknown]([])", "Dictionary[String, int]({\"a\": 1})", "Dictionary[Resource, Resource]({})",
        "[]", "[1, 2]", "[1, 2,]", "{}", "{\"a\": 1}", "{\"a\": 1,}", "{1: 2, 3: 4}", "PackedByteArray()", "PackedByteArray(1, 2)",
        "PackedByteArray(\"AQI=\")", "PoolByteArray(1)", "ByteArray(1)", "PackedInt32Array(1, 2)", "PackedInt32Array()", "PackedIntArray(1)",
        "PoolIntArray(1)", "IntArray(1)", "PackedInt64Array(1)", "PackedFloat32Array(1.5)", "PackedRealArray(1)", "PoolRealArray(1)", "FloatArray(1)",
        "PackedFloat64Array(1.5)", "PackedStringArray(\"a\", \"b\")", "PackedStringArray(\"a\", \"b\",)", "PackedStringArray()",
        "PoolStringArray(\"a\")", "StringArray(\"a\")", "PackedVector2Array(1, 2)", "PackedVector2Array(1, 2, 3)", "PoolVector2Array(1, 2)",
        "Vector2Array(1, 2)", "PackedVector3Array(1, 2, 3)", "PoolVector3Array(1, 2, 3)", "Vector3Array(1, 2, 3)", "PackedVector4Array(1, 2, 3, 4)",
        "PoolVector4Array(1, 2, 3, 4)", "Vector4Array(1, 2, 3, 4)", "PackedColorArray(1, 0, 0, 1)", "PoolColorArray(1, 0, 0, 1)",
        "ColorArray(1, 0, 0, 1)", "5 ; note",
    ];

    private static readonly string[] Refused =
    [
        "\"\\ud83dx\"", "\"\\ud83d\\n\"", "\"\\ud83d\\ud83d\"", "\"\\ude00\\ud83d\"", "\"\\U01F60\"", "\"\\u00e\"", "\"\\U00D83D\"",
        "not_a_value", "-nan", "TRUE", ".5", "+5", "--5", "0x10", "1.2.3", "1_000", "5 6", "5,", "#xyz", "\"unterminated", "\"\\u00g9\"",
        "\"\\ud83d\"", "\"\\ude00\"", "&name", "^\"path\"", "Vector2(1,2,3)", "Vector2(1)", "Vector2()", "Vector2(1, 2", "Vector2 1, 2)",
        "Vector2(1 2)", "Vector2(1, 2,)", "Vector2(a, b)", "Vector2(\"1\", 2)", "Vector2(1, 2) junk", "Vector2(1, 2))", "vector2(1, 2)",
        "Color(1, 0, 0)", "Color(\"red\")", "NodePath(a)", "NodePath()", "RID(\"x\")", "Signal(\"x\")", "Callable(1)", "Object(Node)",
        "Object(Resource, \"name\" \"x\")", "Object(\"Node\",)", "Resource(1)", "SubResource(\"nope\")", "ExtResource(\"nope\")", "Array[int]()",
        "Array[int]([1, 2]", "Array[]([])", "Array([1])", "Dictionary[String, int]()", "Dictionary[String]({})", "Dictionary({})", "[,]", "[1,,2]",
        "[1 2]", "[1, 2", "[not_a_value]", "{\"a\" 1}", "{\"a\": }", "{\"a\": 1", "{not_a_value: 1}", "PackedByteArray(\"AQI\")",
        "PackedByteArray(\"???\")", "PackedByteArray(\"AQI=\", 1)", "PackedByteArray(1, 2,)", "PackedByteArray(a)", "PackedInt32Array(\"a\")",
        "PackedStringArray(1)", "PackedThingArray()", "Vector2(1; 2)", "[1] [2]", "{} {}", "\"a\" \"b\"", "\"a\" b", "true false", "-", "=", ":", ",",
        ")", "]", "}",
    ];

    /// <summary>Text typed for a scene is read or refused as Godot's loader would.</summary>
    [Fact]
    public void Values_are_read_as_godots_loader_reads_them() => Cases.All(
        (nameof(What_Godots_loader_takes_is_read), What_Godots_loader_takes_is_read),
        (nameof(What_Godots_loader_refuses_or_swallows_lines_over_is_refused), What_Godots_loader_refuses_or_swallows_lines_over_is_refused),
        (nameof(A_file_not_in_the_project_is_refused_where_Godot_loads_by_its_path_alone),
            A_file_not_in_the_project_is_refused_where_Godot_loads_by_its_path_alone),
        (nameof(A_class_the_engine_makes_no_object_of_is_refused), A_class_the_engine_makes_no_object_of_is_refused));

    /// <summary>Each Godot's reader takes what it reads and refuses the rest, and a value nested too deep ends
    /// nothing.</summary>
    [Fact]
    public void Each_godots_reader_takes_its_own_and_nothing_deep_ends_the_ide() => Cases.All(
        (nameof(What_an_older_Godots_reader_refuses_is_refused_for_it), What_an_older_Godots_reader_refuses_is_refused_for_it),
        (nameof(A_value_nested_past_what_is_read_is_refused_and_stops_short_of_the_stack),
            A_value_nested_past_what_is_read_is_refused_and_stops_short_of_the_stack));

    private void What_Godots_loader_takes_is_read()
    {
        string refused = string.Join("\n", Read.Select(text => (text, Why: GodotVariantText.WhyNot(text, Bare))).Where(r => r.Why is not null).Select(r => $"{r.text}: {r.Why}"));
        Assert.True(refused.Length == 0, $"A person's good value would be refused:\n{refused}");
    }

    private void What_Godots_loader_refuses_or_swallows_lines_over_is_refused()
    {
        string read = string.Join("\n", Refused.Where(text => GodotVariantText.WhyNot(text, Bare) is null));
        Assert.True(read.Length == 0, $"These would be written into a scene Godot then fails to load, or loads short of its lines:\n{read}");
    }

    private void A_file_not_in_the_project_is_refused_where_Godot_loads_by_its_path_alone()
    {
        GodotVariantBounds project = new(null, path => path == "res://here.tres", null);
        Assert.Null(GodotVariantText.WhyNot("Resource(\"res://here.tres\")", Bare, project));
        Assert.Equal("the project has no res://missing.tres", GodotVariantText.WhyNot("Resource(\"res://missing.tres\")", Bare, project));
        Assert.Equal("the project has no res://missing.tres", GodotVariantText.WhyNot("[Resource(\"res://missing.tres\")]", Bare, project));
        Assert.Null(GodotVariantText.WhyNot("Resource(\"uid://abc\", \"res://missing.tres\")", Bare, project));
        Assert.Null(GodotVariantText.WhyNot("Resource(\"res://missing.tres\")", Bare));
    }

    private void A_class_the_engine_makes_no_object_of_is_refused()
    {
        GodotVariantBounds project = new(null, null, named => named == "Node");
        Assert.Null(GodotVariantText.WhyNot("Object(Node,)", Bare, project));
        Assert.Equal("Godot makes no object of class Unknown", GodotVariantText.WhyNot("Object(Unknown,)", Bare, project));
        Assert.Equal("Godot makes no object of class Unknown", GodotVariantText.WhyNot("{\"a\": Object(Unknown,)}", Bare, project));
        Assert.Null(GodotVariantText.WhyNot("Object(Unknown,)", Bare));
    }

    private void What_an_older_Godots_reader_refuses_is_refused_for_it() => Cases.Each<(string Engine, string Text, string Why)>(
    [
        ("4.0", "Signal()", "Signal needs Godot 4.1 or newer, and this one is 4.0"),
        ("4.2.2", "PackedVector4Array(1, 2, 3, 4)", "PackedVector4Array needs Godot 4.3 or newer, and this one is 4.2"),
        ("4.2", "Vector4Array(1, 2, 3, 4)", "Vector4Array needs Godot 4.3 or newer, and this one is 4.2"),
        ("4.3", "Dictionary[String, int]({\"a\": 1})", "A typed Dictionary needs Godot 4.4 or newer, and this one is 4.3"),
    ], row => What_an_older_Godots_reader_refuses_is_refused_for_it(row.Engine, row.Text, row.Why));

    private void What_an_older_Godots_reader_refuses_is_refused_for_it(string engine, string text, string why)
    {
        Assert.Equal(why, GodotVariantText.WhyNot(text, Bare, new GodotVariantBounds(Version.Parse(engine), null, null)));
    }

    private void A_value_nested_past_what_is_read_is_refused_and_stops_short_of_the_stack()
    {
        Assert.Null(GodotVariantText.WhyNot(new string('[', 100) + new string(']', 100), Bare));
        Assert.Equal("it is nested too deeply", GodotVariantText.WhyNot(new string('[', 200_000), Bare));
    }
}

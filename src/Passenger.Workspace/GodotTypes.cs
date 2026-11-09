namespace Passenger.Workspace;

/// <summary>Godot's Variant types, by the number its encoding writes in a value's header and an exported property's type.</summary>
public enum GodotType
{
    Nil, Bool, Int, Float, String,
    Vector2, Vector2I, Rect2, Rect2I, Vector3, Vector3I, Transform2D, Vector4, Vector4I, Plane, Quaternion, Aabb, Basis, Transform3D, Projection,
    Color, StringName, NodePath, Rid, Object, Callable, Signal, Dictionary, Array,
    PackedByteArray, PackedInt32Array, PackedInt64Array, PackedFloat32Array, PackedFloat64Array, PackedStringArray, PackedVector2Array,
    PackedVector3Array, PackedColorArray, PackedVector4Array,
}

/// <summary>Godot's Variant types by name, each as GDScript and Godot's variant text write it ("int", "Vector2i", "AABB"),
/// in the order of the Godot Engine's <c>Variant::Type</c> (core/variant/variant.h, MIT): the one table of their names and
/// numbers. Pure.</summary>
public static class GodotTypes
{
    private static readonly string[] Names =
    [
        "Nil", "bool", "int", "float", "String", "Vector2", "Vector2i", "Rect2", "Rect2i", "Vector3", "Vector3i", "Transform2D", "Vector4",
        "Vector4i", "Plane", "Quaternion", "AABB", "Basis", "Transform3D", "Projection", "Color", "StringName", "NodePath", "RID", "Object",
        "Callable", "Signal", "Dictionary", "Array", "PackedByteArray", "PackedInt32Array", "PackedInt64Array", "PackedFloat32Array",
        "PackedFloat64Array", "PackedStringArray", "PackedVector2Array", "PackedVector3Array", "PackedColorArray", "PackedVector4Array",
    ];

    /// <summary>Every type's name, in Godot's order of its types, Nil's first.</summary>
    public static IReadOnlyList<string> All => Names;

    /// <summary>A type's name: "Vector2i", "AABB", "Nil".</summary>
    public static string NameOf(GodotType type) => (uint)type < (uint)Names.Length ? Names[(int)type] : type.ToString();

    /// <summary>The type a name names; null for a name of no Variant type (a class's).</summary>
    public static GodotType? Named(string name) => Array.IndexOf(Names, name) is var at and >= 0 ? (GodotType)at : null;
}

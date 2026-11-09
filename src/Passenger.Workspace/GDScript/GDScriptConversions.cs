namespace Passenger.Workspace.GDScript;

/// <summary>Which of Godot's built-in types converts strictly to another, as <c>Variant::can_convert_strict</c> of the Godot
/// Engine's <c>core/variant/variant.cpp</c> (MIT) has them: what a built-in type's constructor of one argument takes beside
/// its own type, said in Godot's order of its types. The same at 4.7.2 and at 4.8 beta 2.</summary>
public static class GDScriptConversions
{
    private static readonly string[] PackedArrays =
    [
        "PackedByteArray", "PackedInt32Array", "PackedInt64Array", "PackedFloat32Array", "PackedFloat64Array", "PackedStringArray",
        "PackedColorArray", "PackedVector2Array", "PackedVector3Array", "PackedVector4Array",
    ];

    /// <summary>Whether a name is a built-in type's, one GDScript makes by a call of its name.</summary>
    internal static bool IsBuiltin(string name) => GodotTypes.Named(name) is not (null or GodotType.Nil or GodotType.Object);

    /// <summary>Whether a built-in type's values are shared by whatever holds one, rather than copied.</summary>
    internal static bool IsShared(string name) => name is "Array" or "Dictionary" or "Object" or "Callable" or "Signal" || Array.IndexOf(PackedArrays, name) >= 0;

    /// <summary>Each type with the types that convert strictly to it, as can_convert_strict's table lists them.</summary>
    private static readonly Dictionary<string, string[]> Into = new(StringComparer.Ordinal)
    {
        ["bool"] = ["int", "float"],
        ["int"] = ["bool", "float"],
        ["float"] = ["bool", "int"],
        ["String"] = ["NodePath", "StringName"],
        ["Vector2"] = ["Vector2i"],
        ["Vector2i"] = ["Vector2"],
        ["Rect2"] = ["Rect2i"],
        ["Rect2i"] = ["Rect2"],
        ["Transform2D"] = ["Transform3D"],
        ["Vector3"] = ["Vector3i"],
        ["Vector3i"] = ["Vector3"],
        ["Vector4"] = ["Vector4i"],
        ["Vector4i"] = ["Vector4"],
        ["Quaternion"] = ["Basis"],
        ["Basis"] = ["Quaternion"],
        ["Transform3D"] = ["Transform2D", "Quaternion", "Basis", "Projection"],
        ["Projection"] = ["Transform3D"],
        ["Color"] = ["String", "int"],
        ["RID"] = ["Object"],
        ["StringName"] = ["String"],
        ["NodePath"] = ["String"],
        ["Array"] = PackedArrays,
        ["PackedByteArray"] = ["Array"],
        ["PackedInt32Array"] = ["Array"],
        ["PackedInt64Array"] = ["Array"],
        ["PackedFloat32Array"] = ["Array"],
        ["PackedFloat64Array"] = ["Array"],
        ["PackedStringArray"] = ["Array"],
        ["PackedVector2Array"] = ["Array"],
        ["PackedVector3Array"] = ["Array"],
        ["PackedColorArray"] = ["Array"],
        ["PackedVector4Array"] = ["Array"],
    };

    /// <summary>Whether a built-in type is one Godot's table knows, so what converts to it can be said.</summary>
    public static bool Knows(string type) => GodotTypes.Named(type) is not (null or GodotType.Nil);

    /// <summary>The other types that convert strictly to a built-in type, in Godot's order of its types.</summary>
    public static IReadOnlyList<string> From(string type) =>
        Into.TryGetValue(type, out string[]? from) ? [.. GodotTypes.All.Where(o => o != type && from.Contains(o))] : [];

    /// <summary>Whether a built-in type's value converts to another at all, as <c>Variant::can_convert</c> has it: strictly,
    /// or a String to a bool or a number, or anything but an object or null to a String.</summary>
    public static bool Converts(string from, string to) =>
        from == to || (from == "Nil" ? to == "Object"
            : to is "bool" or "int" or "float" && from == "String" || to == "String" && from != "Object" || From(to).Contains(from));

    /// <summary>A constructor's first argument as Godot's reduce_call says what it may be: the type itself, then each that
    /// converts to it, joined as <c>Vector2" or "Vector2i</c> or <c>int", "bool", or "float</c> between the words' quotes.</summary>
    public static string Expected(string type)
    {
        IReadOnlyList<string> from = From(type);
        return from.Count switch
        {
            0 => type,
            1 => $"{type}\" or \"{from[0]}",
            _ => $"{type}\", \"{string.Join("\", \"", from.Take(from.Count - 1))}\", or \"{from[^1]}",
        };
    }
}

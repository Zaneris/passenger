using System.Globalization;
using Avalonia.Media;
using Passenger.Dap;

namespace Passenger.App.Godot;

/// <summary>How a Godot game's C# values read in the debugger, as Godot prints them (<see cref="DapTypePresentation"/>):
/// its math types on one line from their fields, which runs none of the game's code; a Color so too, with a swatch
/// (<see cref="Swatch"/>); a node by its name and path, opening to its script's members, the exported ones first, then
/// its children; a StringName and a NodePath as their text; a Variant as what it holds. Each names GodotSharp's types, so
/// a program without them takes none.</summary>
public static class GodotValues
{
    private const string Assembly = "GodotSharp";

    /// <summary>The type of Godot's colour, whose value reads as its four channels (<see cref="Swatch"/>).</summary>
    private const string Colour = "Godot.Color";

    public static IReadOnlyList<DapTypePresentation> Presentations { get; } =
    [
        Shown("Godot.Vector2", "({X}, {Y})"),
        Shown("Godot.Vector2I", "({X}, {Y})"),
        Shown("Godot.Vector3", "({X}, {Y}, {Z})"),
        Shown("Godot.Vector3I", "({X}, {Y}, {Z})"),
        Shown("Godot.Vector4", "({X}, {Y}, {Z}, {W})"),
        Shown("Godot.Vector4I", "({X}, {Y}, {Z}, {W})"),
        Shown("Godot.Quaternion", "({X}, {Y}, {Z}, {W})"),
        Shown(Colour, "({R}, {G}, {B}, {A})"),
        Shown("Godot.Rect2", "[P: {_position}, S: {_size}]"),
        Shown("Godot.Rect2I", "[P: {_position}, S: {_size}]"),
        Shown("Godot.Aabb", "[P: {_position}, S: {_size}]"),
        Shown("Godot.Plane", "[N: {_normal}, D: {_d}]"),
        Shown("Godot.Transform2D", "[X: {X}, Y: {Y}, O: {Origin}]"),
        // A basis holds its rows, and Godot prints its columns.
        Shown("Godot.Basis", "[X: ({Row0.X}, {Row1.X}, {Row2.X}), Y: ({Row0.Y}, {Row1.Y}, {Row2.Y}), Z: ({Row0.Z}, {Row1.Z}, {Row2.Z})]"),
        Shown("Godot.Transform3D", "[X: ({Basis.Row0.X}, {Basis.Row1.X}, {Basis.Row2.X}), Y: ({Basis.Row0.Y}, {Basis.Row1.Y}, {Basis.Row2.Y}), "
            + "Z: ({Basis.Row0.Z}, {Basis.Row1.Z}, {Basis.Row2.Z}), O: {Origin}]"),
        Shown("Godot.Projection", "[X: {X}, Y: {Y}, Z: {Z}, W: {W}]"),
        Shown("Godot.Rid", "RID({_id})"),
        // Their text is the engine's, which only a call reads; what they hold beside it is under Raw View.
        Shown("Godot.StringName", "{ToString()}") with { Rows = [] },
        Shown("Godot.NodePath", "{ToString()}") with { Rows = [] },
        Shown("Godot.Variant", "{Obj}"),
        // A node's path is asked only of one in the tree, which Godot says of any other as an error.
        Shown("Godot.Node", "{Name.ToString(),nq} ({(IsInsideTree() ? GetPath().ToString() : \"not in the tree\"),nq})") with
        {
            Rows = [new DapPresentedRow("Children", "GetChildren()")],
            First = "Godot.ExportAttribute",
        },
    ];

    private static DapTypePresentation Shown(string type, string value) => new() { Type = type, Assembly = Assembly, Value = value };

    /// <summary>The colour a value of Godot's Color shows, read from its four channels as its presentation writes them, or
    /// null for a row of another type or one that did not read so (an error in place of a channel).</summary>
    public static Color? Swatch(string? type, string value)
    {
        if (type != Colour || !value.StartsWith('(') || !value.EndsWith(')'))
        {
            return null;
        }
        string[] parts = value[1..^1].Split(", ");
        float[] channels = new float[parts.Length];
        for (int at = 0; at < parts.Length; at++)
        {
            if (!float.TryParse(parts[at], NumberStyles.Float, CultureInfo.CurrentCulture, out channels[at])
                && !float.TryParse(parts[at], NumberStyles.Float, CultureInfo.InvariantCulture, out channels[at]))
            {
                return null;
            }
        }
        return channels.Length == 4 ? Color.FromArgb(Byte(channels[3]), Byte(channels[0]), Byte(channels[1]), Byte(channels[2])) : null;

        // A channel past the range of a screen's (an HDR colour's) shows as the nearest it has.
        static byte Byte(float channel) => (byte)Math.Round(Math.Clamp(channel, 0f, 1f) * 255f);
    }
}

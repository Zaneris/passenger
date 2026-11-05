namespace Passenger.Workspace;

/// <summary>What the person's Godot editor holds of a file of its project, which a write of Passenger's would go over: a
/// scene it holds unsaved edits in; a scene it has open where it cannot say whether its edits are saved (a Godot before
/// 4.7, which tells no addon); or a resource its inspector has open, of which no Godot says whether it is saved.</summary>
public enum GodotHold
{
    Unsaved,
    PerhapsUnsaved,
    Inspected,
}

/// <summary>A file as Godot's editor holds it, from what Passenger's addon there last said: its open and unsaved scenes, and
/// the resource its inspector shows, each by its res:// path. Pure.</summary>
public static class GodotHolds
{
    /// <summary>How the editor holds a file of its project (res://), or null where a write of it goes over nothing it holds.</summary>
    public static GodotHold? Of(string res, IReadOnlyCollection<string> open, IReadOnlyCollection<string> unsaved, bool saysUnsaved, string? inspected) =>
        unsaved.Contains(res, StringComparer.Ordinal) ? GodotHold.Unsaved
        : !saysUnsaved && open.Contains(res, StringComparer.Ordinal) ? GodotHold.PerhapsUnsaved
        : res == inspected && !GodotProject.IsScene(res) ? GodotHold.Inspected
        : null;

    /// <summary>Whether a write of the file, once made, may have Godot's editor read it again: a scene it has open and saved.</summary>
    public static bool Reloads(string res, IReadOnlyCollection<string> open, IReadOnlyCollection<string> unsaved, bool saysUnsaved) =>
        saysUnsaved && open.Contains(res, StringComparer.Ordinal) && !unsaved.Contains(res, StringComparer.Ordinal);

    /// <summary>What a hold means for a write, of the file by its name.</summary>
    public static string Said(string name, GodotHold hold) => hold switch
    {
        GodotHold.Unsaved => $"Godot's editor holds unsaved edits in {name}",
        GodotHold.PerhapsUnsaved => $"Godot's editor has {name} open, and this Godot does not say whether its edits there are saved",
        _ => $"Godot's editor has {name} open in its inspector, and Godot does not say whether a resource holds unsaved edits",
    };
}

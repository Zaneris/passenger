using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>What Passenger's addon answers of the scene Godot's editor edits, as godot_editor gives it to an agent: its
/// nodes with the properties their file stores, its connections and selection, and whether it is saved, which a Godot
/// before 4.7 does not say. Pure.</summary>
public static class GodotEditedScenes
{
    /// <summary>The edited scene as the addon read it: in words whether it is saved, where its Godot says.</summary>
    public static GodotEditorAnswer Of(JsonElement read)
    {
        string scene = JsonMembers.Text(read, "scene") ?? string.Empty;
        bool? unsaved = JsonMembers.Bool(read, "unsaved");
        List<GodotSceneNode> nodes = [.. JsonMembers.Items(read, "nodes").Select(n => new GodotSceneNode(
            JsonMembers.Text(n, "path") ?? ".", JsonMembers.Text(n, "type") ?? string.Empty, JsonMembers.Text(n, "script") is { Length: > 0 } script ? script : null,
            JsonMembers.Text(n, "instance"),
            JsonMembers.Properties(n, "properties") is { Count: > 0 } stored
                ? stored.ToDictionary(p => p.Name, p => JsonMembers.Text(JsonMembers.Object(n, "properties"), p.Name) ?? string.Empty)
                : null))];
        List<string> connections = [.. JsonMembers.Items(read, "connections").Select(c =>
            $"{JsonMembers.Text(c, "from")} {JsonMembers.Text(c, "signal")} → {JsonMembers.Text(c, "to")} {JsonMembers.Text(c, "method")}")];
        string said = unsaved switch
        {
            true => $"{scene} as Godot's editor holds it, with changes not yet saved",
            false => $"{scene} as Godot's editor holds it, saved",
            null => $"{scene} as Godot's editor holds it (this Godot does not say whether it is saved)",
        };
        return new GodotEditorAnswer(said, scene, unsaved, Nodes: nodes, Connections: connections.Count > 0 ? connections : null,
            Selected: JsonMembers.Texts(read, "selected") is { Count: > 0 } selected ? selected : null);
    }
}

/// <summary>What godot_editor came to: in words, with the scene and node it reached; a read with the scene's nodes, its
/// connections (source signal → target method) and what is selected.</summary>
public sealed record GodotEditorAnswer(string Said, string? Scene = null, bool? Unsaved = null, string? Node = null,
    IReadOnlyList<GodotSceneNode>? Nodes = null, IReadOnlyList<string>? Connections = null, IReadOnlyList<string>? Selected = null);

/// <summary>A node of the edited scene: its path from the root, type, script, the scene it instances, and the properties
/// its scene's file stores, each as a .tscn writes it.</summary>
public sealed record GodotSceneNode(string Path, string Type, string? Script = null, string? Instance = null, IReadOnlyDictionary<string, string>? Properties = null);

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Passenger.App.Godot;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>The person's Godot editor as an agent acts in it, through Passenger's addon there: its scenes opened, read as it
/// holds them and selected in, changed each as one action of its own undo history, so the person undoes it there and the
/// file is written only as they save it; and a scene played there, debugged here.</summary>
public sealed partial class PassengerTools
{
    /// <summary>godot_editor's actions that change no scene, which never ask the person (<see cref="ConnectedTool"/>).</summary>
    internal static readonly IReadOnlySet<string> GodotEditorReads = new HashSet<string>(StringComparer.Ordinal) { "open", "read", "select" };

    /// <summary>How long the editor has to answer an act.</summary>
    private static TimeSpan GodotEditorLimit => Waits.Of(TimeSpan.FromSeconds(30));

    /// <summary>How long a game played in the editor has to come in, built here first.</summary>
    private static TimeSpan GodotPlayLimit => Waits.Of(TimeSpan.FromMinutes(5));

    [McpServerTool(Name = McpTools.GodotEditor, Title = "Act in Godot's editor"), Description(
        "Acts in the person's Godot editor on the folder's Godot project, through Passenger's addon there. action: open (scene), " +
        "read (the edited scene as the editor holds it, unsaved changes too: each node's path, type, script and the properties its " +
        "file stores, its connections and the selection), select (node), play (scene, else the main scene: played there, debugged " +
        "here, the app_ tools reaching it), and, each one step of Godot's Undo there, written to the file only as the person saves: " +
        "set (node's properties), add (a node of type, a class or a res:// scene, under node, with name and properties), move " +
        "(node under to, at index), remove (node), connect (node's signal to method on to). Each acts on scene where given, else " +
        "the one edited; node and to are paths from its root (. the root); properties' values are as a .tscn writes them (12, " +
        "true, \"text\", Vector2(4, 8), res://icon.svg). open, read and select never ask the person.")]
    public Task<GodotEditorAnswer> GodotEditor(string action, string? scene = null, string? node = null, JsonElement? properties = null,
        string? type = null, string? name = null, string? to = null, int? index = null, string? signal = null, string? method = null,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string act = action.Trim().ToLowerInvariant();
        (string game, string? res) = window.GodotSceneOf(scene);
        if (act == "play")
        {
            return new GodotEditorAnswer(await PlayInGodotEditorAsync(game, res, cancel));
        }
        string request = act switch
        {
            "open" => res is null ? throw new ArgumentException("open names the scene to open") : "scene_open",
            "read" => "scene_read",
            "select" => "scene_select",
            "set" or "add" or "move" or "remove" or "connect" => "scene_change",
            _ => throw new ArgumentException($"{action} is no action: open, read, select, play, set, add, move, remove or connect"),
        };
        GodotEditorPeer editor = await window.GodotEditorForAgentAsync(game);
        JsonObject asked = new() { ["action"] = act, ["scene"] = res ?? string.Empty, ["node"] = node ?? "." };
        if (properties is { ValueKind: JsonValueKind.Object } given)
        {
            // Each value as the text a .tscn writes, which the addon reads by its property's type.
            asked["properties"] = new JsonObject([.. given.EnumerateObject().Select(p =>
                KeyValuePair.Create(p.Name, (JsonNode?)(JsonMembers.Scalar(given, p.Name) ?? p.Value.GetRawText())))]);
        }
        foreach ((string key, string? value) in new[] { ("type", type), ("name", name), ("to", to), ("signal", signal), ("method", method) })
        {
            if (value is not null)
            {
                asked[key] = value;
            }
        }
        if (index is int at)
        {
            asked["index"] = at;
        }
        AgentCall.Say(act == "read" ? "Reading the scene Godot's editor edits" : $"Asking Godot's editor to {act}");
        JsonElement answer;
        try
        {
            answer = await editor.RequestAsync(request, asked, GodotEditorLimit, cancel);
        }
        catch (GodotEditorRefused refused)
        {
            throw new InvalidOperationException(refused.Message);
        }
        return act == "read" ? GodotEditedScenes.Of(answer)
            : new GodotEditorAnswer(JsonMembers.Text(answer, "said") ?? $"{act} done", Scene: JsonMembers.Text(answer, "scene"), Node: JsonMembers.Text(answer, "node"));
    });

    /// <summary>A scene played in the person's editor, debugged here once it came in: what it came to.</summary>
    private async Task<string> PlayInGodotEditorAsync(string game, string? scene, CancellationToken cancel)
    {
        GodotGame? before = Session.Game;
        if (await window.PlayInGodotEditorForAgentAsync(game, scene, cancel) is { } refused)
        {
            throw new InvalidOperationException(refused);
        }
        AgentCall.Say("Waiting for the game Godot's editor plays");
        await When(() => Session.Game is { Relayed: true } game && game != before, GodotPlayLimit, cancel, On(h => Session.LiveAppChanged += h, h => Session.LiveAppChanged -= h));
        if (Session.Game is not { Relayed: true } played || played == before)
        {
            throw new TimeoutException($"Godot's editor took the play, and its game did not come in within {GodotPlayLimit.TotalMinutes:0} minutes: {window.Status}");
        }
        // Held at its start for the debugger, it is either attached to or let go saying why.
        await When(() => played.Debugged is not null || played.Undebugged is not null, AppUiLimit, cancel, On(h => played.DebuggedChanged += h, h => played.DebuggedChanged -= h));
        string debugged = played.Debugged is not null ? ", debugged here" : played.Undebugged is { } why ? $", undebugged: {why}" : ", the debugger yet to attach";
        return $"{played.Name} plays in Godot's editor{debugged}; the app_ tools reach it as app {played.Name}, and stop ends it";
    }
}

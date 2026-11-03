using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The one gate every write of files takes (<see cref="Editors.EditorArea.ClearAsync"/>), for what the person's
/// Godot editor holds of them, as Passenger's addon there says (<see cref="GodotHolds"/>): a person's change that would
/// write over it asks first, saying what Godot holds; an agent's is refused (<see cref="Refusals.HeldInGodot"/>); an
/// autosave passes such a file by. Once Passenger has written a scene Godot has open and saved, the addon has Godot read
/// it again, so no "newer on disk" question comes.</summary>
public partial class MainWindow
{
    /// <summary>What Godot's editor holds of a file that a write of it would go over, in words, or null.</summary>
    private string? HeldInGodot(string path)
    {
        if (session.GodotEditors.Editors is not [_, ..] editors || GodotFolder() is not { } folder || !PathNames.IsUnder(path, folder))
        {
            return null;
        }
        string res = GodotProject.ResourcePath(folder, path);
        foreach (GodotEditorPeer peer in editors)
        {
            if (Passenger.Workspace.GodotHolds.Of(res, peer.OpenScenes, peer.UnsavedScenes, peer.SaysUnsaved, peer.Inspected) is { } hold)
            {
                return Passenger.Workspace.GodotHolds.Said(Path.GetFileName(path), hold);
            }
        }
        return null;
    }

    /// <summary>What Godot's editor holds of a file, as the gate sees it: why a write would ask, or null.</summary>
    public string? HeldByGodot(string path) => HeldInGodot(PathNames.Canonical(path));

    /// <summary>Whether Godot's editor has a scene open, as its addon last said.</summary>
    public bool GodotHasOpen(string path) => GodotFolder() is { } folder && PathNames.IsUnder(path, folder)
        && session.GodotEditors.Editors.Any(e => e.OpenScenes.Contains(GodotProject.ResourcePath(folder, PathNames.Canonical(path)), StringComparer.Ordinal));

    /// <summary>The gate's answer to a write of files Godot's editor holds: null where the person lets it go over them, else
    /// why not, an agent's refused unasked.</summary>
    private async Task<string?> WritingOverGodotAsync(IReadOnlyList<string> paths)
    {
        UiThread.Check();
        string said = string.Join("; ", paths.Select(HeldInGodot).OfType<string>());
        if (Mcp.AgentCall.Current is not null || AgentAsks.Beside || AgentsOwn)
        {
            return Refusals.HeldInGodot(said);
        }
        bool one = paths.Count == 1;
        return await ConfirmAsync("Write Over What Godot Holds?",
            $"{said}. Passenger writes {(one ? "that file" : "those files")} now; Godot's editor then asks whether to keep its own version or take this one.",
            "Write Anyway", null) ? null : $"{said}; {(one ? "it was left as it is" : "they were left as they are")}";
    }

    /// <summary>A file Passenger wrote, from any thread: a scene Godot's editor has open and saved is read there again.</summary>
    private void WroteForGodot(string path)
    {
        if (!GodotProject.IsScene(path))
        {
            return;
        }
        UiThread.Post(() =>
        {
            if (GodotFolder() is not { } folder || !PathNames.IsUnder(path, folder))
            {
                return;
            }
            string res = GodotProject.ResourcePath(folder, path);
            foreach (GodotEditorPeer peer in session.GodotEditors.Editors.Where(p => Passenger.Workspace.GodotHolds.Reloads(res, p.OpenScenes, p.UnsavedScenes, p.SaysUnsaved)))
            {
                IdeErrors.Observe(peer.AskAsync("reload", new System.Text.Json.Nodes.JsonObject { ["path"] = res }, TimeSpan.FromSeconds(10)));
            }
        });
    }

    /// <summary>A node Godot's editor selected in its scene dock, while Settings has it shown here: its scene shown at the
    /// node's section, which the outline follows, and the node's script open behind at its class, the keys left where they
    /// are.</summary>
    private void ShowGodotSelection(string? scene, string? node)
    {
        UiThread.Check();
        if (!settings.GodotSelectionFollows || scene is null || node is null || !scene.StartsWith("res://", StringComparison.Ordinal) || GodotFolder() is not { } folder)
        {
            return;
        }
        string file = PathNames.Canonical(Path.Combine(folder, scene["res://".Length..]));
        if (Text.FileText.Load(file) is not { } read || GodotFile.Parse(read.Text) is not { } parsed
            || parsed.Nodes.FirstOrDefault(n => n.PathInScene == node) is not { } selected)
        {
            return;
        }
        if (parsed.External(selected.Script)?.Path is { } res && res.StartsWith("res://", StringComparison.Ordinal))
        {
            string script = PathNames.Canonical(Path.Combine(folder, res["res://".Length..]));
            if (Editors.Find(script) is null && Editors.Open(script, focus: false) is null && Editors.Find(script) is { } opened)
            {
                opened.Selections = Passenger.Text.SelectionSet.Single(ClassAt(opened.Text.Snapshot, Path.GetFileNameWithoutExtension(script)));
            }
        }
        if (Editors.Open(file, focus: false) is null && Editors.Active is { } shown && PathNames.Same(shown.Path, file))
        {
            Editors.CodeEditor.GoTo(new Passenger.Text.TextPosition(selected.Section.Line + 1, 1));
        }
    }

    /// <summary>Where a script declares its class (C#'s class of the file's name), else its start.</summary>
    private static int ClassAt(Passenger.Text.TextSnapshot text, string name)
    {
        System.Text.RegularExpressions.Match found = System.Text.RegularExpressions.Regex.Match(text.ToString(), $@"\bclass\s+{System.Text.RegularExpressions.Regex.Escape(name)}\b");
        return found.Success ? found.Index : 0;
    }
}

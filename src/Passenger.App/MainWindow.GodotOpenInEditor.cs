using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App;

/// <summary>Open in Godot Editor: Godot's editor on the scene at hand, at its node: a scene's node the caret is in, the node
/// a script open is on, or the node the caret's node path names (a GetNode string in C#; <c>$Path</c>, <c>%Unique</c> or a
/// get_node string in GDScript, read by its parser). An editor connected through Passenger's addon opens the scene and selects the node itself, in front, so no
/// second editor starts. With none open, the folder's Godot Editor configuration starts one on the scene, Godot's command
/// line naming the scene alone: the node is selected once the addon there connects, and only so. An editor open without the
/// addon is left alone, since its command line would start a second.</summary>
public partial class MainWindow
{
    /// <summary>A C# GetNode path around the caret on its line: the path's own characters.</summary>
    private static readonly Regex NodePathCall = new(@"GetNode(?:OrNull)?\s*(?:<[^()]*>)?\s*\(\s*(?:new\s+NodePath\s*\(\s*)?""(?<path>[^""]*)""",
        RegexOptions.CultureInvariant);

    /// <summary>The scene and node a launch from here asked an editor to open, which the first editor to connect through the
    /// addon then selects; null for none.</summary>
    private (string Scene, string Node)? selectOnConnect;

    private void AddGodotOpenInEditor()
    {
        actions!.Add(new IdeAction(AppActions.GodotOpenInEditor, "Open in Godot Editor", "Godot", _ => IdeErrors.Observe(OpenInGodotEditorAsync()))
        {
            WhyNot = _ => Folder is null ? Refusals.NoFolder : GodotFolder() is not { } folder ? Refusals.NoGodotProject
                : Editors.Active?.Path is { } path && PathNames.IsUnder(path, folder) && (GodotProject.IsScene(path) || IsGodotScript(path)) ? null : Refusals.NoGodotScene,
            Menus = [new MenuPlace("Tools", 0, 8)],
            Words = ["Godot", "scene", "node", "editor", "select"],
            Works = AppActions.GodotOpenInEditor,
        });
    }

    private static bool IsGodotScript(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase);

    private async Task OpenInGodotEditorAsync()
    {
        UiThread.Check();
        if (work.Begin(AppActions.GodotOpenInEditor, "Finding the scene and node at hand", stoppable: false) is not { } working)
        {
            return;
        }
        using (working)
        {
            (bool done, string said) = await OpenInGodotEditorAsync(working);
            Told(working, done, said);
        }
    }

    private async Task<(bool Done, string Said)> OpenInGodotEditorAsync(Working working)
    {
        if (GodotFolder() is not { } folder || Editors.Active is not { } shown || !PathNames.IsUnder(shown.Path, folder))
        {
            return (false, GodotFolder() is null ? Refusals.NoGodotProject : Refusals.NoGodotScene);
        }
        int caret = Editors.CodeEditor.Document == shown.Text ? Editors.CodeEditor.Selections.Primary.Caret : shown.Selections.Primary.Caret;
        (string? scene, string node, string? why) = await GodotPlaceAtAsync(folder, shown, caret);
        if (scene is null)
        {
            return (false, why ?? Refusals.NoGodotScene);
        }
        string name = Path.GetFileName(folder);
        string place = node == "." ? Path.GetFileName(scene) : $"{node} in {Path.GetFileName(scene)}";
        if (session.GodotEditors.Editors.FirstOrDefault(e => e.Addon >= AddonActs) is { } editor)
        {
            working.Say($"Asking Godot's editor to show {place}");
            editor.SelectionAsked = (scene, node);
            try
            {
                await editor.RequestAsync("scene_select", new JsonObject { ["scene"] = scene, ["node"] = node, ["front"] = !AgentAsks.Beside }, Waits.Of(TimeSpan.FromSeconds(30)));
            }
            catch (Exception refused) when (refused is GodotEditorRefused or TimeoutException)
            {
                editor.SelectionAsked = null;
                return (false, $"Godot's editor on {name} did not show {place}: {refused.Message}");
            }
            return (true, $"Godot's editor on {name} shows {place}{(why is null ? string.Empty : $" ({why})")}");
        }
        if (session.GodotEditors.Editors.FirstOrDefault() is { } older)
        {
            return (false, $"Passenger's addon in {name} is version {older.Addon}, which opens no scene as asked: Install Passenger's Godot Addon… updates it, and Godot's editor takes it as it next opens the project");
        }
        working.Say("Looking for Godot's editor on the project");
        if (await session.GodotIde.IsOpenAsync(folder))
        {
            return (false, $"Godot's editor is open on {name} without Passenger's addon, which alone opens a scene in an editor already running (Godot's command line would start a second editor): "
                + "Install Passenger's Godot Addon…, which Godot's editor takes as it next opens the project");
        }
        if (Projects.FirstOrDefault(p => p.GodotFolder is { } at && PathNames.Same(at, folder)) is not { } project
            || session.Configurations.All.FirstOrDefault(c => c.GodotEditor && PathNames.Same(c.Project, project.Path)) is not { } configuration)
        {
            return (false, $"Godot's editor is not open on {name}, and the folder has no configuration that opens it");
        }
        bool addon = await BackgroundWork.Run(() => FileText.Load(Path.Combine(folder, GodotProject.FileName)) is { } text
            && GodotPlugins.Read(folder, GodotAddon.Enabled(text.Text)).Any(p => GodotAddon.Is(p) && p.Enabled));
        working.Say($"Starting Godot's editor on {Path.GetFileName(scene)}");
        selectOnConnect = addon && node != "." ? (scene, node) : null;
        string kept = session.Configurations.Chosen?.Name ?? configuration.Name;
        session.Configurations.Select(configuration.Name);
        // Godot's command line opens a scene in its editor, which it names after --editor, and no node in it.
        string? refusedRun = await buildRun.RunAsync(arguments: scene);
        session.Configurations.Select(kept);
        if (refusedRun is not null)
        {
            selectOnConnect = null;
            return (false, $"Godot's editor could not open on {name}: {refusedRun}");
        }
        return (true, node == "." ? $"Godot's editor opens on {name} at {Path.GetFileName(scene)}"
            : addon ? $"Godot's editor opens on {name} at {Path.GetFileName(scene)}, where Passenger's addon selects {node} once it is up"
            : $"Godot's editor opens on {name} at {Path.GetFileName(scene)}; its command line opens a scene, never a node in it, so selecting {node} there takes Passenger's addon (Install Passenger's Godot Addon…)");
    }

    /// <summary>The scene (res://) and node path in it a file at a caret leads to, with words where it says more (a node path
    /// the scenes do not know); no scene, saying why, where there is none.</summary>
    private async Task<(string? Scene, string Node, string? Why)> GodotPlaceAtAsync(string folder, OpenDocument shown, int caret)
    {
        TextSnapshot text = shown.Text.Snapshot;
        if (GodotResPaths.Of(folder, shown.Path) is not { } res)
        {
            return (null, ".", null);
        }
        if (GodotProject.IsScene(shown.Path))
        {
            int line = text.GetLineFromOffset(Math.Min(caret, text.Length));
            Passenger.Workspace.GodotNode? at = GodotFile.Parse(text.ToString()) is { } parsed ? parsed.Nodes.LastOrDefault(n => n.Section.Line <= line) : null;
            return (res, at?.PathInScene ?? ".", null);
        }
        GodotScenes scenes = await session.GodotScenes.ScenesAsync(folder);
        string? main = await BackgroundWork.Run(() => GodotProject.Read(folder)?.MainScene);
        IReadOnlyList<(string Scene, GodotTreeNode Node)> attached = scenes.AttachedOf(res) is { Count: > 0 } own ? own : scenes.RunsOf(res);
        if (attached.Count == 0)
        {
            return (null, ".", $"No scene of the project runs {shown.Name}: attach it to a node in Godot's editor first");
        }
        (string scene, GodotTreeNode node) = attached.FirstOrDefault(a => a.Scene == main) is { Node: not null } first ? first : attached[0];
        int offset = Math.Min(caret, text.Length);
        int row = text.GetLineFromOffset(offset);
        int column = offset - text.GetLineStart(row);
        // A GDScript's own parser reads its $Path, %Unique and get_node strings.
        string? path = shown.Path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)
            ? await BackgroundWork.Run(() => GDScriptNodePaths.At(GDScriptTree.Parse(text.ToString()), offset))
            : NodePathCall.Matches(text.GetLineText(row)).FirstOrDefault(m => m.Index <= column && column <= m.Index + m.Length)?.Groups["path"].Value;
        if (path is null)
        {
            return (scene, node.Path, null);
        }
        if (GodotScenes.Resolve(node, path).Found is not { } target)
        {
            return (scene, node.Path, $"no node of {Path.GetFileName(scene)} is at '{path}' from {node.Name}, so it shows {node.Name}");
        }
        // A node of a scene this one instances opens in the scene that declares it.
        return target == target.Root || target.Owner == target.Root ? (scene, target.Path, null) : (target.Declared.Scene, target.Declared.Node.PathInScene, null);
    }

    /// <summary>An editor connected through the addon: the node a launch from here asked for selected there, once.</summary>
    private void SelectOnConnect(GodotEditorPeer editor)
    {
        if (selectOnConnect is not (string scene, string node) || editor.Addon < AddonActs)
        {
            return;
        }
        selectOnConnect = null;
        editor.SelectionAsked = (scene, node);
        IdeErrors.Observe(editor.AskAsync("scene_select", new JsonObject { ["scene"] = scene, ["node"] = node, ["front"] = true }, Waits.Of(TimeSpan.FromSeconds(30))));
    }
}

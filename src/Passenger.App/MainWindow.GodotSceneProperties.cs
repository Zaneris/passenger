using Passenger.App.Godot;
using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Roslyn.Extension;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App;

/// <summary>A scene's node chosen in the Structure window shows its properties under the outline
/// (<see cref="GodotSceneProperties"/>): what the scene sets, its script's exports (C#'s from the server's extension,
/// GDScript's as Passenger reads it) and its engine class's properties at their defaults as the Godot found lists them;
/// each edited with the Live Visual Tree's value editors and written into the scene as one command of Undo, through the
/// gate over what Godot's editor holds.</summary>
public partial class MainWindow
{
    private GodotSceneProperties? sceneProperties;

    /// <summary>The node's properties asked last, so an answer overtaken by a later choice is passed over.</summary>
    private int sceneNodeAsked;

    /// <summary>The properties shown under the Structure window's outline of a scene, made as one is first outlined.</summary>
    public GodotSceneProperties? SceneProperties => sceneProperties;

    /// <summary>The Structure window chose a row (or none): under a scene's outline, the properties of the node chosen.</summary>
    private async Task ShowSceneNodeAsync(SymbolRow? row)
    {
        UiThread.Check();
        int asked = ++sceneNodeAsked;
        if (structure is not { } view)
        {
            return;
        }
        if (view.Outlined is not { } scene || !GodotProject.IsScene(scene) || GodotFolder() is not { } folder || !PathNames.IsUnder(scene, folder))
        {
            view.Details = null;
            sceneProperties?.Show(null);
            return;
        }
        sceneProperties ??= new GodotSceneProperties((popup, from) => popups.Show(popup, popups.PointIn(from, new Avalonia.Point(0, from.Bounds.Height + 4))),
            () => popups.Close(), words => status.Text = words);
        view.Details = sceneProperties;
        row = NodeOf(view.Rows, row);
        if (row is not { Kind: SymbolRow.NodeKind } || !PathNames.Same(row.Path, scene) || Editors.TextOf(scene)?.ToString() is not { } text
            || GodotFile.Parse(text).Nodes.FirstOrDefault(n => n.Section.Line == row.Position.Line) is not { } node)
        {
            sceneProperties.Show(null);
            return;
        }
        string res = GodotProject.ResourcePath(folder, scene);
        GodotScenes scenes = await session.GodotScenes.ScenesAsync(folder);
        GodotTreeNode? tree = scenes.Tree(res)?.Below().FirstOrDefault(n => n.Written.Any(w => w.Scene == res && w.Node.Section.Start == node.Section.Start));
        string? type = node.Type ?? tree?.Type;
        string? script = tree?.Script;
        GodotClassDefaults? classes = await session.GodotApis.ClassesAsync();
        IReadOnlyList<GodotScriptExport> exports = [];
        if (script is not null && GodotChecks.IsCSharp(script) && scenes.FileOf(script) is { } code && session.CSharp is { } service)
        {
            GodotCodeRequest request = await BackgroundWork.Run(() => GodotChecks.SceneAsks(code, scenes, res));
            exports = [.. ((await service.GodotSceneCodeAsync(request, CancellationToken.None))?.Classes.FirstOrDefault(c => c.Scripts.Contains(script))?.Exports ?? [])
                .Where(e => e.Inspected is not null).Select(e => new GodotScriptExport(e.Name, e.Inspected!.Type, e.Inspected.Hint, e.Inspected.HintString, e.Inspected.Default))];
        }
        else if (script is not null && script.EndsWith(".gd", StringComparison.Ordinal) && scenes.FileOf(script) is { } gdscript && Editors.TextOf(gdscript)?.ToString() is { } source)
        {
            exports = await BackgroundWork.Run(() => GDScriptExports.Of(source));
        }
        if (asked != sceneNodeAsked)
        {
            return;
        }
        string path = node.PathInScene;
        string? scriptName = script is null ? null : Path.GetFileName(script);
        IReadOnlyList<GodotBeneath> beneath = Beneath(scenes, res, tree);
        sceneProperties.Show(new GodotSceneValues(scene, path, node.Name, type, file => Editors.TextOf(file)?.ToString(), (name, changes) => Assist.ApplyFileChangesAsync(name, changes),
            now => GodotKeep.NodeIn(GodotFile.Parse(now), path) is { } section ? GodotSceneValues.Of(scene, now, section, type, classes, exports, scriptName, beneath) : []));
    }

    /// <summary>The node a row of a scene's outline stands for: a node's own, or the node a row under it is of (its script,
    /// the scene it instances, a connection it sends), so the caret on such a line still shows that node's properties.</summary>
    private static SymbolRow? NodeOf(IReadOnlyList<SymbolRow> rows, SymbolRow? row)
    {
        if (row is null or { Kind: SymbolRow.NodeKind })
        {
            return row;
        }
        for (int at = IndexOf(rows, row) - 1; at >= 0; at--)
        {
            if (rows[at].Depth < row.Depth)
            {
                return rows[at] is { Kind: SymbolRow.NodeKind } node ? node : row;
            }
        }
        return row;

        static int IndexOf(IReadOnlyList<SymbolRow> rows, SymbolRow row)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (ReferenceEquals(rows[i], row))
                {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>The sections of the scenes a node is instanced from, which give it its values beneath the scene's own: each
    /// scene's that writes it but this one, its declaration first; an instance's root's the instanced scene's root.</summary>
    private static IReadOnlyList<GodotBeneath> Beneath(GodotScenes scenes, string res, GodotTreeNode? tree)
    {
        if (tree is null)
        {
            return [];
        }
        List<GodotBeneath> beneath = [.. tree.Written.Where(w => w.Scene != res && scenes.Files.ContainsKey(w.Scene))
            .Select(w => new GodotBeneath(Path.GetFileName(w.Scene["res://".Length..]), scenes.Files[w.Scene].Text, w.Node.Section))];
        if (tree.Instance is { } instanced && scenes.Files.TryGetValue(instanced, out GodotFile? file) && file.Nodes.FirstOrDefault(n => n.Parent is null) is { } root
            && !beneath.Any(b => b.Section.Start == root.Section.Start && b.Text == file.Text))
        {
            beneath.Insert(0, new GodotBeneath(Path.GetFileName(instanced["res://".Length..]), file.Text, root.Section));
        }
        return beneath;
    }
}

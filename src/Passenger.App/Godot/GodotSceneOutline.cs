using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A Godot scene's outline as Godot builds its tree (<see cref="GodotScenes.Tree"/>): each node the scene writes, by
/// its name and engine class, nested as the tree nests it (a node an instance brings in shown where the scene changes it,
/// with the nodes on its way there); under each, its script and the scene it instances (an imported model too), leading to
/// their files, and the
/// connections of its signals, each leading on to the handler its receiving node's script declares
/// (<see cref="SymbolRow.Leads"/>). A row spans the lines of what writes it. Pure, but for whether a file is there.</summary>
internal static class GodotSceneOutline
{
    /// <summary>The outline of a scene (by its res:// path) of a project's scenes, the scene's file at <paramref name="path"/>.</summary>
    public static Outline<SymbolRow> Of(GodotScenes scenes, string res, string path, Func<string, bool> exists, Outline<SymbolRow>? before = null,
        CancellationToken cancel = default)
    {
        if (!scenes.Files.TryGetValue(res, out GodotFile? file) || scenes.Tree(res) is not { } root)
        {
            return Outline<SymbolRow>.Empty;
        }
        TextSnapshot text = TextSnapshot.From(file.Text);
        Dictionary<GodotTreeNode, List<GodotChecks.Connected>> sent = new(ReferenceEqualityComparer.Instance);
        List<Part> parts = [];
        List<GodotChecks.Connected> lost = [];
        foreach (GodotChecks.Connected connected in GodotChecks.Connections(scenes, res))
        {
            if (connected.From is { } from)
            {
                (sent.TryGetValue(from, out List<GodotChecks.Connected>? list) ? list : sent[from] = []).Add(connected);
            }
            else
            {
                lost.Add(connected);
            }
        }
        if (NodePart(root) is { } top)
        {
            parts.Add(top);
        }
        parts.AddRange(lost.Select(ConnectionPart));
        return Outline<SymbolRow>.Of<Part>(parts, part => part.Children, (part, depth) => (part.Row with { Depth = depth }, part.First, part.Last), before, cancel);

        // A node written here, or on the way to one, with what leads from it; null for a node neither is.
        Part? NodePart(GodotTreeNode node)
        {
            List<Part> children = [.. node.Children.Select(NodePart).OfType<Part>()];
            (string Scene, Passenger.Workspace.GodotNode Node)? here = node.Written.LastOrDefault(w => w.Scene == res) is { Node: not null } own ? own : null;
            if (here is null && children.Count == 0)
            {
                return null;
            }
            List<Part> under = [];
            int first = -1;
            int last = -1;
            SymbolRow row;
            if (here is { Node.Section: { } section })
            {
                (first, last) = (text.GetLineFromOffset(section.Start), text.GetLineFromOffset(End(section)));
                int named = section.Attribute("name")?.Value.TextStart ?? section.Start;
                row = new SymbolRow(node.Name, node.Type is { } type ? $": {type}" : null, node.Unique ? $"%{node.Name}" : null, SymbolRow.NodeKind, path, text.GetPosition(named));
            }
            else
            {
                // On the way to a node the scene changes: where the scene bringing it in writes it.
                Passenger.Workspace.GodotNode declared = node.Declared.Node;
                row = new SymbolRow(node.Name, node.Type is { } type ? $": {type}" : null, null, SymbolRow.NodeKind, scenes.FileOf(node.Declared.Scene) ?? path,
                    new TextPosition(declared.Section.Line, 0));
            }
            if (node.Script is { } script)
            {
                GodotEntry? attached = here is null ? null : node.Written.Where(w => w.Scene == res).Select(w => w.Node.Section.Property("script"))
                    .LastOrDefault(p => p?.Value is { Call: "ExtResource", Text: { } id } && file.External(id)?.Path == script);
                under.Add(FilePart(script, "script", attached?.Value.TextStart ?? (here is { } at ? at.Node.Section.Start : -1)));
            }
            if (node.Instance is { } instanced && here is { } instancing && node.Declared.Scene == res)
            {
                under.Add(FilePart(instanced, node.Placeholder ? "placeholder instance" : "instance", instancing.Node.Section.Start));
            }
            under.AddRange(sent.TryGetValue(node, out List<GodotChecks.Connected>? connections) ? connections.Select(ConnectionPart) : []);
            under.AddRange(children);
            return new Part(row, first, last, under);
        }

        // A file the scene names, by its path in the project, leading to it where it is there, else to where the scene names it.
        Part FilePart(string named, string what, int at)
        {
            int line = at < 0 ? -1 : text.GetLineFromOffset(at);
            string file = named.Split("::")[0];
            string name = file.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) ? file[GodotResPaths.Scheme.Length..] : file;
            SymbolRow row = scenes.FileOf(file) is { } target && exists(target)
                ? new SymbolRow(name, what, null, SymbolRow.FileKind, target, new TextPosition(0, 0))
                : new SymbolRow(name, what, null, SymbolRow.FileKind, path, at < 0 ? new TextPosition(0, 0) : text.GetPosition(at));
            return new Part(row, line, line, []);
        }

        Part ConnectionPart(GodotChecks.Connected connected)
        {
            GodotConnection connection = connected.Connection;
            int method = connection.Section.Attribute("method")?.Value.TextStart ?? connection.Section.Start;
            int line = text.GetLineFromOffset(connection.Section.Start);
            SymbolRow row = new(connection.Signal, $"→ {connected.To?.Name ?? connection.To}.{connection.Method}", null, SymbolKind.Event, path, text.GetPosition(method))
            {
                Leads = true,
            };
            return new Part(row, line, line, []);
        }
    }

    /// <summary>Where a section's text ends: its last property's value, else its tag.</summary>
    private static int End(GodotSection section) => section.Properties.Count > 0 ? Math.Max(section.End, section.Properties[^1].Value.End) : section.End;

    /// <summary>A row and the lines (from zero) it spans, -1 for none, with the rows under it.</summary>
    private sealed record Part(SymbolRow Row, int First, int Last, IReadOnlyList<Part> Children);

    /// <summary>An open scene's outline, read from its project's scenes as they stand (an open file's as the editor has it).</summary>
    internal sealed class Source(GodotSceneIndex index, string folder) : FileOutline
    {
        public override async Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
        {
            GodotScenes scenes = await index.ScenesAsync(folder);
            cancel.ThrowIfCancellationRequested();
            if (GodotResPaths.Of(folder, path) is not { } res)
            {
                return Outline<SymbolRow>.Empty;
            }
            return await BackgroundWork.Run(() => Of(scenes, res, path, File.Exists, before, cancel), cancel);
        }
    }
}

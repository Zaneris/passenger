using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>A Godot project's C# symbols as its scenes name them: an <c>[Export]</c> member a node or resource sets, and a
/// script's class its scenes load (<see cref="GodotNamed"/>). Find Usages lists those places of the scenes beside the
/// code's, a C# rename renames them in the one command of Undo the code's rename is, and a script whose file a rename moves
/// is followed by the scenes that load it, its <c>.uid</c> moving beside it.</summary>
internal sealed partial class CSharpAssist
{
    internal enum GodotKind
    {
        Export,
        Script,
    }

    /// <summary>A symbol the scenes name: the scenes read from its declaring file, its class, what it is, and its name as the
    /// scenes write it.</summary>
    internal sealed record GodotNamed(GodotRead Read, GodotClassFound Type, GodotKind Kind, string Name);

    /// <summary>What a symbol the server named is to the Godot scenes; null for one they never name, or one of no Godot project.</summary>
    internal async Task<GodotNamed?> GodotNamedAsync(NamedSymbol? named)
    {
        if (named is not { Id: { Length: > 2 } id, Path: { } declared } || context.CSharp is not { } csharp || csharp.GodotOf?.Invoke(PathNames.Canonical(declared)) is null
            || await csharp.GodotAsync(PathNames.Canonical(declared), CancellationToken.None) is not { } read)
        {
            return null;
        }
        string member = id[2..].Split('(')[0];
        int dot = member.LastIndexOf('.');
        string container = dot < 0 ? string.Empty : member[..dot];
        string name = member[(dot + 1)..];
        foreach (GodotClassFound type in read.Found.Classes)
        {
            if (id.StartsWith("T:", StringComparison.Ordinal) && member == type.FullName)
            {
                return new GodotNamed(read, type, GodotKind.Script, type.Name);
            }
            if (container != type.FullName)
            {
                continue;
            }
            if (id[0] is 'P' or 'F' && type.Exports.Any(e => e.Name == name))
            {
                return new GodotNamed(read, type, GodotKind.Export, name);
            }
        }
        return null;
    }

    /// <summary>Where the scenes name a symbol, by file: each node and resource setting an exported member (its key), each
    /// scene loading a script (its path); with each place's range.</summary>
    internal static IReadOnlyList<(string Path, string Text, IReadOnlyList<TextRange> Uses)> GodotUses(GodotNamed named)
    {
        GodotScenes scenes = named.Read.Scenes;
        IReadOnlyList<string> scripts = named.Type.Scripts;
        Dictionary<string, List<TextRange>> found = new(StringComparer.Ordinal);
        void Add(string res, int start, int end)
        {
            if (!found.TryGetValue(res, out List<TextRange>? list))
            {
                found[res] = list = [];
            }
            if (!list.Any(r => r.Start == start))
            {
                list.Add(new TextRange(start, end - start));
            }
        }
        if (named.Kind == GodotKind.Export)
        {
            foreach ((string scene, GodotTreeNode node) in scripts.SelectMany(scenes.RunsOf))
            {
                foreach ((string written, Passenger.Workspace.GodotNode section) in node.Written)
                {
                    if (section.Section.Property(named.Name) is { } property)
                    {
                        Add(written, property.KeyStart, property.KeyStart + named.Name.Length);
                    }
                }
            }
            foreach ((string res, GodotFile file) in scenes.Files)
            {
                if (file.Resource is { } resource && scripts.Contains(file.External(resource.Property("script")?.Value is { Call: "ExtResource", Text: { } id } ? id : null)?.Path)
                    && resource.Property(named.Name) is { } property)
                {
                    Add(res, property.KeyStart, property.KeyStart + named.Name.Length);
                }
            }
        }
        else
        {
            foreach ((string res, GodotFile file) in scenes.Files)
            {
                foreach (GodotExternal external in file.Externals.Where(e => e.Path is { } path && scripts.Take(1).Contains(path)))
                {
                    if (external.Section.Attribute("path")?.Value is { Plain: true } value)
                    {
                        Add(res, value.TextStart, value.TextEnd);
                    }
                }
            }
        }
        return [.. found.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (scenes.FileOf(f.Key) ?? f.Key, scenes.Files[f.Key].Text, (IReadOnlyList<TextRange>)f.Value))];
    }

    /// <summary>Where the scenes name the C# symbol at an offset (<see cref="GodotUses"/>), as places for the Find window;
    /// none for a symbol they never name.</summary>
    internal async Task<IReadOnlyList<LspLocation>> GodotUsesAsync(OpenDocument document, LspDocument lsp, int offset) =>
        document.Language == Languages.Language.CSharp ? GodotLocations(await GodotNamedAsync(await lsp.SymbolAtAsync(offset))) : [];

    internal static IReadOnlyList<LspLocation> GodotLocations(GodotNamed? named) => named is null ? [] : [.. GodotUses(named).SelectMany(file =>
    {
        ServerLines lines = ServerLines.Of(TextSnapshot.From(file.Text));
        string uri = DocumentUris.FromPath(file.Path);
        return file.Uses.Select(use => new LspLocation(uri, lines.RangeOf(use)));
    })];

    /// <summary>The edits the scenes take as a C# symbol they name is renamed to <paramref name="name"/>: an exported member's
    /// key where it is set; or why the rename cannot follow them. None for a script's class, whose scenes follow its file
    /// instead (<see cref="GodotMovedAsync"/>).</summary>
    internal async Task<(IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> Files, string? Problem)> GodotRenameAsync(NamedSymbol? symbol, string name)
    {
        if (await GodotNamedAsync(symbol) is not { Kind: not GodotKind.Script } named)
        {
            return ([], null);
        }
        string written = name;
        return ([.. GodotUses(named).Select(file =>
        {
            ServerLines lines = ServerLines.Of(TextSnapshot.From(file.Text));
            return (file.Path, (IReadOnlyList<LspTextEdit>)[.. file.Uses.Select(use => new LspTextEdit(lines.RangeOf(use), written))]);
        })], null);
    }

    /// <summary>The edits the scenes take as a rename moves files of a Godot project: each scene's or resource's res:// path
    /// of a moved file written anew; and the moves Godot's own files beside a moved one take with it (a script's .uid).</summary>
    internal async Task<(IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> Files, IReadOnlyList<(string From, string To)> Moves)> GodotMovedAsync(
        IReadOnlyList<(string From, string To)> moves)
    {
        List<(string, IReadOnlyList<LspTextEdit>)> files = [];
        List<(string, string)> beside = [];
        if (context.CSharp is not { GodotScenes: { } index } csharp)
        {
            return (files, beside);
        }
        foreach (IGrouping<string, (string From, string To)> project in moves.Where(m => csharp.GodotOf?.Invoke(m.From) is not null).GroupBy(m => csharp.GodotOf!(m.From)!, PathNames.Comparer))
        {
            GodotScenes scenes = await index.ScenesAsync(project.Key);
            Dictionary<string, string> renamed = new(StringComparer.Ordinal);
            foreach ((string from, string to) in project)
            {
                if (scenes.ResPathOf(from) is { } was && scenes.ResPathOf(to) is { } now)
                {
                    renamed[was] = now;
                }
                if (File.Exists(from + ".uid") && !File.Exists(to + ".uid"))
                {
                    beside.Add((from + ".uid", to + ".uid"));
                }
            }
            foreach ((string res, GodotFile file) in scenes.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                List<TextEdit> edits = [.. file.Externals.Select(e => e.Section.Attribute("path")?.Value)
                    .Where(v => v is { Plain: true } && renamed.ContainsKey(v.Text!)).Select(v => new TextEdit(v!.TextStart, v.TextEnd - v.TextStart, renamed[v.Text!]))];
                if (edits.Count > 0 && scenes.FileOf(res) is { } path)
                {
                    ServerLines lines = ServerLines.Of(TextSnapshot.From(file.Text));
                    files.Add((path, [.. edits.Select(e => new LspTextEdit(lines.RangeOf(new TextRange(e.Start, e.Length)), e.Text))]));
                }
            }
        }
        return (files, beside);
    }
}

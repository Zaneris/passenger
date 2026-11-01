using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>A Godot project's C# symbols as its scenes name them: a script's method a connection calls, a <c>[Signal]</c>
/// delegate a connection is made to, an <c>[Export]</c> member a node or resource sets, and a script's class its scenes
/// load (<see cref="GodotNamed"/>). Find Usages lists those places of the scenes beside the code's, a C# rename renames
/// them in the one command of Undo the code's rename is, and a script whose file a rename moves is followed by the scenes
/// that load it, its <c>.uid</c> moving beside it.</summary>
internal sealed partial class CSharpAssist
{
    internal enum GodotKind
    {
        Method,
        Signal,
        Export,
        Script,
    }

    /// <summary>A symbol the scenes name: the scenes read from its declaring file, its class, what it is, and its name as the
    /// scenes write it (a signal's less EventHandler).</summary>
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
            if (id.StartsWith("M:", StringComparison.Ordinal))
            {
                return new GodotNamed(read, type, GodotKind.Method, name);
            }
            if (id.StartsWith("T:", StringComparison.Ordinal) && name.EndsWith("EventHandler", StringComparison.Ordinal) && type.Signals.Any(s => s.Name + "EventHandler" == name))
            {
                return new GodotNamed(read, type, GodotKind.Signal, name[..^"EventHandler".Length]);
            }
            if (id[0] is 'P' or 'F' && type.Exports.Any(e => e.Name == name))
            {
                return new GodotNamed(read, type, GodotKind.Export, name);
            }
        }
        return null;
    }

    /// <summary>Where the scenes name a symbol, by file: each connection calling a method or made to a signal, each node and
    /// resource setting an exported member (its key), each scene loading a script (its path); with each place's range.</summary>
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
        HashSet<string> runs = [.. scripts.SelectMany(scenes.RunsOf).Select(r => r.Scene)];
        if (named.Kind is GodotKind.Method or GodotKind.Signal)
        {
            foreach (GodotChecks.Connected connected in runs.Order(StringComparer.Ordinal).SelectMany(scene => GodotChecks.Connections(scenes, scene)))
            {
                bool method = named.Kind == GodotKind.Method && connected.To?.Script is { } to && scripts.Contains(to) && connected.Connection.Method == named.Name;
                bool signal = named.Kind == GodotKind.Signal && connected.From?.Script is { } from && scripts.Contains(from) && connected.Connection.Signal == named.Name;
                if ((method || signal) && connected.Connection.Section.Attribute(method ? "method" : "signal")?.Value is { Plain: true } value)
                {
                    Add(connected.Scene, value.TextStart, value.TextEnd);
                }
            }
        }
        else if (named.Kind == GodotKind.Export)
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

    /// <summary>The edits the scenes take as a C# symbol they name is renamed to <paramref name="name"/>: a connection's method
    /// or signal, an exported member's key where it is set; or why the rename cannot follow them (a <c>[Signal]</c> delegate's
    /// name that would not end with EventHandler, which Godot's source generator needs). None for a script's class, whose
    /// scenes follow its file instead (<see cref="GodotMovedAsync"/>).</summary>
    internal async Task<(IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> Files, string? Problem)> GodotRenameAsync(NamedSymbol? symbol, string name)
    {
        if (await GodotNamedAsync(symbol) is not { Kind: not GodotKind.Script } named)
        {
            return ([], null);
        }
        string written = name;
        if (named.Kind == GodotKind.Signal)
        {
            if (!name.EndsWith("EventHandler", StringComparison.Ordinal) || name.Length == "EventHandler".Length)
            {
                return ([], $"{named.Name}EventHandler is a [Signal] delegate, whose name ends with EventHandler for Godot's source generator");
            }
            written = name[..^"EventHandler".Length];
        }
        return ([.. GodotUses(named).Select(file =>
        {
            ServerLines lines = ServerLines.Of(TextSnapshot.From(file.Text));
            return (file.Path, (IReadOnlyList<LspTextEdit>)[.. file.Uses.Select(use => new LspTextEdit(lines.RangeOf(use), written))]);
        })], null);
    }

    /// <summary>Where Go to Declaration goes from a member Godot's source generator writes for a script, to the member of
    /// the code it stands for: <c>SignalName.X</c> and a <c>[Signal]</c>'s event <c>X</c> to the delegate XEventHandler,
    /// <c>MethodName.X</c> and <c>PropertyName.X</c> to X; null where the server's places are not all a generator's, or the
    /// symbol is none of those.</summary>
    private async Task<Declared?> GodotGeneratedDeclaredAsync(LspDocument lsp, OpenDocument document, int offset, IReadOnlyList<LspLocation> found)
    {
        if (found.Count == 0 || !found.All(f => DocumentUris.GeneratedName(f.Uri) is not null) || context.CSharp is not { Session: { } session } csharp
            || csharp.GodotOf?.Invoke(document.Path) is null || await lsp.SymbolAtAsync(offset) is not { Id: { Length: > 2 } id })
        {
            return null;
        }
        string member = id[2..].Split('(')[0];
        int dot = member.LastIndexOf('.');
        string container = dot < 0 ? string.Empty : member[..dot];
        int inner = container.LastIndexOf('.');
        string owner = inner < 0 ? container : container[..inner];
        string named = member[(dot + 1)..];
        string? declared = (id[0], inner < 0 ? container : container[(inner + 1)..]) switch
        {
            ('F', "SignalName") => $"{Simple(owner)}.{named}EventHandler",
            ('F', "MethodName" or "PropertyName") => $"{Simple(owner)}.{named}",
            ('E', _) when container.Length > 0 => $"{Simple(container)}.{named}EventHandler",
            _ => null,
        };
        if (declared is null)
        {
            return null;
        }
        SymbolsFound symbols;
        try
        {
            symbols = await session.SymbolsNamedAsync(declared, 5);
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
        IReadOnlyList<LspLocation> places = [.. symbols.Symbols.Where(s => s.Path is not null && s.At is not null && !GeneratedCode.IsCopy(s.Path))
            .Select(s => new LspLocation(DocumentUris.FromPath(s.Path!), new LspRange(new LspPosition(s.At!.Line, s.At.Character), new LspPosition(s.At.Line, s.At.Character))))];
        return places.Count == 0 ? null : new Declared(places, Refusals.NoDeclaration);

        static string Simple(string full) => full[(full.LastIndexOf('.') + 1)..];
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

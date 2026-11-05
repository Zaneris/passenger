using Passenger.App.Editors;
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
/// load (<see cref="GodotNamed"/>); and as the code names them by Godot's other names, those its source generator writes
/// and the strings its calls take (<see cref="GodotNamesAsync"/>). Find Usages lists those places beside the code's, a C#
/// rename from any of the names renames them all in the one command of Undo the code's rename is, and a script whose file
/// a rename moves is followed by the scenes that load it, its <c>.uid</c> moving beside it.</summary>
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
                        Add(written, property.NameStart, property.NameStart + named.Name.Length);
                    }
                }
            }
            foreach ((string res, GodotEntry property) in scenes.ResourceKeys(scripts.Contains, named.Name))
            {
                Add(res, property.NameStart, property.NameStart + named.Name.Length);
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

    /// <summary>Where the scenes name the C# member at an offset (<see cref="GodotUses"/>) and where the code names it by
    /// Godot's other names for it (<see cref="GodotNamesAsync"/>), or every place a name of a kind at it is written
    /// (<see cref="KindUsesAsync"/>), as places for the Find window; none for a member neither does.</summary>
    internal async Task<IReadOnlyList<LspLocation>> GodotUsesAsync(OpenDocument document, LspDocument lsp, int offset)
    {
        if (GodotScriptDeclarations.IsScript(document.Path))
        {
            return await GodotScriptUsesAsync(document, offset);
        }
        if (document.Language != Languages.Language.CSharp)
        {
            return [];
        }
        if (await GodotKindAtAsync(document, offset) is { } kind)
        {
            return await KindUsesAsync(kind);
        }
        GodotNamesFound? names = await GodotNamesAsync(document, lsp, offset, uses: true);
        IReadOnlyList<LspLocation> scenes = GodotLocations(await GodotNamedAsync(names?.Symbol ?? await lsp.SymbolAtAsync(offset)));
        return names is null ? scenes : [.. scenes, .. names.Uses.Select(u => new LspLocation(DocumentUris.FromPath(u.Path),
            new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character))))];
    }

    /// <summary>The uses of a GDScript's member at an offset of the script declaring it beside the scripts: the scenes'
    /// connections and keys, the C# strings naming it (<see cref="GodotScriptNamedAsync"/>), as places for the Find window.</summary>
    internal Task<IReadOnlyList<LspLocation>> GodotScriptUsesOfAsync(OpenDocument document, int offset) => GodotScriptUsesAsync(document, offset);

    /// <summary>The uses of a GDScript's member at an offset (<see cref="GodotScriptNamedAsync"/>), as places for the Find window.</summary>
    private async Task<IReadOnlyList<LspLocation>> GodotScriptUsesAsync(OpenDocument document, int offset) =>
        await GodotScriptNamedAsync(document, offset) is { } named ? [.. named.Scenes, .. named.Code] : [];

    /// <summary>The places beside the scripts that name a GDScript's member at an offset (<see cref="GodotScriptNamedAsync"/>):
    /// each scene's connection (unless <paramref name="withScenes"/> is false) and each C# string, by file and line, for a
    /// refactoring that cannot follow them; a call whose arguments a change of signature rewrites aside when
    /// <paramref name="followingCalls"/>.</summary>
    internal async Task<IReadOnlyList<Passenger.Workspace.GDScript.GDScriptPlace>> GodotScriptElsewhereAsync(OpenDocument document, int offset, bool followingCalls = false,
        bool withScenes = true)
    {
        if (await GodotScriptNamedWithCallsAsync(document, offset) is not { } named)
        {
            return [];
        }
        static Passenger.Workspace.GDScript.GDScriptPlace Placed(LspLocation at, string what) =>
            new($"{Path.GetFileName(new Uri(at.Uri).LocalPath)}:{at.Range.Start.Line + 1}", 0, 0, what);
        return [.. named.Scenes.Where(_ => withScenes).Select(s => Placed(s, "a scene's connection names it")),
            .. named.Code.Where((_, i) => !followingCalls || named.Calls[i].Rest is null).Select(c => Placed(c, "C# names it in a string"))];
    }

    /// <summary>The edits a GDScript member's C# calls by its name take as its signature changes: each call's arguments after
    /// the name (<c>Call("f", 1, 2)</c>, <c>EmitSignal("s", 1)</c>) given as <paramref name="rearranged"/> makes them, by
    /// file, in the server's places.</summary>
    internal async Task<IReadOnlyList<PlacedEdits>> GodotScriptCallEditsAsync(OpenDocument document, int offset, Func<IReadOnlyList<string>, IReadOnlyList<string>> rearranged) =>
        await GodotScriptNamedWithCallsAsync(document, offset) is { } named
            ? [.. named.Calls.Where(c => c.Rest is not null).GroupBy(c => c.File, PathNames.Comparer).Select(file => new PlacedEdits(file.Key, [.. file.Select(c =>
            {
                IReadOnlyList<string> made = rearranged(c.Rest!.Arguments);
                return new LspTextEdit(new LspRange(new LspPosition(c.Rest.From.Line, c.Rest.From.Character), new LspPosition(c.Rest.To.Line, c.Rest.To.Character)),
                    string.Concat(made.Select(a => ", " + a)));
            })]))]
            : [];

    /// <summary>Where the project's files set an exported variable of a GDScript (<paramref name="script"/>, a res:// path) on a
    /// node or resource whose class takes in the script, by file and line, for Safe Delete to refuse.</summary>
    internal async Task<IReadOnlyList<Passenger.Workspace.GDScript.GDScriptPlace>> GodotSetsAsync(string path, string script, string property)
    {
        if (context.GodotOf(path) is not ({ } index, { } folder))
        {
            return [];
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        return await BackgroundWork.Run(() =>
        {
            List<Passenger.Workspace.GDScript.GDScriptPlace> sets = [];
            foreach (string scene in scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").Select(f => f.Key).Order(StringComparer.Ordinal))
            {
                foreach (GodotTreeNode node in scenes.Tree(scene)?.Below() ?? [])
                {
                    if (GodotChecks.ChainOf(scenes, node)?.Runs(script) != true)
                    {
                        continue;
                    }
                    foreach ((string written, Passenger.Workspace.GodotNode at) in node.Written.Where(w => w.Node.Section.Properties.Any(p => p.Key == property)))
                    {
                        sets.Add(new($"{Path.GetFileName(scenes.FileOf(written) ?? written)}:{at.Section.Line + 1}", 0, 0, $"a scene sets it on {node.Name}"));
                    }
                }
            }
            foreach ((string res, GodotEntry key) in scenes.ResourceKeys(s => TakesIn(scenes, s, script), property))
            {
                sets.Add(new($"{Path.GetFileName(scenes.FileOf(res) ?? res)}:{scenes.Files[res].LineOf(key.KeyStart) + 1}", 0, 0, "a resource sets it"));
            }
            return (IReadOnlyList<Passenger.Workspace.GDScript.GDScriptPlace>)[.. sets.Distinct()];
        });
    }

    /// <summary>The functions the scenes' connections of a GDScript's signal (of <paramref name="script"/>, a res:// path) call,
    /// and the connections a change of its parameters can't follow (<see cref="GodotChecks.SignalHandlers"/>).</summary>
    internal async Task<(IReadOnlyList<Passenger.Workspace.GDScript.GDScriptHandler> Handlers, IReadOnlyList<Passenger.Workspace.GDScript.GDScriptPlace> Unfollowed)> GodotSignalHandlersAsync(
        string path, string script, string signal)
    {
        if (context.GodotOf(path) is not ({ } index, { } folder))
        {
            return ([], []);
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        return await BackgroundWork.Run(() => GodotChecks.SignalHandlers(scenes, script, signal));
    }

    /// <summary>The edits a GDScript's member's uses beside its own language's take as it is renamed to <paramref name="name"/>
    /// (<see cref="GodotScriptNamedAsync"/>): each scene's connection's method or signal and each string of the C# naming it,
    /// by file, in the server's places.</summary>
    internal async Task<IReadOnlyList<PlacedEdits>> GodotScriptRenameAsync(OpenDocument document, int offset, string name) =>
        await GodotScriptNamedWithCallsAsync(document, offset) is { } named
            ? [.. named.Scenes.Concat(named.Sets).Concat(named.Code).GroupBy(u => new Uri(u.Uri).LocalPath, PathNames.Comparer)
                .Select(file => new PlacedEdits(file.Key, [.. file.Select(u => new LspTextEdit(u.Range, name))], named.Read.ResPathOf(file.Key) is { } res && named.Read.Files.TryGetValue(res, out GodotFile? read) ? read.Text : null))]
            : [];

    /// <summary>The project files of the projects building a Godot project's game, as the extension is told them.</summary>
    private IReadOnlyList<string> GodotBuilding(string folder) => context.CSharp?.GodotProjects?.Invoke(folder) ?? [];

    /// <summary>The uses of a GDScript's member at an offset (the name there, where its class, it or a script it extends,
    /// declares one) beside its own language's: each scene's connection calling it or made to it, and each key setting it as a
    /// variable, on a node whose class takes in the declaring script, and each string of the project's C# naming it on a node
    /// the code tells is one (<see cref="Handlers.GodotNameStrings"/>); null for an offset on no such member.</summary>
    private async Task<(IReadOnlyList<LspLocation> Scenes, IReadOnlyList<LspLocation> Code)?> GodotScriptNamedAsync(OpenDocument document, int offset) =>
        await GodotScriptNamedWithCallsAsync(document, offset) is { } named ? ([.. named.Scenes, .. named.Sets], named.Code) : null;

    /// <summary><see cref="GodotScriptNamedAsync"/> with the scenes' connections apart from their keys setting a variable,
    /// and the C# uses as the extension gave them, each call's arguments after the name among them.</summary>
    private async Task<(IReadOnlyList<LspLocation> Scenes, IReadOnlyList<LspLocation> Sets, IReadOnlyList<LspLocation> Code, IReadOnlyList<GodotKindUse> Calls, GodotScenes Read)?>
        GodotScriptNamedWithCallsAsync(OpenDocument document, int offset)
    {
        TextSnapshot text = document.Text.Snapshot;
        int start = Math.Clamp(offset, 0, text.Length);
        int end = start;
        while (start > 0 && AssistContext.IsIdentifierChar(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && AssistContext.IsIdentifierChar(text[end]))
        {
            end++;
        }
        if (end == start || context.GodotOf(document.Path) is not ({ } index, { } folder))
        {
            return null;
        }
        string name = text.GetText(start, end - start);
        GodotScenes scenes = await index.ScenesAsync(folder);
        if (scenes.ResPathOf(document.Path) is not { } res
            || await BackgroundWork.Run(() => scenes.ChainOf(res) is var chain ? chain.Find(GodotScriptKind.Function, name) ?? chain.Find(GodotScriptKind.Signal, name)
                ?? chain.Find(GodotScriptKind.Variable, name) ?? chain.Find(GodotScriptKind.Constant, name) : null) is not ({ } script, { } member))
        {
            return null;
        }
        IReadOnlyList<LspLocation> uses = [.. await BackgroundWork.Run(() => SceneUses(scenes, script, member))];
        IReadOnlyList<LspLocation> sets = member.Kind == GodotScriptKind.Variable ? [.. await BackgroundWork.Run(() => SceneSets(scenes, script, member.Name))] : [];
        if (member.Kind is not (GodotScriptKind.Function or GodotScriptKind.Signal or GodotScriptKind.Variable) || context.CSharp?.Session is not { } session)
        {
            return (uses, sets, [], [], scenes);
        }
        GodotNameKind kind = member.Kind switch { GodotScriptKind.Function => GodotNameKind.Method, GodotScriptKind.Signal => GodotNameKind.Signal, _ => GodotNameKind.Property };
        GodotKindUses found;
        try
        {
            found = await session.ExtensionAsync<GodotKindUses>(Handlers.GodotNameStrings, new GodotNameStringsAsked(folder, kind, name, GodotBuilding(folder)));
        }
        catch (LanguageServerFailed)
        {
            return (uses, sets, [], [], scenes);
        }
        List<GodotKindUse> calls = [.. found.Uses.Where(u => GodotChecks.Told(scenes, u.Scripts, u.Node, u.Export).Told.Any(n => GodotChecks.ChainOf(scenes, n)?.Runs(script) == true))];
        return (uses, sets, [.. calls.Select(u => new LspLocation(DocumentUris.FromPath(u.File), new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character))))],
            calls, scenes);

        // Each scene's connection naming the member as its method or signal, on a node whose class takes in its script.
        static IEnumerable<LspLocation> SceneUses(GodotScenes scenes, string script, GodotScriptDeclared member)
        {
            bool method = member.Kind == GodotScriptKind.Function;
            if (!method && member.Kind != GodotScriptKind.Signal)
            {
                yield break;
            }
            foreach ((string scene, GodotFile file) in scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                ServerLines? lines = null;
                foreach (GodotChecks.Connected connected in GodotChecks.Connections(scenes, scene))
                {
                    GodotTreeNode? node = method ? connected.To : connected.From;
                    if ((method ? connected.Connection.Method : connected.Connection.Signal) != member.Name || node is null || GodotChecks.ChainOf(scenes, node)?.Runs(script) != true
                        || connected.Connection.Section.Attribute(method ? "method" : "signal")?.Value is not { } value || scenes.FileOf(scene) is not { } path)
                    {
                        continue;
                    }
                    lines ??= ServerLines.Of(TextSnapshot.From(file.Text));
                    yield return new LspLocation(DocumentUris.FromPath(path), lines.RangeOf(new TextRange(value.TextStart, value.TextEnd - value.TextStart)));
                }
            }
        }
    }

    /// <summary>Whether a script of a resource is a GDScript whose class takes in another.</summary>
    private static bool TakesIn(GodotScenes scenes, string running, string script) => GodotScriptDeclarations.IsScript(running) && scenes.ChainOf(running).Runs(script);

    /// <summary>Each key of the project's files setting a GDScript's variable (<paramref name="script"/>, a res:// path) on a
    /// node whose class takes in the script, its section's own or a scene's instancing it, or on a resource whose does, each once.</summary>
    private static IEnumerable<LspLocation> SceneSets(GodotScenes scenes, string script, string variable)
    {
        HashSet<(string, int)> seen = [];
        Dictionary<string, ServerLines> lines = new(StringComparer.Ordinal);
        foreach (string scene in scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").Select(f => f.Key).Order(StringComparer.Ordinal))
        {
            foreach (GodotTreeNode node in scenes.Tree(scene)?.Below() ?? [])
            {
                if (GodotChecks.ChainOf(scenes, node)?.Runs(script) != true)
                {
                    continue;
                }
                foreach ((string written, Passenger.Workspace.GodotNode at) in node.Written)
                {
                    foreach (GodotEntry key in at.Section.Properties.Where(p => p.Key == variable))
                    {
                        if (Placed(written, key) is { } place)
                        {
                            yield return place;
                        }
                    }
                }
            }
        }
        foreach ((string res, GodotEntry key) in scenes.ResourceKeys(s => TakesIn(scenes, s, script), variable))
        {
            if (Placed(res, key) is { } place)
            {
                yield return place;
            }
        }

        LspLocation? Placed(string written, GodotEntry key)
        {
            if (!seen.Add((written, key.KeyStart)) || scenes.FileOf(written) is not { } path || !scenes.Files.TryGetValue(written, out GodotFile? file))
            {
                return null;
            }
            ServerLines read = lines.TryGetValue(written, out ServerLines? kept) ? kept : lines[written] = ServerLines.Of(TextSnapshot.From(file.Text));
            return new LspLocation(DocumentUris.FromPath(path), read.RangeOf(new TextRange(key.NameStart, variable.Length)));
        }
    }

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
    internal async Task<(IReadOnlyList<PlacedEdits> Files, string? Problem)> GodotRenameAsync(NamedSymbol? symbol, string name)
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
            return new PlacedEdits(file.Path, [.. file.Uses.Select(use => new LspTextEdit(lines.RangeOf(use), written))], file.Text);
        })], null);
    }

    /// <summary>The member of a Godot script's code at an offset by any name Godot knows it by (<see cref="GodotGenerated"/>):
    /// its own, one Godot's source generator writes for it, or a string Godot's calls take; with its uses by those other
    /// names when <paramref name="uses"/>. Null for an offset on none, and in a file of no Godot project.</summary>
    internal async Task<GodotNamesFound?> GodotNamesAsync(OpenDocument document, LspDocument lsp, int offset, bool uses)
    {
        if (document.Language != Languages.Language.CSharp || context.CSharp?.GodotOf?.Invoke(document.Path) is null)
        {
            return null;
        }
        try
        {
            GodotNamesFound found = await lsp.ExtensionAsync<GodotNamesFound>(Handlers.GodotNames, new GodotNamesAsked(lsp.PlaceOf(offset), uses));
            return found.Symbol is null ? null : found;
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
    }

    /// <summary>What a Godot script's member renamed from one of its names to <paramref name="name"/> is called: the code's
    /// own name (a signal's delegate's, ending with EventHandler as Godot's source generator needs) and the name Godot
    /// knows it by, which its other names take; or why the name cannot be.</summary>
    internal static (string Own, string Written, string? Problem) GodotRenamed(GodotNamesFound found, string name)
    {
        const string handler = "EventHandler";
        const string emit = "EmitSignal";
        if (found.Kind != GodotNameKind.Signal)
        {
            return (name, name, null);
        }
        return found.Via switch
        {
            GodotNameVia.Own when !name.EndsWith(handler, StringComparison.Ordinal) || name.Length == handler.Length =>
                (name, name, $"{found.Written}{handler} is a [Signal] delegate, whose name ends with {handler} for Godot's source generator"),
            GodotNameVia.Own => (name, name[..^handler.Length], null),
            GodotNameVia.Emit when !name.StartsWith(emit, StringComparison.Ordinal) || name.Length == emit.Length =>
                (name, name, $"{emit}{found.Written} is what Godot's source generator writes to emit the signal {found.Written}, so its name starts with {emit}"),
            GodotNameVia.Emit => (name[emit.Length..] + handler, name[emit.Length..], null),
            _ => (name + handler, name, null),
        };
    }

    /// <summary>The edits a Godot script's member's other names take as it is renamed to <paramref name="written"/>, by
    /// file, in the server's places.</summary>
    internal static IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> GodotNameEdits(GodotNamesFound found, string written) =>
        [.. found.Uses.GroupBy(u => u.Path, PathNames.Comparer).Select(file => (file.Key, (IReadOnlyList<LspTextEdit>)[.. file.Select(u =>
            new LspTextEdit(new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character)), u.Before + written))]))];

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

    /// <summary>The edits the project's files take as a rename moves files of a Godot project: each scene's or resource's res://
    /// path of a moved file written anew, and each GDScript's; and the moves Godot's own files beside a moved one take with it
    /// (a script's .uid).</summary>
    internal async Task<(IReadOnlyList<PlacedEdits> Files, IReadOnlyList<(string From, string To)> Moves)> GodotMovedAsync(IReadOnlyList<(string From, string To)> moves)
    {
        List<PlacedEdits> files = [];
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
                    files.Add(new PlacedEdits(path, [.. edits.Select(e => new LspTextEdit(lines.RangeOf(new TextRange(e.Start, e.Length)), e.Text))], file.Text));
                }
            }
            List<(string From, string To)> paths = [.. renamed.Select(r => (r.Key, r.Value))];
            foreach ((string res, string text) in scenes.Texts.Where(t => GodotScriptDeclarations.IsScript(t.Key)).OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (GodotScenes.MovedScriptEdits(text, res, paths) is { Count: > 0 } edits && scenes.FileOf(res) is { } path)
                {
                    ServerLines lines = ServerLines.Of(TextSnapshot.From(text));
                    files.Add(new PlacedEdits(path, [.. edits.Select(e => new LspTextEdit(lines.RangeOf(new TextRange(e.Start, e.Length)), e.Text))], text));
                }
            }
        }
        return (files, beside);
    }

    /// <summary>Why a Godot project's files must not take edits placed in them as they were read: the words for the first
    /// whose text is another now (<see cref="WorkspaceEdits.OutOfDateAsync"/>), saved before the folder's watcher told of it.
    /// The index reads it again at once, so that a command's next attempt (<paramref name="patience"/>) finds its places in
    /// it as it is. Null when each is as read.</summary>
    internal async Task<string?> GodotOutOfDateAsync(IReadOnlyList<PlacedEdits> files, Asked? asked, Patience? patience)
    {
        if (await WorkspaceEdits.OutOfDateAsync(context.Editors, files, asked) is not { } path)
        {
            return null;
        }
        context.GodotScenes?.Invoke()?.FilesChanged([path]);
        string said = Refusals.ChangedSinceRead(Path.GetFileName(path));
        return patience?.Behind([path], null, said, said) ?? said;
    }

    /// <summary>Why a rename of a Godot name must not change files as it read them (<paramref name="read"/>, each file's text
    /// by its path), the index told at once (<see cref="GodotOutOfDateAsync"/>); null when each is as read.</summary>
    private async Task<string?> GodotBehindAsync(Dictionary<string, string> read, string old, Patience? patience) =>
        await GodotOutOfDateAsync([.. read.Select(r => new PlacedEdits(r.Key, [], r.Value))], null, patience) is { } unread ? $"{old} was not renamed: {unread}" : null;

    /// <summary>Why a rename stopped at a file whose text is not what it was told of, a language server's places in it or the
    /// index's: the file waited for by <paramref name="patience"/> before the next attempt.</summary>
    private static string Overtaken(string file, string old, Patience? patience)
    {
        string said = $"{old} was not renamed: {Path.GetFileName(file)} changed as it was read";
        return patience?.Behind([file], null, said, said) ?? said;
    }

    /// <summary>The edits the Godot projects' files take once files and folders of theirs have moved (the Explorer's move or
    /// rename, already made): every res:// path naming a moved file, or a file inside a moved folder, written anew in each
    /// scene, resource, GDScript, shader's <c>#include</c> and project.godot and in each C# string; by file at its path now, in the
    /// server's places, each read as it stands as this is asked, whatever the index has heard. With the .uid and .import files
    /// beside each moved file, which go with it, an .import's own path to its file following.</summary>
    internal async Task<(IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)> Files, IReadOnlyList<(string From, string To)> Beside)> GodotFollowAsync(
        IReadOnlyList<(string From, string To)> moved)
    {
        List<(string, IReadOnlyList<LspTextEdit>)> files = [];
        List<(string, string)> beside = [];
        if (context.GodotScenes?.Invoke() is not { } index)
        {
            return (files, beside);
        }
        Func<string, string?> godotOf = index.GameOf;
        OpenTexts open = context.Editors.Texts();
        // The index takes the moves in first, so the scenes are read where they are now.
        index.CameOrWent([.. moved.SelectMany(m => new[] { m.From, m.To })]);
        foreach (IGrouping<string, (string From, string To)> project in moved.Where(m => godotOf(m.To) is { } folder && PathNames.Same(godotOf(m.From) ?? string.Empty, folder))
            .GroupBy(m => godotOf(m.To)!, PathNames.Comparer))
        {
            string folder = project.Key;
            List<(string From, string To)> paths = [];
            foreach ((string from, string to) in project)
            {
                bool isFolder = Directory.Exists(to);
                if (GodotScenes.ResPathOf(folder, from) is { } was && GodotScenes.ResPathOf(folder, to) is { } now)
                {
                    paths.Add(isFolder ? (was + "/", now + "/") : (was, now));
                }
                foreach (string kept in isFolder ? [] : new[] { ".uid", ".import" })
                {
                    if (File.Exists(from + kept) && !File.Exists(to + kept))
                    {
                        beside.Add((from + kept, to + kept));
                    }
                }
            }
            if (paths.Count == 0)
            {
                continue;
            }
            GodotScenes scenes = await index.ScenesAsync(folder);
            // Each text the moves reach, where it is read from and its path once every move is made: the scenes, project.godot, the .import files moving.
            List<(string Path, string From)> texts = [.. scenes.Files.Keys.Select(scenes.FileOf).OfType<string>().Select(f => (f, f)), (Path.Combine(folder, "project.godot"), Path.Combine(folder, "project.godot")),
                .. beside.Where(b => b.Item1.EndsWith(".import", StringComparison.OrdinalIgnoreCase)).Select(b => (b.Item2, b.Item1))];
            // A script's or shader's text is read at its path now, its paths written from the folder it was in.
            List<(string Path, string Res)> scripts = [.. scenes.Texts.Keys.Where(res => GodotScriptDeclarations.IsScript(res) || Godot.GodotShaderMarks.IsShader(res))
                .Select(res => (Path: scenes.FileOf(res), res)).Where(s => s.Path is not null).Select(s => (s.Path!, s.res))];
            List<(string, string)> back = [.. paths.Select(p => (p.To, p.From))];
            string Was(string res) => GodotScenes.Moved(back, res) ?? res;
            IReadOnlyList<(string Path, string Text, IReadOnlyList<TextEdit> Edits)> edited = await BackgroundWork.Run(() => (IReadOnlyList<(string, string, IReadOnlyList<TextEdit>)>)
                [.. texts.Select(t => (t.Path, Text: open.Text(t.From))).Where(t => t.Text is not null)
                    .Select(t => (t.Path, t.Text!, GodotScenes.MovedEdits(GodotFile.Parse(t.Text!), paths, Was(GodotScenes.ResPathOf(folder, t.Path) ?? "res://")))).Where(t => t.Item3.Count > 0),
                .. scripts.Select(s => (s.Path, s.Res, Text: open.Text(s.Path))).Where(s => s.Text is not null)
                    .Select(s => (s.Path, s.Text!, GodotScriptDeclarations.IsScript(s.Res) ? GodotScenes.MovedScriptEdits(s.Text!, Was(s.Res), paths) : GodotScenes.MovedShaderEdits(s.Text!, Was(s.Res), paths)))
                    .Where(s => s.Item3.Count > 0)]);
            foreach ((string path, string text, IReadOnlyList<TextEdit> edits) in edited)
            {
                ServerLines lines = ServerLines.Of(TextSnapshot.From(text));
                files.Add((path, [.. edits.Select(e => new LspTextEdit(lines.RangeOf(new TextRange(e.Start, e.Length)), e.Text))]));
            }
            if (context.CSharp?.Session is { } session)
            {
                GodotPathsFound found;
                try
                {
                    found = await session.ExtensionAsync<GodotPathsFound>(Handlers.GodotPaths, new GodotPathsAsked(folder, [.. paths.Select(p => p.From)], GodotBuilding(folder)));
                }
                catch (LanguageServerFailed)
                {
                    continue;
                }
                // A C# file moved itself is written where it went, which the server may not know yet.
                string Now(string file) => moved.FirstOrDefault(m => PathNames.Within(file, m.From)) is { From: not null } move ? PathNames.Moved(file, move.From, move.To) : file;
                files.AddRange(found.Uses.Where(u => GodotScenes.Moved(paths, u.Path) is not null).GroupBy(u => Now(u.File), PathNames.Comparer).Select(file => (file.Key,
                    (IReadOnlyList<LspTextEdit>)[.. file.Select(u => new LspTextEdit(new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character)),
                        GodotScenes.Moved(paths, u.Path)!))])));
            }
        }
        return (files, beside);
    }
}

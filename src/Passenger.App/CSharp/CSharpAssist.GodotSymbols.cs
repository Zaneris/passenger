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

    /// <summary>The uses of a GDScript's member at an offset (<see cref="GodotScriptNamedAsync"/>), as places for the Find window.</summary>
    private async Task<IReadOnlyList<LspLocation>> GodotScriptUsesAsync(OpenDocument document, int offset) =>
        await GodotScriptNamedAsync(document, offset) is { } named ? [.. named.Scenes, .. named.Code] : [];

    /// <summary>The edits a GDScript's member's uses beside its own language's take as it is renamed to <paramref name="name"/>
    /// (<see cref="GodotScriptNamedAsync"/>): each scene's connection's method or signal and each string of the C# naming it,
    /// by file, in the server's places.</summary>
    internal async Task<IReadOnlyList<(string Path, IReadOnlyList<LspTextEdit> Edits)>> GodotScriptRenameAsync(OpenDocument document, int offset, string name) =>
        await GodotScriptNamedAsync(document, offset) is { } named
            ? [.. named.Scenes.Concat(named.Code).GroupBy(u => new Uri(u.Uri).LocalPath, PathNames.Comparer)
                .Select(file => (file.Key, (IReadOnlyList<LspTextEdit>)[.. file.Select(u => new LspTextEdit(u.Range, name))]))]
            : [];

    /// <summary>The uses of a GDScript's member at an offset (the name there, where its class, it or a script it extends,
    /// declares one) beside its own language's: each scene's connection calling it or made to it on a node whose class takes
    /// in the declaring script, and each string of the project's C# naming it on a node the code tells is one
    /// (<see cref="Handlers.GodotNameStrings"/>); null for an offset on no such member.</summary>
    private async Task<(IReadOnlyList<LspLocation> Scenes, IReadOnlyList<LspLocation> Code)?> GodotScriptNamedAsync(OpenDocument document, int offset)
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
        if (member.Kind is not (GodotScriptKind.Function or GodotScriptKind.Signal or GodotScriptKind.Variable) || context.CSharp?.Session is not { } session)
        {
            return (uses, []);
        }
        GodotNameKind kind = member.Kind switch { GodotScriptKind.Function => GodotNameKind.Method, GodotScriptKind.Signal => GodotNameKind.Signal, _ => GodotNameKind.Property };
        GodotKindUses found;
        try
        {
            found = await session.ExtensionAsync<GodotKindUses>(Handlers.GodotNameStrings, new GodotNameStringsAsked(folder, kind, name));
        }
        catch (LanguageServerFailed)
        {
            return (uses, []);
        }
        return (uses, [.. found.Uses.Where(u => GodotChecks.Told(scenes, u.Scripts, u.Node, u.Export).Told.Any(n => GodotChecks.ChainOf(scenes, n)?.Runs(script) == true))
            .Select(u => new LspLocation(DocumentUris.FromPath(u.File), new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character))))]);

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

    /// <summary>The edits the Godot projects' files take once files and folders of theirs have moved (the Explorer's move or
    /// rename, already made): every res:// path naming a moved file, or a file inside a moved folder, written anew in each
    /// scene, resource and project.godot and in each C# string; by file at its path now, in the server's places. With the
    /// .uid and .import files beside each moved file, which go with it, an .import's own path to its file following.</summary>
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
            // Each text the moves reach, at its path once every move is made: the scenes, project.godot, the .import files moving.
            List<(string Path, string? Text)> texts = [.. scenes.Files.Select(f => (scenes.FileOf(f.Key)!, (string?)f.Value.Text)),
                (Path.Combine(folder, "project.godot"), context.Editors.TextOf(Path.Combine(folder, "project.godot"))?.ToString()),
                .. beside.Where(b => b.Item1.EndsWith(".import", StringComparison.OrdinalIgnoreCase)).Select(b => (b.Item2, context.Editors.TextOf(b.Item1)?.ToString()))];
            IReadOnlyList<(string Path, string Text, IReadOnlyList<TextEdit> Edits)> edited = await BackgroundWork.Run(() => (IReadOnlyList<(string, string, IReadOnlyList<TextEdit>)>)
                [.. texts.Where(t => t.Text is not null).Select(t => (t.Path, t.Text!, GodotScenes.MovedEdits(GodotFile.Parse(t.Text!), paths))).Where(t => t.Item3.Count > 0)]);
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
                    found = await session.ExtensionAsync<GodotPathsFound>(Handlers.GodotPaths, new GodotPathsAsked(folder, [.. paths.Select(p => p.From)]));
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

using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App.Godot;

/// <summary>Each open GDScript's own syntax tree and types (<see cref="GDScriptTree"/>, <see cref="GDScriptAnalyzer"/>):
/// the tree parsed again off the window's thread once typing pauses, from the edits the text gave since (only the members
/// they reach parsed again), the types read against the project's other scripts, its autoloads and the engine's API, and
/// the types inferred where none is written shown as inlay hints, and Godot's warnings and errors that reading gives
/// marked, by the project's warning settings, until Godot's server speaks of the file (<see cref="Web.WebDocument.ShowPassengers"/>).
/// What a key costs the window's thread is the edit's note and the pause's restart. A UI-thread object.</summary>
internal sealed class GodotScriptTypes(GodotSceneIndex index, Func<string, string?> godotOf, GodotEngineApis apis, Func<OpenDocument, Web.WebDocument?> webOf)
{
    /// <summary>Who checks a script, as an agent is told while a check is under way.</summary>
    private const string Checker = "Passenger's reading of the script";

    /// <summary>How long typing pauses before the file is parsed and typed again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<OpenDocument, Kept> documents = [];

    /// <summary>An open script's tree and analysis as last made, and the change its text has made since.</summary>
    private sealed class Kept(Debounce pause)
    {
        public Debounce Pause { get; } = pause;

        public EventHandler<DocumentChangedEventArgs>? Typed { get; set; }

        public GDScriptTree? Tree { get; set; }

        public string? TreeText { get; set; }

        public GDScriptTextChange? Pending { get; set; }

        public Task<Typed?>? Current { get; set; }

        /// <summary>What names are, coloured over the grammar's colours, while the script is followed.</summary>
        public Languages.ColourLayer? Layer { get; set; }

        /// <summary>The colours last given the layer, for the text the tree was read from.</summary>
        public SyntaxSpan[] Colours { get; set; } = [];
    }

    /// <summary>A script's types for a version of its text: its tree, its analysis, and the analyzer that read them.</summary>
    public sealed record Typed(int Version, GDScriptTree Tree, GDScriptAnalysis Analysis, GDScriptAnalyzer Analyzer, string Text, HintList Hints,
        IReadOnlyList<GDScriptFinding> Findings, SyntaxSpan[] Colours, TextRange? ColoursDiffer);

    public static bool IsScript(string path) => GodotScriptDeclarations.IsScript(path);

    /// <summary>Godot's class reference for the engine's words on cards; null where none is found (<see cref="GodotEngineApis.ReferenceAsync"/>).</summary>
    public Task<GodotClassReference?> ReferenceAsync() => apis.ReferenceAsync();

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (!IsScript(document.Path) || documents.ContainsKey(document))
        {
            return;
        }
        Kept kept = new(new Debounce(Settle, () => TypeAsync(document), () => document.Checking(Checker)));
        kept.Typed = (_, e) =>
        {
            GDScriptTextChange change = GDScriptTextChange.Of(e.Edits);
            kept.Pending = kept.Pending is { } before ? before.Then(change) : change;
            kept.Pause.Restart();
        };
        if (document.Highlighter is { } grammar)
        {
            kept.Layer = new Languages.ColourLayer(grammar, document.Text);
            document.Highlighter = kept.Layer;
        }
        documents[document] = kept;
        document.Text.Changed += kept.Typed;
        IdeErrors.Observe(TypeAsync(document));
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document, out Kept? gone))
        {
            document.Text.Changed -= gone.Typed;
            gone.Pause.Stop();
            if (gone.Layer is { } layer)
            {
                if (document.Highlighter == layer)
                {
                    document.Highlighter = layer.Syntax;
                }
                layer.Dispose();
            }
        }
    }

    /// <summary>The project's other files changed: each open script typed again once changes pause.</summary>
    public void Changed()
    {
        UiThread.Check();
        foreach (Kept kept in documents.Values)
        {
            kept.Pause.Restart();
        }
    }

    public void Stop()
    {
        foreach (Kept kept in documents.Values)
        {
            kept.Pause.Stop();
        }
    }

    /// <summary>An open script's types as last read, where they are of its text as it stands; null where typing has not
    /// paused since, or for a file this follows not.</summary>
    public Typed? Latest(OpenDocument document)
    {
        UiThread.Check();
        return documents.TryGetValue(document, out Kept? kept) && kept.Current is { IsCompletedSuccessfully: true } done && done.Result is { } typed
            && typed.Version == document.Text.Version ? typed : null;
    }

    /// <summary>An open script's types for its text as it stands, read now where typing has not paused since; null for a
    /// file this follows not.</summary>
    public async Task<Typed?> TypedAsync(OpenDocument document)
    {
        UiThread.Check();
        if (!documents.TryGetValue(document, out Kept? kept))
        {
            return null;
        }
        if (kept.Current is { } current && await current is { } typed && typed.Version == document.Text.Version)
        {
            return typed;
        }
        return await TypeAsync(document);
    }

    /// <summary>Every script of the project a file is in, read as a refactoring reads them (<see cref="GDScriptRead"/>):
    /// each parsed and typed against the others and the engine's API off the window's thread, an open one's text as the
    /// editor has it (<paramref name="open"/>, by path), the rest as the project's files say; null outside a Godot
    /// project.</summary>
    public async Task<IReadOnlyList<GDScriptRead>?> ProjectReadAsync(string path, IReadOnlyDictionary<string, string> open)
    {
        if (godotOf(path) is not { } folder)
        {
            return null;
        }
        GodotEngineApi? api = await apis.ApiAsync();
        GodotScenes scenes = await index.ScenesAsync(folder);
        // A script written a moment ago, before the folder's watcher told of it, is read as the disk has it now.
        if (await BackgroundWork.Run(() => Unheard(scenes, open)) is { Count: > 0 } unheard)
        {
            index.FilesChanged(unheard);
            scenes = await index.ScenesAsync(folder);
        }
        Func<string, bool>? lacks = index.LacksOf(folder);
        return await BackgroundWork.Run(() =>
        {
            Dictionary<string, string> texts = new(StringComparer.Ordinal);
            foreach ((string res, string text) in scenes.Texts.Where(t => GodotScriptDeclarations.IsScript(t.Key)))
            {
                texts[res] = scenes.FileOf(res) is { } file && open.TryGetValue(file, out string? typed) ? typed : text;
            }
            foreach ((string file, string text) in open)
            {
                if (scenes.ResPathOf(file) is { } res && GodotScriptDeclarations.IsScript(res))
                {
                    texts[res] = text;
                }
            }
            Dictionary<string, GDScriptTree> trees = texts.ToDictionary(t => t.Key, t => GDScriptTree.Parse(t.Value), StringComparer.Ordinal);
            Dictionary<string, string> classNames = new(StringComparer.Ordinal);
            foreach ((string res, GDScriptTree tree) in trees)
            {
                if (tree.Class.Identifier is { } named)
                {
                    classNames.TryAdd(named.Name, res);
                }
            }
            GDScriptAnalyzer analyzer = new(new GDScriptProject(api, p => trees.GetValueOrDefault(p), classNames, Singletons(scenes), uids: scenes.ResOfUid, nodes: NodesOf(scenes),
                lacks: lacks));
            return (IReadOnlyList<GDScriptRead>?)[.. trees.Select(t => new GDScriptRead(t.Key, analyzer.Analyze(t.Value, t.Key)))];
        });
    }

    /// <summary>How long after a script is written the folder's watcher may not yet have told of it.</summary>
    private static readonly TimeSpan Telling = TimeSpan.FromSeconds(10);

    /// <summary>The project's closed scripts written within <see cref="Telling"/> whose text on disk is not the one the
    /// index holds: written since the index read them, the watcher not yet heard.</summary>
    private static List<string> Unheard(GodotScenes scenes, IReadOnlyDictionary<string, string> open)
    {
        DateTime since = DateTime.UtcNow - Telling;
        List<string> unheard = [];
        foreach ((string res, string text) in scenes.Texts.Where(t => GodotScriptDeclarations.IsScript(t.Key)))
        {
            if (scenes.FileOf(res) is { } file && !open.ContainsKey(file) && WrittenAt(file) >= since && EditorArea.ReadText(file) is { } now && now != text)
            {
                unheard.Add(file);
            }
        }
        return unheard;
    }

    private static DateTime WrittenAt(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>The project's autoloads that run a GDScript, by name, the res:// path of each's script; none outside a Godot project.</summary>
    public async Task<IReadOnlyDictionary<string, string>> AutoloadsAsync(string path)
    {
        Dictionary<string, string> autoloads = new(StringComparer.Ordinal);
        if (godotOf(path) is { } folder)
        {
            GodotScenes scenes = await index.ScenesAsync(folder);
            foreach (GodotDefined autoload in scenes.Defined.Autoloads)
            {
                if (scenes.AutoloadFile(autoload.Name) is { } file && GodotScriptDeclarations.IsScript(file))
                {
                    autoloads[autoload.Name] = file;
                }
            }
        }
        return autoloads;
    }

    /// <summary>The script typed again, after the typing under way: each starts from the tree the last one left.</summary>
    private Task<Typed?> TypeAsync(OpenDocument document)
    {
        UiThread.Check();
        if (!documents.TryGetValue(document, out Kept? kept))
        {
            return Task.FromResult<Typed?>(null);
        }
        Task<Typed?> typing = AfterAsync(document, kept, kept.Current);
        kept.Current = typing;
        return typing;
    }

    private async Task<Typed?> AfterAsync(OpenDocument document, Kept kept, Task<Typed?>? before)
    {
        if (before is not null)
        {
            await before.Ended();
        }
        if (!documents.ContainsKey(document))
        {
            return null;
        }
        TextSnapshot snapshot = document.Text.Snapshot;
        GDScriptTextChange? change = kept.Pending;
        kept.Pending = null;
        Typed? typed = await TypeOffAsync(document, snapshot, document.Text.Version, kept.Tree, kept.TreeText, change, kept.Colours);
        if (typed is null || !documents.ContainsKey(document))
        {
            return typed;
        }
        kept.Tree = typed.Tree;
        kept.TreeText = typed.Text;
        if (document.Text.Version == typed.Version)
        {
            if (!Alike(document.InlayHints, typed.Hints))
            {
                document.InlayHints = typed.Hints;
            }
            Mark(document, typed.Findings);
            kept.Colours = typed.Colours;
            kept.Layer?.Set(typed.Colours, typed.Version, typed.ColoursDiffer);
        }
        return typed;
    }

    /// <summary>A script's findings marked: beside its server's until the server speaks of it, else alone; none set where
    /// the marks shown, carried through the edits since, already say them.</summary>
    private void Mark(OpenDocument document, IReadOnlyList<GDScriptFinding> findings)
    {
        Web.WebDocument? web = webOf(document);
        IReadOnlyList<Marker> shown = web?.Passengers ?? document.Markers;
        if (findings.Count == 0 && shown.Count == 0)
        {
            return;
        }
        Marker[] marks = [.. findings.Select(f => Marker.Coded(TextRange.FromBounds(f.Start, f.End), f.IsError ? MarkerKind.Error : MarkerKind.Warning,
            f.IsError ? null : f.Code, f.Message))];
        if (marks.AsSpan().SequenceEqual(shown is Marker[] array ? array : [.. shown]))
        {
            return;
        }
        if (web is not null)
        {
            web.ShowPassengers(marks);
        }
        else if (marks.Length > 0 || document.Markers.Count > 0)
        {
            document.Markers = marks;
        }
    }

    private async Task<Typed?> TypeOffAsync(OpenDocument document, TextSnapshot snapshot, int version, GDScriptTree? tree, string? treeText, GDScriptTextChange? change,
        SyntaxSpan[] coloured)
    {
        string path = document.Path;
        GodotEngineApi? api = await apis.ApiAsync();
        string? game = godotOf(path);
        GodotScenes? scenes = game is not null ? await index.ScenesAsync(game) : null;
        Func<string, bool>? lacks = game is not null ? index.LacksOf(game) : null;
        return await BackgroundWork.Run(() =>
        {
            string text = snapshot.ToString();
            TextEdit? made = tree is not null && treeText is not null && change is { } edit && edit.Fits(treeText.Length, text.Length) ? edit.Edit(text) : null;
            GDScriptTree parsed = made is { } one ? tree!.Reparse(text, one) : GDScriptTree.Parse(text);
            string res = scenes?.ResPathOf(path) ?? "res://" + Path.GetFileName(path);
            GDScriptProject project = ProjectOf(scenes, api, res, parsed, lacks);
            GDScriptAnalyzer analyzer = new(project);
            GDScriptAnalysis analysis = analyzer.Analyze(parsed, res);
            HintList hints = HintList.Of([.. analysis.Inferred().Select(i => new InlayHint(i.Offset, $": {i.Type}", Kind: InlayHintKind.Type))]);
            GDScriptWarningSettings settings = GDScriptWarningSettings.Of(scenes?.Texts.GetValueOrDefault("res://" + GodotProject.FileName));
            IReadOnlyList<GDScriptFinding> findings = [.. analysis.Findings(settings).Where(f => f.End <= text.Length)];
            SyntaxSpan[] colours = [.. analysis.Colours().Select(c => new SyntaxSpan(c.Start, c.Length, KindOf(c.Colour)))];
            // Read whole with no edit to carry the last through, every name may differ.
            TextRange? differ = made is null && text != treeText ? TextRange.FromBounds(0, text.Length)
                : Languages.ColourLayer.Differing(coloured, made is { } carried ? [carried] : [], colours);
            return (Typed?)new Typed(version, parsed, analysis, analyzer, text, hints, findings, colours, differ);
        });
    }

    /// <summary>The editor's colour of what a name is: C#'s for its like.</summary>
    private static TokenKind KindOf(GDScriptColour colour) => colour switch
    {
        GDScriptColour.Method => TokenKind.Method,
        GDScriptColour.Field => TokenKind.Field,
        GDScriptColour.Property => TokenKind.Property,
        GDScriptColour.Constant => TokenKind.Constant,
        GDScriptColour.EnumMember => TokenKind.EnumMember,
        GDScriptColour.Event => TokenKind.Event,
        GDScriptColour.Enum => TokenKind.Enum,
        GDScriptColour.Class => TokenKind.Class,
        GDScriptColour.Struct => TokenKind.Struct,
        GDScriptColour.Parameter => TokenKind.Parameter,
        _ => TokenKind.Local,
    };

    /// <summary>Whether the hints shown, carried through the edits since, say what the new ones say where they say it: a
    /// pause that changed no inferred type sets none, so typing in a long script costs the window no list of its hints.</summary>
    private static bool Alike(IReadOnlyList<InlayHint> shown, HintList made)
    {
        if (shown.Count != made.Count)
        {
            return false;
        }
        for (int i = 0; i < made.Count; i++)
        {
            if (shown[i] is var was && made[i] is var now && (was.Offset != now.Offset || was.Label != now.Label || was.Kind != now.Kind))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>What a script's types are read against: the project's scripts (this one as the editor has it), their
    /// <c>class_name</c>s, its singletons, its uids, the paths its files lack (<paramref name="lacks"/>, where listed whole)
    /// and the engine's API.</summary>
    private static GDScriptProject ProjectOf(GodotScenes? scenes, GodotEngineApi? api, string res, GDScriptTree own, Func<string, bool>? lacks)
    {
        if (scenes is null)
        {
            return new GDScriptProject(api, path => path == res ? own : null, new Dictionary<string, string>(), new Dictionary<string, string>());
        }
        Dictionary<string, GDScriptTree> trees = new(StringComparer.Ordinal) { [res] = own };
        GDScriptTree? Tree(string path)
        {
            if (!trees.TryGetValue(path, out GDScriptTree? tree) && scenes.Texts.TryGetValue(path, out string? text) && GodotScriptDeclarations.IsScript(path))
            {
                trees[path] = tree = Parsed.Of(text);
            }
            return tree;
        }
        Dictionary<string, string> classNames = new(StringComparer.Ordinal);
        foreach (string script in scenes.Texts.Keys.Where(GodotScriptDeclarations.IsScript))
        {
            if (scenes.ScriptOf(script)?.ClassName is { } named)
            {
                classNames.TryAdd(named, script);
            }
        }
        return new GDScriptProject(api, Tree, classNames, Singletons(scenes), uids: scenes.ResOfUid, nodes: NodesOf(scenes), lacks: lacks);
    }

    /// <summary>The project's singleton autoloads, by name, the res:// path of each's script or scene: those code reads by
    /// their names, a scene's typed as Godot types it, a Node.</summary>
    private static Dictionary<string, string> Singletons(GodotScenes scenes)
    {
        Dictionary<string, string> autoloads = new(StringComparer.Ordinal);
        foreach (GodotDefined autoload in scenes.Defined.Autoloads)
        {
            if (scenes.Defined.IsSingleton(autoload.Name) && scenes.AutoloadFile(autoload.Name) is { } file)
            {
                autoloads[autoload.Name] = file;
            }
        }
        return autoloads;
    }

    /// <summary>The node a script's node path reaches, as every scene running the script has it: its engine class and the
    /// script it runs; null where a scene reaches none, one of no class written (an instance), or they differ. Each read once.</summary>
    private static Func<string, string, GDScriptNodeAt?> NodesOf(GodotScenes scenes)
    {
        Dictionary<(string, string), GDScriptNodeAt?> known = [];
        return (script, path) =>
        {
            lock (known)
            {
                if (known.TryGetValue((script, path), out GDScriptNodeAt? had))
                {
                    return had;
                }
            }
            GDScriptNodeAt? found = null;
            foreach ((_, GodotTreeNode node) in scenes.RunsOf(script))
            {
                GDScriptNodeAt? here = GodotScenes.Resolve(node, path) is { Found: { Type: { } type } reached, Unknown: false } ? new GDScriptNodeAt(type, reached.Script) : null;
                if (here is null || found is not null && found != here)
                {
                    found = null;
                    break;
                }
                found = here;
            }
            lock (known)
            {
                known[(script, path)] = found;
            }
            return found;
        };
    }

    /// <summary>Each other script's tree, parsed once for each text the project reads.</summary>
    private static class Parsed
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, GDScriptTree> Kept = [];

        public static GDScriptTree Of(string text) => Kept.GetValue(text, GDScriptTree.Parse);
    }
}

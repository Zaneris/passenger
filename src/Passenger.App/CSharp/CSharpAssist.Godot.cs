using System.Text.RegularExpressions;
using Passenger.App.Editors;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A Godot project's C# read against its scenes (<see cref="GodotChecks"/>): in a string GetNode is given,
/// completion offers the nodes of the scenes the script runs in (those of the type asked for), the hover card says the node
/// it names in each, Go to Declaration goes to the node's section and Rename renames the node in every scene and every path
/// of the code that names it (<see cref="RenameNodesAsync"/>); a res:// path completes from the project's files and goes
/// to its file; a string naming a member on a node of GDScript completes from its script's members and hovers the one it
/// names; an <c>[Export]</c> member's card says each value the scenes set it to.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>A line up to the caret that ends inside the string a GetNode or GetNodeOrNull call is given.</summary>
    private static readonly Regex NodeString = new(@"GetNode(OrNull)?\s*(<[^()]*>)?\s*\(\s*(new\s+NodePath\s*\(\s*)?""[^""]*$", RegexOptions.CultureInvariant);

    /// <summary>A line up to the caret that ends inside a string of a res:// path.</summary>
    private static readonly Regex ResString = new(@"""res://[^""]*$", RegexOptions.CultureInvariant);

    /// <summary>The characters a node's path or a res:// path holds beyond a name's, which narrow its list rather than close it.</summary>
    private const string PathCharacters = "/%.:-";

    /// <summary>Whether the caret is where a Godot path is typed, so a '/' or a quote typed there opens its list.</summary>
    private bool InGodotPath(int caret)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int line = text.GetLineFromOffset(Math.Min(caret, text.Length));
        string before = text.GetText(text.GetLineStart(line), Math.Min(caret, text.Length) - text.GetLineStart(line));
        return NodeString.IsMatch(before) || ResString.IsMatch(before);
    }

    /// <summary>The string around an offset on its line: where its characters start (after its quote) and end (at its
    /// closing quote, else the line's end).</summary>
    private static (int Start, int End) StringAround(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        int lineStart = text.GetLineStart(line);
        string row = text.GetLineText(line);
        int column = Math.Min(offset - lineStart, row.Length);
        int open = column > 0 ? row.LastIndexOf('"', column - 1) : -1;
        int close = row.IndexOf('"', column);
        return (lineStart + open + 1, lineStart + (close < 0 ? row.Length : close));
    }

    /// <summary>A place the server names in a file, as an offset of its text.</summary>
    private static int OffsetOf(LspDocument lsp, Place at) => lsp.OffsetOf(new LspPosition(at.Line, at.Character));

    /// <summary>The string naming a member on an engine class's object the code tells the node of that holds an offset, by
    /// the server's places; null for none.</summary>
    private static GodotStringRead? MemberStringAt(LspDocument lsp, GodotRead read, int offset, int quoted) =>
        read.Found.Strings.FirstOrDefault(s => !s.Script && (s.Node is not null || s.Export is not null)
            && (OffsetOf(lsp, s.At) <= offset && offset <= OffsetOf(lsp, s.End) || OffsetOf(lsp, s.At) == quoted - 1));

    /// <summary>What completion offers in a string naming a member on a node of GDScript: the functions, signals, or
    /// variables and constants its scripts declare (<see cref="GodotChecks.Members"/>), each with where; null where none.</summary>
    private static CodeCompletion.OwnItems? MemberCompletion(GodotRead read, GodotStringRead named, int start, string tail)
    {
        GodotChecks.Candidates members = GodotChecks.Members(read.Scenes, named.Kind, GodotChecks.ScriptsOf(read.Found, named.Class), named.Node, named.Export, _ => null);
        CompletionItemKind kind = named.Kind switch { GodotNameKind.Method => CompletionItemKind.Method, GodotNameKind.Signal => CompletionItemKind.Event, _ => CompletionItemKind.Property };
        IReadOnlyList<CompletionItem> items = [.. members.Names.Select(n => new CompletionItem
        {
            Label = n.Name,
            Kind = kind,
            Detail = n.Places.FirstOrDefault() is { } place ? $"{n.Detail.Split(' ')[0]} · {place.Shown}" : n.Detail,
            FilterText = n.Name,
            SortText = " " + n.Name,
        })];
        return items.Count == 0 ? null : new CodeCompletion.OwnItems(start, items, tail);
    }

    /// <summary>What the hover card says of a string naming a member on a node of GDScript: each declaration of its name the
    /// node's scripts hold, as written, with where; null where none declares it.</summary>
    private static string? MemberHover(GodotRead read, GodotStringRead named)
    {
        GodotChecks.Candidates members = GodotChecks.Members(read.Scenes, named.Kind, GodotChecks.ScriptsOf(read.Found, named.Class), named.Node, named.Export, _ => null);
        List<string> lines = [];
        foreach (GodotPlace place in members.Names.Where(n => n.Name == named.Name).SelectMany(n => n.Places))
        {
            if (read.Scenes.Texts.TryGetValue(place.Res, out string? text))
            {
                TextSnapshot script = TextSnapshot.From(text);
                lines.Add($"{script.GetLineText(script.GetLineFromOffset(place.Start)).Trim().TrimEnd(':')}\n{place.Shown}, on {members.Where}");
            }
        }
        return lines.Count == 0 ? null : string.Join("\n", lines.Distinct());
    }

    /// <summary>The GetNode call whose string holds an offset, by the server's places; null for none.</summary>
    private static GodotNodeCall? CallAt(LspDocument lsp, GodotRead read, int offset, int quoted) =>
        read.Found.Calls.FirstOrDefault(c => OffsetOf(lsp, c.At) <= offset && offset <= OffsetOf(lsp, c.End)
            || OffsetOf(lsp, c.At) == quoted - 1);

    /// <summary>What completion offers in a Godot path's string: a GetNode call's nodes, from the node each scene runs its
    /// script on, those of the type it asks for (all where none is), and from its "/" the autoloads; a res:// path's files of
    /// the project; a string of a kind's names (<see cref="KindCompletion"/>). With where the string's characters start and
    /// the rest after the caret, which an item replaces; null anywhere else.</summary>
    private async Task<CodeCompletion.OwnItems?> GodotCompletionAsync(LspDocument lsp, TextSnapshot asked, int caret, CancellationToken cancel)
    {
        bool inPath = InGodotPath(caret);
        if (!(inPath || InString(asked, caret)) || context.Editors.Active is not { } document || context.CSharp is not { } csharp
            || await csharp.GodotAsync(document.Path, cancel) is not { } read || lsp.Text.Snapshot != asked)
        {
            return null;
        }
        (int start, int end) = StringAround(asked, caret);
        string tail = caret < end ? asked.GetText(caret, end - caret) : string.Empty;
        string typed = asked.GetText(start, caret - start);
        if (KindAt(lsp, read, caret, start) is { } kind)
        {
            return KindCompletion(read, kind, start, tail, await EditorNamesAsync(read, document.Path, EditorKindOf(kind), cancel));
        }
        if (MemberStringAt(lsp, read, caret, start) is { } member)
        {
            return MemberCompletion(read, member, start, tail);
        }
        if (OwnSignalAt(lsp, read, caret, start) is { } signal)
        {
            return OwnSignalCompletion(read, signal, start, tail, await EditorNamesAsync(read, document.Path, GodotIdeKind.Signals, cancel));
        }
        if (!inPath)
        {
            return null;
        }
        if (typed.StartsWith('/') && CallAt(lsp, read, caret, start) is not null)
        {
            return AutoloadCompletion(read, start, tail, await EditorNamesAsync(read, document.Path, GodotIdeKind.NodePaths, cancel));
        }
        if (typed.StartsWith("res://", StringComparison.Ordinal) && csharp.GodotScenes is { } index)
        {
            Task<IReadOnlyList<string>> resources = EditorNamesAsync(read, document.Path, GodotIdeKind.ResourcePaths, cancel);
            Task<IReadOnlyList<string>> scenes = EditorNamesAsync(read, document.Path, GodotIdeKind.ScenePaths, cancel);
            List<CompletionItem> files = [.. index.FilesUnder(read.Folder).Select(f => GodotScenes.ResPathOf(read.Folder, f)).OfType<string>()
                .Select(res => new CompletionItem { Label = res, Kind = CompletionItemKind.File, Detail = Path.GetExtension(res), FilterText = res, SortText = " " + res })];
            HashSet<string> listed = [.. files.Select(f => f.Label)];
            files.AddRange((await resources).Concat(await scenes).Where(res => res.StartsWith("res://", StringComparison.Ordinal) && listed.Add(res))
                .Select(res => new CompletionItem { Label = res, Kind = CompletionItemKind.File, Detail = $"{Path.GetExtension(res)} · the open Godot editor's", FilterText = res, SortText = " " + res }));
            return files.Count == 0 ? null : new CodeCompletion.OwnItems(start, files, tail) { WordCharacters = PathCharacters };
        }
        if (CallAt(lsp, read, caret, start) is not { } call || call.Class >= read.Found.Classes.Count)
        {
            return null;
        }
        Task<IReadOnlyList<string>> edited = EditorNamesAsync(read, document.Path, GodotIdeKind.NodePaths, cancel);
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);
        List<CompletionItem> unfit = [];
        foreach ((string scene, GodotTreeNode node) in read.Found.Classes[call.Class].Scripts.SelectMany(read.Scenes.RunsOf))
        {
            foreach ((string path, GodotTreeNode target) in GodotScenes.PathsFrom(node))
            {
                CompletionItem item = new()
                {
                    Label = path,
                    Kind = CompletionItemKind.Field,
                    Detail = $"{GodotChecks.Shown(target)} · {Path.GetFileName(scene)}",
                    FilterText = path,
                    SortText = " " + path,
                };
                if (call.Type is { } wanted && GodotChecks.Fits(wanted, target, read.Found) == false)
                {
                    unfit.Add(item);
                }
                else
                {
                    items.TryAdd(path, item);
                }
            }
        }
        List<CompletionItem> offered = items.Count > 0 ? [.. items.Values] : [.. unfit.DistinctBy(i => i.Label)];
        HashSet<string> known = [.. items.Keys, .. unfit.Select(u => u.Label)];
        // The editor answers its edited scene's nodes as paths from the script's node, the node itself and autoloads too.
        offered.AddRange((await edited).Where(p => p != "." && !p.StartsWith('/') && known.Add(p)).Select(p => new CompletionItem
        {
            Label = p,
            Kind = CompletionItemKind.Field,
            Detail = "node of the scene the open Godot editor edits",
            FilterText = p,
            SortText = " " + p,
        }));
        return offered.Count == 0 ? null : new CodeCompletion.OwnItems(start, offered, tail) { WordCharacters = PathCharacters };
    }

    /// <summary>The string naming a signal of the class's own node that holds an offset (<c>EmitSignal("Scored")</c> in a
    /// script's class), by the server's places; null for none.</summary>
    private static GodotStringRead? OwnSignalAt(LspDocument lsp, GodotRead read, int offset, int quoted) =>
        read.Found.Strings.FirstOrDefault(s => s.Kind == GodotNameKind.Signal && s.Script && s.Node is null && s.Export is null
            && s.Class >= 0 && s.Class < read.Found.Classes.Count && read.Found.Classes[s.Class].Name == s.On
            && (OffsetOf(lsp, s.At) <= offset && offset <= OffsetOf(lsp, s.End) || OffsetOf(lsp, s.At) == quoted - 1));

    /// <summary>What completion offers in a string naming a signal of the class's own node: the class's <c>[Signal]</c>
    /// delegates, then the signals the open Godot editor answers for the script (its engine class's too); null where none.</summary>
    private static CodeCompletion.OwnItems? OwnSignalCompletion(GodotRead read, GodotStringRead named, int start, string tail, IReadOnlyList<string> editor)
    {
        GodotClassFound own = read.Found.Classes[named.Class];
        List<CompletionItem> items = [.. own.Signals.Select(s => new CompletionItem
        {
            Label = s.Name,
            Kind = CompletionItemKind.Event,
            Detail = $"signal{s.Shown} · {own.Name}",
            FilterText = s.Name,
            SortText = " " + s.Name,
        })];
        HashSet<string> listed = [.. items.Select(i => i.Label)];
        items.AddRange(editor.Where(listed.Add).Select(s => new CompletionItem
        {
            Label = s,
            Kind = CompletionItemKind.Event,
            Detail = $"signal of {own.Engine ?? own.Name}, as the open Godot editor answers",
            FilterText = s,
            SortText = "~" + s,
        }));
        return items.Count == 0 ? null : new CodeCompletion.OwnItems(start, items, tail);
    }

    /// <summary>What the hover card says of a Godot project's C# (<see cref="GodotWords"/>), and the preview of the file a
    /// res:// path or uid:// name there names (<see cref="GodotPreviews"/>); neither for a file of no Godot project.</summary>
    private async Task<(string? Words, FilePreview? Preview, bool OfString)> GodotHoverAsync(OpenDocument document, int offset, CancellationToken cancel)
    {
        if (context.WebOf(document) is not null || context.CSharp is not { } csharp || csharp.For(document)?.Lsp is not { } lsp
            || csharp.GodotOf?.Invoke(document.Path) is null || await csharp.GodotAsync(document.Path, cancel) is not { } read)
        {
            return (null, null, false);
        }
        (string? words, bool ofString) = GodotWords(lsp, document, offset, read);
        FilePreview? preview = read.Found.Resources.FirstOrDefault(r => OffsetOf(lsp, r.At) <= offset && offset <= OffsetOf(lsp, r.End)) is { } named
            ? await BackgroundWork.Run(() => GodotPreviews.Of(read.Scenes, named.Path, path => File.Exists(path)), cancel)
            : null;
        return (words, preview, ofString);
    }

    /// <summary>What the hover card says of a Godot project's C#: where a string of a kind's name is defined
    /// (<see cref="KindHover"/>); a GDScript member a string names; the node a GetNode path names in each scene its script
    /// runs in, or that none answers; the file a uid:// names; an <c>[Export]</c> member's value in each scene and resource
    /// that sets it. With whether it says what a string names, which the server would describe as System.String.</summary>
    private static (string? Words, bool OfString) GodotWords(LspDocument lsp, OpenDocument document, int offset, GodotRead read)
    {
        (int start, _) = StringAround(lsp.Text.Snapshot, offset);
        if (KindAt(lsp, read, offset, start) is { Exact: true } kind)
        {
            return (KindHover(read, kind), true);
        }
        if (MemberStringAt(lsp, read, offset, start) is { } member && MemberHover(read, member) is { } declared)
        {
            return (declared, true);
        }
        if (CallAt(lsp, read, offset, start) is { } call && call.Class < read.Found.Classes.Count)
        {
            List<string> lines = [];
            foreach ((string scene, GodotTreeNode node) in read.Found.Classes[call.Class].Scripts.SelectMany(read.Scenes.RunsOf))
            {
                GodotResolved resolved = GodotScenes.Resolve(node, call.Path);
                lines.Add(resolved.Found is { } target ? $"{GodotChecks.Shown(target)} '{target.Path}' in {GodotChecks.Place(read.Scenes, target.Declared.Scene, target)}"
                    : resolved.Unknown ? $"Not known from {Path.GetFileName(scene)}: the path leaves what it holds" : $"No node in {Path.GetFileName(scene)}");
            }
            return (lines.Count == 0 ? null : string.Join('\n', [$"'{call.Path}' from {read.Found.Classes[call.Class].Name}:", .. lines.Distinct()]), lines.Count > 0);
        }
        if (read.Found.Resources.FirstOrDefault(r => r.Path.StartsWith(GodotUids.Scheme, StringComparison.Ordinal) && OffsetOf(lsp, r.At) <= offset && offset <= OffsetOf(lsp, r.End)) is { } uid)
        {
            return (read.Scenes.ResOfUid(uid.Path) is { } res ? $"{uid.Path} is {res}" : $"No file of the Godot project has {uid.Path}", true);
        }
        foreach (GodotClassFound type in read.Found.Classes)
        {
            if (type.Exports.FirstOrDefault(e => SameFile(e.Path, document.Path) && OffsetOf(lsp, e.At) <= offset && offset <= OffsetOf(lsp, e.End)) is { } export)
            {
                IReadOnlyList<string> set = ExportValues(read.Scenes, type.Scripts, export.Name);
                return (set.Count == 0 ? $"No scene or resource sets {export.Name}: each takes its default" : string.Join('\n', [$"{export.Name} is set in:", .. set]), false);
            }
        }
        return (null, false);
    }

    /// <summary>Each value scenes and resources set an exported member to, on the nodes and resources scripts run as: where
    /// (the file and line), on which node, and the value as written.</summary>
    internal static IReadOnlyList<string> ExportValues(GodotScenes scenes, IReadOnlyList<string> scripts, string name)
    {
        List<string> set = [];
        HashSet<(string, int)> seen = [];
        foreach ((string scene, GodotTreeNode node) in scripts.SelectMany(scenes.RunsOf))
        {
            foreach ((string written, Passenger.Workspace.GodotNode section) in node.Written)
            {
                if (section.Section.Property(name) is { } property && seen.Add((written, section.Section.Start)))
                {
                    GodotFile file = scenes.Files[written];
                    set.Add($"{Path.GetFileName(written)}:{file.LineOf(property.KeyStart) + 1}, {node.Name}: {Shortened(file.Written(property.Value))}");
                }
            }
        }
        foreach ((string res, GodotFile file) in scenes.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (file.Resource is { } resource && scripts.Contains(file.External(resource.Property("script")?.Value is { Call: "ExtResource", Text: { } id } ? id : null)?.Path)
                && resource.Property(name) is { } property)
            {
                set.Add($"{Path.GetFileName(res)}:{file.LineOf(property.KeyStart) + 1}: {Shortened(file.Written(property.Value))}");
            }
        }
        return set;
    }

    private static string Shortened(string value) => value.Length > 80 ? value[..77] + "…" : value.ReplaceLineEndings(" ");

    private static bool SameFile(string left, string right) => PathNames.Same(left, right) || PathNames.Same(PathNames.Canonical(left), right);

    /// <summary>Where Go to Declaration goes from a Godot string: a script's signal, method or property a string Godot's calls
    /// take names; what a string of a kind names (<see cref="GodotKindDeclaredAsync"/>); a res:// path's file; a GetNode
    /// path's node, in each scene its script runs in that has it. Null anywhere else.</summary>
    private async Task<Declared?> GodotDeclaredAsync(LspDocument lsp, OpenDocument document, int offset)
    {
        TextSnapshot asked = lsp.Text.Snapshot;
        if (context.CSharp is not { } csharp || csharp.GodotOf?.Invoke(document.Path) is null)
        {
            return null;
        }
        if (!InString(asked, offset))
        {
            // A layer's number leads to its name in project.godot.
            return InLayerArgument(asked, offset) ? await GodotKindDeclaredAsync(document, offset) : null;
        }
        // A string Godot's calls take naming a script's signal, method or property goes to its declaration.
        if (await GodotNamesAsync(document, lsp, offset, uses: false) is { Via: GodotNameVia.String, Symbol: { Path: { } declaring, At: { } place } })
        {
            LspPosition at = new(place.Line, place.Character);
            return new Declared([new LspLocation(DocumentUris.FromPath(declaring), new LspRange(at, at))], Refusals.NoDeclaration);
        }
        if (await GodotKindDeclaredAsync(document, offset) is { } defined)
        {
            return defined;
        }
        if (await csharp.GodotAsync(document.Path, CancellationToken.None) is not { } read || lsp.Text.Snapshot != asked)
        {
            return null;
        }
        if (ScriptMemberDeclared(lsp, read, offset) is { } member)
        {
            return member;
        }
        if (read.Found.Resources.FirstOrDefault(r => OffsetOf(lsp, r.At) <= offset && offset <= OffsetOf(lsp, r.End)) is { } resource)
        {
            string named = resource.Path.Split("::")[0];
            string? file = read.Scenes.FileOf(named);
            bool there = file is not null && await BackgroundWork.Run(() => File.Exists(file));
            return new Declared(there ? [new LspLocation(DocumentUris.FromPath(file!), new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)))] : [],
                $"No file {named} is in the Godot project");
        }
        (int start, _) = StringAround(asked, offset);
        if (CallAt(lsp, read, offset, start) is not { } call || call.Class >= read.Found.Classes.Count)
        {
            return null;
        }
        List<(string Path, TextPosition Position)> places = [];
        List<string?> names = [];
        foreach ((string scene, GodotTreeNode node) in read.Found.Classes[call.Class].Scripts.SelectMany(read.Scenes.RunsOf))
        {
            if (GodotScenes.Resolve(node, call.Path).Found is { } target && read.Scenes.FileOf(target.Declared.Scene) is { } file
                && !places.Any(p => PathNames.Same(p.Path, file) && p.Position.Line == target.Declared.Node.Section.Line))
            {
                places.Add((file, new TextPosition(target.Declared.Node.Section.Line, 0)));
                names.Add($"{GodotChecks.Shown(target)} {target.Name} in {GodotChecks.Place(read.Scenes, target.Declared.Scene, target)}");
            }
        }
        return new Declared(Declared.Served(places, ServerPlaces.Texts(context.Editors)), $"No scene of {read.Found.Classes[call.Class].Name}'s has a node '{call.Path}'")
        {
            Names = names,
            Title = "Nodes",
        };
    }

    /// <summary>Where Go to Declaration goes from a string naming a member an engine class's object lacks on a node the code
    /// tells it is: the function, signal, variable or constant of its name the GDScript of each such node declares, it or a
    /// script it extends (<see cref="GodotChecks.Members"/>); null for an offset in no such string.</summary>
    private static Declared? ScriptMemberDeclared(LspDocument lsp, GodotRead read, int offset)
    {
        if (read.Found.Strings.FirstOrDefault(s => OffsetOf(lsp, s.At) <= offset && offset <= OffsetOf(lsp, s.End)) is not { Found: null, Script: false } named
            || named.Node is null && named.Export is null)
        {
            return null;
        }
        GodotChecks.Candidates members = GodotChecks.Members(read.Scenes, named.Kind, GodotChecks.ScriptsOf(read.Found, named.Class), named.Node, named.Export, _ => null);
        List<LspLocation> places = [];
        List<string?> names = [];
        foreach (GodotDefined member in members.Names.Where(n => n.Name == named.Name))
        {
            foreach (GodotPlace place in member.Places)
            {
                if (read.Scenes.FileOf(place.Res) is { } file && read.Scenes.Defined.TextOf(place.Res) is { } text)
                {
                    places.Add(new LspLocation(DocumentUris.FromPath(file), ServerLines.Of(TextSnapshot.From(text)).RangeOf(new TextRange(place.Start, place.End - place.Start))));
                    names.Add($"{member.Detail} {member.Name} in {place.Shown}");
                }
            }
        }
        return places.Count == 0 ? null : new Declared(places, Refusals.NoDeclaration) { Names = names, Title = "Declarations" };
    }

    /// <summary>The GDScript member a string of a Godot project's C# names at an offset, on a node the code tells it is
    /// (<see cref="ScriptMemberDeclared"/>'s): the script declaring it and where; a problem instead where several scripts
    /// declare one of its name; null where the string names none.</summary>
    internal async Task<(string Script, int Offset, string? Problem)?> ScriptMemberAtAsync(OpenDocument document, int offset)
    {
        if (context.CSharp is not { } csharp || csharp.For(document)?.Lsp is not { } lsp || csharp.GodotOf?.Invoke(document.Path) is null
            || await csharp.GodotAsync(document.Path, CancellationToken.None) is not { } read
            || read.Found.Strings.FirstOrDefault(s => OffsetOf(lsp, s.At) <= offset && offset <= OffsetOf(lsp, s.End)) is not { Found: null, Script: false } named
            || named.Node is null && named.Export is null)
        {
            return null;
        }
        GodotChecks.Candidates members = GodotChecks.Members(read.Scenes, named.Kind, GodotChecks.ScriptsOf(read.Found, named.Class), named.Node, named.Export, _ => null);
        IReadOnlyList<GodotPlace> places = [.. members.Names.Where(n => n.Name == named.Name).SelectMany(n => n.Places).Where(p => p.Defines)];
        string[] scripts = [.. places.Select(p => p.Res).Distinct(StringComparer.Ordinal)];
        if (scripts.Length == 0 || read.Scenes.FileOf(scripts[0]) is not { } file)
        {
            return null;
        }
        return scripts.Length > 1
            ? (file, 0, $"'{named.Name}' on {members.Where} is declared by {Plural.Listed([.. scripts.Select(GodotDefinitions.Shown)])}: rename it in its script")
            : (file, places[0].Start, null);
    }

    /// <summary>The nodes the name of a GetNode path at an offset names, each in the scene its script runs in, with the name
    /// as the path writes it and the scenes read; null where the offset is on no such name.</summary>
    internal async Task<(GodotRead Read, string Name, IReadOnlyList<GodotTreeNode> Nodes)?> GodotNodeAtAsync(OpenDocument document, int offset)
    {
        if (context.CSharp is not { } csharp || csharp.For(document)?.Lsp is not { } lsp || csharp.GodotOf?.Invoke(document.Path) is null
            || await csharp.GodotAsync(document.Path, CancellationToken.None) is not { } read)
        {
            return null;
        }
        (int start, _) = StringAround(lsp.Text.Snapshot, offset);
        if (CallAt(lsp, read, offset, start) is not { Exact: true } call || call.Class >= read.Found.Classes.Count)
        {
            return null;
        }
        int at = offset - OffsetOf(lsp, call.At);
        List<GodotTreeNode> nodes = [];
        string? name = null;
        foreach ((string scene, GodotTreeNode node) in read.Found.Classes[call.Class].Scripts.SelectMany(read.Scenes.RunsOf))
        {
            foreach ((int from, int to, GodotTreeNode step) in GodotScenes.Resolve(node, call.Path).Steps)
            {
                string written = call.Path[from..to];
                if (from <= at && at <= to && written != ".." && !nodes.Any(n => n.Same(step)))
                {
                    nodes.Add(step);
                    name = step.Name;
                }
            }
        }
        return name is null ? null : (read, name, nodes);
    }

    /// <summary>Rename's box for a node a GetNode path names at the caret; once a name is typed, <see cref="RenameNodesAsync"/>.
    /// False where the caret is on no node's name.</summary>
    private async Task<bool> ShowNodeRename(OpenDocument document, int caret)
    {
        int version = document.Text.Version;
        if (await GodotNodeAtAsync(document, caret) is not (_, { } old, { Count: > 0 }) || context.Editors.Active != document || document.Text.Version != version)
        {
            return false;
        }
        RenameBox box = new(old);
        popups.ShowModal(box, caret, async name =>
        {
            if (name is null || name == old)
            {
                return;
            }
            // Read again for each attempt, as a file written a moment before is one the index has yet to hear of.
            RefactorOutcome made = await context.PatientlyAsync(AppActions.Rename, async patience => await GodotNodeAtAsync(document, caret) is ({ } again, { } same, { Count: > 0 } now) && same == old
                ? await RenameNodesAsync(again, now, old, name, patience) : RefactorOutcome.Refused($"{old} was not renamed: {document.Name} changed as it was read"));
            context.Say(made.Problem ?? made.Said);
        });
        return true;
    }

    /// <summary>Nodes renamed, as one command of global undo: in each scene, where it is declared and every path through it
    /// (<see cref="GodotScenes.Renames"/>), and in the code of every script of the scenes that hold it, each GetNode path whose
    /// name leads to it, C#'s and GDScript's (<see cref="GodotScenes.ScriptRenames"/>, the scripts they extend too); refused for
    /// a name Godot would not take or one beside it has, a string that can't say the name, or a file other than as it was
    /// read, which <paramref name="patience"/> asks again for. The window's and an agent's alike.</summary>
    internal async Task<RefactorOutcome> RenameNodesAsync(GodotRead read, IReadOnlyList<GodotTreeNode> nodes, string old, string name, Patience? patience = null)
    {
        GodotScenes scenes = read.Scenes;
        foreach (GodotTreeNode node in nodes)
        {
            if (scenes.RenameProblem(node, name) is { } problem)
            {
                return RefactorOutcome.Refused(problem);
            }
        }
        Dictionary<string, List<TextEdit>> edits = new(PathNames.Comparer);
        Dictionary<string, string> made = new(PathNames.Comparer);
        foreach (GodotTreeNode node in nodes)
        {
            foreach ((string res, IReadOnlyList<TextEdit> sceneEdits) in await BackgroundWork.Run(() => scenes.Renames(node, name)))
            {
                if (scenes.FileOf(res) is { } file)
                {
                    made[file] = scenes.Files[res].Text;
                    Add(file, sceneEdits);
                }
            }
        }
        List<(string Scene, GodotTreeNode Node)> copies = [.. nodes.SelectMany(n => scenes.CopiesOf(n))];
        HashSet<string> scripts = [.. copies.SelectMany(c => c.Node.Root.Below()).Select(n => n.Script).Where(GodotChecks.IsCSharp).OfType<string>()];
        foreach (string script in scripts.Order(StringComparer.Ordinal))
        {
            if (scenes.FileOf(script) is not { } code || context.CSharp is not { } csharp || await csharp.GodotAsync(code, CancellationToken.None) is not { } of
                || context.Editors.TextOf(code) is not { } text)
            {
                continue;
            }
            ServerLines lines = ServerLines.Of(text);
            List<TextEdit> paths = [];
            foreach (GodotNodeCall call in of.Found.Calls.Where(c => c.Exact && c.Class < of.Found.Classes.Count))
            {
                int at = lines.OffsetOf(new LspPosition(call.At.Line, call.At.Character));
                if (at + call.Path.Length > text.Length || text.GetText(at, call.Path.Length) != call.Path)
                {
                    return RefactorOutcome.Refused(Overtaken(code, old, patience));
                }
                foreach ((string scene, GodotTreeNode runner) in of.Found.Classes[call.Class].Scripts.SelectMany(scenes.RunsOf))
                {
                    foreach ((int from, int to, GodotTreeNode step) in GodotScenes.Resolve(runner, call.Path).Steps)
                    {
                        string written = call.Path[from..to];
                        if (nodes.Any(n => n.Same(step)) && written != "..")
                        {
                            int skip = written.StartsWith('%') ? 1 : 0;
                            paths.Add(new TextEdit(at + from + skip, to - from - skip, name.Replace("\\", "\\\\", StringComparison.Ordinal)));
                        }
                    }
                }
            }
            if (paths.Count > 0)
            {
                made[code] = text.ToString();
                Add(code, paths);
            }
        }
        List<string> gdscripts = [.. copies.SelectMany(c => c.Node.Root.Below()).Select(n => n.Script).OfType<string>().Where(GodotScriptDeclarations.IsScript).Distinct(StringComparer.Ordinal)
            .SelectMany(s => scenes.ChainOf(s).Scripts.Select(c => c.Script)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        OpenTexts open = context.Editors.Texts();
        List<(string File, string Text, IReadOnlyList<TextEdit>? Paths)> scripted = await BackgroundWork.Run(() => gdscripts.Select(s => (File: scenes.FileOf(s), Script: s))
            .Where(s => s.File is not null).Select(s => (s.File!, s.Script, Text: open.Text(s.File!))).Where(s => s.Text is not null)
            .Select(s => (s.Item1, s.Text!, scenes.ScriptRenames(s.Script, s.Text!, nodes, name))).ToList());
        foreach ((string file, string text, IReadOnlyList<TextEdit>? paths) in scripted)
        {
            if (paths is null)
            {
                return RefactorOutcome.Refused($"{old} was not renamed: a raw string of {Path.GetFileName(file)} can't say {name}");
            }
            if (paths.Count > 0)
            {
                made[file] = text;
                Add(file, paths);
            }
        }
        List<XamlFileChange> changes = [.. edits.Select(e =>
        {
            string was = made[e.Key];
            IReadOnlyList<TextEdit> these = [.. e.Value.Distinct().OrderBy(t => t.Start)];
            return new XamlFileChange(e.Key, now => now == was ? these : []);
        })];
        if (changes.Count == 0)
        {
            return RefactorOutcome.Refused($"{old} was not renamed: no scene writes it");
        }
        if (await GodotBehindAsync(made, old, patience) is { } behind)
        {
            return RefactorOutcome.Refused(behind);
        }
        if (await actions.ApplyFileChangesAsync($"Rename {old} to {name}", changes) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        return new RefactorOutcome($"Renamed {old} to {name} in {Plural.Of(changes.Count, "file")}", null,
            [.. edits.Select(e => new RefactoredFile(e.Key, "changed", [.. e.Value.Select(t => TextSnapshot.From(made[e.Key]).GetLineFromOffset(t.Start) + 1).Distinct()]))], []);

        void Add(string file, IEnumerable<TextEdit> more)
        {
            if (!edits.TryGetValue(file, out List<TextEdit>? list))
            {
                edits[file] = list = [];
            }
            list.AddRange(more);
        }
    }
}

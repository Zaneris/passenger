using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>A Godot project's text scenes and resources, as their own (<see cref="GodotFile"/>): Go to Declaration from a
/// res:// path or an <c>ExtResource</c> goes to its file, from a node path (a node's parent, a connection's ends, a NodePath)
/// to the node's section, from a connection's method to the C# method its receiving node's script declares and from its
/// signal to the <c>[Signal]</c> delegate its sending node's script declares; Rename on a node's name renames it in every
/// scene and every GetNode path of the code that names it (<see cref="CSharpAssist.RenameNodesAsync"/>); and a res:// string
/// completes from the project's files.</summary>
internal sealed class GodotAssist(AssistContext context, PopupPlacement popups, CodeCompletion completion, CodeNavigation navigation, CSharpAssist csharp) : LanguageAssist
{
    /// <summary>The characters a res:// path holds beyond a name's, which narrow its list rather than close it.</summary>
    private const string PathCharacters = "/.:-";

    /// <summary>Whether a file is a text scene or resource of a Godot project.</summary>
    private bool IsScene(OpenDocument document) => GodotFile.IsText(document.Path) && context.CSharp?.GodotOf?.Invoke(document.Path) is not null;

    /// <summary>A '/' typed in a scene's res:// string opens its list of the project's files.</summary>
    public override bool Typed(string text)
    {
        if (context.Editors.Active is not { } shown || !IsScene(shown))
        {
            return false;
        }
        if (text[^1] == '/' && ResString(shown.Text.Snapshot, context.Caret, begun: true) is not null)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    /// <summary>The project's files where a scene's string the caret is in begins res:// (those Godot keeps beside each file and
    /// in its own folders left out), replacing the whole string.</summary>
    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !IsScene(shown))
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        int caret = context.Caret;
        TextSnapshot asked = shown.Text.Snapshot;
        if (ResString(asked, caret) is not ({ } start, { } end) || context.CSharp is not { GodotScenes: { } index, GodotOf: { } godotOf } || godotOf(shown.Path) is not { } folder)
        {
            completion.Close();
            return true;
        }
        IReadOnlyList<CompletionItem> files = await BackgroundWork.Run(() => (IReadOnlyList<CompletionItem>)[.. index.FilesUnder(folder)
            .Where(f => !PathNames.Same(f, shown.Path)).Select(f => GodotScenes.ResPathOf(folder, f)).OfType<string>().Order(StringComparer.Ordinal)
            .Select(res => new CompletionItem { Label = res, Kind = CompletionItemKind.File, Detail = Path.GetExtension(res), FilterText = res, SortText = " " + res })]);
        if (cancel.IsCancellationRequested || context.Editors.Active != shown || shown.Text.Snapshot != asked || files.Count == 0)
        {
            return true;
        }
        completion.Open(new CodeCompletion.Completion(files, start, [], false)
        {
            Own = this,
            WordCharacters = PathCharacters,
            Tail = caret < end ? asked.GetText(caret, end - caret) : string.Empty,
        });
        return true;
    }

    /// <summary>A file's res:// path in place of the string's characters typed from <paramref name="start"/> and those after
    /// the caret up to its closing quote.</summary>
    public override void Commit(CompletionItem item, int start)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int caret = context.Caret;
        int end = ResString(text, caret) is (_, { } close) && close >= caret ? close : caret;
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), item.Label)], SelectionSet.Single(start + item.Label.Length));
    }

    /// <summary>The string on the caret's line it is in, where its characters start and end (its closing quote, else the
    /// line's end), when what is typed before the caret begins res:// or, unless <paramref name="begun"/>, could still; null
    /// anywhere else.</summary>
    private static (int Start, int End)? ResString(TextSnapshot text, int caret, bool begun = false)
    {
        int line = text.GetLineFromOffset(Math.Min(caret, text.Length));
        int lineStart = text.GetLineStart(line);
        string row = text.GetLineText(line);
        int column = Math.Min(caret - lineStart, row.Length);
        int quotes = row[..column].Count(c => c == '"');
        if (quotes % 2 == 0)
        {
            return null;
        }
        int open = row.LastIndexOf('"', column - 1);
        string typed = row[(open + 1)..column];
        if (!(!begun && "res://".StartsWith(typed, StringComparison.Ordinal) || typed.StartsWith("res://", StringComparison.Ordinal)))
        {
            return null;
        }
        int close = row.IndexOf('"', column);
        return (lineStart + open + 1, lineStart + (close < 0 ? row.Length : close));
    }

    /// <summary>A scene or resource of a Godot project open: its project's folder and res:// path, the scenes as they stand
    /// (the file among them as the editor has it), and the file read; null for any other file.</summary>
    private async Task<(GodotScenes Scenes, string Res, GodotFile File)?> SceneOfAsync(OpenDocument document)
    {
        if (!GodotFile.IsText(document.Path) || context.CSharp is not { GodotScenes: { } index } service || service.GodotOf?.Invoke(document.Path) is not { } folder)
        {
            return null;
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        return scenes.ResPathOf(document.Path) is { } res && scenes.Files.TryGetValue(res, out GodotFile? file) && file.Text == document.Text.Snapshot.ToString()
            ? (scenes, res, file) : null;
    }

    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Editors.Active is not { } shown || !GodotFile.IsText(shown.Path))
        {
            return false;
        }
        Declared? declared = await DeclaredAsync(shown, offset);
        if (declared is null)
        {
            context.Say(Refusals.NoDeclaration);
        }
        else if (context.Editors.Active == shown)
        {
            navigation.Go(declared, offset);
        }
        return true;
    }

    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (await SceneOfAsync(document) is not ({ } scenes, { } res, { } file))
        {
            return null;
        }
        foreach (GodotSection section in file.Sections.Where(s => s.Start <= offset))
        {
            foreach (GodotEntry entry in section.Attributes.Concat(section.Properties))
            {
                if (entry.Value.Start <= offset && offset <= entry.Value.End && await EntryDeclaredAsync(scenes, res, file, section, entry) is { } declared)
                {
                    return declared;
                }
            }
        }
        return null;
    }

    /// <summary>Where an entry of a section leads: a res:// path's or ExtResource's file, a node path's node, a connection's
    /// method or signal in the code.</summary>
    private async Task<Declared?> EntryDeclaredAsync(GodotScenes scenes, string res, GodotFile file, GodotSection section, GodotEntry entry)
    {
        GodotValue value = entry.Value;
        if (value.Text is { } text && text.StartsWith("res://", StringComparison.Ordinal))
        {
            return FileDeclared(scenes, text);
        }
        if (value is { Call: "ExtResource", Text: { } id } && file.External(id)?.Path is { } external)
        {
            return FileDeclared(scenes, external);
        }
        if (scenes.Tree(res) is not { } root)
        {
            return null;
        }
        if (section.Tag == "connection" && entry.Key is "method" or "signal" && file.Connections.FirstOrDefault(c => c.Section == section) is { } connection)
        {
            return await ConnectionDeclaredAsync(scenes, res, connection, entry.Key == "method");
        }
        GodotTreeNode? node = (section.Tag, entry.Key) switch
        {
            ("node", "parent") or ("connection", "from" or "to") or ("editable", "path") => value.Text is { } path ? GodotScenes.Resolve(root, path).Found : null,
            ("node", _) when value is { Call: "NodePath", Text: { } path } && GodotScenes.Resolve(root, new Passenger.Workspace.GodotNode(section).PathInScene).Found is { } holder
                => GodotScenes.Resolve(holder, path).Found,
            _ => null,
        };
        return node is null ? null : NodeDeclared(scenes, node);
    }

    private Declared FileDeclared(GodotScenes scenes, string res)
    {
        string named = res.Split("::")[0];
        string? path = scenes.FileOf(named);
        return new Declared(path is not null && File.Exists(path) ? [new LspLocation(DocumentUris.FromPath(path), new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)))] : [],
            $"No file {named} is in the Godot project");
    }

    private Declared NodeDeclared(GodotScenes scenes, GodotTreeNode node)
    {
        (string scene, Passenger.Workspace.GodotNode declared) = node.Declared;
        return scenes.FileOf(scene) is { } path
            ? new Declared(Declared.Served([(path, new TextPosition(declared.Section.Line, 0))], ServerPlaces.Texts(context.Editors)), Refusals.NoDeclaration)
            : new Declared([], Refusals.NoDeclaration);
    }

    /// <summary>A connection's method in the code of its receiving node's script, or its signal's <c>[Signal]</c> delegate in
    /// its sending node's: asked of the extension for that script's file as its scenes are read.</summary>
    private async Task<Declared?> ConnectionDeclaredAsync(GodotScenes scenes, string res, GodotConnection connection, bool method)
    {
        GodotChecks.Connected connected = GodotChecks.Connections(scenes, res).First(c => c.Connection.Section == connection.Section);
        GodotTreeNode? node = method ? connected.To : connected.From;
        string what = method ? $"No method {connection.Method}" : $"No signal {connection.Signal}";
        if (node?.Script is not { } script || !GodotChecks.IsCSharp(script) || scenes.FileOf(script) is not { } code || context.CSharp is not { } service)
        {
            return new Declared([], $"{what} is declared in C#: {(node is null ? "the scene has no node there" : $"{node.Name} has no C# script")}");
        }
        if (await service.GodotAsync(code, CancellationToken.None) is not { } read)
        {
            return new Declared([], $"{what}: the C# language server cannot read {Path.GetFileName(code)}");
        }
        GodotMemberAsked asked = method ? GodotChecks.MethodAsked(connected) : GodotChecks.SignalAsked(connected);
        IReadOnlyList<GodotMemberAsked> list = method ? read.Asked.Methods : read.Asked.Signals;
        IReadOnlyList<GodotSignature?> found = method ? read.Found.Methods : read.Found.Signals;
        int index = list.ToList().IndexOf(asked);
        if (index < 0 || index >= found.Count || found[index] is not { Path: { } path, At: { } at })
        {
            return new Declared([], $"{what} is declared in {Path.GetFileName(code)}");
        }
        return new Declared([new LspLocation(DocumentUris.FromPath(path), new LspRange(new LspPosition(at.Line, at.Character), new LspPosition(at.Line, at.Character)))],
            Refusals.NoDeclaration);
    }

    /// <summary>Rename on a node's name in a scene: its box, then the node renamed everywhere that names it.</summary>
    public override bool Rename(int caret)
    {
        if (context.Editors.Active is not { } shown || !GodotFile.IsText(shown.Path))
        {
            return false;
        }
        IdeErrors.Observe(popups.Carried("Rename", () => ShowRenameAsync(shown, caret), closeModal: false));
        return true;
    }

    private async Task ShowRenameAsync(OpenDocument document, int caret)
    {
        int version = document.Text.Version;
        if (await NodeAtAsync(document, caret) is not ({ } read, { } node))
        {
            context.Say("Rename in a Godot scene renames a node's name");
            return;
        }
        if (context.Editors.Active != document || document.Text.Version != version)
        {
            return;
        }
        string old = node.Name;
        RenameBox box = new(old);
        popups.ShowModal(box, caret, async name =>
        {
            if (name is null || name == old)
            {
                return;
            }
            RefactorOutcome made = await csharp.RenameNodesAsync(read, [node], old, name);
            context.Say(made.Problem ?? made.Said);
        });
    }

    /// <summary>The node whose name a scene's offset is on, in its own scene's tree; null for any other place.</summary>
    internal async Task<(GodotRead Read, GodotTreeNode Node)?> NodeAtAsync(OpenDocument document, int offset)
    {
        if (await SceneOfAsync(document) is not ({ } scenes, { } res, { } file) || scenes.Tree(res) is not { } root)
        {
            return null;
        }
        foreach (Passenger.Workspace.GodotNode section in file.Nodes)
        {
            if (section.Section.Attribute("name")?.Value is { } name && name.Start <= offset && offset <= name.End
                && root.Below().FirstOrDefault(n => n.Written.Any(w => w.Scene == res && w.Node.Section.Start == section.Section.Start)) is { } node)
            {
                return (new GodotRead(scenes.Folder, scenes, new GodotCodeRequest(document.Path, [], [], [], []), GodotCodeFound.None), node);
            }
        }
        return null;
    }
}

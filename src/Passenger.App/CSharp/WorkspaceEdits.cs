using System.Text.Json;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Held = System.Func<System.Collections.Generic.IReadOnlyList<string>,
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Passenger.Roslyn.Extension.TextHeld>>>;

namespace Passenger.App.CSharp;

/// <summary>Applies a workspace edit (a rename, a fix) across files: an open file is edited through its document, one undo
/// step per file and through the editor when it is the one showing; a file not open is edited on disk, keeping its
/// encoding and line endings; files the edit creates, renames or deletes are made so, an open one's tab going with it. An
/// open file's edits are carried through what was typed since the request; one whose edited text changed meanwhile, or
/// that the server answered for another version, is left alone and named in <c>dropped</c>.</summary>
public static class WorkspaceEdits
{
    /// <summary>Every open document followed from now, for a request about to be sent; disposed once its answer is applied.</summary>
    public static Asked Track(EditorArea editors) => new(editors.Documents.ToDictionary(d => d, d => new ChangeTracker(d.Text)));

    /// <summary>Each file's text as a server's answer was made for it: an open file's as it was when the request was sent
    /// (<paramref name="asked"/>), else as the editor or the disk has it now (<see cref="ServerPlaces.Texts"/>).</summary>
    public static Func<string, TextSnapshot?> Answered(EditorArea editors, Asked? asked)
    {
        Func<string, TextSnapshot?> now = ServerPlaces.Texts(editors);
        return path => asked?.Trackers.FirstOrDefault(t => t.Key.Path == path).Value?.Snapshot ?? now(path);
    }

    /// <summary>How long after a file changes on disk the language server may still hold it as it was: it reads a file
    /// not open again only once the folder's watcher has told it of the change.</summary>
    public static readonly TimeSpan Catching = TimeSpan.FromSeconds(2);

    /// <summary>Why an edit the language server worked out must not be applied to the files not open it changes, but those
    /// it <paramref name="made"/> empty for it: one written since the request was sent (<paramref name="since"/>), or a
    /// moment before, which the server may not have read again, or one whose text the server holds is not the disk's, where
    /// its lines and columns would land elsewhere; null when each is as the server read it. A command's
    /// <paramref name="patience"/> works the edit out again once the server has read those written before the request.</summary>
    public static Task<string?> StaleAsync(EditorArea editors, LspDocument? server, JsonElement edit, DateTime? since, Patience? patience = null,
        IReadOnlyCollection<string>? made = null) =>
        StaleAsync(editors, server is null ? null : paths => server.HeldTextsAsync(paths), edit, since, patience, made);

    /// <summary>As <see cref="StaleAsync(EditorArea, LspDocument?, JsonElement, DateTime?, Patience?, IReadOnlyCollection{string}?)"/>,
    /// the server's texts asked of its session (<paramref name="held"/>, null for a server with none to tell).</summary>
    public static async Task<string?> StaleAsync(EditorArea editors, Held? held, JsonElement edit, DateTime? since, Patience? patience = null,
        IReadOnlyCollection<string>? made = null)
    {
        // With the files written for the command to wait for, which the server must have read as they are, in the edit or not.
        List<string> closed = [.. Shapes.WorkspaceEdit(edit).Where(c => c.Operation is null).Select(c => DocumentUris.ToPath(c.Uri))
            .Concat(patience?.Awaited ?? []).Distinct()
            .Where(p => editors.Documents.All(d => d.Path != p) && File.Exists(p) && made?.Contains(p) != true)];
        // One the command saw the server hold as the disk has it, unwritten since, has had its moment.
        List<(string Path, DateTime At)> written = [.. closed.Select(p => (Path: p, At: File.GetLastWriteTimeUtc(p)))
            .Where(f => since is { } sent && f.At > sent - Catching && patience?.Trusts(f.Path) != true)];
        // Written once the server was asked, its answer may be from the text before, whatever it holds by now.
        if (written.FirstOrDefault(f => patience is null || f.At > since) is { Path: { } meanwhile })
        {
            return ChangedMeanwhile(meanwhile);
        }
        if (written.Count > 0)
        {
            string name = Path.GetFileName(written[0].Path);
            return patience!.Behind([.. written.Select(f => f.Path)], held, $"{name} changed on disk a moment before the language server was asked",
                $"{name} changed on disk a moment before the language server was asked, which had not read it after {Patience.Limit.TotalSeconds:0} s, "
                + "so the places it gave there may be where they were before: nothing was changed");
        }
        if (closed.Count == 0 || held is null)
        {
            return null;
        }
        IReadOnlyList<Roslyn.Extension.TextHeld> texts;
        try
        {
            texts = await held(closed);
        }
        catch (LanguageServerFailed)
        {
            // A server without Passenger's extension: the files' times are all there is to go by.
            return null;
        }
        // Each file read off the window's thread, as an edit's reach is files of any size.
        List<(string Path, string Hash, bool? OnDisk)> read = await BackgroundWork.Run(() => texts.Where(t => t.Hash is not null).Select(t => (t.Path, t.Hash!, OnDisk(t.Path, t.Hash!))).ToList());
        // One the call trusts is also the text it was seen as: a write within the disk's clock tick keeps its time.
        List<string> unread = [.. read.Where(t => t.OnDisk == false || patience?.AsSeen(t.Path, t.Hash) == false).Select(t => t.Path)];
        if (unread.Count > 0)
        {
            string name = Path.GetFileName(unread[0]);
            string said = $"{name} on disk is not the text the language server read, so the places it gave there would be the wrong ones: "
                + "nothing was changed; try again once the server has read it";
            return patience?.Behind(unread, held, said, $"{name} on disk was still not the text the language server read after "
                + $"{Patience.Limit.TotalSeconds:0} s, so the places it gave there would be the wrong ones: nothing was changed") ?? said;
        }
        return null;
    }

    /// <summary>Why an edit is not made in a file not open written once its server was asked.</summary>
    private static string ChangedMeanwhile(string path) =>
        $"{Path.GetFileName(path)} changed on disk as the language server worked this out, so the places it gave there "
        + "may be where they were before: nothing was changed; try again in a moment";

    /// <summary>Why the files not open an edit changes must not take it now, asked before anything changes: one a write of
    /// is asked for or under way (an Undo's), or one written since its server was asked (<paramref name="asked"/>), where the
    /// places it gave may be where they were before; null when none is. One the command made or wrote itself is its own.</summary>
    private static string? Overtaken(EditorArea editors, IReadOnlyList<FileEdits> changes, CommandHistory.Recording recording, Asked? asked)
    {
        foreach (string path in changes.Where(c => c.Operation is null).Select(c => DocumentUris.ToPath(c.Uri)).Distinct())
        {
            if (editors.Find(path) is not null || recording.Owns(path))
            {
                continue;
            }
            if (editors.Commands.IsWriting(path))
            {
                return $"{Path.GetFileName(path)} is being written ({editors.Commands.WritingNow}), so the places the language server gave there "
                    + "may be where they were before: nothing was changed; try again once it is written";
            }
            if (asked is { } sent && File.GetLastWriteTimeUtc(path) > sent.Since)
            {
                return ChangedMeanwhile(path);
            }
        }
        return null;
    }

    /// <summary>Whether a file's text on disk is the one of this fingerprint, read as Passenger reads it or as the server
    /// may: as UTF-8 whatever the bytes (Roslyn's fallback off Windows). Null when it cannot be read, which applying says.</summary>
    internal static bool? OnDisk(string path, string hash)
    {
        byte[] bytes;
        try
        {
            bytes = FileText.Read(path);
        }
        catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return FileText.FromBytes(bytes, null, () => EditorConfig.For(path).Charset) is not { } disk || Roslyn.Extension.TextHash.Of(disk.Text) == hash
            || Roslyn.Extension.TextHash.Of(new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('﻿')) == hash;
    }

    /// <summary>Writes a command's files not open and waits within its recording for those still landing
    /// (<see cref="CommandHistory.Recording.LandedAsync"/>: a file never written is left out of its Undo),
    /// <paramref name="waiting"/> told meanwhile which; one the disk refuses, or that never lands, throws
    /// <see cref="WorkspaceEditFailed"/>. Whether any was landing.</summary>
    public static async Task<bool> LandedAsync(CommandHistory.Recording recording, Action<string>? waiting)
    {
        bool landing = false;
        if (await recording.LandedAsync(said =>
            {
                landing = true;
                waiting?.Invoke(said);
            }) is [(string path, Exception refused), ..])
        {
            throw new WorkspaceEditFailed(Unwritten(path, refused));
        }
        return landing;
    }

    /// <summary>Throws <see cref="WorkspaceEditFailed"/> naming the file a command's writes stopped at, when the disk refused
    /// one (<see cref="CommandHistory.Recording.WriteAsync"/>).</summary>
    public static void Written((string Path, Exception Refused)? refused)
    {
        if (refused is (string path, Exception error))
        {
            throw new WorkspaceEditFailed(Unwritten(path, error));
        }
    }

    /// <summary>What a change says of a file the disk would not take.</summary>
    internal static string Unwritten(string path, Exception error) => $"{Path.GetFileName(path)} could not be written: {error.Message}";

    /// <summary>The edit applied within a command its caller records, which writes the files not open off the window's thread
    /// and waits for them (<see cref="LandedAsync(CommandHistory.Recording, Action{string}?)"/>): the open files edited and
    /// the operations made now, each file not open queued (<see cref="CommandHistory.Recording.Change"/>), one step of Undo.
    /// The number of files changed. Throws <see cref="WorkspaceEditFailed"/> when its operations cannot all be made or a file
    /// not open was overtaken (<see cref="Overtaken"/>), before anything changed, or when the disk refuses an operation.</summary>
    public static int ApplyAsCommand(EditorArea editors, JsonElement edit, CommandHistory.Recording recording, Asked? asked = null, List<string>? dropped = null)
    {
        IReadOnlyList<FileEdits> changes = Shapes.WorkspaceEdit(edit);
        if (Overtaken(editors, changes, recording, asked) is { } overtaken)
        {
            throw new WorkspaceEditFailed(overtaken);
        }
        recording.Naming(editors.Recording?.Invoke());
        if (Refusal(editors, changes) is { } refused)
        {
            throw new WorkspaceEditFailed(refused);
        }
        int files = 0;
        foreach (FileEdits change in changes)
        {
            if (change.Operation is { } operation)
            {
                try
                {
                    Operate(editors, Resource.Read(operation), recording);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new WorkspaceEditFailed(error.Message);
                }
                files++;
                continue;
            }
            string path = DocumentUris.ToPath(change.Uri);
            if (editors.Find(path) is { } open)
            {
                recording.Watch(path);
                if (Placed(open, change, asked?.Trackers.GetValueOrDefault(open)) is not { } edits)
                {
                    dropped?.Add(open.Name);
                    continue;
                }
                ApplyTo(editors, open, edits);
            }
            else
            {
                IReadOnlyList<LspTextEdit> edits = change.Edits;
                recording.Change(path, text => Edited(path, text, edits));
            }
            files++;
        }
        return files;
    }

    /// <summary>A file not open's text with a server's edits made in it, in its own line breaks and indentation.</summary>
    private static string Edited(string path, string text, IReadOnlyList<LspTextEdit> edits)
    {
        TextSnapshot snapshot = TextSnapshot.From(text);
        return snapshot.Apply([.. Convert(snapshot, edits, TextDocument.DetectLineEnding(text), FileIndent.Of(path, text)).OrderBy(e => e.Start)]).ToString();
    }

    /// <summary>Edits in an open file's text as it is now: through the active editor when it is the file shown there, so
    /// its carets follow as typing's would, else to the document, one undo step.</summary>
    public static void ApplyTo(EditorArea editors, OpenDocument open, IReadOnlyList<TextEdit> edits)
    {
        if (open == editors.Active)
        {
            editors.CodeEditor.ApplyEdits(edits);
            return;
        }
        SelectionSet carets = open.Selections;
        open.Text.BreakUndoGroup();
        open.Text.Edit(edits, carets, TextEditing.MapThrough(carets, edits));
    }

    /// <summary>An open file's edits in its text as it is now: as answered when its text has not changed since, carried
    /// through what changed when it was asked for the version the request was, null when they no longer fit.</summary>
    private static List<TextEdit>? Placed(OpenDocument open, FileEdits change, ChangeTracker? asked)
    {
        string eol = open.Text.LineEnding;
        IndentOptions indent = open.Indentation.Options;
        if (change.Version is { } version && version == open.Text.Version)
        {
            return Convert(open.Text.Snapshot, change.Edits, eol, indent);
        }
        if (asked is null || (change.Version is { } answered && answered != asked.Version))
        {
            return change.Version is null ? Convert(open.Text.Snapshot, change.Edits, eol, indent) : null;
        }
        return Carried(asked, change.Edits, indent) is { } mapped ? [.. mapped] : null;
    }

    /// <summary>A server's edits answered for the text <paramref name="asked"/> began with, in its document's line breaks
    /// and <paramref name="indent"/>, carried onto the text as it is now; null when the code they change was edited meanwhile.</summary>
    public static IReadOnlyList<TextEdit>? Carried(ChangeTracker asked, IReadOnlyList<LspTextEdit> edits, IndentOptions indent) =>
        asked.Map(Convert(asked.Snapshot, edits, asked.Document.LineEnding, indent));

    /// <summary>LSP edits as the editor's, in one snapshot's offsets (its lines as the server counts them), sorted, their
    /// line breaks the file's own <paramref name="eol"/> (<see cref="LineEndings.Fit"/>): Roslyn writes the system's, CRLF
    /// on Windows, into any file; and the indentation they write in the file's own characters
    /// (<see cref="Indentation.Fit(TextSnapshot, IReadOnlyList{TextEdit}, IndentOptions)"/>), which Roslyn may not know
    /// when no .editorconfig gives it. An edit that comes to nothing once fitted is left out.</summary>
    public static List<TextEdit> Convert(TextSnapshot snapshot, IReadOnlyList<LspTextEdit> edits, string eol, IndentOptions indent)
    {
        ServerLines lines = ServerLines.Of(snapshot);
        return Indentation.Fit(snapshot, LineEndings.Fit(snapshot, edits.Select(e =>
        {
            int start = lines.OffsetOf(e.Range.Start);
            int end = lines.OffsetOf(e.Range.End);
            return new TextEdit(start, Math.Max(0, end - start), e.NewText);
        }), eol), indent);
    }

    /// <summary>Why an edit's file operations cannot all be made, found before any is: each is played over what the
    /// ones before it leave. The open files a rename moves are saved now, so one that cannot be stops it here too.</summary>
    private static string? Refusal(EditorArea editors, IReadOnlyList<FileEdits> changes)
    {
        // A read-only file would be refused halfway through, the files before it already changed.
        foreach (FileEdits change in changes.Where(c => c.Operation is null))
        {
            string path = DocumentUris.ToPath(change.Uri);
            if (editors.Find(path) is { } open ? open.IsReadOnly : FileText.IsReadOnly(path))
            {
                return Refusals.ReadOnly(Path.GetFileName(path));
            }
        }
        Dictionary<string, bool> there = [];
        bool Exists(string path) => there.TryGetValue(path, out bool known) ? known : File.Exists(path) || Directory.Exists(path);
        foreach (Resource resource in changes.Select(c => c.Operation).OfType<JsonElement>().Select(Resource.Read))
        {
            bool overwrite = resource.Option("overwrite");
            bool ignored = !overwrite && resource.Option("ignoreIfExists");
            // A new name differing only in case is the same file on macOS and Windows, not one already there; on Linux it is another.
            bool taken = resource.To is { } to && Exists(to) && !PathNames.Same(to, resource.Path);
            switch (resource.Kind)
            {
                case "create" when overwrite && FileText.IsReadOnly(resource.Path):
                    return $"{Path.GetFileName(resource.Path)} is read-only";
                case "create" when Exists(resource.Path) && !overwrite && !ignored:
                case "rename" when taken && !ignored && (!overwrite || Directory.Exists(resource.To)):
                    return $"{Path.GetFileName(resource.To ?? resource.Path)} is already there";
                case "rename" when !Exists(resource.Path):
                case "delete" when !Exists(resource.Path) && !resource.Option("ignoreIfNotExists"):
                    return $"{Path.GetFileName(resource.Path)} is not there to {resource.Kind}";
                case "create":
                    there[resource.Path] = true;
                    break;
                case "rename" when !(taken && ignored):
                    there[resource.Path] = false;
                    there[resource.To!] = true;
                    foreach (OpenDocument document in editors.Documents.Where(d => d.IsModified && PathNames.Within(d.Path, resource.Path)))
                    {
                        if (!editors.Save(document))
                        {
                            return $"{document.Name} could not be saved before it moved";
                        }
                    }
                    break;
                case "delete":
                    there[resource.Path] = false;
                    break;
            }
        }
        return null;
    }

    /// <summary>Makes a file operation, telling the recording of a file not open it makes, or empties to be written over.</summary>
    private static void Operate(EditorArea editors, Resource resource, CommandHistory.Recording recording)
    {
        bool exists = File.Exists(resource.Path) || Directory.Exists(resource.Path);
        bool closed = editors.Find(resource.Path) is null;
        switch (resource.Kind)
        {
            case "create" when exists && closed && File.Exists(resource.Path) && resource.Option("overwrite"):
                // Emptied in order with the changes before and after it, its text before kept for Undo.
                recording.Change(resource.Path, _ => string.Empty);
                break;
            case "create" when !exists || resource.Option("overwrite"):
            {
                if (closed)
                {
                    recording.Made(resource.Path);
                }
                else
                {
                    recording.Watch(resource.Path);
                }
                // Written as a save writes, in the charset its .editorconfig names.
                FileEncoding charset = EditorConfig.For(resource.Path).Charset ?? FileEncoding.Utf8;
                FileText.Save(resource.Path, string.Empty, charset.Encoding, charset.Bom);
                editors.Find(resource.Path)?.Reload(overUnsaved: true);
                break;
            }
            case "rename" when resource.To is { } to:
                if ((File.Exists(to) || Directory.Exists(to)) && !PathNames.Same(to, resource.Path))
                {
                    // Ignored, as its options ask: the refusal stopped every other rename onto something there.
                    if (!resource.Option("overwrite") || Directory.Exists(to))
                    {
                        return;
                    }
                    Delete(editors, to, recursive: false);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (recording.Move(resource.Path, to) is { } failed)
                {
                    throw new WorkspaceEditFailed(failed);
                }
                break;
            case "delete" when exists:
                Delete(editors, resource.Path, resource.Option("recursive"));
                break;
        }
    }

    /// <summary>Deletes a file or folder, closing the open files that went with it.</summary>
    private static void Delete(EditorArea editors, string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
        else
        {
            File.Delete(path);
        }
        foreach (OpenDocument document in editors.Documents.Where(d => PathNames.Within(d.Path, path)).ToList())
        {
            editors.CloseDeleted(document);
        }
    }

    /// <summary>What an edit does to each file, as a refactoring's answer names them: made, moved or deleted, else changed,
    /// with the lines (from one) its edits start on, as the editor counts those of the text it was made for, which
    /// <paramref name="texts"/> gives (<see cref="Answered"/>), so found before it is applied. A file made and then filled is
    /// one made.</summary>
    public static IReadOnlyList<RefactoredFile> Files(JsonElement edit, Func<string, TextSnapshot?> texts)
    {
        List<RefactoredFile> files = [];
        HashSet<string> made = new(StringComparer.Ordinal);
        foreach (FileEdits change in Shapes.WorkspaceEdit(edit))
        {
            if (change.Operation is { } operation)
            {
                Resource resource = Resource.Read(operation);
                string kind = resource.Kind switch { "create" => "created", "rename" => "moved", _ => "deleted" };
                if (kind == "created")
                {
                    made.Add(resource.Path);
                }
                files.Add(new RefactoredFile(resource.Path, kind, [], resource.To));
                continue;
            }
            string path = DocumentUris.ToPath(change.Uri);
            if (made.Contains(path) || change.Edits.Count == 0)
            {
                continue;
            }
            TextSnapshot? text = texts(path);
            IReadOnlyList<int> lines = [.. change.Edits.Select(e => ServerPlaces.PositionOf(text, e.Range.Start).Line + 1).Distinct().Order()];
            if (files.FindIndex(f => f.Path == path && f.Change == "changed") is int at and >= 0)
            {
                files[at] = files[at] with { Lines = [.. files[at].Lines.Concat(lines).Distinct().Order()] };
            }
            else
            {
                files.Add(new RefactoredFile(path, "changed", lines));
            }
        }
        return files;
    }

    /// <summary>What an edit does to each file (<see cref="Files"/>), as the open files were as it was asked
    /// (<see cref="Answered"/>), the files not open read off the window's thread.</summary>
    public static async Task<IReadOnlyList<RefactoredFile>> FilesAsync(EditorArea editors, JsonElement edit, Asked? asked)
    {
        Func<string, TextSnapshot?> answered = Answered(editors, asked);
        Dictionary<string, TextSnapshot?> open = new(PathNames.Comparer);
        foreach (string path in Paths(edit).Where(p => editors.Find(p) is not null || asked?.Trackers.Keys.Any(d => d.Path == p) == true))
        {
            open.TryAdd(path, answered(path));
        }
        return await BackgroundWork.Run(() => Files(edit, path => open.TryGetValue(path, out TextSnapshot? text) ? text : ServerPlaces.Read(path)));
    }

    /// <summary>Every file an edit changes, makes, moves (from and to) or deletes.</summary>
    public static IReadOnlyList<string> Paths(JsonElement edit)
    {
        List<string> paths = [];
        foreach (FileEdits change in Shapes.WorkspaceEdit(edit))
        {
            if (change.Operation is not { } operation)
            {
                paths.Add(DocumentUris.ToPath(change.Uri));
                continue;
            }
            Resource resource = Resource.Read(operation);
            paths.Add(resource.Path);
            if (resource.To is { } to)
            {
                paths.Add(to);
            }
        }
        return paths;
    }

    /// <summary>One of an edit's file operations (create, rename or delete): its paths and its options.</summary>
    private sealed record Resource(string Kind, string Path, string? To, JsonElement Options)
    {
        public static Resource Read(JsonElement operation)
        {
            string kind = operation.GetProperty("kind").GetString() ?? string.Empty;
            string path = DocumentUris.ToPath(operation.GetProperty(kind == "rename" ? "oldUri" : "uri").GetString()!);
            string? to = kind == "rename" ? DocumentUris.ToPath(operation.GetProperty("newUri").GetString()!) : null;
            return new Resource(kind, path, to, operation.TryGetProperty("options", out JsonElement options) ? options : default);
        }

        public bool Option(string name) =>
            Options.ValueKind == JsonValueKind.Object && Options.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }
}

/// <summary>What a refactoring came to, for its dialog, the status bar and an agent alike: what to say of it, why it was
/// not made (then nothing changed), each file it changed, the open files it left alone as edited meanwhile, by name, and
/// the uses it could not change, each with why.</summary>
public sealed record RefactorOutcome(string Said, string? Problem, IReadOnlyList<RefactoredFile> Files, IReadOnlyList<string> Left)
{
    public IReadOnlyList<UnchangedUse> Unchanged { get; init; } = [];

    /// <summary>Where the server that made it asked a name it wrote be renamed at once (TypeScript's Extract to function's
    /// new function), in the text as it left it; null where it asked none.</summary>
    public LspLocation? RenameAt { get; init; }

    public static RefactorOutcome Refused(string problem) => new(problem, problem, [], []);
}

/// <summary>A use a refactoring left as it was, which the change leaves wrong: its file, its line and column from one as
/// the editor counts the text as it was, and why.</summary>
public sealed record UnchangedUse(string Path, int Line, int Column, string Why);

/// <summary>A file a refactoring changed: created, changed, moved (to <paramref name="To"/>) or deleted, with the lines its
/// edits start on, from one, as the editor counts the text as it was.</summary>
public sealed record RefactoredFile(string Path, string Change, IReadOnlyList<int> Lines, string? To = null);

/// <summary>A workspace edit that could not be made, and why: a file it renames onto is there already, one it renames
/// or deletes is not, an open file it moves could not be saved first, or the disk refused. An MCP tool says it as its
/// error.</summary>
public sealed class WorkspaceEditFailed(string message) : InvalidOperationException(message);

/// <summary>The open documents followed from a request's sending until its answer is applied.</summary>
public sealed class Asked(Dictionary<OpenDocument, ChangeTracker> trackers) : IDisposable
{
    public IReadOnlyDictionary<OpenDocument, ChangeTracker> Trackers => trackers;

    /// <summary>When the request was about to be sent: a file not open written after it, or a moment before, may be one
    /// the server answered for as it was (<see cref="WorkspaceEdits.StaleAsync"/>).</summary>
    public DateTime Since { get; } = DateTime.UtcNow;

    public void Dispose()
    {
        foreach (ChangeTracker tracker in trackers.Values)
        {
            tracker.Dispose();
        }
    }
}

/// <summary>A command that applies a language server's edit across files, a person's or an agent's, made a moment after
/// a file not open was written (by Undo, a refactoring, an agent, a checkout): refused by
/// <see cref="WorkspaceEdits.StaleAsync"/> for files the server may not have read as they are on disk, it is worked out
/// again once the server holds each as the disk has it, for up to <see cref="Limit"/> in all, saying what it waits for. A
/// file seen so is trusted until it is written again; one written once the server was asked is refused at once.</summary>
public sealed class Patience
{
    /// <summary>How long a command waits for the server, in all, before it refuses.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly DateTime until = DateTime.UtcNow + Limit;
    private readonly Dictionary<string, (FileStamp Stamp, string Hash)> seen = new(StringComparer.Ordinal);
    private readonly List<string> behind = [];
    private readonly HashSet<string> awaited = new(StringComparer.Ordinal);
    private readonly Action<string?> waiting;
    private Held? held;

    private Patience(Action<string?> waiting) => this.waiting = waiting;

    /// <summary>The attempt's answer, the attempt made again each time it threw for files the server had yet to read, once
    /// it has read them, <paramref name="waiting"/> told what it waits for meanwhile and null once it waits no more. Each
    /// attempt waits first for the writes of files not open asked for before it (<paramref name="writes"/>' Undo, another
    /// change), so its server is asked of the files as they leave them.</summary>
    public static Task<T> RunAsync<T>(CommandHistory writes, Func<Patience, Task<T>> attempt, Action<string?> waiting, CancellationToken cancel) =>
        RunAsync(writes, attempt, _ => false, waiting, cancel);

    /// <summary>As <see cref="RunAsync{T}(CommandHistory, Func{Patience, Task{T}}, Action{string?}, CancellationToken)"/>, the
    /// attempt also made again when its answer is one it <paramref name="refused"/> for such files. An attempt changes nothing
    /// before its edit is found fit, so another is as the first; its refusal once the time is spent says what it waited for.</summary>
    public static async Task<T> RunAsync<T>(CommandHistory writes, Func<Patience, Task<T>> attempt, Func<T, bool> refused, Action<string?> waiting,
        CancellationToken cancel)
    {
        Patience patience = new(waiting);
        try
        {
            while (true)
            {
                if (writes.WritingNow is { } other)
                {
                    patience.awaited.UnionWith(writes.PathsWriting());
                    waiting($"Waiting for {other} to write its files");
                    await writes.WrittenAsync().WaitAsync(cancel);
                    waiting(null);
                }
                patience.behind.Clear();
                try
                {
                    T answer = await attempt(patience);
                    if (patience.behind.Count == 0 || !refused(answer))
                    {
                        return answer;
                    }
                }
                catch (InvalidOperationException) when (patience.behind.Count > 0)
                {
                    // Refused for the files behind, which the server reads in a moment.
                }
                await patience.CatchUpAsync(cancel);
            }
        }
        finally
        {
            waiting(null);
        }
    }

    /// <summary>The files not open written for the command to wait for before it was asked (an Undo's), which its server is to
    /// have read as they are on disk whether its edit changes them or not.</summary>
    internal IReadOnlyCollection<string> Awaited => awaited;

    /// <summary>Whether the server was seen holding a file as the disk has it, unwritten since: what it works out now is
    /// from that text, so the moment after the write is past.</summary>
    internal bool Trusts(string path) => seen.TryGetValue(path, out (FileStamp Stamp, string Hash) was) && was.Stamp == FileStamp.Of(path);

    /// <summary>Whether the server holds a file trusted as the text it was seen as; one not trusted is judged by its time.</summary>
    internal bool AsSeen(string path, string hash) => !Trusts(path) || seen[path].Hash == hash;

    /// <summary>The words an attempt is refused in for files the server may not have read as they are: while time is left
    /// <paramref name="said"/>, the files waited for before the next attempt; after it <paramref name="kept"/>.</summary>
    internal string Behind(IReadOnlyList<string> paths, Held? server, string said, string kept)
    {
        if (DateTime.UtcNow >= until)
        {
            return kept;
        }
        behind.AddRange(paths);
        held = server;
        return said;
    }

    /// <summary>Waits until the server holds each file behind as the disk has it, unwritten while it was read; where the
    /// server cannot say (no Passenger extension, no document of that path), until the moment after its write has passed.</summary>
    private async Task CatchUpAsync(CancellationToken cancel)
    {
        HashSet<string> untold = new(StringComparer.Ordinal);
        if (DateTime.UtcNow < until)
        {
            waiting($"Waiting for the language server to read {Path.GetFileName(behind[0])} as it is on disk");
        }
        while (DateTime.UtcNow < until)
        {
            List<string> asking = [.. behind.Where(p => !Trusts(p) && !untold.Contains(p) && File.Exists(p))];
            if (held is not null && asking.Count > 0)
            {
                try
                {
                    IReadOnlyList<Roslyn.Extension.TextHeld> texts = await held(asking);
                    untold.UnionWith(texts.Where(t => t.Hash is null).Select(t => t.Path));
                    // Each file read off the window's thread, as files of any size may be waited for.
                    foreach ((string path, FileStamp stamp, string hash) in await BackgroundWork.Run(() => HeldAsOnDisk(texts)))
                    {
                        seen[path] = (stamp, hash);
                    }
                }
                catch (LanguageServerFailed)
                {
                    held = null;
                }
            }
            DateTime past = DateTime.UtcNow - WorkspaceEdits.Catching;
            if (behind.All(p => !File.Exists(p) || Trusts(p) || ((held is null || untold.Contains(p)) && File.GetLastWriteTimeUtc(p) <= past)))
            {
                return;
            }
            await Task.Delay(100, cancel);
        }
    }

    /// <summary>The files the server holds as the disk has them, unwritten while they were read, each with its stamp and the
    /// fingerprint of the text held.</summary>
    private static List<(string Path, FileStamp Stamp, string Hash)> HeldAsOnDisk(IReadOnlyList<Roslyn.Extension.TextHeld> texts)
    {
        List<(string Path, FileStamp Stamp, string Hash)> unwritten = [];
        foreach (Roslyn.Extension.TextHeld text in texts)
        {
            FileStamp stamp = FileStamp.Of(text.Path);
            if (text.Hash is { } hash && WorkspaceEdits.OnDisk(text.Path, hash) == true && FileStamp.Of(text.Path) == stamp)
            {
                unwritten.Add((text.Path, stamp, hash));
            }
        }
        return unwritten;
    }

    /// <summary>When a file was last written and how long it was then.</summary>
    private readonly record struct FileStamp(DateTime Written, long Length)
    {
        public static FileStamp Of(string path)
        {
            FileInfo file = new(path);
            return file.Exists ? new FileStamp(file.LastWriteTimeUtc, file.Length) : default;
        }
    }
}

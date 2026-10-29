using System.Text;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Undo of a command that changed more than one file, moved, made one or changed more than files (a
/// <see cref="Part"/>), as a rename or Write to Launch Profile does: Undo where it is the last change (an editor of one
/// of its files, or the Solution view) takes all of it back at once, the files it moved going back, every file's text
/// as it was and the files it made gone, and Redo makes all of it again, each only while every file is still as the
/// command left it. One that changed one open file's text alone is that file's own undo step; one file not open, with
/// no undo of its own, is one.</summary>
public sealed class CommandHistory
{
    /// <summary>The most commands kept, the oldest going first.</summary>
    public const int Limit = 10;

    private readonly EditorArea editors;
    private readonly List<Command> done = [];
    private readonly List<Command> undone = [];

    internal CommandHistory(EditorArea editors) => this.editors = editors;

    /// <summary>Follows what a command changes from now until the recording is disposed: the open files' text, the files
    /// not open it is told it writes or makes (<see cref="Recording.Watch"/>) and the files moved. Kept as a command of its
    /// own when it changed more than one file or one not open, moved one or made one.</summary>
    public Recording Record(string name) => new(this, editors, name, ownWrites: false);

    /// <summary>A command that waits between its writes (a project's read, a program it runs), followed as <see cref="Record"/>
    /// follows one but taking of the open files only those it writes (<see cref="Recording.Watch"/>), each as it wrote it:
    /// what the person types meanwhile, in any file, stays the person's own.</summary>
    public Recording RecordWrites(string name) => new(this, editors, name, ownWrites: true);

    /// <summary>The command Undo takes back there: in a file's editor, the one whose change is that file's next undo
    /// step; in the Solution view (no file), the latest. Null when Undo is the file's own.</summary>
    public Command? ToUndo(OpenDocument? document) => Find(done, document, redo: false);

    /// <summary>The command Redo makes again there, as <see cref="ToUndo"/> finds it.</summary>
    public Command? ToRedo(OpenDocument? document) => Find(undone, document, redo: true);

    /// <summary>Forgets every command, as a folder closing does.</summary>
    public void Clear()
    {
        done.Clear();
        undone.Clear();
    }

    /// <summary>Why a command cannot be taken back (or made again) now, naming the first file changed or moved since;
    /// null when it can.</summary>
    public string? Blocked(Command command, bool redo)
    {
        foreach ((string from, string to) in command.Moves)
        {
            (string there, string free) = redo ? (from, to) : (to, from);
            if (!Exists(there))
            {
                return $"{Path.GetFileName(there)} is no longer there";
            }
            if (Exists(free) && !string.Equals(free, there, StringComparison.OrdinalIgnoreCase))
            {
                return $"{Path.GetFileName(free)} is already there";
            }
        }
        foreach (Creation made in command.Created)
        {
            if (redo && Exists(made.Path))
            {
                return $"{Path.GetFileName(made.Path)} is already there";
            }
            if (!redo && Made(made) is { } changed)
            {
                return changed;
            }
        }
        foreach (TextChange text in command.Texts)
        {
            if (IsOpen(text.Document))
            {
                if ((redo ? text.Document!.Text.NextRedo : text.Document!.Text.NextUndo) != text.Step)
                {
                    return $"{text.Document.Name} has been changed since";
                }
            }
            else
            {
                string at = redo ? text.Path : command.Moved(text.Path);
                string expected = (redo ? text.Before : text.After).ToString();
                if (!File.Exists(at) || FileText.Load(at)?.Text != expected)
                {
                    return $"{Path.GetFileName(at)} has been changed since";
                }
            }
        }
        foreach (Part part in command.Parts)
        {
            if (part.Blocked(redo) is { } changed)
            {
                return changed;
            }
        }
        return null;
    }

    /// <summary>Takes a command back: the files it moved go back, then every file's text is as it was. Null once done,
    /// else why not, from the disk.</summary>
    public string? Undo(Command command)
    {
        foreach ((string from, string to) in command.Moves.Reverse())
        {
            if (editors.MoveFile(to, from) is { } failed)
            {
                return failed;
            }
        }
        foreach (TextChange text in command.Texts)
        {
            if (Put(text, redo: false) is { } failed)
            {
                return failed;
            }
        }
        foreach (Creation made in command.Created)
        {
            if (Unmake(made) is { } failed)
            {
                return failed;
            }
        }
        foreach (Part part in command.Parts.Reverse())
        {
            part.Put(false);
        }
        done.Remove(command);
        undone.Add(command);
        return null;
    }

    /// <summary>Makes a command again: the files it made, every file's text as it left it, then the files it moved move
    /// again.</summary>
    public string? Redo(Command command)
    {
        foreach (Creation made in command.Created)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(made.Path)!);
                FileText.Save(made.Path, made.Text.ToString(), new UTF8Encoding(false), false, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"{Path.GetFileName(made.Path)} could not be written: {error.Message}";
            }
        }
        foreach (TextChange text in command.Texts)
        {
            if (Put(text, redo: true) is { } failed)
            {
                return failed;
            }
        }
        foreach ((string from, string to) in command.Moves)
        {
            if (editors.MoveFile(from, to) is { } failed)
            {
                return failed;
            }
        }
        foreach (Part part in command.Parts)
        {
            part.Put(true);
        }
        undone.Remove(command);
        done.Add(command);
        return null;
    }

    /// <summary>The files not open that taking a command back (or making it again, given true) writes, whose saves may
    /// still be landing once it returns (<see cref="EditorArea.LandedAsync(IEnumerable{string}, Action{string}?)"/>).</summary>
    public IReadOnlyList<string> Writes(Command command, bool redo) =>
        [.. command.Texts.Where(t => !IsOpen(t.Document)).Select(t => t.Path), .. redo ? command.Created.Select(c => c.Path) : []];

    private void Keep(Command command)
    {
        undone.Clear();
        done.Add(command);
        if (done.Count > Limit)
        {
            done.RemoveAt(0);
        }
    }

    /// <summary>A file's text as it was before the command or after it: an open file's through its own undo (the editor
    /// showing it putting its carets back), a closed one's written.</summary>
    private string? Put(TextChange text, bool redo)
    {
        if (IsOpen(text.Document))
        {
            OpenDocument document = text.Document!;
            if (document == editors.Active)
            {
                editors.CodeEditor.TryExecute(redo ? EditorActions.Redo : EditorActions.Undo);
            }
            else if ((redo ? document.Text.Redo() : document.Text.Undo()) is { } carets)
            {
                document.Selections = carets;
            }
            return null;
        }
        try
        {
            FileText? now = FileText.Load(text.Path);
            FileText.Save(text.Path, (redo ? text.After : text.Before).ToString(), now?.Encoding ?? new UTF8Encoding(false), now?.HasBom ?? false, now?.CrBreaks ?? false);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(text.Path)} could not be written: {error.Message}";
        }
    }

    /// <summary>Why a file a command made cannot be taken away: it is gone, or its text is no longer what the command
    /// wrote; null when it can.</summary>
    private string? Made(Creation made)
    {
        string? now = editors.Find(made.Path) is { } open
            ? open.Text.Snapshot.ToString()
            : File.Exists(made.Path) ? FileText.Load(made.Path)?.Text : null;
        return now is null ? $"{Path.GetFileName(made.Path)} is no longer there"
            : now != made.Text.ToString() ? $"{Path.GetFileName(made.Path)} has been changed since"
            : null;
    }

    /// <summary>A file a command made, deleted, its tab closed if it was opened since.</summary>
    private string? Unmake(Creation made)
    {
        try
        {
            File.Delete(made.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(made.Path)} could not be deleted: {error.Message}";
        }
        foreach (OpenDocument document in editors.Documents.Where(d => d.Path == made.Path).ToList())
        {
            editors.CloseDeleted(document);
        }
        return null;
    }

    private bool IsOpen(OpenDocument? document) => document is not null && editors.Documents.Contains(document);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static Command? Find(List<Command> commands, OpenDocument? document, bool redo)
    {
        for (int i = commands.Count - 1; i >= 0; i--)
        {
            Command command = commands[i];
            if (document is null
                || command.Texts.Any(t => t.Document == document && (redo ? document.Text.NextRedo : document.Text.NextUndo) == t.Step))
            {
                return command;
            }
        }
        return null;
    }

    /// <summary>What a command did: its name ("Rename Foo to Bar"), each file's text before and after it (at the path the
    /// file had then; an open file's with the undo step that made it), the files it moved, in order, the files it made, and
    /// what else it changed.</summary>
    public sealed class Command(string name, IReadOnlyList<TextChange> texts, IReadOnlyList<(string From, string To)> moves, IReadOnlyList<Creation>? created = null,
        IReadOnlyList<Part>? parts = null)
    {
        public IReadOnlyList<Creation> Created { get; } = created ?? [];

        public IReadOnlyList<Part> Parts { get; } = parts ?? [];

        public string Name { get; } = name;

        public IReadOnlyList<TextChange> Texts { get; } = texts;

        public IReadOnlyList<(string From, string To)> Moves { get; } = moves;

        /// <summary>Where a path is once the command's moves are made.</summary>
        public string Moved(string path)
        {
            foreach ((string from, string to) in Moves)
            {
                path = PathNames.Moved(path, from, to);
            }
            return path;
        }
    }

    /// <summary>A file a command made, with the text it wrote there.</summary>
    public sealed record Creation(string Path, TextSnapshot Text);

    /// <summary>Something besides files a command changed (a run configuration's changes kept with the folder), named as the
    /// question before Undo names it: why it cannot be taken back (or made again, given true) now, null when it can; and
    /// how it is put as it was before the command (false) or after it (true).</summary>
    public sealed record Part(string Name, Func<bool, string?> Blocked, Action<bool> Put);

    /// <summary>One file's text changed by a command: the open file (null for one changed on disk) and the undo step its
    /// change is there.</summary>
    public sealed record TextChange(OpenDocument? Document, string Path, TextSnapshot Before, TextSnapshot After, object? Step);

    /// <summary>A command being made: what it changes is followed until it is disposed, then kept when it is one of
    /// several files, a file not open or a move.</summary>
    public sealed class Recording : IDisposable
    {
        private readonly CommandHistory history;
        private readonly EditorArea editors;
        private readonly string name;
        private readonly Dictionary<OpenDocument, (string Path, object? Step, TextSnapshot Text)> open;
        private readonly bool ownWrites;
        // Taking only what it writes: each open file it watched, as it was then, and the change it made in each taken so far.
        private readonly Dictionary<OpenDocument, (string Path, object? Step, TextSnapshot Text)> writing = [];
        private readonly Dictionary<OpenDocument, TextChange> wrote = [];
        private readonly Dictionary<string, TextSnapshot?> closed = [];
        private readonly List<(string From, string To)> moves = [];
        private readonly List<Part> parts = [];
        private List<TextChange>? openTaken;
        private bool ended;

        internal Recording(CommandHistory history, EditorArea editors, string name, bool ownWrites)
        {
            this.history = history;
            this.editors = editors;
            this.name = name;
            this.ownWrites = ownWrites;
            open = ownWrites ? [] : editors.Documents.ToDictionary(d => d, d => (d.Path, d.Text.NextUndo, d.Text.Snapshot));
            editors.Moved += OnMoved;
        }

        /// <summary>A file not open the command is about to write, at its path now: its text now, for the undo to put back;
        /// or one not there yet that it is about to make, for the undo to take away. One the command moved is kept by the
        /// path it had as the command began, where its undo moves it back before putting its text there.</summary>
        public void Watch(string path)
        {
            string began = Began(path);
            if (editors.Documents.FirstOrDefault(d => d.Path == path) is { } document)
            {
                if (ownWrites)
                {
                    writing.TryAdd(document, (path, document.Text.NextUndo, document.Text.Snapshot));
                }
                return;
            }
            if (!closed.ContainsKey(began))
            {
                closed[began] = File.Exists(path) && FileText.Load(path) is { } file ? TextSnapshot.From(file.Text) : null;
            }
        }

        /// <summary>A file not open the command watched left out of it: its write never landed, so it changed nothing.</summary>
        public void Unwritten(string path) => closed.Remove(Began(path));

        /// <summary>The files it watched under a folder the command made whole, which a part of its own takes away and
        /// makes again (<see cref="Also"/>), left out of its texts and the files it made.</summary>
        public void Leave(string folder)
        {
            foreach (string path in closed.Keys.Where(p => PathNames.IsUnder(p, folder)).ToList())
            {
                closed.Remove(path);
            }
            foreach (OpenDocument document in writing.Keys.Concat(wrote.Keys).Where(d => PathNames.IsUnder(d.Path, folder)).ToList())
            {
                writing.Remove(document);
                wrote.Remove(document);
            }
        }

        /// <summary>What the command changed in the open files, taken as it stands now: what is typed later, while it waits
        /// for its writes to files not open to land, is no part of it. Taking only what it writes, the open files it wrote
        /// since the last take are taken, and those it writes later are taken from what they hold then.</summary>
        public void TakeOpen()
        {
            if (!ownWrites)
            {
                openTaken = OpenChanges();
                return;
            }
            foreach ((OpenDocument document, (string path, object? step, TextSnapshot text)) in writing)
            {
                if (editors.Documents.Contains(document) && document.Text.NextUndo is { } now && now != step)
                {
                    wrote[document] = wrote.TryGetValue(document, out TextChange? first)
                        ? first with { After = document.Text.Snapshot, Step = now }
                        : new TextChange(document, path, text, document.Text.Snapshot, now);
                }
            }
            writing.Clear();
        }

        /// <summary>Waits for the saves still landing of the files not open it watched (<see cref="EditorArea.LandedAsync(IEnumerable{string}, Action{string}?)"/>,
        /// <paramref name="waiting"/> told which), the open files' changes taken first (<see cref="TakeOpen"/>) and each file
        /// whose save never landed left out (<see cref="Unwritten"/>): those files, with why.</summary>
        public async Task<IReadOnlyList<(string Path, Exception Refused)>> LandedAsync(Action<string>? waiting)
        {
            TakeOpen();
            IReadOnlyList<(string Path, Exception Refused)> unlanded = await EditorArea.LandedAsync([.. closed.Keys], waiting);
            foreach ((string path, _) in unlanded)
            {
                closed.Remove(path);
            }
            return unlanded;
        }

        /// <summary>Something besides files the command changed, taken back and made again with them; the command is kept
        /// for it however few files it changed.</summary>
        public void Also(Part part) => parts.Add(part);

        public void Dispose()
        {
            if (ended)
            {
                return;
            }
            ended = true;
            editors.Moved -= OnMoved;
            Command made = new(name, [], [.. moves]);
            if (ownWrites)
            {
                TakeOpen();
            }
            List<TextChange> texts = ownWrites ? [.. wrote.Values] : openTaken ?? OpenChanges();
            List<Creation> created = [];
            foreach ((string path, TextSnapshot? before) in closed)
            {
                string at = made.Moved(path);
                if (before is null)
                {
                    if (File.Exists(at) && FileText.Load(at)?.Text is { } written)
                    {
                        created.Add(new Creation(at, TextSnapshot.From(written)));
                    }
                }
                else if (File.Exists(at) && FileText.Load(at)?.Text is { } after && after != before.ToString())
                {
                    texts.Add(new TextChange(null, path, before, TextSnapshot.From(after), null));
                }
            }
            if (texts.Count > 1 || texts.Any(t => t.Document is null) || moves.Count > 0 || created.Count > 0 || parts.Count > 0)
            {
                history.Keep(new Command(name, texts, made.Moves, created, [.. parts]));
            }
        }

        private void OnMoved(object? sender, FileMove move) => moves.Add((move.From, move.To));

        /// <summary>Where a file was as the command began, before the moves it made since.</summary>
        private string Began(string path) => Enumerable.Reverse(moves).Aggregate(path, (at, move) => PathNames.Moved(at, move.To, move.From));

        /// <summary>Each open file the command changed: its text before and now, and the undo step that made it now.</summary>
        private List<TextChange> OpenChanges()
        {
            List<TextChange> texts = [];
            foreach ((OpenDocument document, (string path, object? step, TextSnapshot text)) in open)
            {
                if (editors.Documents.Contains(document) && document.Text.NextUndo is { } now && now != step)
                {
                    texts.Add(new TextChange(document, path, text, document.Text.Snapshot, now));
                }
            }
            return texts;
        }
    }
}

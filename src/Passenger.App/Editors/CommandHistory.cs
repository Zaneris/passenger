using System.Text;
using Passenger.App.History;
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
/// no undo of its own, is one. The files not open are read and written off the window's thread, every write of them (a
/// command's, Replace in Files', Undo's and Redo's) in one order (<see cref="InTurnAsync"/>).</summary>
public sealed class CommandHistory
{
    /// <summary>The most commands kept, the oldest going first.</summary>
    public const int Limit = 10;

    /// <summary>The id the window's work of writing files not open is told under, saying which are left.</summary>
    public const string WritingFiles = "edit.writeFiles";

    private readonly EditorArea editors;
    private readonly List<Command> done = [];
    private readonly List<Command> undone = [];
    /// <summary>The commands being made, from their recording's start until it is disposed.</summary>
    private readonly List<Recording> making = [];
    /// <summary>The writes of files not open asked for and not yet ended, in their order.</summary>
    private readonly List<Turn> turns = [];
    /// <summary>Done once every write of files not open asked for so far has ended; it never faults.</summary>
    private Task written = Task.CompletedTask;
    private Working? writing;
    private (Task Until, TaskCompletionSource Reached)? holdNext;
    /// <summary>The last look at a command's files not open, for taking it back or making it again (true), by path, with how
    /// many puts had begun as it looked: a later look of the same reads only a file whose length or write time moved.</summary>
    private (Command Command, bool Redo, Dictionary<string, Look> Files, int Puts)? looked;
    /// <summary>The command an Undo or Redo is putting, until its files not open are written and it has ended.</summary>
    private (Command Command, Task Ended)? putting;
    private int puts;
    /// <summary>Whether the history let go of the command being put meanwhile, which it then does once that ends.</summary>
    private bool putDropped;

    internal CommandHistory(EditorArea editors) => this.editors = editors;

    /// <summary>Follows what a command changes from now until the recording is disposed: the open files' text, the files
    /// not open it is told it writes or makes (<see cref="Recording.Watch"/>, or with their texts <see cref="Recording.Wrote"/>)
    /// and the files it moves (<see cref="Recording.Move"/>). Kept as a command of its own when it changed more than one
    /// file or one not open, moved one or made one.</summary>
    public Recording Record(string name) => new(this, editors, name, ownWrites: false);

    /// <summary>A command that waits between its writes (a project's read, a program it runs), followed as <see cref="Record"/>
    /// follows one but taking of the open files only those it writes (<see cref="Recording.Watch"/>), each as it wrote it:
    /// what the person types meanwhile, in any file, stays the person's own.</summary>
    public Recording RecordWrites(string name) => new(this, editors, name, ownWrites: true);

    /// <summary>Where the writes of files not open are told as the window's work (<see cref="WritingFiles"/>); null tells
    /// none.</summary>
    public ActionWork? Work { get; set; }

    /// <summary>The command being made that Undo or Redo there waits for: in a file's editor, one whose change is what that
    /// file's next undo step would take; in the Solution view (no file), the first being made that has changed anything.</summary>
    public Recording? Making(OpenDocument? document) => making.FirstOrDefault(r => r.Changed(document));

    /// <summary>Whether a write of a file not open is asked for or under way and has yet to end.</summary>
    public bool IsWriting(string path) => turns.Any(t => !t.Ended && t.Paths.Contains(path));

    /// <summary>The files not open the writes asked for or under way write.</summary>
    public IReadOnlyList<string> PathsWriting() => [.. turns.Where(t => !t.Ended).SelectMany(t => t.Paths)];

    /// <summary>What writes files not open now, as its work says it ("Undo Rename Foo to Bar"); null when nothing does.</summary>
    public string? WritingNow => turns.FirstOrDefault(t => !t.Ended)?.What;

    /// <summary>Done once every write of files not open asked for so far has ended, whether or not it was refused.</summary>
    public Task WrittenAsync() => written;

    /// <summary>The next write of files not open waits until <paramref name="until"/> has ended before it begins, as a slow
    /// disk keeps it, for a test of what goes on meanwhile: done once that write is waiting.</summary>
    public Task HoldNextWrite(Task until)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        holdNext = (until, reached);
        return reached.Task;
    }

    /// <summary>Writes files not open off the window's thread once every write asked for before it has ended, the window's
    /// work saying which of <paramref name="paths"/> are left under <paramref name="what"/> until it ends; the write is
    /// told of each file it has written, from its own thread. What it answers.</summary>
    internal Task<T> InTurnAsync<T>(string what, IReadOnlyCollection<string> paths, Func<Action<string>, T> write)
    {
        UiThread.Check();
        Turn turn = new(what, new HashSet<string>(paths, PathNames.Comparer), [.. paths]);
        turns.Add(turn);
        Say();
        Task before = written;
        (Task Until, TaskCompletionSource Reached)? hold = holdNext;
        holdNext = null;
        Task<T> work = BackgroundWork.Run(async () =>
        {
            await before;
            if (hold is { } held)
            {
                held.Reached.TrySetResult();
                await held.Until;
            }
            return write(path => UiThread.Post(() => Wrote(turn, path)));
        });
        turn.Work = work;
        written = work.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return EndedAsync(work, turn);
    }

    private async Task<T> EndedAsync<T>(Task<T> work, Turn turn)
    {
        try
        {
            return await work;
        }
        finally
        {
            turns.Remove(turn);
            Say();
        }
    }

    private void Wrote(Turn turn, string path)
    {
        if (turn.Left.Remove(path) && turns.Contains(turn))
        {
            Say();
        }
    }

    /// <summary>The window's work while files not open are written: the first write under way and the files it has left.</summary>
    private void Say()
    {
        if (turns.Count == 0)
        {
            // Its words go with it: what the change says once written is said after.
            writing?.Say(string.Empty);
            writing?.Dispose();
            writing = null;
            return;
        }
        Turn first = turns[0];
        List<string> left = [.. first.Left.Select(Path.GetFileName).OfType<string>()];
        string stage = left.Count == 0 ? $"{first.What}: writing…"
            : $"{first.What}: writing {string.Join(", ", left.Take(3))}{(left.Count > 3 ? $" and {left.Count - 3} more" : string.Empty)}…";
        if (writing is null)
        {
            writing = Work?.Begin(WritingFiles, stage, stoppable: false);
        }
        else if (writing.Stage != stage)
        {
            writing.Say(stage);
        }
    }

    /// <summary>A write of files not open asked for: what it is, the files it writes, those it has yet to, and the work
    /// writing them, ended once its last write has (before the window has heard).</summary>
    private sealed class Turn(string what, HashSet<string> paths, List<string> left)
    {
        public string What { get; } = what;

        public HashSet<string> Paths { get; } = paths;

        public List<string> Left { get; } = left;

        public Task? Work { get; set; }

        public bool Ended => Work?.IsCompleted == true;
    }

    /// <summary>The command Undo takes back there: in a file's editor, the one whose change is that file's next undo
    /// step; in the Solution view (no file), the latest. Null when Undo is the file's own.</summary>
    public Command? ToUndo(OpenDocument? document) => Find(done, document, redo: false);

    /// <summary>The command Redo makes again there, as <see cref="ToUndo"/> finds it.</summary>
    public Command? ToRedo(OpenDocument? document) => Find(undone, document, redo: true);

    /// <summary>Forgets every command, as a folder closing does.</summary>
    public void Clear()
    {
        looked = null;
        Drop(done);
        Drop(undone);
    }

    /// <summary>Why a command cannot be taken back (or made again) now, naming the first file changed or moved since, or
    /// saying the history has let it go; null when it can. What its parts read of the disk at length (<see cref="Part.Read"/>)
    /// and its files not open are looked at off the window's thread, a file read only when its length or last write time
    /// moved since the last look at it; an Undo or Redo under way, and the writes asked for of its files, are waited out
    /// first.</summary>
    public async Task<string?> BlockedAsync(Command command, bool redo)
    {
        while (true)
        {
            if (putting is { } other)
            {
                await other.Ended;
            }
            else if (Closed(command, redo).Any(f => IsWriting(f.Path)))
            {
                await written;
            }
            else
            {
                break;
            }
        }
        foreach (Part part in command.Parts)
        {
            if (part.Read is { } read && await read(redo) is { } changed)
            {
                return changed;
            }
        }
        if (Blocked(command, redo, null) is { } blocked)
        {
            return blocked;
        }
        int begun = puts;
        List<(string Path, TextSnapshot Text, bool Made)> files = Closed(command, redo);
        Dictionary<string, Look>? earlier = looked is { } last && last.Command == command && last.Redo == redo ? last.Files : null;
        (string? unlike, Dictionary<string, Look> looks) = files.Count == 0 ? (null, []) : await BackgroundWork.Run(() => LookAt(files, earlier));
        looked = unlike is null ? (command, redo, looks, begun) : null;
        return unlike ?? Blocked(command, redo, looks);
    }

    /// <summary>Takes a command back (or makes it again, given true), asked again first as <see cref="BlockedAsync"/> asks and
    /// at once after, so nothing comes between the look and the change: its moves (made within <paramref name="moving"/>)
    /// and the open files' steps on the window's thread, its files not open written off it in order, each stored as the look
    /// found it. Why not, null once done; and whether it began, false when refused before it changed anything.</summary>
    public async Task<(string? Why, bool Began)> PutAsync(Command command, bool redo, Func<Func<string?>, string?> moving)
    {
        Dictionary<string, Look> looks;
        while (true)
        {
            if (await BlockedAsync(command, redo) is { } changed)
            {
                return (changed, false);
            }
            // Another put begun since the look, or a write of its files asked for since, may have written what it found.
            if (putting is null && looked is { } look && look.Command == command && look.Redo == redo && look.Puts == puts
                && !Closed(command, redo).Any(f => IsWriting(f.Path)))
            {
                looks = look.Files;
                break;
            }
        }
        if (Blocked(command, redo, looks) is { } since)
        {
            return (since, false);
        }
        looked = null;
        List<Command> from = redo ? undone : done;
        List<Command> to = redo ? done : undone;
        int at = from.IndexOf(command);
        from.RemoveAt(at);
        to.Add(command);
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        putting = (command, ended.Task);
        puts++;
        try
        {
            string? failed = redo ? await RedoAsync(command, looks, moving) : await UndoAsync(command, looks, moving);
            if (failed is not null && to.Remove(command))
            {
                from.Insert(Math.Min(at, from.Count), command);
            }
            return (failed, true);
        }
        finally
        {
            putting = null;
            ended.SetResult();
            if (putDropped)
            {
                putDropped = false;
                Dropped(command);
            }
        }
    }

    /// <summary>The files not open that taking a command back (or making it again, given true) writes, whose saves may
    /// still be landing once it returns (<see cref="EditorArea.LandedAsync(IEnumerable{string}, Action{string}?)"/>).</summary>
    public IReadOnlyList<string> Writes(Command command, bool redo) =>
        [.. command.Texts.Where(t => !IsOpen(t.Document)).Select(t => t.Path), .. redo ? command.Created.Select(c => c.Path) : []];

    /// <summary>Why a command cannot be taken back (or made again) now, from what the window holds and where its files are:
    /// its files not open as <paramref name="looks"/> found them, one not looked at counting as changed; with no look, no
    /// file's text is compared, the open files' undo steps alone asked of.</summary>
    private string? Blocked(Command command, bool redo, IReadOnlyDictionary<string, Look>? looks)
    {
        if (!(redo ? undone : done).Contains(command))
        {
            return "commands made since have taken its place";
        }
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
            if (redo)
            {
                if (Exists(made.Path))
                {
                    return $"{Path.GetFileName(made.Path)} is already there";
                }
                continue;
            }
            bool unlike = looks is not null && (editors.Find(made.Path) is { } open
                ? open.Text.Snapshot.ToString() != made.Text.ToString()
                : !looks.ContainsKey(made.Path));
            if (unlike)
            {
                return $"{Path.GetFileName(made.Path)} has been changed since";
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
                continue;
            }
            string at = redo ? text.Path : command.Moved(text.Path);
            if (looks is not null && !looks.ContainsKey(at))
            {
                return $"{Path.GetFileName(at)} has been changed since";
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

    /// <summary>Takes a command back: its parts put first (<see cref="Part.First"/>), the files it moved go back and the open
    /// files take their undo steps, then the files not open are written as they were, off the window's thread, the files it
    /// made go and its other parts are put. Null once done, else why not.</summary>
    private async Task<string?> UndoAsync(Command command, IReadOnlyDictionary<string, Look> looks, Func<Func<string?>, string?> moving)
    {
        if (PutFirst(command, redo: false) is { } unput)
        {
            return unput;
        }
        List<(string Path, TextSnapshot Text, Look? Stored)> writes = [.. command.Texts.Where(t => !IsOpen(t.Document)).Select(t => (t.Path, t.Before, (Look?)looks[command.Moved(t.Path)]))];
        string? unmoved = moving(() =>
        {
            foreach ((string from, string to) in command.Moves.Reverse())
            {
                if (editors.MoveFile(to, from) is { } failed)
                {
                    return failed;
                }
            }
            Step(command, redo: false);
            return null;
        });
        if (unmoved is not null)
        {
            return unmoved;
        }
        if (writes.Count > 0 && await InTurnAsync($"Undo {command.Name}", [.. writes.Select(w => w.Path)], wrote => Write(writes, wrote)) is { } unwritten)
        {
            return unwritten;
        }
        foreach (Creation made in command.Created)
        {
            if (Unmake(made) is { } failed)
            {
                return failed;
            }
        }
        foreach (Part part in command.Parts.Reverse().Where(p => !p.First))
        {
            if (part.Put(false) is { } refused)
            {
                return refused;
            }
        }
        return null;
    }

    /// <summary>Makes a command again: its parts put first (<see cref="Part.First"/>), the open files take their redo steps,
    /// the files it made and the files not open are written as it left them, off the window's thread, then the files it
    /// moved move again and its other parts are put.</summary>
    private async Task<string?> RedoAsync(Command command, IReadOnlyDictionary<string, Look> looks, Func<Func<string?>, string?> moving)
    {
        if (PutFirst(command, redo: true) is { } unput)
        {
            return unput;
        }
        List<(string Path, TextSnapshot Text, Look? Stored)> writes =
        [
            .. command.Created.Select(c => (c.Path, c.Text, (Look?)null)),
            .. command.Texts.Where(t => !IsOpen(t.Document)).Select(t => (t.Path, t.After, (Look?)looks[t.Path])),
        ];
        Step(command, redo: true);
        if (writes.Count > 0 && await InTurnAsync($"Redo {command.Name}", [.. writes.Select(w => w.Path)], wrote => Write(writes, wrote)) is { } unwritten)
        {
            return unwritten;
        }
        string? unmoved = moving(() =>
        {
            foreach ((string from, string to) in command.Moves)
            {
                if (editors.MoveFile(from, to) is { } failed)
                {
                    return failed;
                }
            }
            return null;
        });
        if (unmoved is not null)
        {
            return unmoved;
        }
        foreach (Part part in command.Parts.Where(p => !p.First))
        {
            if (part.Put(true) is { } failed)
            {
                return failed;
            }
        }
        return null;
    }

    /// <summary>Puts a command's parts that go first (<see cref="Part.First"/>), in the order taking it back (or making it
    /// again, given true) takes them: the first refused puts back those before it, so nothing of the command has changed.
    /// Why it was refused, null once each is put.</summary>
    private static string? PutFirst(Command command, bool redo)
    {
        List<Part> first = [.. (redo ? command.Parts : command.Parts.Reverse()).Where(p => p.First)];
        for (int at = 0; at < first.Count; at++)
        {
            if (first[at].Put(redo) is { } failed)
            {
                return string.Join("; ", first.Take(at).Reverse().Select(p => p.Put(!redo)).OfType<string>().Prepend(failed));
            }
        }
        return null;
    }

    private void Keep(Command command)
    {
        Drop(undone);
        done.Add(command);
        if (done.Count > Limit)
        {
            Command oldest = done[0];
            done.RemoveAt(0);
            Dropped(oldest);
        }
    }

    private void Drop(List<Command> commands)
    {
        List<Command> going = [.. commands];
        commands.Clear();
        going.ForEach(Dropped);
    }

    private void Dropped(Command command)
    {
        if (putting?.Command == command)
        {
            putDropped = true;
            return;
        }
        if (looked?.Command == command)
        {
            looked = null;
        }
        foreach (Part part in command.Parts)
        {
            part.Dropped?.Invoke();
        }
    }

    /// <summary>The open files' texts as they were before the command or after it (true), each through its own undo, the
    /// editor showing it putting its carets back.</summary>
    private void Step(Command command, bool redo)
    {
        foreach (TextChange text in command.Texts.Where(t => IsOpen(t.Document)))
        {
            editors.Step(text.Document!, redo);
        }
    }

    /// <summary>The files not open a command changes, as a look finds them: where each is now, the text it holds while all
    /// is as the command left it (or found it, for a redo), and whether the command made it, which only its Undo asks of
    /// (a Redo asks only that the place is free).</summary>
    private List<(string Path, TextSnapshot Text, bool Made)> Closed(Command command, bool redo)
    {
        List<(string Path, TextSnapshot Text, bool Made)> files = [];
        if (!redo)
        {
            files.AddRange(command.Created.Where(c => editors.Find(c.Path) is null).Select(c => (c.Path, c.Text, true)));
        }
        files.AddRange(command.Texts.Where(t => !IsOpen(t.Document)).Select(t => (redo ? t.Path : command.Moved(t.Path), redo ? t.Before : t.After, false)));
        return files;
    }

    /// <summary>Looks at files not open, off the window's thread, each read only when its length or last write time moved
    /// since <paramref name="earlier"/> looked: why the first not holding its text does not (gone, changed or unread), else
    /// null; with how each looked at is stored.</summary>
    private static (string? Unlike, Dictionary<string, Look> Looks) LookAt(IReadOnlyList<(string Path, TextSnapshot Text, bool Made)> files,
        IReadOnlyDictionary<string, Look>? earlier)
    {
        Dictionary<string, Look> looks = new(PathNames.Comparer);
        foreach ((string path, TextSnapshot text, bool made) in files)
        {
            string name = Path.GetFileName(path);
            try
            {
                if (Stamp(path) is not { } stamp)
                {
                    return (made ? $"{name} is no longer there" : $"{name} has been changed since", looks);
                }
                if (earlier?.GetValueOrDefault(path) is { } seen && seen.Length == stamp.Length && seen.Written == stamp.Written)
                {
                    looks[path] = seen;
                    continue;
                }
                if (FileText.Load(path) is not { } file || file.Text != text.ToString())
                {
                    return ($"{name} has been changed since", looks);
                }
                looks[path] = new Look(stamp.Length, stamp.Written, file.Encoding, file.HasBom, file.CrBreaks);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return ($"{name} could not be read: {error.Message}", looks);
            }
        }
        return (null, looks);
    }

    /// <summary>A file's length and last write time (through a link, its target's), or null when it is not there.</summary>
    private static (long Length, DateTime Written)? Stamp(string path)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
        {
            info = target;
        }
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <summary>Writes files not open, off the window's thread, in order, <paramref name="wrote"/> told of each: each stored
    /// as a look found it, or as UTF-8 for one a command made. Null once each is written, else the first that could not be,
    /// with why.</summary>
    private static string? Write(IReadOnlyList<(string Path, TextSnapshot Text, Look? Stored)> files, Action<string> wrote)
    {
        foreach ((string path, TextSnapshot text, Look? stored) in files)
        {
            try
            {
                FileText.Save(path, text.ToString(), stored?.Encoding ?? new UTF8Encoding(false), stored?.HasBom ?? false, stored?.CrBreaks ?? false);
                wrote(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"{Path.GetFileName(path)} could not be written: {error.Message}";
            }
        }
        return null;
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

    /// <summary>A file not open as a look found it: its length and last write time, which a later look compares before it
    /// reads the file again, and how it is stored, which its Undo or Redo writes it in.</summary>
    private sealed record Look(long Length, DateTime Written, Encoding Encoding, bool HasBom, bool CrBreaks);

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
    /// how it is put as it was before the command (false) or after it (true), null once put, else why it could not be.</summary>
    public sealed record Part(string Name, Func<bool, string?> Blocked, Func<bool, string?> Put)
    {
        /// <summary>Put before the command's files and moves, whichever way it goes, so that its failure leaves the rest of
        /// the command as it was: a part the disk may refuse (a folder moved).</summary>
        public bool First { get; init; }

        /// <summary>Why it cannot be taken back (or made again, given true) now, from what it reads of the disk at length,
        /// read off the window's thread; null when it can.</summary>
        public Func<bool, Task<string?>>? Read { get; init; }

        /// <summary>Lets go of what it keeps to be made again, once its command has left the history.</summary>
        public Action? Dropped { get; init; }
    }

    /// <summary>A file not open a queued change was made in: its path as the command began and where it was written, its
    /// texts before and after, and whether they differ.</summary>
    private sealed record ChangeMade(string Began, string At, TextSnapshot Before, TextSnapshot After, bool Changed);

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
        /// <summary>Each file not open it watched or wrote, by its path as the command began: its text before (none for one it
        /// made) and its text after, when the command gave it (<see cref="Wrote"/>) or made it (<see cref="Change"/>), else
        /// read once it ends.</summary>
        private readonly Dictionary<string, (TextSnapshot? Before, TextSnapshot? After)> closed = [];
        /// <summary>The changes of files not open it has yet to make (<see cref="Change"/>), in order, each by the file's path
        /// as the command began.</summary>
        private readonly List<(string Began, Func<string, string> Change)> queued = [];
        /// <summary>Where it made each change of <see cref="queued"/>, with the file's path as the command began.</summary>
        private readonly Dictionary<string, string> madeAt = new(PathNames.Comparer);
        /// <summary>What names its writes in Local History (a refactoring's change set): over the window while it changes
        /// files on the window's thread, then only over its own writes of files not open, made off it.</summary>
        private readonly List<LocalHistory.Activity> naming = [];
        private readonly TaskCompletionSource ending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string From, string To)> moves = [];
        private readonly List<Part> parts = [];
        private List<TextChange>? openTaken;
        // Whether it has said it writes a file since the open files were last taken.
        private bool writes = true;
        private bool ended;

        internal Recording(CommandHistory history, EditorArea editors, string name, bool ownWrites)
        {
            this.history = history;
            this.editors = editors;
            this.name = name;
            this.ownWrites = ownWrites;
            open = ownWrites ? [] : editors.Documents.ToDictionary(d => d, d => (d.Path, d.Text.NextUndo, d.Text.Snapshot));
            history.making.Add(this);
        }

        /// <summary>The command's name, as Undo names it.</summary>
        public string Name => name;

        /// <summary>Done once the recording is disposed, the command kept when it is one.</summary>
        public Task Ended => ending.Task;

        /// <summary>Whether a file not open is one the command has made, watched or written: a change of it the command
        /// makes later is on its own text.</summary>
        public bool Owns(string path) => closed.ContainsKey(Began(path)) || madeAt.ContainsKey(path);

        /// <summary>Whether it has changed what Undo there would take: in a file's editor (<paramref name="document"/>), the
        /// change that file's next undo step takes; with no file, anything.</summary>
        internal bool Changed(OpenDocument? document)
        {
            if (document is null)
            {
                return moves.Count > 0 || closed.Count > 0 || queued.Count > 0 || madeAt.Count > 0 || parts.Count > 0
                    || (ownWrites ? wrote.Count > 0 || writing.Any(w => w.Key.Text.NextUndo != w.Value.Step) : Steps().Count > 0);
            }
            object? next = document.Text.NextUndo;
            if (ownWrites)
            {
                return (wrote.TryGetValue(document, out TextChange? made) && made.Step == next)
                    || (writing.TryGetValue(document, out (string Path, object? Step, TextSnapshot Text) watched) && watched.Step != next);
            }
            return Steps().Any(t => t.Document == document && t.Step == next);
        }

        /// <summary>The open files' changes as it stands: those taken, unless it has said it writes another since.</summary>
        private List<TextChange> Steps() => writes || openTaken is null ? OpenChanges() : openTaken;

        /// <summary>A file not open the command is about to write, at its path now: its text now, for the undo to put back;
        /// or one not there yet that it is about to make, for the undo to take away. One the command moved is kept by the
        /// path it had as the command began, where its undo moves it back before putting its text there.</summary>
        public void Watch(string path)
        {
            writes = true;
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
                closed[began] = (File.Exists(path) && FileText.Load(path) is { } file ? TextSnapshot.From(file.Text) : null, null);
            }
        }

        /// <summary>Moves a file or folder with its open files as part of the command (<see cref="EditorArea.MoveFile(string, string)"/>),
        /// which its Undo moves back: the moves it takes are those it makes here alone, never one made meanwhile by the
        /// person, another command or an Undo. Null once moved, else why not, nothing moved.</summary>
        public string? Move(string from, string to)
        {
            string? failed = editors.MoveFile(from, to, out FileMove? made);
            if (made is not null)
            {
                moves.Add((made.From, made.To));
            }
            return failed;
        }

        /// <summary>A file not open that the command, or its language server a moment before, made empty for the command to
        /// fill (TypeScript's Move to a new file): one the command made, which its Undo takes away.</summary>
        public void Made(string path) => closed.TryAdd(Began(path), (null, null));

        /// <summary>A file not open the command wrote, at its path now, with its text before and after as the command had
        /// them, kept as they are: neither is read again. One it watched before keeps the text it had then.</summary>
        public void Wrote(string path, TextSnapshot before, TextSnapshot after) => Keep(Began(path), before, after);

        /// <summary>A file not open the command changes, at its path now, by a change of its text that any thread may run:
        /// made off the window's thread, after the changes queued before it, once the command writes (<see cref="WriteAsync"/>),
        /// the file read there once and written in its own encoding, its texts kept for Undo. One the command moves meanwhile
        /// is changed where it went.</summary>
        public void Change(string path, Func<string, string> change) => queued.Add((Began(path), change));

        /// <summary>What names the command's writes in Local History (a refactoring's change set): its saves on the window's
        /// thread until it writes the files not open, then those writes alone.</summary>
        public void Naming(LocalHistory.Activity? during)
        {
            if (during is not null)
            {
                naming.Add(during);
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
            queued.RemoveAll(q => PathNames.IsUnder(q.Began, folder));
            foreach (OpenDocument document in writing.Keys.Concat(wrote.Keys).Where(d => PathNames.IsUnder(d.Path, folder)).ToList())
            {
                writing.Remove(document);
                wrote.Remove(document);
            }
        }

        /// <summary>What the command changed in the open files, taken as it stands now: what is typed later, while it writes
        /// files not open or waits for them to land, is no part of it, the open files taken again only once it says it writes
        /// another (<see cref="Watch"/>). Taking only what it writes, the open files it wrote since the last take are taken,
        /// and those it writes later are taken from what they hold then.</summary>
        public void TakeOpen()
        {
            if (!ownWrites)
            {
                if (writes || openTaken is null)
                {
                    openTaken = OpenChanges();
                    writes = false;
                }
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
            (string Path, Exception Refused)? refused = await WriteAsync();
            IReadOnlyList<(string Path, Exception Refused)> unlanded = await EditorArea.LandedAsync([.. closed.Keys, .. madeAt.Keys], waiting);
            foreach ((string path, _) in unlanded)
            {
                closed.Remove(madeAt.GetValueOrDefault(path) ?? path);
            }
            return refused is { } first ? [first, .. unlanded] : unlanded;
        }

        /// <summary>Makes the changes queued so far (<see cref="Change"/>) off the window's thread, in order, the open files'
        /// changes taken first (<see cref="TakeOpen"/>): the first file the disk refused, with why, the changes after it left
        /// unmade; else null.</summary>
        public async Task<(string Path, Exception Refused)?> WriteAsync()
        {
            TakeOpen();
            if (queued.Count == 0)
            {
                Unname();
                return null;
            }
            List<(string Began, string At, Func<string, string> Change)> files = [.. queued.Select(q => (q.Began, Now(q.Began), q.Change))];
            queued.Clear();
            // From here Local History names only these writes, not what is saved meanwhile.
            LocalHistory.Activity? named = naming.FirstOrDefault();
            Unname();
            (List<ChangeMade> made, (string Path, Exception Refused)? refused) = await history.InTurnAsync(name, [.. files.Select(f => f.At).Distinct(PathNames.Comparer)], wrote =>
            {
                using IDisposable? own = named?.Own();
                return Make(files, wrote);
            });
            Took(made);
            return refused;
        }

        /// <summary>Something besides files the command changed, taken back and made again with them; the command is kept
        /// for it however few files it changed.</summary>
        public void Also(Part part) => parts.Add(part);

        /// <summary>Ends the recording, keeping the command when it is one. Changes it queued and never wrote (a command
        /// stopped on a refusal after them) are made first, in turn, off the window's thread, as <see cref="WriteAsync"/>
        /// makes them, the command still being made until they are; a file the disk refuses there goes to the IDE's log.</summary>
        public void Dispose()
        {
            if (ended)
            {
                return;
            }
            ended = true;
            if (queued.Count > 0)
            {
                IdeErrors.Observe(WrittenThenEndAsync());
                return;
            }
            End();
        }

        private async Task WrittenThenEndAsync()
        {
            try
            {
                if (await WriteAsync() is (string path, Exception refused))
                {
                    IdeLog.Write(IdeLog.Ide, $"{name} ended with changes it had not written, and {Path.GetFileName(path)} could not be written: {refused.Message}");
                }
            }
            finally
            {
                End();
            }
        }

        private void End()
        {
            history.making.Remove(this);
            try
            {
                Finish();
            }
            finally
            {
                ending.SetResult();
            }
        }

        /// <summary>Keeps the command when it is one.</summary>
        private void Finish()
        {
            Unname();
            Command made = new(name, [], [.. moves]);
            if (ownWrites)
            {
                TakeOpen();
            }
            List<TextChange> texts = ownWrites ? [.. wrote.Values] : openTaken ?? OpenChanges();
            List<Creation> created = [];
            foreach ((string path, (TextSnapshot? before, TextSnapshot? given)) in closed)
            {
                string at = made.Moved(path);
                if (given is not null)
                {
                    if (before is null)
                    {
                        created.Add(new Creation(at, given));
                    }
                    else
                    {
                        texts.Add(new TextChange(null, path, before, given, null));
                    }
                }
                else if (before is null)
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


        /// <summary>Where a file was as the command began, before the moves it made since.</summary>
        private string Began(string path) => Enumerable.Reverse(moves).Aggregate(path, (at, move) => PathNames.Moved(at, move.To, move.From));

        /// <summary>Where a file the command began with is once the moves it made since.</summary>
        private string Now(string began) => moves.Aggregate(began, (at, move) => PathNames.Moved(at, move.From, move.To));

        private void Keep(string began, TextSnapshot before, TextSnapshot after) =>
            closed[began] = (closed.TryGetValue(began, out (TextSnapshot? Before, TextSnapshot? After) watched) ? watched.Before : before, after);

        /// <summary>The files a write made kept for Undo: one it left as it was only when the command made it.</summary>
        private void Took(IEnumerable<ChangeMade> made)
        {
            foreach (ChangeMade file in made.Where(m => m.Changed || closed.ContainsKey(m.Began)))
            {
                Keep(file.Began, file.Before, file.After);
                madeAt[file.At] = file.Began;
            }
        }

        private void Unname()
        {
            naming.ForEach(n => n.Dispose());
            naming.Clear();
        }

        /// <summary>Makes queued changes of files not open on the calling thread, in order: each file read once, its changes
        /// run over its text in turn and written once, in its encoding, where it changed, <paramref name="wrote"/> told. The
        /// first the disk refused, with why, ends it.</summary>
        private static (List<ChangeMade> Made, (string Path, Exception Refused)? Refused) Make(IReadOnlyList<(string Began, string At, Func<string, string> Change)> files,
            Action<string> wrote)
        {
            List<ChangeMade> made = [];
            foreach (IGrouping<string, (string Began, string At, Func<string, string> Change)> file in files.GroupBy(f => f.Began, PathNames.Comparer))
            {
                string at = file.First().At;
                try
                {
                    if (FileText.Load(at) is not { } loaded)
                    {
                        continue;
                    }
                    string text = file.Aggregate(loaded.Text, (now, change) => change.Change(now));
                    bool changed = text != loaded.Text;
                    if (changed)
                    {
                        FileText.Save(at, text, loaded.Encoding, loaded.HasBom, loaded.CrBreaks);
                    }
                    TextSnapshot before = TextSnapshot.From(loaded.Text);
                    made.Add(new ChangeMade(file.Key, at, before, changed ? TextSnapshot.From(text) : before, changed));
                    wrote(at);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return (made, (at, error));
                }
            }
            return (made, null);
        }

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

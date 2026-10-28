namespace Passenger.Text;

/// <summary>Where a document's text was saved: the text then and its place in the history.</summary>
public readonly record struct SaveMark(TextSnapshot Text, long Place);

/// <summary>What an edit was, so consecutive edits of one kind can undo as one.</summary>
public enum EditKind
{
    Other,
    Typing,
    DeleteBackward,
    DeleteForward,
}

public enum ChangeReason
{
    Edit,
    Undo,
    Redo,
}

/// <summary>One change from <see cref="Before"/> to <see cref="After"/>. <see cref="Edits"/> are in
/// Before's coordinates, sorted and not overlapping; applied last to first, each is valid in the text
/// the previous one left, which is the order an LSP incremental sync sends them in.</summary>
public sealed class DocumentChangedEventArgs(TextSnapshot before, TextSnapshot after, IReadOnlyList<TextEdit> edits, int version, ChangeReason reason) : EventArgs
{
    public TextSnapshot Before { get; } = before;
    public TextSnapshot After { get; } = after;
    public IReadOnlyList<TextEdit> Edits { get; } = edits;
    public int Version { get; } = version;
    public ChangeReason Reason { get; } = reason;
}

/// <summary>A document's text, its history, its line ending and its anchors. Every change goes through
/// <see cref="Edit"/>, <see cref="Undo"/> or <see cref="Redo"/>, carries the anchors, and raises <see cref="ChangedFirst"/>
/// then <see cref="Changed"/> once per step, telling each handler apart: one that throws is reported as the IDE's error
/// (<see cref="Core.PassengerLog.Report"/>), and those after it still hear of the change.</summary>
public sealed class TextDocument
{
    /// <summary>What the undo history keeps at most, in bytes (<see cref="HistoryBytes"/>), but for its newest
    /// <see cref="KeptSteps"/>: about three thousand steps of a key each, or of a word typed.</summary>
    public const long KeptBytes = 1 << 20;

    /// <summary>The newest undo steps kept whatever they hold, so the last few changes of a large file stay undoable.</summary>
    public const int KeptSteps = 100;

    /// <summary>How long a pause still lets typing join the previous undo step.</summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(2);

    /// <summary>A place in the history no undo or redo comes back to.</summary>
    private const long Nowhere = -1;

    private readonly List<UndoEntry> undo = [];
    private readonly List<UndoEntry> redo = [];
    private readonly AnchorTree anchors = new();
    private readonly ChangeHandlers first = new();
    private readonly ChangeHandlers changed = new();
    private TextSnapshot saved;
    private bool mergeBroken = true;
    private long kept;
    private long lastId;

    /// <summary>The place before the oldest step kept: the text as loaded, or as the newest step dropped left it.</summary>
    private long oldest;

    /// <summary>The place the history held when the text was last saved, where an undo or redo gives back that very
    /// version, so the document reads as unmodified again.</summary>
    private long savedAt;

    public TextDocument(string text = "")
    {
        Snapshot = TextSnapshot.From(text);
        saved = Snapshot;
        LineEnding = DetectLineEnding(text);
    }

    /// <summary>Raised on the thread that made the change, before the change returns: a document an editor shows is
    /// changed on the UI thread alone.</summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed
    {
        add => changed.Add(value);
        remove => changed.Remove(value);
    }

    /// <summary>Raised for each change before <see cref="Changed"/>, for what keeps a view of the text in step with it,
    /// so a handler of Changed that asks the view where a line is drawn finds it carried through the change rather than
    /// made again for the whole text.</summary>
    public event EventHandler<DocumentChangedEventArgs>? ChangedFirst
    {
        add => first.Add(value);
        remove => first.Remove(value);
    }

    public TextSnapshot Snapshot { get; private set; }

    /// <summary>Counts every change, undo and redo included; the language server's document version.</summary>
    public int Version { get; private set; }

    /// <summary>What Enter inserts: the file's own ending as loaded, "\n" for a new file.</summary>
    public string LineEnding { get; set; }

    public bool IsModified => !ReferenceEquals(Snapshot, saved);

    /// <summary>Whether the text refuses every change while it is set: an edit, an undo or redo, or a console's own each
    /// leaves it as it is. Its owner (a file shown read-only) lifts it only for what the text must take.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>The text as last loaded or saved: what the file on disk holds, unless something else changed it.</summary>
    public TextSnapshot Saved => saved;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>About what the undo and redo steps keep, in bytes: the text each took out and put in, and its carets.</summary>
    public long HistoryBytes => kept;

    /// <summary>The step the next <see cref="Undo"/> takes back, or null: the same object for as long as it is that step,
    /// so a change made in several files at once (a rename) knows whether it is still each one's latest.</summary>
    public object? NextUndo => undo.Count > 0 ? undo[^1] : null;

    /// <summary>The step the next <see cref="Redo"/> makes again, or null, as <see cref="NextUndo"/> is.</summary>
    public object? NextRedo => redo.Count > 0 ? redo[^1] : null;

    /// <summary>How many anchors the document carries.</summary>
    public int Anchors => anchors.Count;

    /// <summary>A place the document carries through its changes from now on, until <see cref="Anchor.Remove"/>.</summary>
    public Anchor AnchorAt(int offset, AnchorGravity gravity)
    {
        Anchor anchor = new(gravity);
        anchors.Add(anchor, Math.Clamp(offset, 0, Snapshot.Length));
        return anchor;
    }

    /// <summary>A stretch the document carries through its changes from now on, its ends of the gravities given; with
    /// <paramref name="empty"/>, both ends take that gravity once it is empty.</summary>
    public AnchoredRange RangeAt(TextRange range, AnchorGravity start, AnchorGravity end, AnchorGravity? empty = null) =>
        new(AnchorAt(range.Start, start), AnchorAt(range.End, end), empty);

    /// <summary>A stretch carried as a mark on the text is (a squiggle, a match, a use): what is typed at its start stays
    /// out, what is typed at its end goes in while it holds text, and once its text is gone it stays empty.</summary>
    public AnchoredRange MarkAt(TextRange range) => RangeAt(range, AnchorGravity.After, AnchorGravity.After, empty: AnchorGravity.Before);

    public static string DetectLineEnding(string text)
    {
        int lf = text.IndexOf('\n');
        return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>Applies non-overlapping <paramref name="edits"/> (in the current snapshot's
    /// coordinates) as one undo step, merged with the last if it continues the same kind of edit; none while
    /// <see cref="IsReadOnly"/>.</summary>
    public void Edit(IReadOnlyList<TextEdit> edits, SelectionSet selectionsBefore, SelectionSet selectionsAfter, EditKind kind = EditKind.Other)
    {
        if (IsReadOnly)
        {
            return;
        }
        TextEdit[] sorted = Sorted(edits, apart: true);
        if (sorted.Length == 0)
        {
            return;
        }

        TextSnapshot before = Snapshot;
        string[] removed = Removed(before, sorted);
        TextSnapshot after = before.Apply(sorted);

        DropRedo();
        long now = Environment.TickCount64;
        if (undo.Count > 0 && Continues(undo[^1], kind, selectionsBefore, sorted, now))
        {
            UndoEntry last = undo[^1];
            // The text saved lies inside the step now, where no undo or redo stops.
            if (savedAt == last.Id)
            {
                savedAt = Nowhere;
            }
            kept -= last.Bytes;
            last.Continue(sorted, removed, selectionsAfter, now);
            kept += last.Bytes;
        }
        else
        {
            Keep(new UndoEntry(++lastId, kind, Carried(selectionsBefore), selectionsAfter, now, sorted, removed));
        }
        mergeBroken = false;
        Raise(before, after, sorted, ChangeReason.Edit);
    }

    /// <summary>Changes the text, or only something kept beside it (how its file is stored), as an undoable step of its
    /// own, even with no edits: <paramref name="undone"/> runs once Undo has put the text back, <paramref name="redone"/>
    /// once Redo has made the edits again.</summary>
    public void Perform(IReadOnlyList<TextEdit> edits, SelectionSet selectionsBefore, SelectionSet selectionsAfter, Action undone, Action redone)
    {
        if (IsReadOnly)
        {
            return;
        }
        TextEdit[] sorted = Sorted(edits, apart: false);
        TextSnapshot before = Snapshot;
        string[] removed = Removed(before, sorted);
        DropRedo();
        Keep(new UndoEntry(++lastId, EditKind.Other, Carried(selectionsBefore), selectionsAfter, Environment.TickCount64, sorted, removed)
        {
            Undone = undone,
            Redone = redone,
        });
        mergeBroken = true;
        if (sorted.Length > 0)
        {
            Raise(before, before.Apply(sorted), sorted, ChangeReason.Edit);
        }
    }

    /// <summary>Changes the whole text to another (a reload from disk, a replace across files) as one undoable step of
    /// one edit per changed stretch of lines, so carets, breakpoints and folds outside them stay where they were.</summary>
    public void SetText(string text, SelectionSet selectionsBefore)
    {
        IReadOnlyList<TextEdit> edits = LineDiff.Edits(Snapshot.ToString(), text);
        BreakUndoGroup();
        Edit(edits, selectionsBefore, TextEditing.MapThrough(selectionsBefore, edits).Clamp(text.Length));
        BreakUndoGroup();
    }

    /// <summary>Adds text at the end with no undo step, as a console's output arrives: nobody undoes
    /// what a program printed, and keeping it would hold every line twice.</summary>
    public void Append(string text)
    {
        if (text.Length == 0 || IsReadOnly)
        {
            return;
        }
        TextSnapshot before = Snapshot;
        TextEdit edit = new(before.Length, 0, text);
        Raise(before, before.Replace(before.Length, 0, text), [edit], ChangeReason.Edit);
        MarkSaved();
    }

    /// <summary>Changes a stretch with no undo step, as a console's own edits are (its oldest output dropped, output put
    /// before the line a person is typing): any history no longer lines up with the text, so it goes.</summary>
    public void Rewrite(int start, int length, string text)
    {
        if ((length == 0 && text.Length == 0) || IsReadOnly)
        {
            return;
        }
        TextSnapshot before = Snapshot;
        Forget();
        Raise(before, before.Replace(start, length, text), [new TextEdit(start, length, text)], ChangeReason.Edit);
        MarkSaved();
    }

    /// <summary>Empties the document with no undo step (a console cleared), forgetting its history.</summary>
    public void Clear()
    {
        if (IsReadOnly)
        {
            return;
        }
        TextSnapshot before = Snapshot;
        Forget();
        if (before.Length > 0)
        {
            Raise(before, TextSnapshot.Empty, [new TextEdit(0, before.Length, string.Empty)], ChangeReason.Edit);
        }
        MarkSaved();
    }

    /// <summary>Stops the next edit joining the current undo step: a caret move, a click, a focus
    /// change.</summary>
    public void BreakUndoGroup() => mergeBroken = true;

    /// <summary>Undoes the last step and returns the carets as they were before it, or null if there
    /// was nothing to undo or the text is read-only.</summary>
    public SelectionSet? Undo()
    {
        if (undo.Count == 0 || IsReadOnly)
        {
            return null;
        }
        UndoEntry entry = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        // On the other stack before anyone hears of the change, so a handler that throws never loses the step.
        redo.Add(entry);
        mergeBroken = true;
        if (entry.Edits.Length > 0)
        {
            TextSnapshot before = Snapshot;
            TextEdit[] inverse = entry.Inverse();
            Raise(before, Place == savedAt ? saved : before.Apply(inverse), inverse, ChangeReason.Undo);
        }
        entry.Undone?.Invoke();
        return entry.SelectionsBefore;
    }

    public SelectionSet? Redo()
    {
        if (redo.Count == 0 || IsReadOnly)
        {
            return null;
        }
        UndoEntry entry = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(entry);
        mergeBroken = true;
        if (entry.Edits.Length > 0)
        {
            TextSnapshot before = Snapshot;
            Raise(before, Place == savedAt ? saved : before.Apply(entry.Edits), entry.Edits, ChangeReason.Redo);
        }
        entry.Redone?.Invoke();
        return entry.SelectionsAfter;
    }

    public void MarkSaved()
    {
        saved = Snapshot;
        savedAt = Place;
    }

    /// <summary>Where the text was last loaded or saved, which <see cref="MarkSaved(SaveMark)"/> puts back.</summary>
    public SaveMark LastSave => new(saved, savedAt);

    /// <summary>Takes the text as saved where an earlier mark says, for a save taken for done that never reached the disk.</summary>
    public void MarkSaved(SaveMark mark)
    {
        saved = mark.Text;
        savedAt = mark.Place;
    }

    /// <summary>Where the history stands: the step the next undo takes back, or the place before the oldest kept.</summary>
    private long Place => undo.Count > 0 ? undo[^1].Id : oldest;

    /// <summary>The edits that change something, sorted by where they start (those at one place kept in their order),
    /// and refused when <paramref name="apart"/> and two overlap.</summary>
    private static TextEdit[] Sorted(IReadOnlyList<TextEdit> edits, bool apart)
    {
        int count = 0;
        bool ordered = true;
        int last = int.MinValue;
        for (int i = 0; i < edits.Count; i++)
        {
            TextEdit edit = edits[i];
            if (edit.Length > 0 || edit.Text.Length > 0)
            {
                count++;
                ordered &= edit.Start >= last;
                last = edit.Start;
            }
        }
        TextEdit[] sorted = new TextEdit[count];
        long[] order = ordered ? [] : new long[count];
        int at = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            TextEdit edit = edits[i];
            if (edit.Length > 0 || edit.Text.Length > 0)
            {
                if (!ordered)
                {
                    order[at] = ((long)edit.Start << 32) | (uint)at;
                }
                sorted[at++] = edit;
            }
        }
        if (!ordered)
        {
            Array.Sort(order, sorted);
        }
        for (int i = 1; apart && i < sorted.Length; i++)
        {
            if (sorted[i].Start < sorted[i - 1].End)
            {
                throw new ArgumentException("Edits overlap.", nameof(edits));
            }
        }
        return sorted;
    }

    private static string[] Removed(TextSnapshot text, TextEdit[] edits)
    {
        string[] removed = new string[edits.Length];
        for (int i = 0; i < edits.Length; i++)
        {
            removed[i] = text.GetText(edits[i].Start, edits[i].Length);
        }
        return removed;
    }

    /// <summary>The carets a new step starts from: the last step's own set when they are the same, so it is kept once.</summary>
    private SelectionSet Carried(SelectionSet selections) =>
        undo.Count > 0 && undo[^1].SelectionsAfter.SameAs(selections) ? undo[^1].SelectionsAfter : selections;

    /// <summary>Adds a step, then drops the oldest while the history keeps more than <see cref="KeptBytes"/>, the newest
    /// <see cref="KeptSteps"/> kept whatever they hold.</summary>
    private void Keep(UndoEntry entry)
    {
        undo.Add(entry);
        kept += entry.Bytes;
        int drop = 0;
        long over = kept - KeptBytes;
        while (over > 0 && undo.Count - drop > KeptSteps)
        {
            UndoEntry dropped = undo[drop++];
            over -= dropped.Bytes;
            kept -= dropped.Bytes;
            if (savedAt == oldest)
            {
                savedAt = Nowhere;
            }
            oldest = dropped.Id;
        }
        undo.RemoveRange(0, drop);
    }

    /// <summary>Forgets the steps a new one leaves no way to redo.</summary>
    private void DropRedo()
    {
        foreach (UndoEntry entry in redo)
        {
            kept -= entry.Bytes;
            if (savedAt == entry.Id)
            {
                savedAt = Nowhere;
            }
        }
        redo.Clear();
    }

    /// <summary>Forgets every step, the text as it stands the history's start.</summary>
    private void Forget()
    {
        undo.Clear();
        redo.Clear();
        kept = 0;
        oldest = ++lastId;
    }

    private bool Continues(UndoEntry last, EditKind kind, SelectionSet selectionsBefore, TextEdit[] edits, long now)
    {
        if (mergeBroken || kind == EditKind.Other || last.Kind != kind || now - last.Time > MergeWindow.TotalMilliseconds)
        {
            return false;
        }
        if (!last.SelectionsAfter.SameAs(selectionsBefore))
        {
            return false;
        }
        // Typing undoes a word at a time: a space or a line break after other text starts a new step.
        if (kind == EditKind.Typing)
        {
            string typed = edits[0].Text;
            bool startsGap = typed.Length > 0 && char.IsWhiteSpace(typed[0]);
            if ((startsGap && !last.EndsGap) || typed.Contains('\n'))
            {
                return false;
            }
        }
        return true;
    }

    private void Raise(TextSnapshot before, TextSnapshot after, IReadOnlyList<TextEdit> edits, ChangeReason reason)
    {
        Snapshot = after;
        Version++;
        DocumentChangedEventArgs change = new(before, after, SortedEdits.Of(edits), Version, reason);
        anchors.Apply(change.Edits);
        first.Raise(this, change);
        changed.Raise(this, change);
    }
}

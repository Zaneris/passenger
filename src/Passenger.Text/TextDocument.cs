namespace Passenger.Text;

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
    /// <summary>How long a pause still lets typing join the previous undo step.</summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(2);

    private readonly List<UndoEntry> undo = [];
    private readonly List<UndoEntry> redo = [];
    private readonly AnchorTree anchors = new();
    private readonly ChangeHandlers first = new();
    private readonly ChangeHandlers changed = new();
    private TextSnapshot saved;
    private bool mergeBroken = true;

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

    /// <summary>The text as last loaded or saved: what the file on disk holds, unless something else changed it.</summary>
    public TextSnapshot Saved => saved;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

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
    public AnchoredRange MarkAt(TextRange range) => RangeAt(range, AnchorGravity.Before, AnchorGravity.After, empty: AnchorGravity.Before);

    public static string DetectLineEnding(string text)
    {
        int lf = text.IndexOf('\n');
        return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>Applies non-overlapping <paramref name="edits"/> (in the current snapshot's
    /// coordinates) as one undo step, merged with the last if it continues the same kind of edit.</summary>
    public void Edit(IReadOnlyList<TextEdit> edits, SelectionSet selectionsBefore, SelectionSet selectionsAfter, EditKind kind = EditKind.Other)
    {
        TextEdit[] sorted = edits.Where(e => e.Length > 0 || e.Text.Length > 0).OrderBy(e => e.Start).ToArray();
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Start < sorted[i - 1].End)
            {
                throw new ArgumentException("Edits overlap.", nameof(edits));
            }
        }
        if (sorted.Length == 0)
        {
            return;
        }

        TextSnapshot before = Snapshot;
        string[] removed = sorted.Select(e => before.GetText(e.Start, e.Length)).ToArray();
        TextSnapshot after = before.Apply(sorted);
        Step step = new(before, after, sorted, removed);

        redo.Clear();
        long now = Environment.TickCount64;
        if (undo.Count > 0 && Continues(undo[^1], kind, selectionsBefore, sorted, now))
        {
            UndoEntry last = undo[^1];
            last.Steps.Add(step);
            last.SelectionsAfter = selectionsAfter;
            last.Time = now;
        }
        else
        {
            undo.Add(new UndoEntry(kind, selectionsBefore, selectionsAfter, now, [step]));
        }
        mergeBroken = false;
        Raise(before, after, sorted, ChangeReason.Edit);
    }

    /// <summary>Changes the text, or only something kept beside it (how its file is stored), as an undoable step of its
    /// own, even with no edits: <paramref name="undone"/> runs once Undo has put the text back, <paramref name="redone"/>
    /// once Redo has made the edits again.</summary>
    public void Perform(IReadOnlyList<TextEdit> edits, SelectionSet selectionsBefore, SelectionSet selectionsAfter, Action undone, Action redone)
    {
        TextEdit[] sorted = edits.Where(e => e.Length > 0 || e.Text.Length > 0).OrderBy(e => e.Start).ToArray();
        TextSnapshot before = Snapshot;
        List<Step> steps = [];
        if (sorted.Length > 0)
        {
            steps.Add(new Step(before, before.Apply(sorted), sorted, [.. sorted.Select(e => before.GetText(e.Start, e.Length))]));
        }
        redo.Clear();
        undo.Add(new UndoEntry(EditKind.Other, selectionsBefore, selectionsAfter, Environment.TickCount64, steps) { Undone = undone, Redone = redone });
        mergeBroken = true;
        if (steps.Count > 0)
        {
            Raise(before, steps[0].After, sorted, ChangeReason.Edit);
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
        if (text.Length == 0)
        {
            return;
        }
        TextSnapshot before = Snapshot;
        TextEdit edit = new(before.Length, 0, text);
        Raise(before, before.Replace(before.Length, 0, text), [edit], ChangeReason.Edit);
        saved = Snapshot;
    }

    /// <summary>Changes a stretch with no undo step, as a console's own edits are (its oldest output dropped, output put
    /// before the line a person is typing): any history no longer lines up with the text, so it goes.</summary>
    public void Rewrite(int start, int length, string text)
    {
        if (length == 0 && text.Length == 0)
        {
            return;
        }
        TextSnapshot before = Snapshot;
        undo.Clear();
        redo.Clear();
        Raise(before, before.Replace(start, length, text), [new TextEdit(start, length, text)], ChangeReason.Edit);
        saved = Snapshot;
    }

    /// <summary>Empties the document with no undo step (a console cleared), forgetting its history.</summary>
    public void Clear()
    {
        TextSnapshot before = Snapshot;
        undo.Clear();
        redo.Clear();
        if (before.Length > 0)
        {
            Raise(before, TextSnapshot.Empty, [new TextEdit(0, before.Length, string.Empty)], ChangeReason.Edit);
        }
        saved = Snapshot;
    }

    /// <summary>Stops the next edit joining the current undo step: a caret move, a click, a focus
    /// change.</summary>
    public void BreakUndoGroup() => mergeBroken = true;

    /// <summary>Undoes the last step and returns the carets as they were before it, or null if there
    /// was nothing to undo.</summary>
    public SelectionSet? Undo()
    {
        if (undo.Count == 0)
        {
            return null;
        }
        UndoEntry entry = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        // On the other stack before anyone hears of the change, so a handler that throws never loses the step.
        redo.Add(entry);
        mergeBroken = true;
        for (int i = entry.Steps.Count - 1; i >= 0; i--)
        {
            Step step = entry.Steps[i];
            Snapshot = step.Before;
            Raise(step.After, step.Before, step.Inverse(), ChangeReason.Undo);
        }
        entry.Undone?.Invoke();
        return entry.SelectionsBefore;
    }

    public SelectionSet? Redo()
    {
        if (redo.Count == 0)
        {
            return null;
        }
        UndoEntry entry = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add(entry);
        mergeBroken = true;
        foreach (Step step in entry.Steps)
        {
            Snapshot = step.After;
            Raise(step.Before, step.After, step.Edits, ChangeReason.Redo);
        }
        entry.Redone?.Invoke();
        return entry.SelectionsAfter;
    }

    public void MarkSaved() => saved = Snapshot;

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
            string previous = last.Steps[^1].Edits[0].Text;
            bool startsGap = typed.Length > 0 && char.IsWhiteSpace(typed[0]);
            bool endedGap = previous.Length > 0 && char.IsWhiteSpace(previous[^1]);
            if ((startsGap && !endedGap) || typed.Contains('\n'))
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

    private sealed class UndoEntry(EditKind kind, SelectionSet selectionsBefore, SelectionSet selectionsAfter, long time, List<Step> steps)
    {
        public EditKind Kind { get; } = kind;
        public SelectionSet SelectionsBefore { get; } = selectionsBefore;
        public SelectionSet SelectionsAfter { get; set; } = selectionsAfter;
        public long Time { get; set; } = time;
        public List<Step> Steps { get; } = steps;

        /// <summary>What <see cref="Perform"/> was given to run after the step is undone, and after it is redone.</summary>
        public Action? Undone { get; init; }
        public Action? Redone { get; init; }
    }

    /// <summary>One applied batch: the snapshots on either side, the edits and the text each one
    /// removed, which is what its inverse puts back.</summary>
    private sealed record Step(TextSnapshot Before, TextSnapshot After, TextEdit[] Edits, string[] Removed)
    {
        public TextEdit[] Inverse()
        {
            TextEdit[] inverse = new TextEdit[Edits.Length];
            int shift = 0;
            for (int i = 0; i < Edits.Length; i++)
            {
                TextEdit edit = Edits[i];
                inverse[i] = new TextEdit(edit.Start + shift, edit.Text.Length, Removed[i]);
                shift += edit.Text.Length - edit.Length;
            }
            return inverse;
        }
    }
}

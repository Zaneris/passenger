using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

public sealed partial class CodeEditor
{
    /// <summary>Text the last whole-line copy put on the clipboard, shared by every editor of every window, as the
    /// clipboard it marks is the system's: pasting exactly that text pastes it as lines, above the caret's line.</summary>
    private static string? lineCopy;

    /// <summary>Runs an action from <see cref="EditorActions"/>; <paramref name="extend"/> makes a
    /// movement extend the selection. Returns false for an action that is not the editor's, or one
    /// with nothing to do that a host may want instead (Escape with one plain caret).</summary>
    public bool TryExecute(string action, bool extend = false)
    {
        formattedKey = null;
        TextSnapshot text = document.Snapshot;
        if (RunChoiceKey(action) || RunSnippetKey(action) || RunFolding(action))
        {
            return true;
        }
        switch (action)
        {
            case EditorActions.Left:
                StepHorizontally(text, forward: false, extend);
                return true;
            case EditorActions.Right:
                StepHorizontally(text, forward: true, extend);
                return true;
            case EditorActions.WordLeft:
                Move(s => TextNavigation.PreviousWord(text, s.Caret), extend);
                return true;
            case EditorActions.WordRight:
                Move(s => TextNavigation.NextWord(text, s.Caret, WordStop), extend);
                return true;
            case EditorActions.LineStart:
                MoveToRowStart(extend);
                return true;
            case EditorActions.LineEnd:
                MoveToRowEnd(extend);
                return true;
            case EditorActions.TextStart:
                Move(_ => 0, extend);
                return true;
            case EditorActions.TextEnd:
                Move(_ => text.Length, extend);
                return true;
            case EditorActions.Up:
                MoveVertically(-1, extend);
                return true;
            case EditorActions.Down:
                MoveVertically(1, extend);
                return true;
            case EditorActions.PageUp:
                ScrollTo(scroll - new Vector(0, PageLines * metrics.LineHeight));
                MoveVertically(-PageLines, extend);
                return true;
            case EditorActions.PageDown:
                ScrollTo(scroll + new Vector(0, PageLines * metrics.LineHeight));
                MoveVertically(PageLines, extend);
                return true;
            case EditorActions.ScrollUp:
                ScrollTo(scroll - new Vector(0, metrics.LineHeight));
                return true;
            case EditorActions.ScrollDown:
                ScrollTo(scroll + new Vector(0, metrics.LineHeight));
                return true;
            case EditorActions.SelectAll:
                SetSelections(SelectionSet.Single(new Selection(0, text.Length)), reveal: false);
                return true;
            case EditorActions.SelectNextOccurrence:
                if (selections.Count >= SelectionSet.MaxCount && selections.Items.All(s => !s.IsEmpty))
                {
                    CaretLimitReached?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                bool fromCarets = selections.Items.Any(s => s.IsEmpty);
                bool wholeWords = ReferenceEquals(selections, wordOccurrences);
                SetSelections(SelectionCommands.SelectNextOccurrence(text, selections, wholeWords), reveal: true);
                wordOccurrences = fromCarets || wholeWords ? selections : null;
                return true;
            case EditorActions.UnselectOccurrence:
                bool ofWords = ReferenceEquals(selections, wordOccurrences);
                SetSelections(SelectionCommands.UnselectLast(selections), reveal: true);
                wordOccurrences = ofWords ? selections : null;
                return true;
            case EditorActions.SelectAllOccurrences:
                SetSelections(SelectionCommands.SelectAllOccurrences(text, selections, ReferenceEquals(selections, wordOccurrences), out bool every), reveal: false);
                if (every)
                {
                    CaretLimitReached?.Invoke(this, EventArgs.Empty);
                }
                return true;
            case EditorActions.CaretPerLine:
                SetSelections(SelectionCommands.CaretPerLine(text, selections, out bool lines), reveal: false);
                if (lines)
                {
                    CaretLimitReached?.Invoke(this, EventArgs.Empty);
                }
                return true;
            case EditorActions.CloneCaretAbove:
                CloneCaret(-1);
                return true;
            case EditorActions.CloneCaretBelow:
                CloneCaret(1);
                return true;
            case EditorActions.Escape:
                if (selections.HasMultiple)
                {
                    SetSelections(selections.OnlyPrimary(), reveal: true);
                    return true;
                }
                if (!selections.Primary.IsEmpty)
                {
                    SetSelections(SelectionSet.Single(selections.Primary.Caret), reveal: true);
                    return true;
                }
                if (UsagesKept)
                {
                    SetUsageHighlights([]);
                    return true;
                }
                return false;
            case EditorActions.MatchBrace:
                MatchBrace();
                return true;
            case EditorActions.Copy:
                CopyToClipboard();
                return true;
            case EditorActions.Undo:
                if (document.Undo() is { } before)
                {
                    SetSelectionsQuietly(before);
                }
                return true;
            case EditorActions.Redo:
                if (document.Redo() is { } after)
                {
                    SetSelectionsQuietly(after);
                }
                return true;
        }

        if (IsReadOnly)
        {
            return IsEdit(action);
        }
        string eol = document.LineEnding;
        if (action is EditorActions.NewLine or EditorActions.Indent or EditorActions.Paste or EditorActions.PasteAsPlainText)
        {
            LeaveEndTag();
            text = document.Snapshot;
        }
        switch (action)
        {
            case EditorActions.Backspace:
                if (!StepsBackOverEndTag())
                {
                    Apply(TextEditing.Backspace(document.Snapshot, selections, Indent));
                }
                return true;
            case EditorActions.Delete:
                Apply(TextEditing.Delete(text, selections));
                return true;
            case EditorActions.DeleteWordLeft:
                Apply(TextEditing.DeleteWord(text, selections, backward: true, WordStop));
                return true;
            case EditorActions.DeleteWordRight:
                Apply(TextEditing.DeleteWord(text, selections, backward: false, WordStop));
                return true;
            case EditorActions.NewLine:
                NewLine(TextEditing.NewLine(text, selections, eol, Indent, Markup));
                return true;
            case EditorActions.StartNewLine:
                Apply(TextEditing.StartNewLine(text, selections, eol, Indent, above: false, Markup));
                return true;
            case EditorActions.StartNewLineBefore:
                Apply(TextEditing.StartNewLine(text, selections, eol, Indent, above: true));
                return true;
            case EditorActions.Indent:
                if (!StepsOverTypedCloser())
                {
                    Apply(TextEditing.Indent(text, selections, Indent));
                }
                return true;
            case EditorActions.Outdent:
                Apply(TextEditing.Outdent(text, selections, Indent));
                return true;
            case EditorActions.Duplicate:
                Apply(TextEditing.Duplicate(text, selections, eol, Folding.Hidden));
                return true;
            case EditorActions.DeleteLine:
                Apply(TextEditing.DeleteLines(text, selections, Folding.Hidden));
                return true;
            case EditorActions.MoveLineUp:
                Apply(TextEditing.MoveLines(text, selections, eol, up: true, Folding.Hidden));
                return true;
            case EditorActions.MoveLineDown:
                Apply(TextEditing.MoveLines(text, selections, eol, up: false, Folding.Hidden));
                return true;
            case EditorActions.ToggleLineComment:
                Apply(LineComments(text, add: null));
                return true;
            case EditorActions.ToggleBlockComment:
                Apply(BlockComment is { } block ? TextEditing.ToggleBlockComment(text, selections, block.Start, block.End) : null);
                return true;
            case EditorActions.Comment or EditorActions.Uncomment:
                Apply(LineComments(text, add: action == EditorActions.Comment));
                return true;
            case EditorActions.JoinLines:
                Apply(TextEditing.JoinLines(text, selections));
                return true;
            case EditorActions.ToggleCase:
                Apply(TextEditing.ToggleCase(text, selections));
                return true;
            case EditorActions.CutToLineEnd:
                (EditPlan? plan, string removed) = TextEditing.CutToLineEnd(text, selections);
                if (plan is not null)
                {
                    SetClipboard(removed, wholeLines: false);
                    Apply(plan);
                }
                return true;
            case EditorActions.Cut:
                CopyToClipboard();
                Apply(TextEditing.Cut(text, selections, Folding.Hidden));
                return true;
            case EditorActions.Paste:
                BackgroundWork.Track(PasteFromClipboardAsync(plain: false));
                return true;
            case EditorActions.PasteAsPlainText:
                BackgroundWork.Track(PasteFromClipboardAsync(plain: true));
                return true;
            case EditorActions.ExpandWord or EditorActions.ExpandWordBackward:
                ExpandWord(backward: action == EditorActions.ExpandWordBackward);
                return true;
        }
        return false;
    }

    /// <summary>Applies edits made outside the keyboard (a replace, a fix from the language server)
    /// as one undo step, the carets carried through them or set to <paramref name="after"/>.</summary>
    public void ApplyEdits(IReadOnlyList<TextEdit> edits, SelectionSet? after = null)
    {
        if (edits.Count == 0 || IsReadOnly)
        {
            return;
        }
        List<TextEdit> sorted = edits.OrderBy(e => e.Start).ToList();
        document.BreakUndoGroup();
        Apply(new EditPlan(sorted, after ?? TextEditing.MapThrough(selections, sorted), EditKind.Other));
        document.BreakUndoGroup();
    }

    /// <summary>Types text at every caret as if from the keyboard, the editor's typing help included.</summary>
    public void Type(string typed) => Apply(TextEditing.Type(document.Snapshot, selections, typed, ClosesWhole(typed)));

    /// <summary>The highlighter's word whether a closer is its code's own (<see cref="IBraceMatcher.ClosesWhole"/>), for a
    /// closer typed; null for anything else typed, or where no highlighter tells.</summary>
    private Func<int, bool>? ClosesWhole(string typed)
    {
        if (typed is not (")" or "]" or "}") || highlighter is not IBraceMatcher matcher)
        {
            return null;
        }
        TextSnapshot text = document.Snapshot;
        return at => matcher.ClosesWhole(text, at);
    }

    private int PageLines => Math.Max(1, (int)(Viewport.Height / metrics.LineHeight) - 1);

    private void Apply(EditPlan? plan)
    {
        if (plan is null || IsReadOnly)
        {
            return;
        }
        IReadOnlyList<FoldRegion> foldsBefore = FoldRegions;
        editing = true;
        try
        {
            document.Edit(plan.Edits, selections, plan.Selections, StepOf(plan.Kind));
        }
        finally
        {
            editing = false;
        }
        KeepSplitBlocks(plan);
        KeepTypedClosers(plan);
        FollowMoves(foldsBefore, plan);
        // A caret the edit leaves where a row wraps stands after the text it typed, at the end of the row before.
        nextWrapLeaning = CaretsAtWraps(plan.Selections);
        SetSelections(plan.Selections, reveal: true);
    }

    private void Move(Func<Selection, int> to, bool extend)
    {
        document.BreakUndoGroup();
        SetSelections(selections.With(s => s.MoveTo(to(s), extend)), reveal: true);
    }

    /// <summary>Up and Down go by rows of text, a wrapped line's included and the rows above lines passed by, and keep
    /// returning to the x a vertical run of moves started from, even through shorter rows.</summary>
    private void MoveVertically(int rows, bool extend)
    {
        document.BreakUndoGroup();
        TextSnapshot text = document.Snapshot;
        HashSet<int> lean = [];
        SelectionSet moved = selections.With(s =>
        {
            (int row, double caretX) = Place(text, s.Caret, LeansBack(s.Caret));
            double x = double.IsNaN(s.PreferredX) ? caretX : s.PreferredX;
            int target = TextRowsAway(row, rows);
            int offset;
            if (target < 0)
            {
                offset = 0;
            }
            else if (target >= RowCount)
            {
                offset = text.Length;
            }
            else
            {
                offset = OffsetAtRow(text, target, x, out bool before);
                if (before)
                {
                    lean.Add(offset);
                }
            }
            return s.MoveTo(offset, extend) with { PreferredX = x };
        });
        nextWrapLeaning = lean;
        SetSelections(moved, reveal: true);
    }

    private void CloneCaret(int direction)
    {
        TextSnapshot text = document.Snapshot;
        if (selections.Count >= SelectionSet.MaxCount)
        {
            CaretLimitReached?.Invoke(this, EventArgs.Empty);
            return;
        }
        Selection edge = direction < 0 ? selections.Items[0] : selections.Items[^1];
        (int at, double caretX) = Place(text, edge.Caret, LeansBack(edge.Caret));
        int row = TextRowsAway(at, direction);
        if (row < 0 || row >= RowCount)
        {
            return;
        }
        double x = double.IsNaN(edge.PreferredX) ? caretX : edge.PreferredX;
        int offset = OffsetAtRow(text, row, x, out bool before);
        // The carets there stand where they did, the new one before its wrap when it lands at a row's end.
        HashSet<int> lean = [.. wrapLeaning];
        if (before)
        {
            lean.Add(offset);
        }
        nextWrapLeaning = lean;
        SetSelections(selections.Add(Selection.At(offset) with { PreferredX = x }), reveal: true);
    }

    /// <summary>The touched lines commented or uncommented with the language's line comment, or wrapped in its block
    /// comment where it has none; the separate Comment and Uncomment actions (<paramref name="add"/> given) only ever
    /// add or only ever remove, unlike the toggle.</summary>
    private EditPlan? LineComments(TextSnapshot text, bool? add) =>
        LineCommentPrefix is { } prefix ? TextEditing.ToggleLineComment(text, selections, prefix, add: add)
        : BlockComment is { } block ? TextEditing.ToggleLineComment(text, selections, block.Start, block.End, add)
        : null;

    private void CopyToClipboard()
    {
        (string copied, bool wholeLines) = TextEditing.Copy(document.Snapshot, selections, document.LineEnding, Folding.Hidden);
        SetClipboard(copied, wholeLines);
    }

    private void SetClipboard(string copied, bool wholeLines)
    {
        lineCopy = wholeLines ? copied : null;
        Copied?.Invoke(this, new CopiedText(copied, wholeLines));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            UnawaitedWork.Observe(clipboard.SetTextAsync(copied));
        }
    }

    /// <summary>The clipboard's text pasted at the carets, whole lines above the caret's line where this IDE copied it as
    /// them; reindented as the language reindents a paste unless <paramref name="plain"/>.</summary>
    private async Task PasteFromClipboardAsync(bool plain)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } pasted)
        {
            return;
        }
        string normalized = pasted.Replace("\r\n", "\n");
        bool wholeLines = lineCopy is not null && normalized == lineCopy.Replace("\r\n", "\n");
        Apply(TextEditing.Paste(document.Snapshot, selections, pasted, wholeLines, document.LineEnding, ReindentsPaste && !plain ? Indent : null));
    }

    /// <summary>Text pasted at the carets as Paste pastes the clipboard's, whole lines above the caret's line where it was
    /// copied as them, and put on the clipboard; Paste from History's choice. A read-only editor refuses it.</summary>
    public void PasteText(string pasted, bool wholeLines)
    {
        if (IsReadOnly)
        {
            return;
        }
        SetClipboard(pasted, wholeLines);
        LeaveEndTag();
        Apply(TextEditing.Paste(document.Snapshot, selections, pasted, wholeLines, document.LineEnding, ReindentsPaste ? Indent : null));
    }

    /// <summary>Cyclic Expand Word's last step: where the word it finishes begins, as typed, the round of words it steps
    /// through (<see cref="WordExpansion"/>), the one it shows (0 for the word as typed), and the text's version and caret
    /// it left, so a press at once after steps on from there.</summary>
    private sealed record Expansion(int Start, string Begun, IReadOnlyList<string> Words, int Index, int Version, int Caret);

    private Expansion? expansion;

    /// <summary>The other texts whose words Cyclic Expand Word offers after this one's above the caret: the other open files'.</summary>
    public Func<IEnumerable<string>>? WordsElsewhere { get; set; }

    /// <summary>The word begun at the caret finished as the next word of its round, or the one before; each press one more
    /// step of the same undo, the word as typed coming round again after the last.</summary>
    private void ExpandWord(bool backward)
    {
        if (selections.HasMultiple || !selections.Primary.IsEmpty)
        {
            return;
        }
        int caret = selections.Primary.Caret;
        Expansion? going = expansion is { } last && last.Version == document.Version && last.Caret == caret ? last : null;
        if (going is null)
        {
            if (WordExpansion.At(document.Snapshot.ToString(), caret, WordsElsewhere?.Invoke() ?? []) is not { Words.Count: > 0 } found)
            {
                return;
            }
            going = new Expansion(found.Start, found.Begun, found.Words, 0, 0, 0);
        }
        int round = going.Words.Count + 1;
        int index = (((going.Index + (backward ? -1 : 1)) % round) + round) % round;
        string word = index == 0 ? going.Begun : going.Words[index - 1];
        Apply(new EditPlan([new TextEdit(going.Start, caret - going.Start, word)], SelectionSet.Single(going.Start + word.Length), EditKind.Joined));
        expansion = going with { Index = index, Version = document.Version, Caret = going.Start + word.Length };
    }

    private static bool IsEdit(string action) => action.StartsWith("editor.", StringComparison.Ordinal);
}

/// <summary>Text an editor put on the clipboard, and whether it was whole lines, which a paste puts above the caret's line.</summary>
public readonly record struct CopiedText(string Text, bool WholeLines);

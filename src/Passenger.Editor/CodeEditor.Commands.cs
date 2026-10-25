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
                SetSelections(SelectionCommands.SelectNextOccurrence(text, selections), reveal: true);
                return true;
            case EditorActions.UnselectOccurrence:
                SetSelections(SelectionCommands.UnselectLast(selections), reveal: true);
                return true;
            case EditorActions.SelectAllOccurrences:
                SetSelections(SelectionCommands.SelectAllOccurrences(text, selections, out bool every), reveal: false);
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
                    SetSelections(before, reveal: true);
                }
                return true;
            case EditorActions.Redo:
                if (document.Redo() is { } after)
                {
                    SetSelections(after, reveal: true);
                }
                return true;
        }

        if (IsReadOnly)
        {
            return IsEdit(action);
        }
        string eol = document.LineEnding;
        switch (action)
        {
            case EditorActions.Backspace:
                Apply(TextEditing.Backspace(text, selections, Indent));
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
                Apply(TextEditing.NewLine(text, selections, eol, Indent, Markup));
                return true;
            case EditorActions.StartNewLine:
                Apply(TextEditing.StartNewLine(text, selections, eol, Indent, above: false, Markup));
                return true;
            case EditorActions.StartNewLineBefore:
                Apply(TextEditing.StartNewLine(text, selections, eol, Indent, above: true));
                return true;
            case EditorActions.Indent:
                Apply(TextEditing.Indent(text, selections, Indent));
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
                BackgroundWork.Track(PasteFromClipboardAsync());
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
    public void Type(string typed) => Apply(TextEditing.Type(document.Snapshot, selections, typed));

    private int PageLines => Math.Max(1, (int)(Viewport.Height / metrics.LineHeight) - 1);

    private void Apply(EditPlan? plan)
    {
        if (plan is null || IsReadOnly)
        {
            return;
        }
        IReadOnlyList<FoldRegion> foldsBefore = folds;
        editing = true;
        try
        {
            document.Edit(plan.Edits, selections, plan.Selections, plan.Kind);
        }
        finally
        {
            editing = false;
        }
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

    /// <summary>Up and Down go by rows, a wrapped line's included, and keep returning to the x a vertical run of moves
    /// started from, even through shorter rows.</summary>
    private void MoveVertically(int rows, bool extend)
    {
        document.BreakUndoGroup();
        TextSnapshot text = document.Snapshot;
        HashSet<int> lean = [];
        SelectionSet moved = selections.With(s =>
        {
            (int row, double caretX) = Place(text, s.Caret, LeansBack(s.Caret));
            double x = double.IsNaN(s.PreferredX) ? caretX : s.PreferredX;
            int target = row + rows;
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
        int row = at + direction;
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
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            UnawaitedWork.Observe(clipboard.SetTextAsync(copied));
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } pasted)
        {
            return;
        }
        string normalized = pasted.Replace("\r\n", "\n");
        bool wholeLines = lineCopy is not null && normalized == lineCopy.Replace("\r\n", "\n");
        Apply(TextEditing.Paste(document.Snapshot, selections, pasted, wholeLines, document.LineEnding, ReindentsPaste ? Indent : null));
    }

    private static bool IsEdit(string action) => action.StartsWith("editor.", StringComparison.Ordinal);
}

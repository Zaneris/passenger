using Avalonia.Controls;
using Avalonia.Input;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

internal sealed partial class MarkdownTableEditing
{
    private sealed record Spot(Entry Entry, int Row, int Column);

    public bool TakesKey(KeyEventArgs e)
    {
        if (!CanEditInline || !editor.IsKeyboardFocusWithin)
        {
            return false;
        }
        Key key = e.Key;
        bool inCell = active is not null;
        return IsToolbarKey(e) || (e.KeyModifiers switch
        {
            KeyModifiers.None => key == Key.Escape ? inCell || toolbar.IsVisible : inCell && key is Key.Enter or Key.Tab,
            KeyModifiers.Shift => inCell && key == Key.Tab,
            KeyModifiers modifiers when modifiers == Keystroke.Command =>
                key is Key.B or Key.I || (key == Key.Enter && AtCaret() is not null) || (inCell && key is Key.Z or Key.Y),
            KeyModifiers modifiers when modifiers == (Keystroke.Command | KeyModifiers.Shift) => inCell && key == Key.Z,
            _ => false,
        });
    }

    public void KeyDown(KeyEventArgs e)
    {
        if (e.Handled || !TakesKey(e))
        {
            return;
        }
        e.Handled = true;
        switch (e.Key)
        {
            case Key.I when IsToolbarKey(e):
                ShowToolbar();
                break;
            case Key.B or Key.I:
                Format(e.Key == Key.B);
                break;
            case Key.Enter when e.KeyModifiers == Keystroke.Command && AtCaret() is { } spot:
                AddRow(spot);
                break;
            case Key.Enter:
                FocusSource();
                break;
            case Key.Tab:
                MoveCell(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                break;
            case Key.Escape:
                FocusSource();
                ResetToolbar(ToolbarRequest.Dismissed);
                break;
            case Key.Z or Key.Y:
                FocusSource();
                editor.TryExecute(e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? EditorActions.Redo : EditorActions.Undo);
                break;
        }
    }

    // Auto-Indent uses Ctrl+Alt+I in the application keymap, so this chord belongs only to the inline editor.
    private static bool IsToolbarKey(KeyEventArgs e) => e.Key == Key.I && e.KeyModifiers == (Keystroke.Command | KeyModifiers.Alt);

    private void FocusSource()
    {
        CloseCell();
        editor.Focus();
    }

    private Spot? AtCaret()
    {
        if (active is { } cell)
        {
            return new Spot(cell.Entry, cell.Row, cell.Column);
        }
        int caret = editor.Selections.Primary.Caret;
        Entry? entry = tables.FirstOrDefault(item => item.IsCurrent && caret >= item.Source.Start && caret <= item.Source.End);
        return entry is not null && Source(entry).At(caret) is { } at ? new Spot(entry, at.Row, at.Column) : null;
    }

    private void Format(bool bold)
    {
        if (!CanEditInline)
        {
            return;
        }
        if (active is { } cell)
        {
            TextBox field = cell.Field;
            string text = field.Text ?? "";
            TextRange selected = TextRange.FromBounds(Math.Min(field.SelectionStart, field.SelectionEnd), Math.Max(field.SelectionStart, field.SelectionEnd));
            if (MarkdownFormatting.Toggle(text, selected, bold) is { } change)
            {
                field.Text = text[..change.Edit.Start] + change.Edit.Text + text[change.Edit.End..];
                field.SelectionStart = change.Selection.Start;
                field.SelectionEnd = change.Selection.End;
                field.Focus();
            }
            return;
        }
        if (editor.Selections.Count != 1)
        {
            return;
        }
        TextSnapshot snapshot = editor.Document.Snapshot;
        Selection selection = editor.Selections.Primary;
        int start = Math.Max(0, selection.Start - 3);
        int end = Math.Min(snapshot.Length, selection.End + 3);
        TextRange range = new(selection.Start - start, selection.End - selection.Start);
        if (MarkdownFormatting.Toggle(snapshot.GetText(start, end - start), range, bold) is { } toggle)
        {
            TextRange after = toggle.Selection;
            editor.ApplyEdits([toggle.Edit with { Start = start + toggle.Edit.Start }],
                SelectionSet.Single(new Selection(start + after.Start, start + after.End)));
            editor.Focus();
        }
    }

    public void InsertTable()
    {
        TextSnapshot snapshot = editor.Document.Snapshot;
        LineSpan line = snapshot.GetLine(editor.CaretPosition.Line);
        int at = AtCaret()?.Entry.Source.End ?? line.End;
        string eol = editor.Document.LineEnding;
        string prefix = at == 0 ? "" : eol + eol;
        string table = string.Join(eol, MarkdownTableSource.WrittenRow(["Column 1", "Column 2"]),
            MarkdownTableSource.WrittenRow(["---", "---"]), MarkdownTableSource.WrittenRow(["", ""]));
        ApplyTableEdits([new TextEdit(at, 0, prefix + table + eol)], at + prefix.Length, 0, 0);
    }

    private void AddRow(Spot spot, bool above = false)
    {
        if (above && spot.Row == 0)
        {
            return;
        }
        int after = above ? spot.Row - 1 : spot.Row;
        MarkdownTableSource source = Source(spot.Entry);
        ApplyTableEdits([source.AddRow(after, editor.Document.LineEnding)], spot.Entry.Source.Start, after + 1, 0);
    }

    private void DeleteRow(Spot spot)
    {
        if (spot.Row == 0)
        {
            return;
        }
        MarkdownTableSource source = Source(spot.Entry);
        ApplyTableEdits([source.DeleteRow(spot.Row)], spot.Entry.Source.Start, Math.Min(spot.Row, source.Rows - 2), spot.Column);
    }

    private void ChangeColumn(Spot spot, bool insert, bool after)
    {
        MarkdownTableSource source = Source(spot.Entry);
        if (!insert && source.Columns == 1)
        {
            return;
        }
        int column = spot.Column + (after ? 1 : 0);
        ApplyTableEdits(source.ChangeColumn(column, insert), spot.Entry.Source.Start, spot.Row, Math.Min(column, source.Columns - (insert ? 0 : 2)));
    }

    private void Align(Spot spot, string delimiter) =>
        ApplyTableEdits([Source(spot.Entry).Align(spot.Column, delimiter)], spot.Entry.Source.Start, spot.Row, spot.Column);

    private void EditSource(Spot spot)
    {
        MarkdownTableSource source = Source(spot.Entry);
        FocusSource();
        editor.Select(source.Cell(spot.Row, spot.Column) ?? new TextRange(source.Line(spot.Row).End, 0));
        ResetToolbar(ToolbarRequest.Dismissed);
    }

    private void ApplyTableEdits(IReadOnlyList<TextEdit> edits, int start, int row, int column)
    {
        if (!CanEditInline)
        {
            return;
        }
        TextSnapshot snapshot = editor.Document.Snapshot;
        if (edits.All(edit => snapshot.GetText(edit.Start, edit.Length) == edit.Text))
        {
            return;
        }
        FocusSource();
        pending = (start, row, column);
        editor.ApplyEdits(edits, reveal: false);
        reading.Stop();
        IdeErrors.Observe(ReadAsync());
    }
}

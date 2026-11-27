using Avalonia.Controls;
using Avalonia.Input;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

internal sealed partial class MarkdownTableEditing
{
    private sealed record Spot(Entry Entry, int Row, int Column);

    private void FillTableMenu(ContextMenu menu)
    {
        Spot? selected = AtCaret();
        TextSnapshot snapshot = editor.Document.Snapshot;
        menu.Items.Clear();
        menu.Items.Add(Item("Insert Table", InsertTable, editor.IsReadOnly ? editor.ReadOnlyWhy : null));
        if (selected is { } spot && !editor.IsReadOnly)
        {
            string? header = spot.Row == 0 ? "The header row is required" : null;
            string? last = spot.Entry.Table.Aligns.Count == 1 ? "A table requires a column" : null;
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Insert Row Above", () => AddRow(spot, above: true), header));
            menu.Items.Add(Item("Insert Row Below", () => AddRow(spot)));
            menu.Items.Add(Item("Delete Row", () => DeleteRow(spot), header));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Insert Column Before", () => ChangeColumn(spot, insert: true, after: false)));
            menu.Items.Add(Item("Insert Column After", () => ChangeColumn(spot, insert: true, after: true)));
            menu.Items.Add(Item("Delete Column", () => ChangeColumn(spot, insert: false, after: false), last));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Align Left", () => Align(spot, ":---")));
            menu.Items.Add(Item("Align Center", () => Align(spot, ":---:")));
            menu.Items.Add(Item("Align Right", () => Align(spot, "---:")));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Edit Table as Markdown", () => EditSource(spot)));
        }

        MenuItem Item(string title, Action run, string? refusal = null) => ListMenus.Item(title, () =>
        {
            if (editor.PresentsMarkdown && !editor.IsReadOnly && ReferenceEquals(editor.Document.Snapshot, snapshot))
            {
                run();
            }
        }, refusal);
    }

    public bool TakesKey(KeyEventArgs e)
    {
        if (!editor.PresentsMarkdown || !editor.IsKeyboardFocusWithin || editor.IsReadOnly)
        {
            return false;
        }
        if (e.KeyModifiers == Keystroke.Command)
        {
            return e.Key is Key.B or Key.I || (e.Key == Key.Enter && AtCaret() is not null)
                || (active is not null && e.Key is Key.Z or Key.Y);
        }
        if (active is not null && e.KeyModifiers == (Keystroke.Command | KeyModifiers.Shift) && e.Key == Key.Z)
        {
            return true;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && toolbar.IsVisible)
        {
            return true;
        }
        return active is not null && ((e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
            || (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Escape or Key.Enter));
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
            case Key.B or Key.I:
                Format(e.Key == Key.B);
                break;
            case Key.Enter when e.KeyModifiers == Keystroke.Command && AtCaret() is { } spot:
                AddRow(spot);
                break;
            case Key.Enter:
                CloseCell();
                editor.Focus();
                break;
            case Key.Tab:
                MoveCell(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                break;
            case Key.Escape:
                CloseCell();
                editor.Focus();
                toolbarRequest = ToolbarRequest.Dismissed;
                PositionToolbar();
                break;
            case Key.Z or Key.Y:
                CloseCell();
                editor.Focus();
                editor.TryExecute(e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? EditorActions.Redo : EditorActions.Undo);
                break;
        }
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
        if (editor.IsReadOnly)
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
        int after = above ? Math.Max(0, spot.Row - 1) : spot.Row;
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
        CloseCell();
        editor.Focus();
        editor.Select(source.Cell(spot.Row, spot.Column) ?? new TextRange(source.Line(spot.Row).End, 0));
    }

    private void ApplyTableEdits(IReadOnlyList<TextEdit> edits, int start, int row, int column)
    {
        if (editor.IsReadOnly || followed != editor.Document)
        {
            return;
        }
        TextSnapshot snapshot = editor.Document.Snapshot;
        if (edits.All(edit => snapshot.GetText(edit.Start, edit.Length) == edit.Text))
        {
            return;
        }
        CloseCell();
        editor.Focus();
        pending = (start, row, column);
        editor.ApplyEdits(edits, reveal: false);
        reading.Stop();
        IdeErrors.Observe(ReadAsync());
    }
}

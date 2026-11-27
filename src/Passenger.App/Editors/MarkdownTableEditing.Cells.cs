using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.Text;

namespace Passenger.App.Editors;

internal sealed partial class MarkdownTableEditing
{
    private sealed class CellEditor(Entry entry, int row, int column, Border host, TextBox field, TextRange? range, string written)
    {
        public Entry Entry { get; } = entry;
        public int Row { get; } = row;
        public int Column { get; } = column;
        public Border Host { get; } = host;
        public TextBox Field { get; } = field;
        public TextRange? Range { get; set; } = range;
        public string Written { get; set; } = written;
    }

    private void Open(Entry entry, int row, int column, Grid grid) =>
        Open(entry, row, column, grid.Children.OfType<Border>().First(cell => Grid.GetRow(cell) == row && Grid.GetColumn(cell) == column));

    private void Open(Entry entry, int row, int column, Border host)
    {
        if (editor.IsReadOnly)
        {
            return;
        }
        CloseCell();
        MarkdownTableSource source = Source(entry);
        TextRange? range = source.Cell(row, column);
        string value = range is { } cell ? editor.Document.Snapshot.GetText(cell.Start, cell.Length) : "";
        TextBox field = new()
        {
            Name = "MarkdownTableCell",
            Text = value,
            MinWidth = Math.Max(host.Bounds.Width - host.Padding.Left - host.Padding.Right, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(field, $"Markdown table row {row + 1}, column {column + 1}");
        CellEditor opened = new(entry, row, column, host, field, range, value);
        active = opened;
        field.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && active == opened)
            {
                WriteCell(opened);
            }
        };
        field.LostFocus += (_, _) =>
        {
            if (active == opened)
            {
                CloseCell();
            }
        };
        host.Child = field;
        editor.RevealOffset(source.Line(row).Start);
        Present(editor.Document.Snapshot);
        field.Focus();
        field.SelectAll();
        field.BringIntoView();
        ToolbarChanged();
    }

    private void WriteCell(CellEditor cell)
    {
        if (editor.IsReadOnly || followed != editor.Document)
        {
            return;
        }
        string value = MarkdownTableSource.Escape(cell.Field.Text ?? "");
        if (value == cell.Written)
        {
            return;
        }
        TextEdit edit;
        if (cell.Range is { } range)
        {
            TextEdit changed = TextEdit.Between(cell.Written, value);
            edit = changed with { Start = range.Start + changed.Start };
            cell.Range = range with { Length = value.Length };
        }
        else
        {
            (edit, cell.Range) = Source(cell.Entry).PutMissing(cell.Row, cell.Column, value);
        }
        cell.Written = value;
        writesCell = true;
        try
        {
            editor.ApplyEdits([edit], reveal: false);
        }
        finally
        {
            writesCell = false;
        }
    }

    public void FollowReadOnly()
    {
        if (editor.IsReadOnly)
        {
            CloseCell();
            ResetToolbar();
        }
        else
        {
            QueueToolbarPosition();
        }
    }

    private void CloseCell(bool present = true)
    {
        if (active is not { } cell)
        {
            return;
        }
        active = null;
        cell.Host.Child = Markdown.CellWords(cell.Written, Page, cell.Entry.Table.Aligns[cell.Column], cell.Row == 0, inline: true);
        if (present)
        {
            Present(editor.Document.Snapshot);
        }
        ToolbarChanged();
    }

    private void MoveCell(int direction)
    {
        if (active is not { } cell || cell.Host.GetVisualParent() is not Grid grid)
        {
            return;
        }
        MarkdownTableSource source = Source(cell.Entry);
        int next = (cell.Row * source.Columns) + cell.Column + direction;
        if (next < 0)
        {
            return;
        }
        if (next >= source.Rows * source.Columns)
        {
            AddRow(new Spot(cell.Entry, cell.Row, cell.Column));
            return;
        }
        Open(cell.Entry, next / source.Columns, next % source.Columns, grid);
    }
}

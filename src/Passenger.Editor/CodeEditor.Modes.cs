using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Two ways of taking keys a person turns on and off: overwrite, where what is typed takes the place of the
/// characters after the caret on its line, the caret drawn as a block; and column selection, where Shift's moves draw a
/// column from where they began, a selection on every row between, and a plain drag selects a column as an Alt drag does.</summary>
public sealed partial class CodeEditor
{
    private bool overwrite;
    private bool columnMode;

    /// <summary>The column Shift's moves draw in column selection: its corner where it began and its corner now, each a row
    /// and an x on it, and the carets it made, which any other move of them leaves behind.</summary>
    private (int AnchorRow, double AnchorX, int Row, double X, SelectionSet Made)? column;

    /// <summary>Overwrite or column selection was turned on or off.</summary>
    public event EventHandler? ModesChanged;

    /// <summary>Whether what is typed takes the place of what follows the caret (Insert), outside Vim's own modes.</summary>
    public bool Overwrite
    {
        get => overwrite;
        set
        {
            if (overwrite != value)
            {
                overwrite = value;
                RedrawOverlay();
                ModesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether Shift's moves and a plain drag select a column.</summary>
    public bool ColumnMode
    {
        get => columnMode;
        set
        {
            if (columnMode != value)
            {
                columnMode = value;
                column = null;
                ModesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>A move with Shift in column selection: the column's far corner moved as the move moves a caret (by rows, by a
    /// character's width across, to the rows' starts or ends), a selection on every row between its corners; false for a move
    /// it leaves to the editor's own way (a word's, the text's ends).</summary>
    private bool ExtendColumn(string action, TextSnapshot text)
    {
        (int anchorRow, double anchorX, int row, double x) = column is { } held && held.Made.SameAs(selections)
            ? (held.AnchorRow, held.AnchorX, held.Row, held.X)
            : Corners(text);
        switch (action)
        {
            case EditorActions.Up:
                row--;
                break;
            case EditorActions.Down:
                row++;
                break;
            case EditorActions.PageUp:
                row -= PageLines;
                break;
            case EditorActions.PageDown:
                row += PageLines;
                break;
            case EditorActions.Left:
                x = Math.Max(0, x - metrics.CharWidth);
                break;
            case EditorActions.Right:
                x += metrics.CharWidth;
                break;
            case EditorActions.LineStart:
                x = 0;
                break;
            case EditorActions.LineEnd:
                // Past every row's end, so each row's selection reaches its own.
                x = double.MaxValue / 4;
                break;
            default:
                return false;
        }
        document.BreakUndoGroup();
        row = Math.Clamp(row, 0, Math.Max(0, RowCount - 1));
        List<Selection> rows = [];
        RowMap map = Rows;
        int step = row >= anchorRow ? 1 : -1;
        for (int at = anchorRow; ; at += step)
        {
            // A row above a line holds no text to select.
            if (map.LineOfRow(at).Row >= 0)
            {
                rows.Add(new Selection(OffsetAtRow(text, at, anchorX, out _), OffsetAtRow(text, at, x, out _)));
            }
            if (at == row || rows.Count >= SelectionSet.MaxCount)
            {
                break;
            }
        }
        if (rows.Count == 0)
        {
            return true;
        }
        SetSelections(SelectionSet.Create(rows, rows.Count - 1), reveal: true);
        column = (anchorRow, anchorX, row, x, selections);
        return true;
    }

    /// <summary>The primary selection's corners as a column's: its anchor's row and x, and its caret's.</summary>
    private (int AnchorRow, double AnchorX, int Row, double X) Corners(TextSnapshot text)
    {
        Selection primary = selections.Primary;
        (int anchorRow, double anchorX) = Place(text, primary.Anchor, LeansBack(primary.Anchor));
        (int row, double x) = Place(text, primary.Caret, LeansBack(primary.Caret));
        return (anchorRow, anchorX, row, x);
    }
}

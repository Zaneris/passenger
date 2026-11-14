using System.Globalization;
using System.Text;

namespace Passenger.Terminal;

/// <summary>Text an input method is composing, laid on the terminal's cells from the cursor as the program will count it
/// once it is typed: each character (a letter with its marks one) in the columns <see cref="TerminalScreen.Width"/>
/// gives its code points, one at least, on the next row's first column where it would pass the last.</summary>
public static class ComposedCells
{
    /// <summary>Each character of the text in order, from the cursor's cell at <paramref name="column"/> on rows
    /// <paramref name="columns"/> wide.</summary>
    public static List<ComposedCell> Lay(string text, int column, int columns)
    {
        List<ComposedCell> cells = [];
        int row = 0;
        for (int start = 0; start < text.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(start));
            int width = 0;
            foreach (Rune rune in text.AsSpan(start, length).EnumerateRunes())
            {
                width += TerminalScreen.Width(rune.Value);
            }
            width = Math.Max(1, width);
            if (column > 0 && column + width > columns)
            {
                row++;
                column = 0;
            }
            cells.Add(new ComposedCell(start, length, row, column, width));
            column += width;
            start += length;
        }
        return cells;
    }

    /// <summary>The cell the input method's cursor stands at, as a row from the cursor's and a column: the first cell of
    /// the character it stands before, else just past the last (the cursor's own cell for no text).</summary>
    public static (int Row, int Column) CursorOf(IReadOnlyList<ComposedCell> cells, int? cursor, int column)
    {
        foreach (ComposedCell cell in cells)
        {
            if (cursor is { } at && cell.Start >= at)
            {
                return (cell.Row, cell.Column);
            }
        }
        return cells is [.., ComposedCell last] ? (last.Row, last.Column + last.Width) : (0, column);
    }
}

/// <summary>A character of a composition: where it starts in the text and how long it is there, the row (counted from the
/// cursor's) and column of its first cell, and how many columns it takes.</summary>
public readonly record struct ComposedCell(int Start, int Length, int Row, int Column, int Width);

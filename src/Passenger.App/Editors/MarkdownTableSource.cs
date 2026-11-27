using System.Text;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>The source rows behind a GFM grid. Its delimiter row has no cell in the grid.</summary>
internal sealed class MarkdownTableSource(TextSnapshot text, int firstLine, int rows, int columns)
{
    public int Rows { get; } = rows;
    public int Columns { get; } = columns;

    public LineSpan Line(int row) => text.GetLine(firstLine + (row == 0 ? 0 : row + 1));

    public (int Row, int Column)? At(int offset)
    {
        int line = text.GetLineFromOffset(offset) - firstLine;
        if (line < 0 || line > Rows || line == 1)
        {
            return null;
        }
        int row = line == 0 ? 0 : line - 1;
        LineSpan span = Line(row);
        List<TextRange> cells = Markdown.CellRanges(text.GetText(span.Start, span.Length));
        int column = Math.Min(Columns - 1, cells.FindLastIndex(cell => span.Start + cell.Start <= offset));
        return (row, Math.Max(0, column));
    }

    public TextRange? Cell(int row, int column)
    {
        LineSpan line = Line(row);
        List<TextRange> cells = Markdown.CellRanges(text.GetText(line.Start, line.Length));
        return column < cells.Count ? new TextRange(line.Start + cells[column].Start, cells[column].Length) : null;
    }

    public TextEdit AddRow(int after, string eol)
    {
        LineSpan line = after == 0 ? text.GetLine(firstLine + 1) : Line(after);
        return new TextEdit(line.End, 0, LineEnding(line, eol) + WrittenRow(Enumerable.Repeat("", Columns)));
    }

    public TextEdit DeleteRow(int row)
    {
        LineSpan line = Line(row);
        int start = line.BreakLength > 0 ? line.Start : text.GetLine(line.Line - 1).End;
        return new TextEdit(start, line.EndIncludingBreak - start, "");
    }

    public IReadOnlyList<TextEdit> ChangeColumn(int column, bool insert)
    {
        List<TextEdit> edits = [];
        for (int i = 0; i <= Rows; i++)
        {
            LineSpan line = text.GetLine(firstLine + i);
            string source = text.GetText(line.Start, line.Length);
            List<string> cells = Cells(source);
            if (insert)
            {
                cells.Insert(column, i == 1 ? "---" : "");
            }
            else
            {
                cells.RemoveAt(column);
            }
            edits.Add(Changed(line, source, WrittenRow(cells)));
        }
        return edits;
    }

    public TextEdit Align(int column, string delimiter)
    {
        LineSpan line = text.GetLine(firstLine + 1);
        TextRange cell = Markdown.CellRanges(text.GetText(line.Start, line.Length))[column];
        return new TextEdit(line.Start + cell.Start, cell.Length, delimiter);
    }

    public (TextEdit Edit, TextRange Cell) PutMissing(int row, int column, string written)
    {
        LineSpan line = Line(row);
        string source = text.GetText(line.Start, line.Length);
        List<string> cells = Cells(source);
        cells[column] = written;
        string next = WrittenRow(cells);
        int at = 2 + cells.Take(column).Sum(cell => cell.Length + 3);
        return (Changed(line, source, next), new TextRange(line.Start + at, written.Length));
    }

    public static string WrittenRow(IEnumerable<string> cells) => "| " + string.Join(" | ", cells) + " |";

    private List<string> Cells(string source)
    {
        List<string> cells = [.. Markdown.CellRanges(source).Select(cell => source[cell.Start..cell.End])];
        cells.AddRange(Enumerable.Repeat("", Math.Max(0, Columns - cells.Count)));
        return cells;
    }

    private string LineEnding(LineSpan line, string fallback) => line.BreakLength > 0
        ? text.GetText(line.End, line.BreakLength) : fallback;

    private static TextEdit Changed(LineSpan line, string source, string next)
    {
        TextEdit edit = TextEdit.Between(source, next);
        return edit with { Start = line.Start + edit.Start };
    }

    /// <summary>Keeps pasted line breaks and literal pipes inside a single GFM cell.</summary>
    public static string Escape(string value)
    {
        string line = value.Contains('\r') || value.Contains('\n') ? value.ReplaceLineEndings("<br>") : value;
        if (!line.Contains('|'))
        {
            return line;
        }
        StringBuilder written = new(line.Length);
        int ticks = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                written.Append(c).Append(line[++i]);
                continue;
            }
            if (c == '`')
            {
                int at = i;
                while (i + 1 < line.Length && line[i + 1] == '`')
                {
                    i++;
                }
                int run = i - at + 1;
                ticks = ticks == 0 ? run : ticks == run ? 0 : ticks;
                written.Append('`', run);
                continue;
            }
            if (c == '|' && ticks == 0)
            {
                written.Append('\\');
            }
            written.Append(c);
        }
        return written.ToString();
    }
}

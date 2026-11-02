using System.Globalization;

namespace Passenger.App.Tables;

/// <summary>A table's rows in another order, by one column: numbers as numbers where the column holds only numbers,
/// dates as dates where it holds only dates, else text as the cells show it, digits within it as numbers and case aside.
/// Empty cells go last either way, and rows alike keep the file's order. Made off the UI thread; the file is untouched.</summary>
public static class TableSort
{
    /// <summary>The sort a column's title asks for next: ascending, then descending, then the file's order again; another
    /// column starts again ascending.</summary>
    public static (int Column, bool Descending)? Next((int Column, bool Descending)? now, int column) =>
        now is not { } sorted || sorted.Column != column ? (column, false) : !sorted.Descending ? (column, true) : null;

    /// <summary>The table sorted by a column, each row still numbered as the file has it.</summary>
    public static SortedTable By(Table table, int column, bool descending)
    {
        Table source = table.Unsorted;
        return new SortedTable(source, column, descending, Order(source, column, descending));
    }

    /// <summary>The rows' places in the file, in the order the column sorts them.</summary>
    public static int[] Order(Table table, int column, bool descending)
    {
        int count = table.RowCount;
        CellValue[] values = new CellValue[count];
        bool numbers = true;
        bool dates = true;
        for (int row = 0; row < count; row++)
        {
            CellValue value = table.ValueOf(row, column);
            values[row] = value;
            numbers &= value.Kind is CellKind.Empty or CellKind.Number;
            dates &= value.Kind is CellKind.Empty or CellKind.Date;
        }
        string?[]? texts = null;
        if (!numbers && !dates)
        {
            texts = new string?[count];
            for (int row = 0; row < count; row++)
            {
                texts[row] = values[row].Kind == CellKind.Empty ? null : table.Cell(row, column);
            }
        }
        CompareInfo words = CultureInfo.CurrentCulture.CompareInfo;
        int[] order = new int[count];
        for (int row = 0; row < count; row++)
        {
            order[row] = row;
        }
        int sign = descending ? -1 : 1;
        Array.Sort(order, (a, b) =>
        {
            bool aEmpty = values[a].Kind == CellKind.Empty;
            bool bEmpty = values[b].Kind == CellKind.Empty;
            int compared = aEmpty || bEmpty ? aEmpty.CompareTo(bEmpty)
                : sign * (texts is null ? values[a].Value.CompareTo(values[b].Value)
                    : words.Compare(texts[a], texts[b], CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));
            return compared != 0 ? compared : a.CompareTo(b);
        });
        return order;
    }
}

/// <summary>A table shown in a sort's order over the table its file has: each row its file's row, numbered as there.</summary>
public sealed class SortedTable(Table source, int by, bool descending, int[] order) : Table
{
    public override int RowCount => source.RowCount;

    public override int ColumnCount => source.ColumnCount;

    public override string Cell(int row, int column) => source.Cell(order[row], column);

    public override bool IsNumber(int row, int column) => source.IsNumber(order[row], column);

    public override CellValue ValueOf(int row, int column) => source.ValueOf(order[row], column);

    public override string Header(int column) => source.Header(column);

    public override int RowNumber(int row) => source.RowNumber(order[row]);

    public override string RowLabel(int row) => source.RowLabel(order[row]);

    public override int FileRow(int row) => source.FileRow(order[row]);

    public override bool IsEditable(int row, int column) => source.IsEditable(order[row], column);

    public override bool IsNewRow(int row) => source.IsNewRow(order[row]);

    public override string? DeleteTitle(int rows) => source.DeleteTitle(rows);

    public override Table Unsorted => source;

    public override (int Column, bool Descending)? Sort => (by, descending);
}

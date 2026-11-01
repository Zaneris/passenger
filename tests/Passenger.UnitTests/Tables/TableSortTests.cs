using System.Globalization;
using Passenger.App.Tables;

namespace Passenger.UnitTests.Tables;

/// <summary>How a table's rows sort by a column: numbers by their values where the column holds only numbers, dates by
/// when they are where it holds only dates, else text with its digits as numbers and case aside; empty cells last either
/// way, rows alike in the file's order, and each row numbered as the file has it.</summary>
public sealed class TableSortTests
{
    private static Table Csv(string text) => CsvTable.Parse(text, ',', header: true);

    private static string[] Column(Table table, int column) => [.. Enumerable.Range(0, table.RowCount).Select(row => table.Cell(row, column))];

    [Fact]
    public void A_column_of_numbers_sorts_by_their_values_its_empty_cells_last_both_ways()
    {
        Table table = Csv("N\n10\n9\n\n100\n\"1,000.5\"\n2.5\n");
        Assert.Equal(["2.5", "9", "10", "100", "1,000.5", ""], Column(TableSort.By(table, 0, descending: false), 0));
        Assert.Equal(["1,000.5", "100", "10", "9", "2.5", ""], Column(TableSort.By(table, 0, descending: true), 0));
    }

    [Fact]
    public void A_column_of_dates_sorts_by_when_they_are()
    {
        Table table = Csv("When\n2024-03-01\n2023-12-31T08:00:00\n2024-01-15\n");
        Assert.Equal(["2023-12-31T08:00:00", "2024-01-15", "2024-03-01"], Column(TableSort.By(table, 0, descending: false), 0));
    }

    [Fact]
    public void A_column_mixing_kinds_sorts_as_text_its_digits_as_numbers_and_case_aside()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Table table = Csv("Item\nitem10\nItem2\n7\nitem1\n");
            Assert.Equal(["7", "item1", "Item2", "item10"], Column(TableSort.By(table, 0, descending: false), 0));
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void Rows_alike_keep_the_files_order_and_each_row_keeps_its_files_number()
    {
        Table table = Csv("Name,Team\nAda,B\nAlan,A\nGrace,B\nEdsger,A\n");
        Table sorted = TableSort.By(table, 1, descending: true);
        Assert.Equal(["Ada", "Grace", "Alan", "Edsger"], Column(sorted, 0));
        Assert.Equal([1, 3, 2, 4], Enumerable.Range(0, sorted.RowCount).Select(sorted.RowNumber));
        Assert.Same(table, sorted.Unsorted);
        Assert.Equal((1, true), sorted.Sort);
        Assert.Equal(["Alan", "Edsger", "Ada", "Grace"], Column(TableSort.By(sorted, 1, descending: false), 0));
    }

    [Fact]
    public void A_title_sorts_ascending_then_descending_then_in_the_files_order_and_another_starts_ascending()
    {
        Assert.Equal((2, false), TableSort.Next(null, 2));
        Assert.Equal((2, true), TableSort.Next((2, false), 2));
        Assert.Null(TableSort.Next((2, true), 2));
        Assert.Equal((0, false), TableSort.Next((2, true), 0));
    }
}

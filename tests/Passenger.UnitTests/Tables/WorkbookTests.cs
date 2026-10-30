using System.Globalization;
using System.IO.Compression;
using System.Text;
using Passenger.App.Tables;

namespace Passenger.UnitTests.Tables;

/// <summary>A workbook's sheet read for the table view: a date written as one shown as the date it is in either date
/// system and whatever its style, and a sheet's cells kept at their columns, the empty ones between made only as drawn.</summary>
public class WorkbookTests
{
    private const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    /// <summary>Styles: General, then a date as yyyy-mm-dd.</summary>
    private const string DateStyles = "<numFmts><numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"165\"/></cellXfs>";

    [Fact]
    public void An_ISO_date_in_a_1904_date_workbook_shows_the_date_as_written_whatever_its_style()
    {
        Table sheet = Sheet(Book("<row r=\"1\"><c r=\"A1\" t=\"d\"><v>2023-03-15</v></c><c r=\"B1\" t=\"d\" s=\"1\"><v>2023-03-15T00:00:00</v></c>"
            + "<c r=\"C1\" s=\"1\"><v>43538</v></c><c r=\"D1\" t=\"d\"><v>2023-03-15T10:30:00</v></c></row>", date1904: true, DateStyles));
        CultureInfo culture = CultureInfo.CurrentCulture;

        string general = new DateTime(2023, 3, 15).ToString("d", culture);
        Assert.True(sheet.Cell(0, 0) == general, $"An ISO date in General style showed {sheet.Cell(0, 0)}, not the date written, {general}");
        Assert.True(sheet.Cell(0, 1) == "2023-03-15", $"An ISO date in a date style showed {sheet.Cell(0, 1)}, not the date written, 2023-03-15");
        Assert.True(sheet.Cell(0, 2) == "2023-03-15", $"The 1904 system's serial of the same date showed {sheet.Cell(0, 2)}");
        Assert.Equal(new DateTime(2023, 3, 15, 10, 30, 0).ToString("g", culture), sheet.Cell(0, 3));
        Assert.True(sheet.IsNumber(0, 0), "A date should be drawn against its cell's right edge, as a number is");
    }

    [Fact]
    public void An_ISO_date_in_a_1900_date_workbook_shows_the_date_as_written()
    {
        Table sheet = Sheet(Book("<row r=\"1\"><c r=\"A1\" t=\"d\" s=\"1\"><v>2023-03-15</v></c><c r=\"B1\" t=\"d\"><v>2023-03-15</v></c></row>", date1904: false, DateStyles));
        Assert.Equal("2023-03-15", sheet.Cell(0, 0));
        Assert.Equal(new DateTime(2023, 3, 15).ToString("d", CultureInfo.CurrentCulture), sheet.Cell(0, 1));
    }

    [Fact]
    public void A_small_workbook_holding_a_cell_at_the_last_column_of_each_row_reads_in_a_few_megabytes()
    {
        StringBuilder rows = new();
        for (int row = 1; row <= 200; row++)
        {
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{row}\"><c r=\"XFD{row}\"><v>{row}</v></c></row>");
        }
        byte[] book = Book(rows.ToString());

        long before = GC.GetAllocatedBytesForCurrentThread();
        Table sheet = Sheet(book);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 << 20,
            $"Reading a {book.Length} byte workbook of 200 cells allocated {allocated >> 20} MB: such a file shared to a person would take the window's memory");
        Assert.Equal((200, 16384), (sheet.RowCount, sheet.ColumnCount));
        Assert.Equal("200", sheet.Cell(199, 16383));
        Assert.Equal(string.Empty, sheet.Cell(199, 16382));
        Assert.False(sheet.IsNumber(0, 0), "A column a row has no cell in should be empty");
    }

    [Fact]
    public void A_rows_cells_show_at_their_columns_with_the_gaps_between_empty_and_one_out_of_order_left_out()
    {
        Table sheet = Sheet(Book("<row r=\"1\"><c r=\"B1\" t=\"inlineStr\"><is><t>b</t></is></c><c r=\"D1\"><v>4</v></c><c><v>5</v></c><c r=\"C1\"><v>3</v></c></row>"
            + "<row r=\"3\"><c><v>1</v></c><c><v>2</v></c><c r=\"F3\"/></row>"));

        Assert.Equal((3, 6), (sheet.RowCount, sheet.ColumnCount));
        Assert.Equal(["", "b", "", "4", "5", ""], Enumerable.Range(0, 6).Select(c => sheet.Cell(0, c)));
        Assert.Equal(["", "", "", "", "", ""], Enumerable.Range(0, 6).Select(c => sheet.Cell(1, c)));
        Assert.Equal(["1", "2", "", "", "", ""], Enumerable.Range(0, 6).Select(c => sheet.Cell(2, c)));
        Assert.True(sheet.IsNumber(0, 3) && !sheet.IsNumber(0, 2) && !sheet.IsNumber(0, 1), "Only the numbers' cells should be drawn as numbers");
    }

    private static Table Sheet(byte[] book) => Workbook.Read("book.xlsx", () => new MemoryStream(book, writable: false)).ReadSheet(0);

    /// <summary>A workbook of one sheet holding these rows, in the 1904 date system where asked, with these styles.</summary>
    private static byte[] Book(string rows, bool date1904 = false, string styles = "")
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string xml)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open());
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
            }
            Part("xl/workbook.xml", $"<workbook {Ns}>{(date1904 ? "<workbookPr date1904=\"1\"/>" : "")}<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"ws\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Part("xl/styles.xml", $"<styleSheet {Ns}>{styles}</styleSheet>");
            Part("xl/worksheets/sheet1.xml", $"<worksheet {Ns}><sheetData>{rows}</sheetData></worksheet>");
        }
        return bytes.ToArray();
    }
}

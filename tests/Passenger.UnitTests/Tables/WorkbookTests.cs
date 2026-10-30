using System.Globalization;
using System.IO.Compression;
using Passenger.App.Tables;

namespace Passenger.UnitTests.Tables;

/// <summary>A workbook's sheet read for the table view: a date written as one shown as the date it is in either date
/// system and whatever its style.</summary>
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

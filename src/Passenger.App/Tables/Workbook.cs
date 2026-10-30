using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Passenger.App.Tables;

/// <summary>An Excel workbook (.xlsx, .xlsm) read for showing: its visible sheets' names, each sheet's cells read only when
/// it is chosen, with their numbers formatted as the sheet's styles say. Its parts are read straight from the zip with an
/// XmlReader, nothing kept but the shared strings and the formats.</summary>
public sealed class Workbook
{
    private const string Relations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Excel's last column, XFD: a cell past it is not a cell.</summary>
    private const int LastColumn = 16384;

    /// <summary>Excel's last row: a row numbered past it (in a damaged or hostile file) would make millions of rows of nothing.</summary>
    private const int LastRow = 1_048_576;

    private readonly string name;
    private readonly Func<Stream> open;
    private readonly string[] strings;
    private readonly CellFormat[] formats;
    private readonly bool from1904;

    private Workbook(string name, Func<Stream> open, IReadOnlyList<(string Name, string Part)> sheets, string[] strings, CellFormat[] formats, bool from1904)
    {
        this.name = name;
        this.open = open;
        Sheets = sheets;
        this.strings = strings;
        this.formats = formats;
        this.from1904 = from1904;
    }

    /// <summary>The sheets Excel shows, in its order, with the zip part each is in.</summary>
    public IReadOnlyList<(string Name, string Part)> Sheets { get; }

    /// <summary>Whether a file is a workbook the table view reads.</summary>
    public static bool IsWorkbook(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xlsm";

    /// <summary>Reads a workbook's sheets, strings and styles; its sheets' cells are <see cref="ReadSheet"/>'s. The file is
    /// read sharing writes and deletes, so Excel saving it meanwhile is never refused.</summary>
    public static Workbook Open(string path) =>
        Read(Path.GetFileName(path), () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));

    /// <summary>Reads a workbook from the stream <paramref name="open"/> gives, asked for a new one at each sheet read;
    /// <paramref name="name"/> names it where it is damaged.</summary>
    public static Workbook Read(string name, Func<Stream> open)
    {
        using ZipArchive zip = Zip(open);
        Dictionary<string, ZipArchiveEntry> parts = Parts(zip);
        Dictionary<string, string> targets = [];
        if (parts.TryGetValue("xl/_rels/workbook.xml.rels", out ZipArchiveEntry? rels))
        {
            using XmlReader reader = Reader(rels);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                    && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                {
                    targets[id] = target.StartsWith('/') ? target[1..] : "xl/" + target;
                }
            }
        }
        List<(string, string)> sheets = [];
        bool from1904 = false;
        ZipArchiveEntry book = parts.GetValueOrDefault("xl/workbook.xml") ?? throw new InvalidDataException($"{name} has no workbook part.");
        using (XmlReader reader = Reader(book))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }
                if (reader.LocalName == "workbookPr")
                {
                    from1904 = reader.GetAttribute("date1904") is "1" or "true";
                }
                else if (reader.LocalName == "sheet" && reader.GetAttribute("state") is null or "visible"
                    && reader.GetAttribute("id", Relations) is { } id && targets.TryGetValue(id, out string? part))
                {
                    sheets.Add((reader.GetAttribute("name") ?? part, part));
                }
            }
        }
        string[] strings = parts.TryGetValue("xl/sharedStrings.xml", out ZipArchiveEntry? shared) ? SharedStrings(shared) : [];
        CellFormat[] formats = parts.TryGetValue("xl/styles.xml", out ZipArchiveEntry? styles) ? Styles(styles) : [];
        return new Workbook(name, open, sheets, strings, formats, from1904);
    }

    /// <summary>One sheet's cells, from A1 to the last cell it has.</summary>
    public SheetTable ReadSheet(int index)
    {
        using ZipArchive zip = Zip(open);
        if (!Parts(zip).TryGetValue(Sheets[index].Part, out ZipArchiveEntry? entry))
        {
            throw new InvalidDataException($"{name} has no part {Sheets[index].Part}.");
        }
        List<SheetCell> cells = [];
        List<int> rows = [];
        int columns = 0;
        using XmlReader reader = Reader(entry);
        int row = -1;
        int column = -1;
        bool inRow = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            if (reader.LocalName == "row")
            {
                // A row's number may skip empty rows, which are kept as rows of no cells.
                int number = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r - 1 : row + 1;
                // Rows come in order; one numbered at or before the last, or past Excel's last, is no row of the sheet.
                inRow = number > row && number < LastRow;
                if (!inRow)
                {
                    continue;
                }
                while (row < number)
                {
                    row++;
                    rows.Add(cells.Count);
                }
                column = -1;
            }
            else if (reader.LocalName == "c" && inRow)
            {
                column = reader.GetAttribute("r") is { } at ? ColumnOf(at) : column + 1;
                if (column is < 0 or >= LastColumn)
                {
                    continue;
                }
                string? type = reader.GetAttribute("t");
                // Held at the styles' count, so a damaged file's vast style never overflows a cell's kind.
                int style = int.TryParse(reader.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? Math.Min(s, formats.Length) : 0;
                SheetCell cell = Cell(reader, type, style);
                int start = rows[row];
                while (cells.Count - start < column)
                {
                    cells.Add(default);
                }
                cells.Add(cell);
                columns = Math.Max(columns, column + 1);
            }
        }
        // Trailing rows of no cells (a format applied to whole rows) show nothing, so they go.
        while (rows.Count > 0 && rows[^1] == cells.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        rows.Add(cells.Count);
        return new SheetTable(this, [.. cells], [.. rows], columns);
    }

    /// <summary>A cell's value as its type says, read from the reader on its <c>c</c> element.</summary>
    private SheetCell Cell(XmlReader reader, string? type, int style)
    {
        if (reader.IsEmptyElement)
        {
            return default;
        }
        string? value = null;
        StringBuilder? inline = null;
        int depth = reader.Depth;
        reader.Read();
        // Left on the cell's end, so the sheet's loop reads on from the next cell; a value read moves past its own end.
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v")
            {
                value = reader.ReadElementContentAsString();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && type == "inlineStr")
            {
                (inline ??= new StringBuilder()).Append(reader.ReadElementContentAsString());
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh")
            {
                reader.Skip();
            }
            else
            {
                reader.Read();
            }
        }
        return type switch
        {
            "s" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < strings.Length ? SheetCell.Of(strings[index]) : default,
            "inlineStr" => SheetCell.Of(inline?.ToString()),
            "str" or "e" => SheetCell.Of(value),
            "b" => SheetCell.Of(value is null ? null : value == "1" ? "TRUE" : "FALSE"),
            "d" => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime date)
                ? SheetCell.Date(date.ToOADate() - (from1904 ? CellFormat.Day1904 : 0), style) : SheetCell.Of(value),
            _ => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? SheetCell.Number(number, style) : SheetCell.Of(value),
        };
    }

    /// <summary>A number cell's value as its style formats it; a date written as one shows as a date whatever its style.</summary>
    internal string Format(SheetCell cell)
    {
        CellFormat format = cell.Style < formats.Length ? formats[cell.Style] : CellFormat.General;
        if (cell.IsDate && !format.ShowsDate)
        {
            format = cell.Value % 1 == 0 ? CellFormat.ShortDate : CellFormat.ShortDateTime;
        }
        return format.Show(cell.Value, from1904);
    }

    /// <summary>The column of a cell reference ("BC12" is 54), or -1 for none.</summary>
    private static int ColumnOf(string reference)
    {
        int column = 0;
        int i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
        {
            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        }
        return i == 0 ? -1 : column - 1;
    }

    /// <summary>The shared strings, each its runs' text joined; phonetic runs (rPh) left out, as Excel shows them.</summary>
    private static string[] SharedStrings(ZipArchiveEntry entry)
    {
        List<string> strings = [];
        using XmlReader reader = Reader(entry);
        StringBuilder text = new();
        reader.MoveToContent();
        // A text read moves past its own end, so the loop reads on only where nothing was read.
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                text.Clear();
                if (reader.IsEmptyElement)
                {
                    strings.Add(string.Empty);
                }
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh")
            {
                reader.Skip();
                continue;
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                text.Append(reader.ReadElementContentAsString());
                continue;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                strings.Add(text.ToString());
            }
            reader.Read();
        }
        return [.. strings];
    }

    /// <summary>Each cell style's number format (cellXfs, by index), from the custom formats and Excel's built-in ones.</summary>
    private static CellFormat[] Styles(ZipArchiveEntry entry)
    {
        Dictionary<int, string> custom = [];
        List<CellFormat> formats = [];
        Dictionary<int, CellFormat> made = [];
        using XmlReader reader = Reader(entry);
        bool inCellXfs = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
            {
                break;
            }
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            if (reader.LocalName == "numFmt" && int.TryParse(reader.GetAttribute("numFmtId"), CultureInfo.InvariantCulture, out int id)
                && reader.GetAttribute("formatCode") is { } code)
            {
                custom[id] = code;
            }
            else if (reader.LocalName == "cellXfs")
            {
                inCellXfs = true;
            }
            else if (inCellXfs && reader.LocalName == "xf")
            {
                int format = int.TryParse(reader.GetAttribute("numFmtId"), CultureInfo.InvariantCulture, out int f) ? f : 0;
                if (!made.TryGetValue(format, out CellFormat? cellFormat))
                {
                    cellFormat = custom.TryGetValue(format, out string? code2) ? CellFormat.Parse(code2) : CellFormat.BuiltIn(format);
                    made[format] = cellFormat;
                }
                formats.Add(cellFormat);
            }
        }
        return [.. formats];
    }

    private static ZipArchive Zip(Func<Stream> open) => new(open(), ZipArchiveMode.Read, leaveOpen: false);

    private static Dictionary<string, ZipArchiveEntry> Parts(ZipArchive zip)
    {
        Dictionary<string, ZipArchiveEntry> parts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            parts[entry.FullName.Replace('\\', '/')] = entry;
        }
        return parts;
    }

    private static XmlReader Reader(ZipArchiveEntry entry) =>
        XmlReader.Create(entry.Open(), new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true });
}

/// <summary>A sheet's cell: nothing (the default), its text, or a number with the cell style that formats it, which may
/// be a date written as one (its serial in the workbook's date system).</summary>
public readonly record struct SheetCell(string? Text, double Value, int Kind)
{
    /// <summary>A text cell's <see cref="Kind"/>; a number's is its style plus one, a date's its style plus two negated,
    /// and an empty cell's 0.</summary>
    private const int TextKind = -1;

    public bool IsNumber => Kind is > 0 or < TextKind;

    /// <summary>Whether the cell's number was written as a date, so shown as one whatever its style.</summary>
    public bool IsDate => Kind < TextKind;

    public int Style => IsDate ? -Kind - 2 : Kind - 1;

    public static SheetCell Of(string? text) => text is null ? default : new SheetCell(text, 0, TextKind);

    public static SheetCell Number(double value, int style) => new(null, value, style + 1);

    public static SheetCell Date(double serial, int style) => new(null, serial, -style - 2);
}

/// <summary>A sheet's cells as rows, each row's cells in one array; a number formatted only when it is drawn.</summary>
public sealed class SheetTable(Workbook book, SheetCell[] cells, int[] rows, int columns) : Table
{
    public override int RowCount => rows.Length - 1;

    public override int ColumnCount => columns;

    public override string Cell(int row, int column)
    {
        int at = rows[row] + column;
        if (at >= rows[row + 1])
        {
            return string.Empty;
        }
        SheetCell cell = cells[at];
        return cell.IsNumber ? book.Format(cell) : cell.Text ?? string.Empty;
    }

    public override bool IsNumber(int row, int column)
    {
        int at = rows[row] + column;
        return at < rows[row + 1] && cells[at].IsNumber;
    }
}

using System.Globalization;

namespace Passenger.App.Tables;

/// <summary>Cells in rows, as the table view draws them: a header for each column and the body's rows below it. A cell's
/// text is made only when asked, so a large file costs its rows' places, not a string per cell.</summary>
public abstract class Table
{
    /// <summary>The body's rows, the header's not counted.</summary>
    public abstract int RowCount { get; }

    public abstract int ColumnCount { get; }

    /// <summary>A cell's text as shown, empty for none.</summary>
    public abstract string Cell(int row, int column);

    /// <summary>Whether a cell holds a number, which is drawn against its column's right edge.</summary>
    public abstract bool IsNumber(int row, int column);

    /// <summary>A column's header: a spreadsheet's letters unless the table has a header row of its own.</summary>
    public virtual string Header(int column) => Letters(column);

    /// <summary>The number shown beside a row, counting from 1.</summary>
    public virtual int RowNumber(int row) => row + 1;

    /// <summary>A spreadsheet's name of a column: A to Z, then AA.</summary>
    public static string Letters(int column)
    {
        Span<char> name = stackalloc char[4];
        int at = name.Length;
        for (int n = column + 1; n > 0; n = (n - 1) / 26)
        {
            name[--at] = (char)('A' + (n - 1) % 26);
        }
        return new string(name[at..]);
    }
}

/// <summary>A CSV or TSV file's records, read as RFC 4180 reads them: a quoted field may hold the separator, quotes doubled
/// and line breaks. Each field is kept as where it is in the text, unquoted only when drawn.</summary>
public sealed class CsvTable : Table
{
    private readonly string text;

    /// <summary>Each field's start and end in the text, a pair per field, row after row.</summary>
    private readonly int[] fields;

    /// <summary>Where each record's first field is among the pairs, and one past the last record's.</summary>
    private readonly int[] records;

    private readonly int columns;
    private readonly int first;

    private CsvTable(string text, int[] fields, int[] records, int columns, bool header)
    {
        this.text = text;
        this.fields = fields;
        this.records = records;
        this.columns = columns;
        first = header && records.Length > 1 ? 1 : 0;
    }

    public override int RowCount => records.Length - 1 - first;

    public override int ColumnCount => columns;

    public override string Cell(int row, int column) => Field(row + first, column);

    public override bool IsNumber(int row, int column)
    {
        ReadOnlySpan<char> field = Span(row + first, column);
        // Quoted as a number with thousands' commas must be ("1,234.50"), and Excel reads it as the number it is.
        if (field.Length > 1 && field[0] == '"' && field[^1] == '"')
        {
            field = field[1..^1];
        }
        field = field.Trim();
        return field.Length > 0 && double.TryParse(field, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
    }

    public override string Header(int column) => first == 1 ? Field(0, column) : Letters(column);

    /// <summary>The separator a file's name says (a tab for .tsv), or for a .csv whichever of comma and semicolon its first
    /// record has more of outside quoted fields, as Excel writes semicolons where the comma is the decimal point. The record
    /// is read as <see cref="Parse"/> reads one, a quoted field's line breaks within it.</summary>
    public static char SeparatorFor(string path, ReadOnlySpan<char> text)
    {
        if (Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            return '\t';
        }
        int commas = 0;
        int semicolons = 0;
        int i = 0;
        while (i < text.Length)
        {
            // A field, either separator ending it: quoted only where a quote begins it, then on to its end.
            if (text[i] == '"')
            {
                i = PastQuoted(text, i);
            }
            while (i < text.Length && text[i] is not (',' or ';' or '\r' or '\n'))
            {
                i++;
            }
            if (i == text.Length || text[i] is '\r' or '\n')
            {
                break;
            }
            if (text[i] == ',')
            {
                commas++;
            }
            else
            {
                semicolons++;
            }
            i++;
        }
        return semicolons > commas ? ';' : ',';
    }

    /// <summary>Reads a text's records in one pass; with <paramref name="header"/>, the first is the columns' names.</summary>
    public static CsvTable Parse(string text, char separator, bool header)
    {
        List<int> fields = new(Math.Min(text.Length / 4, 1 << 20));
        List<int> records = [];
        int columns = 0;
        int i = 0;
        int length = text.Length;
        while (i < length)
        {
            int record = fields.Count / 2;
            records.Add(record);
            while (true)
            {
                int start = i;
                if (i < length && text[i] == '"')
                {
                    // Quoted: past its closing quote, then on to the separator or the line's end.
                    i = PastQuoted(text, i);
                }
                while (i < length && text[i] != separator && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }
                fields.Add(start);
                fields.Add(i);
                if (i < length && text[i] == separator)
                {
                    // A separator last of all still ends in an empty field, which the next turn adds.
                    i++;
                    continue;
                }
                if (i < length && text[i] == '\r')
                {
                    i++;
                }
                if (i < length && text[i] == '\n')
                {
                    i++;
                }
                break;
            }
            columns = Math.Max(columns, fields.Count / 2 - record);
        }
        records.Add(fields.Count / 2);
        return new CsvTable(text, [.. fields], [.. records], columns, header);
    }

    /// <summary>Just past the quote that closes a quoted field opened at <paramref name="open"/>, the one no other quote
    /// follows, or the text's end where none does.</summary>
    private static int PastQuoted(ReadOnlySpan<char> text, int open)
    {
        int i = open + 1;
        while (i < text.Length)
        {
            int quote = text[i..].IndexOf('"');
            if (quote < 0)
            {
                break;
            }
            quote += i;
            if (quote + 1 < text.Length && text[quote + 1] == '"')
            {
                i = quote + 2;
                continue;
            }
            return quote + 1;
        }
        return text.Length;
    }

    private ReadOnlySpan<char> Span(int record, int column)
    {
        int pair = records[record] + column;
        if (pair >= records[record + 1])
        {
            return [];
        }
        int start = fields[pair * 2];
        return text.AsSpan(start, fields[pair * 2 + 1] - start);
    }

    private string Field(int record, int column)
    {
        ReadOnlySpan<char> field = Span(record, column);
        if (field.Length == 0 || field[0] != '"')
        {
            return field.ToString();
        }
        int close = field.LastIndexOf('"');
        ReadOnlySpan<char> inside = close > 0 ? field[1..close] : field[1..];
        return inside.Contains('"') ? inside.ToString().Replace("\"\"", "\"", StringComparison.Ordinal) : inside.ToString();
    }
}

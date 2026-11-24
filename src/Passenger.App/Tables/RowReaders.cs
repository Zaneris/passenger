using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Passenger.App.Tables;

/// <summary>A CSV's records as they come from a reader, read as RFC 4180 reads them: a quoted field may hold the separator,
/// line breaks and doubled quotes, and a record ends at CRLF, LF or CR. A field written bare and empty is no value (null)
/// and one quoted empty is empty text, as <see cref="CsvRows"/> writes them; a line holding nothing is passed over. It holds
/// the record it reads, never the file.</summary>
public sealed class CsvRecords(TextReader from, char separator)
{
    private readonly char[] buffer = new char[1 << 14];
    private readonly List<string?> fields = [];
    private readonly StringBuilder text = new();
    private int at;
    private int end;

    /// <summary>The next record's fields, null past the last.</summary>
    public string?[]? Next()
    {
        while (Peek() is '\r' or '\n')
        {
            Take();
        }
        if (Peek() < 0)
        {
            return null;
        }
        fields.Clear();
        while (true)
        {
            text.Clear();
            bool quoted = Peek() == '"';
            if (quoted)
            {
                Take();
                for (int c = Take(); c >= 0; c = Take())
                {
                    if (c == '"' && Peek() != '"')
                    {
                        break;
                    }
                    text.Append((char)c);
                    if (c == '"')
                    {
                        Take();
                    }
                }
            }
            // What follows a closing quote before the separator stays the field's, as spreadsheets read it.
            while (Peek() is >= 0 and int next && next != separator && next is not ('\r' or '\n'))
            {
                text.Append((char)Take());
            }
            fields.Add(!quoted && text.Length == 0 ? null : text.ToString());
            int ended = Take();
            if (ended == separator)
            {
                continue;
            }
            if (ended == '\r' && Peek() == '\n')
            {
                Take();
            }
            return [.. fields];
        }
    }

    private int Peek()
    {
        if (at == end)
        {
            end = from.Read(buffer, 0, buffer.Length);
            at = 0;
            if (end <= 0)
            {
                end = 0;
                return -1;
            }
        }
        return buffer[at];
    }

    private int Take()
    {
        int c = Peek();
        if (c >= 0)
        {
            at++;
        }
        return c;
    }
}

/// <summary>A JSON file's rows as they come: an array of objects, or one object a line (JSON Lines). Each row is its members'
/// values as text by their names, a number as written, true or false, an object's or array's JSON, and null for null.</summary>
public static class JsonRecords
{
    /// <summary>The rows of a stream, read as they come; a value that is no object refused, naming its row.</summary>
    public static async IAsyncEnumerable<IReadOnlyList<(string Name, string? Value)>> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancel)
    {
        bool array = await ArrayAsync(stream, cancel);
        long row = 0;
        await foreach (JsonElement element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, topLevelValues: !array, cancellationToken: cancel))
        {
            row++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"Row {row.ToString(System.Globalization.CultureInfo.InvariantCulture)} is no object of names and values");
            }
            yield return [.. element.EnumerateObject().Select(member => (member.Name, Text(member.Value)))];
        }
    }

    /// <summary>A value as text, as a row's cell takes it.</summary>
    public static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    /// <summary>Whether the stream holds an array, its first character past any BOM and white space a '['; the stream put
    /// back where it began.</summary>
    private static async Task<bool> ArrayAsync(Stream stream, CancellationToken cancel)
    {
        long start = stream.Position;
        byte[] first = new byte[256];
        int read = await stream.ReadAsync(first, cancel);
        stream.Position = start;
        ReadOnlySpan<byte> text = first.AsSpan(0, read);
        if (text.StartsWith(Encoding.UTF8.Preamble))
        {
            text = text[Encoding.UTF8.Preamble.Length..];
        }
        text = text.TrimStart(" \t\r\n"u8);
        return text.Length > 0 && text[0] == '[';
    }
}

/// <summary>Rows of text as a file of rows gave them, under its columns' names: a preview's, no value shown as NULL.</summary>
public sealed class TextRows(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows) : Table
{
    public override int RowCount => rows.Count;

    public override int ColumnCount => columns.Count;

    public override string Header(int column) => columns[column];

    public override string Cell(int row, int column) => column < rows[row].Length ? rows[row][column] ?? "NULL" : "NULL";

    public override object? ValueAt(int row, int column) => column < rows[row].Length ? rows[row][column] : null;

    public override bool IsNumber(int row, int column) => ValueAt(row, column) is string text
        && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    public override CellValue ValueOf(int row, int column) => ValueAt(row, column) switch
    {
        null => new CellValue(CellKind.Empty),
        string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) =>
            new CellValue(CellKind.Number, number),
        _ => new CellValue(CellKind.Text),
    };
}

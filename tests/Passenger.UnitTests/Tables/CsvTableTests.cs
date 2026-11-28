using System.Text;
using Passenger.App.Tables;

namespace Passenger.UnitTests.Tables;

/// <summary>A CSV read and written: the separator it is read with, whichever of comma and semicolon its first record has
/// more of between its fields, those inside a quoted field (line breaks too) never counted; rows written as RFC 4180 has
/// them and read back as they were, no value told apart from empty text; and rows written as JSON objects, read back from
/// an array or JSON Lines.</summary>
public class CsvTableTests
{
    [Fact]
    public async Task A_csvs_and_a_json_files_rows_are_read_and_written_as_their_formats_have_them()
    {
        Cases.All(
            ("A quoted value holding semicolons leaves a comma separated file read at its commas", A_quoted_value_holding_semicolons_leaves_a_comma_separated_file_read_at_its_commas),
            ("A csvs separator is counted between the first records fields alone", A_csvs_separator_is_counted_between_the_first_records_fields_alone),
            ("A tsv is read at its tabs whatever its first line holds", A_tsv_is_read_at_its_tabs_whatever_its_first_line_holds),
            ("Rows written as CSV read back as they were", Rows_written_as_csv_read_back_as_they_were),
            ("A CSVs records are read as RFC 4180 reads them", A_csvs_records_are_read_as_rfc_4180_reads_them));
        await Rows_written_as_json_read_back_as_their_values();
    }

    private static readonly RowColumn[] Columns = [new("id", "int8"), new("name", "text"), new("note", "text"), new("price", "numeric"), new("on", "date"),
        new("at", "timestamp"), new("bytes", "bytea"), new("ok", "bool")];

    private static readonly object?[] Row = [7L, "say \"hi\", twice", null, 2.50m, new DateTime(2024, 5, 1), new DateTime(2024, 5, 1, 13, 45, 0, 500), new byte[] { 1, 2, 255 }, true];

    private void Rows_written_as_csv_read_back_as_they_were()
    {
        object?[] multiline = [8L, "two\nlines", string.Empty, -1.25m, null, null, null, false];
        string text = RowWriter.Written(into => new CsvRows(into, ',', header: true, "\r\n"), Columns, [Row, multiline]);
        Assert.Equal("id,name,note,price,on,at,bytes,ok\r\n7,\"say \"\"hi\"\", twice\",,2.50,2024-05-01,2024-05-01 13:45:00.5,AQL/,true\r\n"
            + "8,\"two\nlines\",\"\",-1.25,,,,false\r\n", text);
        CsvRecords records = new(new StringReader(text), ',');
        Assert.Equal("id|name|note|price|on|at|bytes|ok", Joined(records.Next()));
        Assert.Equal("7|say \"hi\", twice|<none>|2.50|2024-05-01|2024-05-01 13:45:00.5|AQL/|true", Joined(records.Next()));
        string?[]? second = records.Next();
        Assert.True(second is [_, "two\nlines", "", ..] && second[4] is null, $"An empty field and empty text read back otherwise: {string.Join("|", second ?? [])}");
        Assert.Null(records.Next());
        Assert.Equal("a;\"b;c\";d,e\n", RowWriter.Written(into => new CsvRows(into, ';', header: false, "\n"), [new("x"), new("y"), new("z")], [["a", "b;c", "d,e"]]));
    }

    private static string Joined(string?[]? record) => record is null ? "<no record>" : string.Join("|", record.Select(f => f ?? "<none>"));

    private void A_csvs_records_are_read_as_rfc_4180_reads_them() => Cases.Each<(string Text, string?[][] Records)>(
    [
        ("a,b\r\nc,d\ne,f\rg,h", [["a", "b"], ["c", "d"], ["e", "f"], ["g", "h"]]),
        ("\"a\r\nb\",\"c\"\"d\"\n", [["a\r\nb", "c\"d"]]),
        ("a,b\n\n\r\nc,\n", [["a", "b"], ["c", null]]),
        ("\"ab\"cd,e", [["abcd", "e"]]),
        ("\"open, never closed", [["open, never closed"]]),
    ], row =>
    {
        CsvRecords records = new(new StringReader(row.Text), ',');
        List<string?[]> read = [];
        while (records.Next() is { } record)
        {
            read.Add(record);
        }
        Assert.True(read.Count == row.Records.Length && read.Zip(row.Records).All(p => p.First.SequenceEqual(p.Second)),
            $"Read {row.Text.ReplaceLineEndings("⏎")} as {string.Join(" / ", read.Select(r => string.Join("|", r.Select(f => f ?? "<null>"))))}");
    });

    private static async Task Rows_written_as_json_read_back_as_their_values()
    {
        RowColumn[] columns = [.. Columns, new("doc", "jsonb"), new("tags", "_text"), new("name", "text"), new("odd", "float8")];
        object?[] row = [.. Row, "{\"a\": [1, 2]}", new[] { "x", "y" }, "é\t", double.NaN];
        string text = RowWriter.Written(into => new JsonRows(into, "\n"), columns, [row]);
        Assert.Equal("[\n  {\"id\":7,\"name\":\"say \\\"hi\\\", twice\",\"note\":null,\"price\":2.50,\"on\":\"2024-05-01\",\"at\":\"2024-05-01T13:45:00.5\","
            + "\"bytes\":\"AQL/\",\"ok\":true,\"doc\":{\"a\": [1, 2]},\"tags\":[\"x\",\"y\"],\"name_2\":\"é\\t\",\"odd\":\"NaN\"}\n]\n", text);
        Assert.Equal("[]\n", RowWriter.Written(into => new JsonRows(into, "\n"), columns, []));
        foreach (string file in (string[])[text, "{\"id\": 1, \"name\": null}\n{\"id\": 2.5, \"ok\": false}\n"])
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(file));
            List<(string Name, string? Value)[]> read = [];
            await foreach (IReadOnlyList<(string Name, string? Value)> values in JsonRecords.ReadAsync(stream, TestContext.Current.CancellationToken))
            {
                read.Add([.. values]);
            }
            (string Name, string? Value)[][] expected = file == text
                ? [[("id", "7"), ("name", "say \"hi\", twice"), ("note", null), ("price", "2.50"), ("on", "2024-05-01"), ("at", "2024-05-01T13:45:00.5"), ("bytes", "AQL/"),
                    ("ok", "true"), ("doc", "{\"a\": [1, 2]}"), ("tags", "[\"x\",\"y\"]"), ("name_2", "é\t"), ("odd", "NaN")]]
                : [[("id", "1"), ("name", null)], [("id", "2.5"), ("ok", "false")]];
            Assert.Equal(expected, read);
        }
    }

    private void A_quoted_value_holding_semicolons_leaves_a_comma_separated_file_read_at_its_commas()
    {
        Assert.True(CsvTable.SeparatorFor("data.csv", "name,\"a;b;c\",x\n1,2,3\n") == ',',
            "The semicolons inside a quoted value were counted as separators, so the file's columns would run together and its values be cut");
        const string Text = "id,\"a;b;c\"\n1,\"x;y\"\n";
        CsvTable table = CsvTable.Parse(Text, CsvTable.SeparatorFor("data.csv", Text), header: true);
        Assert.Equal(["id", "a;b;c"], Enumerable.Range(0, table.ColumnCount).Select(table.Header));
        Assert.Equal("x;y", table.Cell(0, 1));
    }

    private void A_csvs_separator_is_counted_between_the_first_records_fields_alone() => Cases.Each<(string text, char separator)>(
    [
        ("name,\"a;b;c\"", ','),
        ("Name;\"Smith, Jones\";Total", ';'),
        ("\"a;\nb;c;d\",x,y\n1;2;3;4;5;6", ','),
        ("Price;Size 5\" screen;Note,more", ';'),
        ("a;b;c", ';'),
        ("", ','),
    ], row =>
    {
        (string text, char separator) = row;
        char found = CsvTable.SeparatorFor("data.csv", text);
        Assert.True(found == separator, $"Read '{text}' as separated by '{found}', not '{separator}': its columns would run together");
    });

    private void A_tsv_is_read_at_its_tabs_whatever_its_first_line_holds()
    {
        Assert.Equal('\t', CsvTable.SeparatorFor("data.TSV", "a;b;c"));
    }
}

using Passenger.App.Tables;

namespace Passenger.UnitTests.Tables;

/// <summary>The separator a CSV is read with: whichever of comma and semicolon its first record has more of between its
/// fields, those inside a quoted field (line breaks too) never counted.</summary>
public class CsvTableTests
{
    [Fact]
    public void A_csv_is_read_with_the_separator_its_first_record_has_more_of() => Cases.All(
        ("A quoted value holding semicolons leaves a comma separated file read at its commas", A_quoted_value_holding_semicolons_leaves_a_comma_separated_file_read_at_its_commas),
        ("A csvs separator is counted between the first records fields alone", A_csvs_separator_is_counted_between_the_first_records_fields_alone),
        ("A tsv is read at its tabs whatever its first line holds", A_tsv_is_read_at_its_tabs_whatever_its_first_line_holds));

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

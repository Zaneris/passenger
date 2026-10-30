using Passenger.App.Tables;

namespace Passenger.UnitTests.Tables;

/// <summary>The separator a CSV is read with: whichever of comma and semicolon its first record has more of between its
/// fields, those inside a quoted field (line breaks too) never counted.</summary>
public class CsvTableTests
{
    [Fact]
    public void A_quoted_value_holding_semicolons_leaves_a_comma_separated_file_read_at_its_commas()
    {
        Assert.True(CsvTable.SeparatorFor("data.csv", "name,\"a;b;c\",x\n1,2,3\n") == ',',
            "The semicolons inside a quoted value were counted as separators, so the file's columns would run together and its values be cut");
        const string Text = "id,\"a;b;c\"\n1,\"x;y\"\n";
        CsvTable table = CsvTable.Parse(Text, CsvTable.SeparatorFor("data.csv", Text), header: true);
        Assert.Equal(["id", "a;b;c"], Enumerable.Range(0, table.ColumnCount).Select(table.Header));
        Assert.Equal("x;y", table.Cell(0, 1));
    }

    [Theory]
    [InlineData("name,\"a;b;c\"", ',')]
    [InlineData("name,\"a;b;c;d;e\",x", ',')]
    [InlineData("Name;\"Smith, Jones\";Total", ';')]
    [InlineData("\"a;\nb;c;d\",x,y\n1;2;3;4;5;6", ',')]
    [InlineData("\"say \"\"a;b;c\"\"\";x;y,z", ';')]
    [InlineData("Price;Size 5\" screen;Note,more", ';')]
    [InlineData("a;b;c", ';')]
    [InlineData("a,b;c", ',')]
    [InlineData("", ',')]
    public void A_csvs_separator_is_counted_between_the_first_records_fields_alone(string text, char separator)
    {
        char found = CsvTable.SeparatorFor("data.csv", text);
        Assert.True(found == separator, $"Read '{text}' as separated by '{found}', not '{separator}': its columns would run together");
    }

    [Fact]
    public void A_tsv_is_read_at_its_tabs_whatever_its_first_line_holds()
    {
        Assert.Equal('\t', CsvTable.SeparatorFor("data.TSV", "a;b;c"));
    }
}

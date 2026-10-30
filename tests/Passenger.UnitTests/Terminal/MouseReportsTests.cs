using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>A mouse report's bytes as the program reads them: the original form's place a byte each, one past 127
/// written as it is, and SGR's in its digits.</summary>
public class MouseReportsTests
{
    [Fact]
    public void A_click_at_column_96_reaches_the_program_as_the_byte_0x80()
    {
        byte[]? report = MouseReports.Of(0, 96, 1, release: false, sgr: false);
        Assert.True(report is [0x1b, 0x5b, 0x4d, 0x20, 0x80, 0x21],
            $"A press at column 96, row 1 was written {Hex(report)}: the program reads the click somewhere else");
    }

    [Fact]
    public void The_original_forms_last_place_is_column_and_row_223_and_a_release_names_no_button()
    {
        byte[]? report = MouseReports.Of(2, 223, 223, release: true, sgr: false);
        Assert.True(report is [0x1b, 0x5b, 0x4d, 0x23, 0xff, 0xff], $"A release at column and row 223 was written {Hex(report)}");
        Assert.True(MouseReports.Of(0, 224, 1, release: false, sgr: false) is null,
            "A press at column 224, which the original form's byte cannot hold, was reported somewhere else");
    }

    [Fact]
    public void An_SGR_report_names_its_place_in_digits_past_column_223()
    {
        Assert.Equal("\e[<0;300;2M", System.Text.Encoding.ASCII.GetString(MouseReports.Of(0, 300, 2, release: false, sgr: true)!));
        Assert.Equal("\e[<2;5;7m", System.Text.Encoding.ASCII.GetString(MouseReports.Of(2, 5, 7, release: true, sgr: true)!));
    }

    private static string Hex(byte[]? bytes) => bytes is null ? "nothing" : Convert.ToHexString(bytes);
}

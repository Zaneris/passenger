using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>How the lines two texts have alike changed their separators (<see cref="LineDiff.SeparatorChanges(string, string)"/>),
/// which a comparison of their lines alone never sees.</summary>
public class SeparatorChangesTests
{
    [Theory]
    [InlineData("a\nb\nc\n", "a\r\nb\r\nc\r\n", "Line separators changed: 3 lines from LF to CRLF")]
    [InlineData("a\r\nb\r\nc\r\n", "a\nb\r\nc\r\n", "Line separators changed: 1 line from CRLF to LF")]
    [InlineData("a\nb", "a\nb\n", "Line separators changed: LF added at the end")]
    [InlineData("a\r\nb\r\n", "a\r\nb", "Line separators changed: CRLF removed from the end")]
    [InlineData("a\nb\nc", "a\r\nb\r\nc\r\n", "Line separators changed: 2 lines from LF to CRLF; CRLF added at the end")]
    public void Lines_alike_but_for_their_separators_are_said_by_how_many_from_which_to_which(string before, string after, string said) =>
        Assert.Equal(said, LineDiff.SeparatorChanges(before, after));

    [Theory]
    [InlineData("a\nb\nc\n", "a\nb\nc\n")]
    [InlineData("a\r\nb\r\n", "a\r\nb\r\n")]
    [InlineData("", "")]
    [InlineData("a\nb\n", "a\nB\n")]
    public void Texts_whose_lines_alike_keep_their_separators_have_none_changed(string before, string after) =>
        Assert.Null(LineDiff.SeparatorChanges(before, after));

    [Fact]
    public void A_changed_lines_separator_goes_with_its_change_and_only_the_lines_alike_are_counted()
    {
        // Line b changed; a and c turned CRLF, and the lines around the change keep their pairs.
        Assert.Equal("Line separators changed: 2 lines from LF to CRLF", LineDiff.SeparatorChanges("a\nb\nc\n", "a\r\nB\r\nc\r\n"));
        Assert.Equal("Line separators changed: 2 lines from LF to CRLF", LineDiff.SeparatorChanges("a\nb\nc\nd\n", "a\r\nx\ny\nc\nd\r\n"));
    }
}

using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A string's lines start where a document of it starts them, an offset's line counting from zero; a line's
/// indentation ends at its first character that is no space or tab.</summary>
public sealed class LineStartsTests
{
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("ab", 2, 0)]
    [InlineData("a\nb", 1, 0)]
    [InlineData("a\nb", 2, 1)]
    [InlineData("a\r\nb", 2, 0)]
    [InlineData("a\r\nb", 3, 1)]
    [InlineData("a\rb", 2, 0)]
    [InlineData("a\n\nb", 3, 2)]
    [InlineData("a\n", 2, 1)]
    public void An_offsets_line_is_the_one_a_document_of_the_text_puts_it_on(string text, int offset, int line)
    {
        LineStarts lines = new(text);
        TextSnapshot document = TextSnapshot.From(text);
        Assert.Equal(document.LineCount, lines.Count);
        Assert.Equal(document.GetLineFromOffset(offset), lines.LineOf(offset));
        Assert.Equal(line, lines.LineOf(offset));
        Assert.Equal(document.GetLineStart(line), lines[line]);
    }

    [Fact]
    public void An_offset_before_the_text_is_on_its_first_line()
    {
        Assert.Equal(0, new LineStarts("a\nb").LineOf(-1));
    }

    [Theory]
    [InlineData("\t  x = 1", 0, 3)]
    [InlineData("a\n    b", 2, 6)]
    [InlineData("a\n  ", 2, 4)]
    [InlineData("a\n\r\n", 2, 2)]
    public void A_lines_indentation_in_a_string_ends_where_its_snapshot_says(string text, int lineStart, int end)
    {
        TextSnapshot document = TextSnapshot.From(text);
        Assert.Equal(end, TextNavigation.IndentEnd(text, lineStart));
        Assert.Equal(TextNavigation.IndentEnd(document, document.GetLine(document.GetLineFromOffset(lineStart))), TextNavigation.IndentEnd(text, lineStart));
    }

    [Theory]
    [InlineData("\nab", 0, 0)]
    [InlineData("ab\ncd", 2, 0)]
    [InlineData("ab\ncd", 3, 3)]
    [InlineData("ab\ncd", 5, 3)]
    [InlineData("ab\r\ncd", 3, 0)]
    public void A_line_of_a_string_starts_after_the_line_feed_before_it(string text, int offset, int start)
    {
        Assert.Equal(start, TextNavigation.LineStart(text, offset));
    }
}

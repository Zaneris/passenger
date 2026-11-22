using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A string's lines start where a document of it starts them, an offset's line counting from zero; a line's
/// indentation ends at its first character that is no space or tab, and its end and the next line's start are read as
/// its start is.</summary>
public sealed class LineStartsTests
{
    [Fact]
    public void A_strings_lines_start_and_end_where_a_document_of_it_puts_them() => Cases.All(
        ("An offsets line is the one a document of the text puts it on", An_offsets_line_is_the_one_a_document_of_the_text_puts_it_on),
        ("An offset before the text is on its first line", An_offset_before_the_text_is_on_its_first_line),
        ("A lines indentation in a string ends where its snapshot says", A_lines_indentation_in_a_string_ends_where_its_snapshot_says),
        ("A lines indentation in a string is what its snapshot gives the line", A_lines_indentation_in_a_string_is_what_its_snapshot_gives_the_line));

    private void An_offsets_line_is_the_one_a_document_of_the_text_puts_it_on() => Cases.Each<(string text, int offset, int line)>(
    [
        ("", 0, 0),
        ("a\nb", 2, 1),
        ("a\r\nb", 3, 1),
        ("a\n", 2, 1),
    ], row =>
    {
        (string text, int offset, int line) = row;
        LineStarts lines = new(text);
        TextSnapshot document = TextSnapshot.From(text);
        Assert.Equal(document.LineCount, lines.Count);
        Assert.Equal(document.GetLineFromOffset(offset), lines.LineOf(offset));
        Assert.Equal(line, lines.LineOf(offset));
        Assert.Equal(document.GetLineStart(line), lines[line]);
    });

    private void An_offset_before_the_text_is_on_its_first_line()
    {
        Assert.Equal(0, new LineStarts("a\nb").LineOf(-1));
    }

    private void A_lines_indentation_in_a_string_ends_where_its_snapshot_says() => Cases.Each<(string text, int lineStart, int end)>(
    [
        ("\t  x = 1", 0, 3),
        ("a\n    b", 2, 6),
        ("a\n  ", 2, 4),
        ("a\n\r\n", 2, 2),
    ], row =>
    {
        (string text, int lineStart, int end) = row;
        TextSnapshot document = TextSnapshot.From(text);
        Assert.Equal(end, TextNavigation.IndentEnd(text, lineStart));
        Assert.Equal(TextNavigation.IndentEnd(document, document.GetLine(document.GetLineFromOffset(lineStart))), TextNavigation.IndentEnd(text, lineStart));
    });

    private void A_lines_indentation_in_a_string_is_what_its_snapshot_gives_the_line() => Cases.Each<(string text, int offset, string indent)>(
    [
        ("a\n    b", 6, "    "),
        ("\t x\r\n  y", 3, "\t "),
        ("  a\r\n\tb", 6, "\t"),
        ("a\n", 2, ""),
    ], row =>
    {
        (string text, int offset, string indent) = row;
        Assert.Equal(indent, TextNavigation.IndentOf(text, offset));
        Assert.Equal(TextNavigation.IndentOf(TextSnapshot.From(text), offset), TextNavigation.IndentOf(text, offset));
    });

    [Fact]
    public void A_strings_line_indentation_and_next_line_are_what_its_snapshot_gives() => Cases.All(
        ("A line of a string starts after the line feed before it", A_line_of_a_string_starts_after_the_line_feed_before_it),
        ("A line of a string ends before its line break", A_line_of_a_string_ends_before_its_line_break),
        ("The next line of a string starts past the line feed ending the offsets", The_next_line_of_a_string_starts_past_the_line_feed_ending_the_offsets));

    private void A_line_of_a_string_starts_after_the_line_feed_before_it() => Cases.Each<(string text, int offset, int start)>(
    [
        ("\nab", 0, 0),
        ("ab\ncd", 3, 3),
        ("ab\r\ncd", 3, 0),
        ("", 2, 0),
    ], row =>
    {
        (string text, int offset, int start) = row;
        Assert.Equal(start, TextNavigation.LineStart(text, offset));
    });

    /// <summary>A line ends before its '\n' and a '\r' just before that, never before the offset asked from; a lone '\r'
    /// breaks no line, as <see cref="TextNavigation.LineStart(string, int)"/> reads them.</summary>
    private void A_line_of_a_string_ends_before_its_line_break() => Cases.Each<(string text, int offset, int end)>(
    [
        ("ab\ncd", 0, 2),
        ("ab\r\ncd", 2, 2),
        ("ab\ncd", 4, 5),
        ("ab", 5, 2),
    ], row =>
    {
        (string text, int offset, int end) = row;
        Assert.Equal(end, TextNavigation.LineEnd(text, offset));
    });

    private void The_next_line_of_a_string_starts_past_the_line_feed_ending_the_offsets() => Cases.Each<(string text, int offset, int next)>(
    [
        ("ab\ncd", 0, 3),
        ("ab\ncd", 2, 3),
        ("ab\ncd", 3, 5),
        ("ab", 9, 2),
    ], row =>
    {
        (string text, int offset, int next) = row;
        Assert.Equal(next, TextNavigation.NextLineStart(text, offset));
    });
}

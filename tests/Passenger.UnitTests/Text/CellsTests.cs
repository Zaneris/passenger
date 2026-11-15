using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>One count of the columns text takes on screen (<see cref="WideCharacters.Cells"/>, read through
/// <see cref="TextNavigation.ColumnAfter(ReadOnlySpan{char}, int, int)"/>): a wide character two, a mark none, a tab to
/// its next stop, as the editor's Tab and the snapshot's count read it.</summary>
public class CellsTests
{
    private static readonly IndentOptions Spaces = new(4, UseTabs: false, 4);

    [Theory]
    [InlineData("a|", "a   |")]
    [InlineData("日|", "日  |")]
    [InlineData("cafe\u0301|", "cafe\u0301    |")]
    [InlineData("x\t日|", "x\t日  |")]
    public void Tab_reaches_the_next_stop_as_the_line_is_drawn(string before, string after)
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Indent(text, caret, Spaces)));
    }

    [Fact]
    public void A_lines_columns_read_a_block_at_a_time_take_a_pair_a_blocks_end_cuts_whole()
    {
        string line = new string('a', 255) + "\U0001F600x";
        TextSnapshot text = TextSnapshot.From("first\n" + line);
        int end = text.Length;
        Assert.True(TextNavigation.VisualColumn(text, end, 4) == 258, $"The line's columns came to {TextNavigation.VisualColumn(text, end, 4)}, not 255 letters, an emoji's two and one more");
        Assert.Equal(TextNavigation.ColumnAfter(line, 0, 4), TextNavigation.VisualColumn(text, end, 4));
    }

    [Theory]
    [InlineData(0x41, 1)]
    [InlineData(0x65E5, 2)]
    [InlineData(0x0301, 0)]
    [InlineData(0x200D, 0)]
    [InlineData(0x1161, 0)]
    [InlineData(0x093E, 1)]
    [InlineData(0x1F600, 2)]
    [InlineData(0x1F1FA, 2)]
    public void A_characters_cells_are_the_terminals_widths(int rune, int cells) =>
        Assert.Equal(cells, WideCharacters.Cells(rune));
}

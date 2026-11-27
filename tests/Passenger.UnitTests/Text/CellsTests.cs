using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>One count of the columns text takes on screen (<see cref="WideCharacters.Cells"/>, read through
/// <see cref="TextNavigation.ColumnAfter(ReadOnlySpan{char}, int, int)"/>): a wide character two, a mark none, a tab to
/// its next stop, a control character or a line or paragraph separator its drawn mark's (<see cref="ControlCharacters"/>),
/// as the editor's Tab and the snapshot's count read it.</summary>
public class CellsTests
{
    private static readonly IndentOptions Spaces = new(4, UseTabs: false, 4);

    [Fact]
    public void Texts_columns_on_screen_are_counted_one_way() => Cases.All(
        ("Tab reaches the next stop as the line is drawn", Tab_reaches_the_next_stop_as_the_line_is_drawn),
        ("A lines columns read a block at a time take a pair a blocks end cuts whole", A_lines_columns_read_a_block_at_a_time_take_a_pair_a_blocks_end_cuts_whole),
        ("A characters cells are the terminals widths", A_characters_cells_are_the_terminals_widths));

    private void Tab_reaches_the_next_stop_as_the_line_is_drawn() => Cases.Each<(string before, string after)>(
    [
        ("a|", "a   |"),
        ("日|", "日  |"),
        ("cafe\u0301|", "cafe\u0301    |"),
        ("x\t日|", "x\t日  |"),
        ("\f|", "\f  |"),
        ("x\u0085|", "x\u0085   |"),
        ("x\u2028\u2029|", "x\u2028\u2029 |"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Indent(text, caret, Spaces)));
    });

    private void A_lines_columns_read_a_block_at_a_time_take_a_pair_a_blocks_end_cuts_whole()
    {
        string line = new string('a', 255) + "\U0001F600x";
        TextSnapshot text = TextSnapshot.From("first\n" + line);
        int end = text.Length;
        Assert.True(TextNavigation.VisualColumn(text, end, 4) == 258, $"The line's columns came to {TextNavigation.VisualColumn(text, end, 4)}, not 255 letters, an emoji's two and one more");
        Assert.Equal(TextNavigation.ColumnAfter(line, 0, 4), TextNavigation.VisualColumn(text, end, 4));
    }

    private void A_characters_cells_are_the_terminals_widths() => Cases.Each<(int rune, int cells)>(
    [
        (0x41, 1),
        (0x65E5, 2),
        (0x200D, 0),
        (0x1161, 0),
        (0x1F600, 2),
        (0x1F1FA, 2),
    ], row =>
    {
        (int rune, int cells) = row;
        
                Assert.Equal(cells, WideCharacters.Cells(rune));
    });
}

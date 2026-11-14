using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>Text an input method composes takes the terminal's cells from the cursor as the program will count it once it is
/// typed: a kana, a Han character or a Hangul syllable two columns, a pinyin letter one, a letter with its marks one
/// character; a character that would pass the last column starts the next row, and the input method's cursor stands at
/// the character it is before.</summary>
public class ComposedCellsTests
{
    private static string Laid(string text, int column, int columns) =>
        string.Join(" ", ComposedCells.Lay(text, column, columns).Select(c => $"{text.Substring(c.Start, c.Length)}@{c.Row}:{c.Column}+{c.Width}"));

    [Fact]
    public void Kana_take_two_columns_each_from_the_cursor_and_the_input_methods_cursor_ends_past_them()
    {
        Assert.Equal("に@0:3+2 ほ@0:5+2 ん@0:7+2", Laid("にほん", 3, 80));
        List<ComposedCell> cells = ComposedCells.Lay("にほん", 3, 80);
        Assert.Equal((0, 9), ComposedCells.CursorOf(cells, null, 3));
        Assert.Equal((0, 9), ComposedCells.CursorOf(cells, 3, 3));
        Assert.Equal((0, 5), ComposedCells.CursorOf(cells, 1, 3));
    }

    [Fact]
    public void Pinyin_takes_a_column_a_letter_and_the_input_methods_cursor_stands_before_the_letter_it_names()
    {
        Assert.Equal("n@0:0+1 i@0:1+1 '@0:2+1 h@0:3+1 a@0:4+1 o@0:5+1", Laid("ni'hao", 0, 80));
        Assert.Equal((0, 3), ComposedCells.CursorOf(ComposedCells.Lay("ni'hao", 0, 80), 3, 0));
    }

    [Fact]
    public void A_wide_character_that_would_pass_the_last_column_starts_the_next_row_and_a_narrow_one_fills_it()
    {
        Assert.Equal("日@0:77+2 本@1:0+2 語@1:2+2", Laid("日本語", 77, 80));
        Assert.Equal((1, 4), ComposedCells.CursorOf(ComposedCells.Lay("日本語", 77, 80), null, 77));
        Assert.Equal("a@0:78+1 b@0:79+1 c@1:0+1", Laid("abc", 78, 80));
        Assert.Equal((0, 80), ComposedCells.CursorOf(ComposedCells.Lay("ab", 78, 80), null, 78));
    }

    [Fact]
    public void A_letter_with_its_marks_is_one_character_of_one_column_and_a_hangul_syllable_two()
    {
        Assert.Equal("é@0:0+1 x@0:1+1", Laid("éx", 0, 80));
        Assert.Equal((0, 1), ComposedCells.CursorOf(ComposedCells.Lay("éx", 0, 80), 2, 0));
        Assert.Equal("한@0:10+2 글@0:12+2", Laid("한글", 10, 80));
    }

    [Fact]
    public void No_composition_leaves_the_input_methods_cursor_at_the_terminals()
    {
        Assert.Empty(ComposedCells.Lay(string.Empty, 5, 80));
        Assert.Equal((0, 5), ComposedCells.CursorOf([], null, 5));
    }
}

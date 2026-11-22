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
    public void An_input_methods_composition_takes_its_characters_columns_and_its_cursor_stands_where_it_names() => Cases.All(
        ("Kana take two columns each from the cursor and the input methods cursor ends past them", Kana_take_two_columns_each_from_the_cursor_and_the_input_methods_cursor_ends_past_them),
        ("Pinyin takes a column a letter and the input methods cursor stands before the letter it names", Pinyin_takes_a_column_a_letter_and_the_input_methods_cursor_stands_before_the_letter_it_names),
        ("No composition leaves the input methods cursor at the terminals", No_composition_leaves_the_input_methods_cursor_at_the_terminals));

    private void Kana_take_two_columns_each_from_the_cursor_and_the_input_methods_cursor_ends_past_them()
    {
        Assert.Equal("に@0:3+2 ほ@0:5+2 ん@0:7+2", Laid("にほん", 3, 80));
        List<ComposedCell> cells = ComposedCells.Lay("にほん", 3, 80);
        Assert.Equal((0, 9), ComposedCells.CursorOf(cells, null, 3));
        Assert.Equal((0, 9), ComposedCells.CursorOf(cells, 3, 3));
        Assert.Equal((0, 5), ComposedCells.CursorOf(cells, 1, 3));
    }

    private void Pinyin_takes_a_column_a_letter_and_the_input_methods_cursor_stands_before_the_letter_it_names()
    {
        Assert.Equal("n@0:0+1 i@0:1+1 '@0:2+1 h@0:3+1 a@0:4+1 o@0:5+1", Laid("ni'hao", 0, 80));
        Assert.Equal((0, 3), ComposedCells.CursorOf(ComposedCells.Lay("ni'hao", 0, 80), 3, 0));
    }

    private void No_composition_leaves_the_input_methods_cursor_at_the_terminals()
    {
        Assert.Empty(ComposedCells.Lay(string.Empty, 5, 80));
        Assert.Equal((0, 5), ComposedCells.CursorOf([], null, 5));
    }
}

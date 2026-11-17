using Passenger.Terminal;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The pieces a screen reader steps a line by: a character as a person sees one, and a word as a run of one kind
/// of character with the white space after it, the words following one another to the line's end; and a terminal row's
/// text placed by its cells, a wide character's two cells one character.</summary>
public class ReadingUnitsTests
{
    [Theory]
    [InlineData("int count = 0;", 5, "count ")]
    [InlineData("int count = 0;", 3, "int ")]
    [InlineData("a->b", 1, "->")]
    [InlineData("a->b", 3, "b")]
    [InlineData("    return x;", 2, "    ")]
    [InlineData("    return x;", 4, "return ")]
    [InlineData("snake_case2 + 1", 4, "snake_case2 ")]
    [InlineData("cafe\u0301 au lait", 4, "cafe\u0301 ")]
    [InlineData("x", 7, "x")]
    public void A_word_is_the_run_of_one_kind_of_character_holding_the_place_with_the_white_space_after_it(string line, int at, string word)
    {
        (int start, int end) = ReadingUnits.WordAt(line, at);
        Assert.True(line[start..end] == word, $"The word at {at} of \"{line}\" reads \"{line[start..end]}\", not \"{word}\"");
    }

    [Fact]
    public void A_lines_words_follow_one_another_to_its_end()
    {
        const string Line = "if (count >= 10) { Say(\"done\"); }";
        List<string> words = [];
        for (int at = 0; at < Line.Length;)
        {
            (int start, int end) = ReadingUnits.WordAt(Line, at);
            Assert.True(start == at && end > at, $"The word at {at} runs {start} to {end}, leaving a gap or going back");
            words.Add(Line[start..end]);
            at = end;
        }
        Assert.Equal(["if ", "(", "count ", ">= ", "10", ") ", "{ ", "Say", "(\"", "done", "\"); ", "}"], words);
    }

    [Theory]
    [InlineData("ab", 1, "b")]
    [InlineData("e\u0301x", 1, "e\u0301")]
    [InlineData("a\U0001F600b", 2, "\U0001F600")]
    [InlineData("\U0001F469\u200D\U0001F4BB!", 3, "\U0001F469\u200D\U0001F4BB")]
    [InlineData("\r\n", 0, "\r\n")]
    public void A_character_is_what_a_person_sees_as_one(string line, int at, string character)
    {
        (int start, int end) = ReadingUnits.CharacterAt(line, at);
        Assert.True(line[start..end] == character, $"The character at {at} of \"{line}\" reads \"{line[start..end]}\", not \"{character}\"");
    }

    [Fact]
    public void A_terminal_rows_text_is_placed_by_its_cells_a_wide_character_one_character_in_two()
    {
        TerminalScreen screen = new(20, 3);
        screen.Feed("a\u4E2Db\u00E9  ");
        TerminalLine row = screen.Lines[0];
        Assert.Equal("a\u4E2Db\u00E9", row.Text());
        Assert.Equal(row.Text().Length, row.TextLength());
        Assert.Equal([0, 1, 1, 2, 3, 4, 4], Enumerable.Range(0, 7).Select(row.IndexOf));
        Assert.Equal([0, 1, 3, 4, 5, 6], Enumerable.Range(0, 6).Select(row.ColumnOf));
    }
}

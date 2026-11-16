using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>The links a program makes (OSC 8): what it writes between a link's start and end is linked to its address,
/// whichever of ST and BEL ends each, its parameters passed over, the text as written; a mark or a flag's second letter
/// joins a linked cell as it joins any; a reflow keeps each cell's link; text written over a link, an erase and a reset
/// leave none.</summary>
public class TerminalLinksTests
{
    private const string Docs = "https://example.com/docs";

    [Fact]
    public void What_a_program_writes_inside_a_link_is_linked_to_its_address_and_nothing_else_is()
    {
        TerminalScreen screen = new(40, 5);
        screen.Feed($"a \e]8;;{Docs}\e\\docs\e]8;;\e\\ b \e]8;id=x:y;https://example.com/other\aé\e]8;;\a c");
        Assert.Equal("a docs b é c", screen.Lines[0].Text());
        Assert.Equal("..DDDD...O..", Links(screen.Lines[0], 12));
    }

    [Fact]
    public void A_mark_or_a_flags_second_letter_joins_a_linked_cell_and_keeps_its_link()
    {
        TerminalScreen screen = new(40, 5);
        screen.Feed($"\e]8;;{Docs}\e\\é\U0001F1EB\U0001F1F7!\e]8;;\e\\.");
        TerminalLine line = screen.Lines[0];
        Assert.Equal("é\U0001F1EB\U0001F1F7!.", line.Text());
        Assert.True((line.TextOf(1), line.Cells[1].Width) == ("\U0001F1EB\U0001F1F7", 2),
            $"A flag's two letters inside a link are not one grapheme in two columns: '{line.TextOf(1)}' in {line.Cells[1].Width}");
        Assert.Equal("DD.D.", Links(line, 5));
    }

    [Fact]
    public void A_reflow_keeps_each_cells_link_on_the_rows_it_wraps_onto()
    {
        TerminalScreen screen = new(20, 5);
        screen.Feed($"0123456789\e]8;;{Docs}\e\\linked\e]8;;\e\\ plain\r\n");
        screen.Resize(8, 5);
        Assert.Equal("0123456789linked plain", string.Concat(Enumerable.Range(0, 3).Select(row => screen.Lines[row].Text().PadRight(8))).TrimEnd());
        Assert.Equal(["........", "..DDDDDD", "........"], Enumerable.Range(0, 3).Select(row => Links(screen.Lines[row], 8)));
    }

    [Fact]
    public void Text_written_over_a_link_an_erase_and_a_reset_leave_no_link()
    {
        TerminalScreen screen = new(20, 5);
        screen.Feed($"\e]8;;{Docs}\e\\abcd\r\nefgh\e]8;;\e\\\e[1;2HX\e[2;3H\e[K");
        Assert.Equal("D.DD", Links(screen.Lines[0], 4));
        Assert.Equal("DD..", Links(screen.Lines[1], 4));

        screen.Feed($"\r\n\e]8;;{Docs}\e\\\ecstill open");
        Assert.Equal("still open", screen.Lines[0].Text());
        Assert.Equal("..........", Links(screen.Lines[0], 10));
    }

    /// <summary>Each cell's link in a letter: D for the docs' address, O for another, a dot for none.</summary>
    private static string Links(TerminalLine line, int columns) =>
        string.Concat(Enumerable.Range(0, columns).Select(column => line.LinkOf(column) switch { null => '.', Docs => 'D', _ => 'O' }));
}

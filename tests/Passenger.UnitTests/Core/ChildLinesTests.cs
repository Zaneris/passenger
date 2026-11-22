using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A child's output heard as lines (ChildProcesses.Hear), whatever pieces its pipe cuts it into: each line without
/// its end, which is "\n", "\r\n" or a lone "\r", and with a longest each line cut there, the rest of it passed over.</summary>
public class ChildLinesTests
{
    [Fact]
    public void A_childs_output_is_heard_as_lines_whatever_pieces_its_pipe_cuts_it_into() => Cases.All(
        ("Lines come without their ends and a last line without an end is a line", Lines_come_without_their_ends_and_a_last_line_without_an_end_is_a_line),
        ("A CRLF cut between two pieces reads as if it came in one", A_CRLF_cut_between_two_pieces_reads_as_if_it_came_in_one),
        ("A line past the longest is cut there and the lines after it come whole", A_line_past_the_longest_is_cut_there_and_the_lines_after_it_come_whole),
        ("A longest of none is refused", A_longest_of_none_is_refused));

    private void Lines_come_without_their_ends_and_a_last_line_without_an_end_is_a_line()
    {
        Assert.Equal(["one", string.Empty, "two", "a", "b", string.Empty, "last"], Lines(["one\r\n\ntwo\na\rb\r\n\r\nlast"]));
        Assert.Equal(["one", "two"], Lines(["one\ntwo\n"]));
        Assert.Equal(["one"], Lines(["one\r"]));
        Assert.Empty(Lines([string.Empty]));
        Assert.Empty(Lines([]));
    }

    /// <summary>Every place the text can be cut into two pieces, and pieces of one character: a "\r\n" cut between two is
    /// still one end, and a lone "\r" at a piece's end still ends its line.</summary>
    private void A_CRLF_cut_between_two_pieces_reads_as_if_it_came_in_one()
    {
        const string Text = "first\r\nsecond\r\rthird\n\r\nfourth\r";
        string[] expected = ["first", "second", string.Empty, "third", string.Empty, "fourth"];
        for (int cut = 0; cut <= Text.Length; cut++)
        {
            Assert.Equal(expected, Lines([Text[..cut], Text[cut..]]));
        }
        Assert.Equal(expected, Lines([.. Text.Select(c => c.ToString())]));
    }

    /// <summary>A line past the longest is cut there, however the pieces fall, the lines after it whole; the longest itself and
    /// shorter come whole.</summary>
    private void A_line_past_the_longest_is_cut_there_and_the_lines_after_it_come_whole()
    {
        string over = new('x', 10_000);
        string text = $"short\n{over}\r\n{new string('y', 8)}\n{over}";
        string[] expected = ["short", new string('x', 8), new string('y', 8), new string('x', 8)];
        Assert.Equal(expected, Lines([text], longest: 8));
        Assert.Equal(expected, Lines([.. text.Chunk(7).Select(c => new string(c))], longest: 8));
        Assert.Equal(["short", over, new string('y', 8), over], Lines([text]));
    }

    private void A_longest_of_none_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChildLines(_ => { }, longest: 0));
    }

    private static List<string> Lines(string[] pieces, int? longest = null)
    {
        List<string> heard = [];
        ChildLines lines = new(heard.Add, longest);
        foreach (string piece in pieces)
        {
            lines.Read(piece);
        }
        lines.End();
        return heard;
    }
}

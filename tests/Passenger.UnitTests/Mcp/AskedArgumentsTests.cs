using Passenger.App.Mcp;

namespace Passenger.UnitTests.Mcp;

/// <summary>What the person's question shows of an agent's call: each argument on one line, whatever its value holds, so
/// none reads as another or hides what it says, and a long one cut saying so.</summary>
public sealed class AskedArgumentsTests
{
    /// <summary>A value's line breaks are written as escapes, so a value that goes on to look like another argument stays on
    /// its own line: the question shows two arguments, not three.</summary>
    [Fact]
    public void A_values_line_breaks_are_escaped_so_it_never_reads_as_another_argument()
    {
        string shown = AskedArguments.Of([("path", "Notes.txt\npath: README.md"), ("newText", "one\r\ntwo\tthree")]);
        Assert.Equal("path: Notes.txt\\npath: README.md\nnewText: one\\r\\ntwo\\tthree", shown);
        Assert.Equal(2, shown.Split('\n').Length);
    }

    /// <summary>A character that turns the text after it around, joins it unseen or breaks a line where Unicode says so is
    /// shown by its code, as a name of an argument is: "evil.txt" written backwards reads as what it is.</summary>
    [Fact]
    public void Characters_that_reverse_hide_or_break_text_are_shown_by_their_codes()
    {
        Assert.Equal("file: \\u202Etxt.live", AskedArguments.Of([("file", "\u202Etxt.live")]));
        Assert.Equal("a\\u200Bb\\u2028c\\u0000d\\u0085e", AskedArguments.Escaped("a\u200Bb\u2028c\0d\u0085e"));
        Assert.Equal("o\\nk: plain", AskedArguments.Of([("o\nk", "plain")]));
        Assert.Equal("C:\\Users\\café ✓", AskedArguments.Escaped("C:\\Users\\café ✓"));
    }

    /// <summary>A value longer than the question shows is cut there, saying where and how long it is; one exactly that long is
    /// shown whole, and a cut never splits a character written in two halves.</summary>
    [Fact]
    public void A_long_value_is_cut_saying_how_many_characters_it_had()
    {
        string whole = new('x', AskedArguments.Longest);
        Assert.Equal(whole, AskedArguments.Shown(whole));
        Assert.Equal($"{whole}… (cut at 400 of 1,234 characters)", AskedArguments.Shown(whole + new string('y', 834)));
        string paired = new string('x', AskedArguments.Longest - 1) + "😀" + "tail";
        Assert.Equal($"{new string('x', AskedArguments.Longest - 1)}… (cut at 399 of 405 characters)", AskedArguments.Shown(paired));
    }
}

using Passenger.App.Mcp;

namespace Passenger.UnitTests.Mcp;

/// <summary>What the person's question shows of an agent's call: each argument on one line, whatever its value holds, so
/// none reads as another or hides what it says, and a long one cut saying so.</summary>
public sealed class AskedArgumentsTests
{
    [Fact]
    public void The_persons_question_shows_each_argument_of_an_agents_call_on_its_own_line_nothing_hidden_and_a_long_one_cut_saying_so() => Cases.All(
        ("a values line breaks are escaped so it never reads as another argument", A_values_line_breaks_are_escaped_so_it_never_reads_as_another_argument),
        ("characters that reverse hide or break text are shown by their codes", Characters_that_reverse_hide_or_break_text_are_shown_by_their_codes),
        ("characters past the first plane that hide text and halves of a pair alone are shown by their codes", Characters_past_the_first_plane_that_hide_text_and_halves_of_a_pair_alone_are_shown_by_their_codes),
        ("variation selectors and fillers that draw as nothing are shown by their codes", Variation_selectors_and_fillers_that_draw_as_nothing_are_shown_by_their_codes),
        ("a backslash is written twice so a line break and its escape written out differ", A_backslash_is_written_twice_so_a_line_break_and_its_escape_written_out_differ),
        ("a long value is cut saying how many characters it had", A_long_value_is_cut_saying_how_many_characters_it_had));

    /// <summary>A value's line breaks are written as escapes, so a value that goes on to look like another argument stays on
    /// its own line: the question shows two arguments, not three.</summary>
    private void A_values_line_breaks_are_escaped_so_it_never_reads_as_another_argument()
    {
        string shown = AskedArguments.Of([("path", "Notes.txt\npath: README.md"), ("newText", "one\r\ntwo\tthree")]);
        Assert.Equal("path: Notes.txt\\npath: README.md\nnewText: one\\r\\ntwo\\tthree", shown);
        Assert.Equal(2, shown.Split('\n').Length);
    }

    /// <summary>A character that turns the text after it around, joins it unseen or breaks a line where Unicode says so is
    /// shown by its code, as a name of an argument is: "evil.txt" written backwards reads as what it is.</summary>
    private void Characters_that_reverse_hide_or_break_text_are_shown_by_their_codes()
    {
        Assert.Equal("file: \\u202Etxt.live", AskedArguments.Of([("file", "\u202Etxt.live")]));
        Assert.Equal("a\\u200Bb\\u2028c\\u0000d\\u0085e", AskedArguments.Escaped("a\u200Bb\u2028c\0d\u0085e"));
        Assert.Equal("o\\nk: plain", AskedArguments.Of([("o\nk", "plain")]));
        Assert.Equal("café ✓ 😀", AskedArguments.Escaped("café ✓ 😀"));
    }

    /// <summary>A character past the Basic Multilingual Plane that shows nothing of itself (Unicode's tag characters, which
    /// can spell out words no one sees, and its shorthand format controls) is shown by its code as one character, not passed
    /// over as two halves that each look printable; a half of a pair alone is shown by its code too.</summary>
    private void Characters_past_the_first_plane_that_hide_text_and_halves_of_a_pair_alone_are_shown_by_their_codes()
    {
        string tagged = "Notes.txt" + string.Concat("rm -rf ~".Select(c => char.ConvertFromUtf32(0xE0000 + c)));
        Assert.Equal("path: Notes.txt\\U000E0072\\U000E006D\\U000E0020\\U000E002D\\U000E0072\\U000E0066\\U000E0020\\U000E007E",
            AskedArguments.Of([("path", tagged)]));
        Assert.Equal("a\\U000E0001b\\U000E007Fc\\U0001BCA0d\\U0001BCA3", AskedArguments.Escaped("a\U000E0001b\U000E007Fc\U0001BCA0d\U0001BCA3"));
        Assert.Equal("a\\uD800b\\uDC00", AskedArguments.Escaped("a\uD800b\uDC00"));
    }

    /// <summary>The rest of what Unicode says to draw as nothing, which is no format character (variation selectors, the
    /// Hangul fillers, the combining grapheme joiner), is shown by its code too.</summary>
    private void Variation_selectors_and_fillers_that_draw_as_nothing_are_shown_by_their_codes()
    {
        Assert.Equal("x\\uFE0Fy\\U000E0100z\\U000E01EF", AskedArguments.Escaped("x\uFE0Fy\U000E0100z\U000E01EF"));
        Assert.Equal("\\u3164\\uFFA0\\u115F\\u034F\\u180B", AskedArguments.Escaped("\u3164\uFFA0\u115F\u034F\u180B"));
    }

    /// <summary>A backslash is written twice, so a value's own backslash never reads as an escape: a line break and the two
    /// characters \n differ in the question, as does a Windows path's every backslash.</summary>
    private void A_backslash_is_written_twice_so_a_line_break_and_its_escape_written_out_differ()
    {
        Assert.Equal("a\\nb", AskedArguments.Escaped("a\nb"));
        Assert.Equal("a\\\\nb", AskedArguments.Escaped("a\\nb"));
        Assert.Equal("command: echo one\\\\necho two", AskedArguments.Of([("command", "echo one\\necho two")]));
        Assert.Equal("C:\\\\Users\\\\café ✓", AskedArguments.Escaped("C:\\Users\\café ✓"));
        Assert.Equal("\\\\u202E", AskedArguments.Escaped("\\u202E"));
    }

    /// <summary>A value longer than the question shows is cut there, saying where and how long it is; one exactly that long is
    /// shown whole, and a cut never splits a character written in two halves.</summary>
    private void A_long_value_is_cut_saying_how_many_characters_it_had()
    {
        string whole = new('x', AskedArguments.Longest);
        Assert.Equal(whole, AskedArguments.Shown(whole));
        Assert.Equal($"{whole}… (cut at 400 of 1,234 characters)", AskedArguments.Shown(whole + new string('y', 834)));
        string paired = new string('x', AskedArguments.Longest - 1) + "😀" + "tail";
        Assert.Equal($"{new string('x', AskedArguments.Longest - 1)}… (cut at 399 of 405 characters)", AskedArguments.Shown(paired));
    }
}

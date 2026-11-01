using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's indentation written again in tabs or in spaces: each line's leading whitespace at the columns it
/// reaches, what a level leaves over kept as spaces, and the rest of the line (alignment, a comment's) as it was.</summary>
public sealed class IndentConversionTests
{
    private static string Converted(string text, IndentOptions from, IndentOptions to)
    {
        TextSnapshot snapshot = TextSnapshot.From(text);
        return snapshot.Apply(Indentation.Convert(snapshot, from, to)).ToString();
    }

    [Fact]
    public void Spaces_become_a_tab_a_level_and_what_a_level_leaves_over_stays_spaces()
    {
        Assert.Equal("a\n\tb  = 1;\n\t\t  c\n\n\td\r\n",
            Converted("a\n    b  = 1;\n          c\n\n    d\r\n", new IndentOptions(4), new IndentOptions(4, UseTabs: true, TabWidth: 4)));
    }

    [Fact]
    public void Tabs_become_spaces_to_their_stops_and_a_tab_after_the_words_stays()
    {
        Assert.Equal("        a\tb\n    c\n",
            Converted("\t\ta\tb\n  \tc\n", new IndentOptions(4, UseTabs: true, TabWidth: 4), new IndentOptions(4, UseTabs: false, TabWidth: 4)));
    }

    [Fact]
    public void Two_spaces_a_level_become_a_tab_each()
    {
        Assert.Equal("\tx\n\t\ty\n", Converted("  x\n    y\n", new IndentOptions(2, TabWidth: 4), new IndentOptions(2, UseTabs: true, TabWidth: 2)));
    }

    [Fact]
    public void A_line_already_written_so_and_a_whitespace_run_longer_than_a_read_are_handled()
    {
        TextSnapshot tabbed = TextSnapshot.From("\tx\n\t\ty\n");
        Assert.Empty(Indentation.Convert(tabbed, new IndentOptions(4, true, 4), new IndentOptions(4, true, 4)));
        string deep = new string(' ', 400) + "z\n";
        Assert.Equal(new string('\t', 100) + "z\n", Converted(deep, new IndentOptions(4), new IndentOptions(4, UseTabs: true, TabWidth: 4)));
    }
}

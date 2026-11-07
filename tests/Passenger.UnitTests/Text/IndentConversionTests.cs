using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's indentation written again in tabs or in spaces: each line's leading whitespace at the columns it
/// reaches, what a level leaves over kept as spaces, and the rest of the line (alignment, a comment's) as it was.</summary>
public sealed class IndentConversionTests
{
    private static string Converted(string text, IndentOptions from, IndentOptions to)
    {
        TextSnapshot snapshot = TextSnapshot.From(text);
        return snapshot.Apply(Indentation.Convert(snapshot, from, to).Edits).ToString();
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
        Assert.Empty(Indentation.Convert(tabbed, new IndentOptions(4, true, 4), new IndentOptions(4, true, 4)).Edits);
        string deep = new string(' ', 400) + "z\n";
        Assert.Equal(new string('\t', 100) + "z\n", Converted(deep, new IndentOptions(4), new IndentOptions(4, UseTabs: true, TabWidth: 4)));
    }

    [Fact]
    public void A_line_kept_is_left_as_it_is_and_counted_only_where_it_would_have_changed()
    {
        TextSnapshot text = TextSnapshot.From("a\n    b\n    c\n\td\n    e\n");
        (List<TextEdit> edits, int kept) = Indentation.Convert(text, new IndentOptions(4), new IndentOptions(4, UseTabs: true, TabWidth: 4), new HashSet<int> { 2, 3 });
        Assert.Equal("a\n\tb\n    c\n\td\n\te\n", text.Apply(edits).ToString());
        Assert.Equal(1, kept);
    }

    private static int? AtRisk(string text, int size, int tabWidth = 0, bool toTabs = true, params int[] kept) =>
        Indentation.FirstBlockAtRisk(TextSnapshot.From(text), new IndentOptions(size, TabWidth: tabWidth == 0 ? size : tabWidth), toTabs, new HashSet<int>(kept),
            new HashSet<string>(StringComparer.Ordinal) { "class", "def", "if" });

    /// <summary>A file of 4-column levels read at 8, as a detected indent can be: the class's method would stay in spaces, and the
    /// tab under it is no deeper to a reader counting a tab as one column.</summary>
    [Fact]
    public void A_block_opened_between_levels_is_at_risk_from_tabs_alone()
    {
        const string Code = "class A:\n    def f(self):\n        return 1\n";
        Assert.Equal(1, AtRisk(Code, 8));
        Assert.Null(AtRisk(Code, 8, toTabs: false));
        Assert.Null(AtRisk(Code, 4));
        Assert.Equal(1, AtRisk("if a:\n  if b:\n    c()\n", 4));
    }

    [Fact]
    public void Only_a_blocks_parent_needs_to_be_a_whole_number_of_levels_deep()
    {
        Assert.Null(AtRisk("x = call(1,\n         2)\n", 4));
        Assert.Null(AtRisk("if a:\n      b()\n      c()\nd()\n", 4));
        Assert.Null(AtRisk("if a:\n    b()\n  # a comment\n  iffy()\n", 4));
    }

    [Fact]
    public void A_line_kept_is_no_blocks_and_a_hidden_start_may_be_one()
    {
        Assert.Null(AtRisk("x = \"\"\"\n  if a:\n\"\"\"\n", 4, kept: [1, 2]));
        Assert.Equal(0, AtRisk("  \\\nif a:\n    b()\n", 4));
        Assert.Equal(0, AtRisk("  \fif a:\n    b()\n", 4));
    }

    /// <summary>A reader's tab reaches the next multiple of 8 and counts as one column too. Tabs before spaces keep that order at
    /// any width up to 8; a tab after a space, or a width past 8, may not, so either way of converting waits on it.</summary>
    [Fact]
    public void A_tab_a_file_may_read_unlike_the_reader_is_at_risk_either_way()
    {
        const string Mixed = "if a:\n\t    \tb()\n";
        Assert.Equal(1, AtRisk(Mixed, 4, toTabs: false));
        Assert.Equal(1, AtRisk(Mixed, 4));
        Assert.Null(AtRisk(Mixed, 4, tabWidth: 8, toTabs: false));
        Assert.Null(AtRisk("if a:\n\tb()\n\t  c()\n", 4, toTabs: false));
        Assert.Equal(1, AtRisk("if a:\n\tb()\n", 4, tabWidth: 16, toTabs: false));
    }
}

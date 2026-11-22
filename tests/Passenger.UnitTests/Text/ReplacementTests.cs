using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>What Replace writes for each match: a regex's \n, \t and \\ a line break, a tab and a backslash, as .NET's own
/// replacement syntax does not read them, its groups as before; every line break a replacement makes the text's own.</summary>
public sealed class ReplacementTests
{
    [Fact]
    public void Replace_writes_a_regexs_escapes_as_their_characters() => Cases.All(
        ("A regex replacements backslash n t and backslash are a line break a tab and a backslash its groups as dotnet fills them", A_regex_replacements_backslash_n_t_and_backslash_are_a_line_break_a_tab_and_a_backslash_its_groups_as_dotnet_fills_them),
        ("A line break a replacement makes is written as the texts own regex or plain", A_line_break_a_replacement_makes_is_written_as_the_texts_own_regex_or_plain));

    private void A_regex_replacements_backslash_n_t_and_backslash_are_a_line_break_a_tab_and_a_backslash_its_groups_as_dotnet_fills_them()
    {
        SearchResult found = TextSearch.FindAll(TextSnapshot.From("ab,cd\n"), new SearchQuery(@"(\w+),(\w+)", Regex: true), TextSearch.AtOnce);

        Assert.Equal(["cd\n\tab\\x \\d $9"], found.Replacements(@"$2\n\t$1\\x \d $9").Select(e => e.Text));
    }

    private void A_line_break_a_replacement_makes_is_written_as_the_texts_own_regex_or_plain()
    {
        TextSnapshot crlf = TextSnapshot.From("one,two\r\nthree\r\n");

        SearchResult regex = TextSearch.FindAll(crlf, new SearchQuery(",", Regex: true), TextSearch.AtOnce);
        SearchResult plain = TextSearch.FindAll(crlf, new SearchQuery(","), TextSearch.AtOnce);

        Assert.Equal(["\r\n"], regex.Replacements(@"\n").Select(e => e.Text));
        Assert.Equal(["\r\nand\r\n"], plain.Replacements("\nand\n").Select(e => e.Text));
        Assert.Equal(crlf.Length - 1 + "\r\nand\r\n".Length, plain.ReplacedLength("\nand\n"));
        Assert.Equal(@"\n", plain.Replacement(0, @"\n"));
    }
}

using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>Who a stretch of a file is said to be by, from Git blame.</summary>
public sealed class BlameAuthorsTests
{
    private static readonly DateTimeOffset Day = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BlameLine Line(string author, int days, string? hash = null) =>
        new(hash ?? new string((char)('a' + days), 40), author, Day.AddDays(days), $"Change {days}");

    private static BlameLine Pending() => new(new string('0', 40), "Not Committed Yet", Day, "Version of file");

    [Fact]
    public void The_author_of_most_of_the_lines_is_named_with_how_many_others_changed_any()
    {
        BlameLine[] blame = [Line("Jane Doe", 1), Line("Jane Doe", 1), Line("Jane Doe", 1), Line("Sam Roe", 5), Line("Kim Poe", 3)];
        StretchAuthors? authors = BlameAuthors.Of(blame, 0, 4);
        Assert.True(authors is { Label: "Jane Doe +2", IsCommitted: true } && authors.Latest.Author == "Sam Roe",
            $"The authors were read as \"{authors?.Label}\" (newest by {authors?.Latest.Author}), not \"Jane Doe +2\" with Sam Roe's the newest commit");
    }

    [Fact]
    public void Of_two_authors_with_as_many_lines_the_one_who_came_first_is_named()
    {
        StretchAuthors? authors = BlameAuthors.Of([Line("Sam Roe", 5), Line("Jane Doe", 1)], 0, 1);
        Assert.True(authors?.Top == "Sam Roe", $"{authors?.Top} was named of two authors with a line each, not the one whose line is first");
    }

    [Fact]
    public void A_line_not_committed_yet_is_no_ones_and_a_stretch_of_such_lines_says_so()
    {
        StretchAuthors? mixed = BlameAuthors.Of([Pending(), Line("Jane Doe", 1), Pending()], 0, 2);
        StretchAuthors? alone = BlameAuthors.Of([Pending(), Pending()], 0, 1);
        Assert.True(mixed is { Label: "Jane Doe" } && alone is { Label: "Not committed yet", IsCommitted: false },
            $"The authors read as \"{mixed?.Label}\" and \"{alone?.Label}\", not \"Jane Doe\" and \"Not committed yet\", so a pending change is named as a person");
    }

    [Fact]
    public void A_stretch_the_blame_does_not_reach_has_no_authors()
    {
        Assert.True(BlameAuthors.Of([Line("Jane Doe", 1)], 3, 5) is null && BlameAuthors.Of([], 0, 0) is null,
            "A stretch past the blame's lines was given authors, so a hint names someone for lines that were not blamed");
    }

    [Fact]
    public void A_stretch_running_past_the_blames_end_is_cut_to_it()
    {
        Assert.True(BlameAuthors.Of([Line("Jane Doe", 1), Line("Sam Roe", 2)], 1, 9)?.Label == "Sam Roe",
            "A stretch running past the blame's end was not cut to the lines blamed, so the authors of its first line were lost");
    }
}

using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>A table's names matched to the members Reqnroll's table helpers set, as Reqnroll 3.3.4 matches them at run time:
/// each column below set the member named (or none) when CreateSet&lt;Book&gt; read a table of it alone; CreateInstance
/// reads a table of one row by its header and any other by its first column, as Reqnroll's run did.</summary>
public sealed class TableColumnsTests
{
    private static readonly TableRead Set = new(0, "CreateSet", "Shop.Book", Members);

    private static TableMember[] Members =>
        [new("Title", true, []), new("Author_Name", true, []), new("Pages", false, []), new("Price", true, [])];

    private static FeatureTable TableOf(string feature) => FeatureReading.Read(feature, "en").Steps[0].Table!;

    [Theory]
    [InlineData("Title", "Title")]
    [InlineData("title", "Title")]
    [InlineData("TITLE", "Title")]
    [InlineData("author name", "Author_Name")]
    [InlineData("Author Name", "Author_Name")]
    [InlineData("Author_Name", "Author_Name")]
    [InlineData("author-name", "Author_Name")]
    [InlineData("AuthorName", "Author_Name")]
    [InlineData("authorname", "Author_Name")]
    [InlineData("Author.Name", "Author_Name")]
    [InlineData("PAGES", "Pages")]
    [InlineData("Pages", "Pages")]
    [InlineData("pages", "Pages")]
    [InlineData("Price ($)", "Price")]
    [InlineData("Price", "Price")]
    [InlineData("price!", "Price")]
    [InlineData("Titel", null)]
    [InlineData("Seiten", null)]
    [InlineData("_pages", "Pages")]
    [InlineData("1Title", null)]
    [InlineData("Tïtle", "Title")]
    [InlineData("Prïce", "Price")]
    [InlineData("Pa ges", "Pages")]
    [InlineData("P_ages", "Pages")]
    public void A_column_sets_the_member_reqnroll_sets_from_it(string column, string? member)
    {
        Assert.Equal(member, Members.FirstOrDefault(m => TableColumns.Takes(m, column) == true)?.Name);
    }

    [Fact]
    public void Create_set_passes_over_each_header_cell_no_member_takes_saying_what_the_type_has()
    {
        FeatureTable table = TableOf("Feature: F\nScenario: S\n  Given books\n    | Title | Titel | author name |\n    | A     | B     | C           |\n");

        (FeatureRow row, FeatureCell cell, _) = Assert.Single(TableColumns.Unread(table, Set));

        Assert.Equal((3, 14, "Titel"), (row.Line, cell.Column, cell.Value));
        Assert.Equal("CreateSet<Book> sets no member from \"Titel\", so it is passed over: Book has Title, Author_Name, Pages and Price "
            + "(a column names one in any letter case, spaces and other characters left out)", TableColumns.Why(Set, "Titel"));
    }

    [Fact]
    public void Create_instance_reads_a_table_of_one_row_by_its_header_and_one_of_several_by_its_first_column()
    {
        TableRead instance = Set with { Helper = "CreateInstance" };
        FeatureTable horizontal = TableOf("Feature: F\nScenario: S\n  Given a book\n    | Title | Pagez |\n    | A     | 1     |\n");
        FeatureTable vertical = TableOf("Feature: F\nScenario: S\n  Given a book\n    | Field | Value |\n    | Title | A     |\n    | Pagez | 1     |\n");
        FeatureTable named = TableOf("Feature: F\nScenario: S\n  Given a book\n    | Nothing | here |\n    | Pagez   | 1    |\n    | Title   | A    |\n");

        Assert.Equal("Pagez", Assert.Single(TableColumns.Unread(horizontal, instance)).Cell.Value);
        Assert.Equal((5, "Pagez"), Assert.Single(TableColumns.Unread(vertical, instance)) is var (row, cell, _) ? (row.Line, cell.Value) : default);
        Assert.Equal("Pagez", Assert.Single(TableColumns.Unread(named, instance)).Cell.Value);
    }

    [Fact]
    public void A_members_table_alias_takes_a_column_it_matches()
    {
        TableMember title = new("Title", true, ["^Book ?Name$"]);

        Assert.True(TableColumns.Takes(title, "Book Name"));
        Assert.False(TableColumns.Takes(title, "Name"));
    }

    /// <summary>An alias stopped at its time limit (held there in place of a slow machine) neither takes nor passes over the
    /// column: it is said unchecked in the one wording, never passed over for want of a member.</summary>
    [Fact]
    public void A_column_whose_alias_stops_at_the_time_limit_is_said_unchecked_never_passed_over()
    {
        TableRead read = new(0, "CreateSet", "Shop.Book", [new("Title", true, ["^Book ?Name$(?#held1984-alias)"])]);
        FeatureTable table = TableOf("Feature: Books\n  Scenario: Some\n    Given these books\n      | Book Name |\n      | Dune      |\n");
        using (Passenger.Text.TextSearch.HoldPastLimit("held1984-alias"))
        {
            (FeatureRow _, FeatureCell cell, bool unjudged) = Assert.Single(TableColumns.Unread(table, read));
            Assert.True(unjudged && cell.Value == "Book Name", $"A column whose alias stopped came to {cell.Value}, unjudged {unjudged}, not said unchecked");
            Assert.Equal($"CreateSet<Book> may set a member from \"Book Name\" by an alias, not checked: {Passenger.Text.TextSearch.RegexStopped}", TableColumns.Unjudged(read, cell.Value));
        }
        Assert.Empty(TableColumns.Unread(table, read));
    }
}

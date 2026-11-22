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

    private static void ColumnSets(string column, string? member)
    {
        Assert.Equal(member, Members.FirstOrDefault(m => TableColumns.Takes(m, column) == true)?.Name);
    }

    [Fact]
    public void A_tables_columns_set_the_members_Reqnrolls_table_helpers_set_from_them() => Cases.All(
        ("create set passes over each header cell no member takes saying what the type has", () =>
        {
            FeatureTable table = TableOf("Feature: F\nScenario: S\n  Given books\n    | Title | Titel | author name |\n    | A     | B     | C           |\n");

            (FeatureRow row, FeatureCell cell, _) = Assert.Single(TableColumns.Unread(table, Set));

            Assert.Equal((3, 14, "Titel"), (row.Line, cell.Column, cell.Value));
            Assert.Equal("CreateSet<Book> sets no member from \"Titel\", so it is passed over: Book has Title, Author_Name, Pages and Price "
                + "(a column names one in any letter case, spaces and other characters left out)", TableColumns.Why(Set, "Titel"));
        }),
        ("create instance reads a table of one row by its header and one of several by its first column", () =>
        {
            TableRead instance = Set with { Helper = "CreateInstance" };
            FeatureTable horizontal = TableOf("Feature: F\nScenario: S\n  Given a book\n    | Title | Pagez |\n    | A     | 1     |\n");
            FeatureTable vertical = TableOf("Feature: F\nScenario: S\n  Given a book\n    | Field | Value |\n    | Title | A     |\n    | Pagez | 1     |\n");
            FeatureTable named = TableOf("Feature: F\nScenario: S\n  Given a book\n    | Nothing | here |\n    | Pagez   | 1    |\n    | Title   | A    |\n");

            Assert.Equal("Pagez", Assert.Single(TableColumns.Unread(horizontal, instance)).Cell.Value);
            Assert.Equal((5, "Pagez"), Assert.Single(TableColumns.Unread(vertical, instance)) is var (row, cell, _) ? (row.Line, cell.Value) : default);
            Assert.Equal("Pagez", Assert.Single(TableColumns.Unread(named, instance)).Cell.Value);
        }),
        ("a members table alias takes a column it matches", () =>
        {
            TableMember title = new("Title", true, ["^Book ?Name$"]);

            Assert.True(TableColumns.Takes(title, "Book Name"));
            Assert.False(TableColumns.Takes(title, "Name"));
        }),
        ("a column whose alias stops at the time limit is said unchecked never passed over", () =>
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
        }),
        ("a column sets the member Reqnroll sets from it", () => Cases.Each(
        [
            ("TITLE", "Title"), ("author name", "Author_Name"), ("author-name", "Author_Name"), ("authorname", "Author_Name"),
            ("Price ($)", "Price"), ("Titel", (string?)null), ("1Title", null), ("Tïtle", "Title"), ("Pa ges", "Pages"),
        ], row => ColumnSets(row.Item1, row.Item2))));

}

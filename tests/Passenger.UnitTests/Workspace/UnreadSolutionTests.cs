using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A .slnx's text that is not well-formed XML is named in its reader's words, less the place they end with, and
/// with the line and column where the reader stopped; text that reads is none.</summary>
public sealed class UnreadSolutionTests
{
    private const string Path = "/work/Shop/Shop.slnx";

    [Fact]
    public void A_solution_cut_short_is_named_where_its_reader_met_the_end()
    {
        UnreadSolution? unread = SolutionModel.Unread(Path, "<Solution>\n  <Project Path=\"App/App.csproj\" />\n");
        Assert.True(unread is not null, "A .slnx cut short reads, so nothing says why the folder opens without it");
        Assert.True((unread.Path, unread.Line, unread.Column) == (Path, 3, 1), $"The reader is said to stop at line {unread.Line}, column {unread.Column} of {unread.Path}");
        Assert.Equal("Unexpected end of file has occurred. The following elements are not closed: Solution", unread.Why);
    }

    [Fact]
    public void A_merge_conflicts_markers_are_named_where_they_begin()
    {
        UnreadSolution? unread = SolutionModel.Unread(Path,
            "<Solution>\n<<<<<<< HEAD\n  <Project Path=\"A/A.csproj\" />\n=======\n  <Project Path=\"B/B.csproj\" />\n>>>>>>> theirs\n</Solution>\n");
        Assert.True(unread is not null, "A .slnx holding a conflict's markers reads, so nothing says why the folder opens without it");
        Assert.True((unread.Line, unread.Column) == (2, 2), $"The conflict's markers are said to be at line {unread.Line}, column {unread.Column}, not where they begin");
        Assert.Equal("Name cannot begin with the '<' character, hexadecimal value 0x3C", unread.Why);
    }

    [Fact]
    public void A_solution_that_reads_is_none() =>
        Assert.Null(SolutionModel.Unread(Path, "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n"));
}

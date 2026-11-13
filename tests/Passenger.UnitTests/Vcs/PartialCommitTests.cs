using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

public sealed class PartialCommitTests
{
    [Theory]
    [InlineData("a\nb\nc\n", "A\nb\nC\n", "A\nb\nc\n")]
    [InlineData("a\r\nb\r\nc\r\n", "A\r\nb\r\nC\r\n", "A\r\nb\r\nc\r\n")]
    [InlineData("a", "a\nb", "a\nb")]
    [InlineData("a\nb", "a", "a")]
    [InlineData("a\nb\n", "a\n", "a\n")]
    [InlineData("a\nb\nc", "A\nb\nc", "A\nb\nc")]
    [InlineData("", "a\n", "a\n")]
    public void Only_the_chosen_change_enters_the_content_with_its_line_endings(string before, string after, string expected)
    {
        FileDiff diff = FileDiff.Compare(before, new TextDocument(after).Snapshot);
        Assert.Equal(expected, new PartialCommit(diff, [diff.Hunks[0]]).Content());
        Assert.Equal(before, new PartialCommit(diff, []).Content());
        Assert.Equal(after, new PartialCommit(diff, diff.Hunks).Content());
    }

    private const string Head = "a\nb\nc\nd\ne\n";

    /// <summary>The first of "A" and "E" chosen, the text edited again: which changes of the new text stay chosen.</summary>
    [Theory]
    [InlineData("A\nb\nc\nd\nE\n", true)]
    [InlineData("A\nb\nc\nd\nEE\n", true)]
    [InlineData("AA\nb\nc\nd\nE\n", true)]
    [InlineData("A\nb\nC\nd\nE\n", true)]
    [InlineData("A\nB\nC\nD\nE\n", false)]
    [InlineData("a\nb\nc\nd\nE\n", false)]
    [InlineData("A\nx\nb\nc\nd\nE\n", true)]
    public void A_choice_follows_the_changes_it_still_makes_and_leaves_new_ones_out(string next, bool keepsFirst)
    {
        FileDiff diff = FileDiff.Compare(Head, new TextDocument("A\nb\nc\nd\nE\n").Snapshot);
        FileDiff edited = diff.Compare(new TextDocument(next).Snapshot);
        IReadOnlyList<DiffHunk> expected = keepsFirst ? [edited.Hunks[0]] : [];
        Assert.Equal(expected, new PartialCommit(diff, [diff.Hunks[0]]).Following(edited));
    }

    [Fact]
    public void A_choice_made_against_another_last_commit_follows_nothing()
    {
        FileDiff diff = FileDiff.Compare(Head, new TextDocument("A\nb\nc\nd\nE\n").Snapshot);
        FileDiff other = FileDiff.Compare("a\nb\nc\nd\nf\n", diff.Text);
        Assert.Empty(new PartialCommit(diff, [diff.Hunks[0]]).Following(other));
    }
    /// <summary>"a".."h" with line 2 changed, lines 4 and 5 changed, and "g" deleted from between "f" and "h".</summary>
    private static FileDiff Changed() => FileDiff.Compare("a\nb\nc\nd\ne\nf\ng\nh\n", new TextDocument("a\nB\nc\nD\nE\nf\nh\n").Snapshot);

    [Theory]
    [InlineData("2", "2")]
    [InlineData("4-5", "4")]
    [InlineData("5", "4")]
    [InlineData("1-3", "2")]
    [InlineData(" 2 , 5 ", "2 4")]
    [InlineData("6", "7")]
    [InlineData("7", "7")]
    [InlineData("1-7", "2 4 7")]
    public void Lines_of_the_file_now_take_each_change_they_touch_whole(string ranges, string starts)
    {
        FileDiff diff = Changed();
        (IReadOnlyList<DiffHunk> hunks, string? why) = PartialCommit.Touched(diff, ranges);
        Assert.Null(why);
        // Each change named by the line of the file now where it starts (a deletion's by the line after it).
        Assert.Equal(starts, string.Join(' ', hunks.Select(h => h.NewStart + 1)));
    }

    [Theory]
    [InlineData("3", "no change is at line 3: its changes are at line 2, lines 4-5 and between lines 6 and 7")]
    [InlineData("8-9", "no change is at lines 8-9: its changes are at line 2, lines 4-5 and between lines 6 and 7")]
    [InlineData("2,x", "x is not a line or a range of lines, as 3 or 12-14")]
    [InlineData("5-4", "5-4 is not a line or a range of lines, as 3 or 12-14")]
    [InlineData("0", "0 is not a line or a range of lines, as 3 or 12-14")]
    [InlineData(" ", "no lines are named")]
    public void Lines_touching_no_change_or_not_read_as_lines_are_refused_saying_where_the_changes_are(string ranges, string expected)
    {
        (IReadOnlyList<DiffHunk> hunks, string? why) = PartialCommit.Touched(Changed(), ranges);
        Assert.Empty(hunks);
        Assert.Equal(expected, why);
    }
}

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
}

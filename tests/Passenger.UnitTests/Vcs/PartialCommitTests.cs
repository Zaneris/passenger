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
}

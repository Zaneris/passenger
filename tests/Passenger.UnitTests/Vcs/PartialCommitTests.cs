using System.Text;
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
    [InlineData("5", "5")]
    [InlineData("1-3", "2")]
    [InlineData(" 2 , 5 ", "2 5")]
    [InlineData("6", "7")]
    [InlineData("7", "7")]
    [InlineData("1-7", "2 4 7")]
    public void Lines_of_the_file_now_take_the_changed_lines_they_name_and_a_deletion_beside_them_whole(string ranges, string starts)
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

    /// <summary>What the index holds of "a".."f" once a commit took its changes: the staged text with the committed
    /// changes taken in, each staged change left out of the commit still there; where both changed the same lines,
    /// the committed lines.</summary>
    [Theory]
    [InlineData("a\nb\nC\nd\ne\nf\n", "A\nb\nc\nd\ne\nf\n", "A\nb\nC\nd\ne\nf\n")]
    [InlineData("a\nB\nc\nd\ne\nf\n", "A\nb\nc\nd\ne\nf\n", "A\nB\nc\nd\ne\nf\n")]
    [InlineData("A\nb\nc\nd\ne\nF\n", "A\nb\nc\nd\ne\nf\n", "A\nb\nc\nd\ne\nF\n")]
    [InlineData("A\nB\nc\nd\ne\nf\n", "a\nb\nc\nd\ne\nF\n", "A\nB\nc\nd\ne\nF\n")]
    [InlineData("a\nb\nc\nd\ne\nf\n", "a\nb\nX\nd\ne\nf\n", "a\nb\nX\nd\ne\nf\n")]
    [InlineData("a\nb\nY\nd\ne\nf\n", "a\nb\nX\nd\ne\nf\n", "a\nb\nX\nd\ne\nf\n")]
    [InlineData("a\nY\nZ\nd\ne\nf\n", "a\nb\nX\nd\ne\nf\n", "a\nb\nX\nd\ne\nf\n")]
    [InlineData("a\nb\nnew\nc\nd\ne\nf\n", "a\nb\nc\nD\ne\nf\n", "a\nb\nnew\nc\nD\ne\nf\n")]
    [InlineData("a\nb\nc\nnew\nd\ne\nf\n", "a\nb\nC\nd\ne\nf\n", "a\nb\nC\nnew\nd\ne\nf\n")]
    [InlineData("a\nb\nc\nnew\nd\ne\nf\n", "a\nb\nc\nd\ne\nf\n", "a\nb\nc\nnew\nd\ne\nf\n")]
    [InlineData("a\nb\nc\nx\nd\ne\nf\n", "a\nb\nc\nx\ny\nd\ne\nf\n", "a\nb\nc\nx\ny\nd\ne\nf\n")]
    [InlineData("b\nc\nd\ne\nf\n", "a\nb\nc\nd\ne\n", "b\nc\nd\ne\n")]
    [InlineData("a\nb\nc\nd\ne\nf", "A\nb\nc\nd\ne\nf\n", "A\nb\nc\nd\ne\nf")]
    [InlineData("a\r\nb\nc\nd\ne\nf\n", "a\nb\nc\nd\ne\nF\r\n", "a\r\nb\nc\nd\ne\nF\r\n")]
    public void A_partial_commit_leaves_what_it_did_not_take_staged_with_what_it_took_in(string staged, string committed, string expected)
    {
        const string Head = "a\nb\nc\nd\ne\nf\n";
        byte[]? restaged = PartialCommit.Restaged(Encoding.UTF8.GetBytes(Head), Encoding.UTF8.GetBytes(staged), Encoding.UTF8.GetBytes(committed));
        Assert.Equal(expected, restaged is null ? null : Encoding.UTF8.GetString(restaged));
    }

    /// <summary>A UTF-16 text is merged by its own lines, each byte of what it holds kept; a text UTF-16 in one of the
    /// three and not the others can't be read alike, and nothing is made of it.</summary>
    [Fact]
    public void A_wide_text_is_restaged_by_its_lines_and_one_wide_in_part_is_not()
    {
        static byte[] Wide(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
        byte[]? restaged = PartialCommit.Restaged(Wide("a\r\nb\r\nc\r\n"), Wide("a\r\nB\u5496\r\nc\r\n"), Wide("A\r\nb\r\nc\r\n"));
        Assert.Equal(Wide("A\r\nB\u5496\r\nc\r\n"), restaged);
        Assert.Null(PartialCommit.Restaged(Wide("a\nb\n"), Encoding.UTF8.GetBytes("a\nB\n"), Wide("A\nb\n")));
    }

    /// <summary>Bytes that are not UTF-8 (Latin-1's, a UTF-8 BOM) come back as they were wherever no change reached.</summary>
    [Fact]
    public void An_eight_bit_text_is_restaged_byte_for_byte()
    {
        byte[] head = [0xEF, 0xBB, 0xBF, (byte)'a', 0xE9, (byte)'\n', (byte)'b', (byte)'\n', (byte)'c', 0xFF, (byte)'\n'];
        byte[] staged = [0xEF, 0xBB, 0xBF, (byte)'a', 0xE9, (byte)'\n', (byte)'B', (byte)'\n', (byte)'c', 0xFF, (byte)'\n'];
        byte[] committed = [0xEF, 0xBB, 0xBF, (byte)'a', 0xE9, (byte)'\n', (byte)'b', (byte)'\n', (byte)'C', 0xFE, (byte)'\n'];
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'a', 0xE9, (byte)'\n', (byte)'B', (byte)'\n', (byte)'C', 0xFE, (byte)'\n'], PartialCommit.Restaged(head, staged, committed));
    }

    /// <summary>A change taken out of the index puts back the last commit's lines wherever the index changed those the file's
    /// change has there, a replacement, a line added or one deleted alike, and keeps what else it holds; one that meets no
    /// change the index holds leaves it as it was.</summary>
    [Theory]
    [InlineData("A\nb\nc\nd\nE\nf\n", 0, 1, "a\nb\nc\nd\nE\nf\n")]
    [InlineData("A\nb\nc\nd\nE\nf\n", 4, 1, "A\nb\nc\nd\ne\nf\n")]
    [InlineData("a\nb\nnew\nc\nd\nE\nf\n", 2, 0, "a\nb\nc\nd\nE\nf\n")]
    [InlineData("a\nc\nd\nE\nf\n", 1, 1, "a\nb\nc\nd\nE\nf\n")]
    [InlineData("A\nb\nc\nd\ne\nf\n", 3, 1, "A\nb\nc\nd\ne\nf\n")]
    [InlineData("a\nb\nc\nd\ne\nf\nlast\n", 6, 0, "a\nb\nc\nd\ne\nf\n")]
    public void A_change_unstaged_gives_back_the_last_commits_lines_where_the_index_changed_them_and_keeps_the_rest(string staged, int start, int count, string expected)
    {
        const string Head = "a\nb\nc\nd\ne\nf\n";
        byte[]? unstaged = PartialCommit.Unstaged(Encoding.UTF8.GetBytes(Head), Encoding.UTF8.GetBytes(staged), [new DiffHunk(start, count, start, 1)]);
        Assert.Equal(expected, unstaged is null ? null : Encoding.UTF8.GetString(unstaged));
    }

    /// <summary>Unstaging keeps every byte the index held where no change reached, its line breaks included, and refuses a
    /// text wide in one of the two and not the other.</summary>
    [Fact]
    public void Unstaging_keeps_the_index_byte_for_byte_beyond_the_change_and_refuses_one_wide_in_part()
    {
        byte[] head = [(byte)'a', 0xE9, (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n', (byte)'c', 0xFF, (byte)'\r', (byte)'\n'];
        byte[] staged = [(byte)'A', 0xE9, (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n', (byte)'C', 0xFE, (byte)'\r', (byte)'\n'];
        Assert.Equal([(byte)'A', 0xE9, (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n', (byte)'c', 0xFF, (byte)'\r', (byte)'\n'],
            PartialCommit.Unstaged(head, staged, [new DiffHunk(2, 1, 2, 1)]));
        byte[] wide = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("a\nB\n")];
        Assert.Null(PartialCommit.Unstaged(Encoding.UTF8.GetBytes("a\nb\n"), wide, [new DiffHunk(1, 1, 1, 1)]));
    }

    /// <summary>A change is held whole in the index where the file's text differs from the index's nowhere among its lines: a
    /// replacement, a line added and a line taken out alike; one the index holds none or part of is not.</summary>
    [Theory]
    [InlineData("a\nb\nc\nd\ne\nf\n", "FFF")]
    [InlineData("A\nb\nc\nd\ne\nf\n", "TFF")]
    [InlineData("A\nb\nnew\nc\nd\nf\n", "TTT")]
    [InlineData("a\nb\nnew\nc\nd\ne\nf\n", "FTF")]
    [InlineData("a\nb\nc\nd\nf\n", "FFT")]
    public void A_change_is_held_in_the_index_where_the_file_differs_from_it_nowhere_among_its_lines(string index, string held)
    {
        const string Head = "a\nb\nc\nd\ne\nf\n";
        TextSnapshot text = new TextDocument("A\nb\nnew\nc\nd\nf\n").Snapshot;
        FileDiff diff = FileDiff.Compare(Head, text);
        Assert.Equal(3, diff.Hunks.Count);
        Assert.Equal(held, string.Concat(diff.HeldIn(FileDiff.Compare(index, text)).Select(h => h ? 'T' : 'F')));
    }

    /// <summary>Lines chosen take their own part of a change: of the file's side a replaced line its replacement and an added
    /// line itself, of the last commit's a replaced line its replacement and a deleted line its deletion; every line of a
    /// side chosen takes the change whole.</summary>
    [Theory]
    [InlineData("a\nb\nc\nd\n", "A\nB\nC\nd\n", "1", "", "a\nB\nc\nd\n")]
    [InlineData("a\nb\nc\nd\n", "A\nB\nC\nd\n", "0-2", "", "A\nB\nC\nd\n")]
    [InlineData("a\nb\n", "a\nx\ny\nb\n", "2", "", "a\ny\nb\n")]
    [InlineData("a\nb\nc\nd\n", "a\nX\nd\n", "1", "", "a\nX\nd\n")]
    [InlineData("a\nb\nc\nd\n", "a\nX\nd\n", "", "2", "a\nb\nd\n")]
    [InlineData("a\nb\nc\nd\n", "a\nX\nd\n", "", "1", "a\nX\nc\nd\n")]
    [InlineData("a\nb\nc\nd\n", "a\nd\n", "0", "", "a\nd\n")]
    public void Lines_chosen_take_their_own_part_of_a_change(string head, string now, string file, string last, string expected)
    {
        FileDiff diff = FileDiff.Compare(head, new TextDocument(now).Snapshot);
        Assert.Equal(expected, new PartialCommit(diff, PartialCommit.LinesChosen(diff, Lines(file), Lines(last))).Content());

        static IReadOnlyList<(int, int)> Lines(string range) => range.Length == 0 ? [] : range.Split('-') is [{ } one] ? [(int.Parse(one), int.Parse(one))]
            : range.Split('-') is [{ } first, { } last] ? [(int.Parse(first), int.Parse(last))] : [];
    }

    /// <summary>Unstaging a line of a change the index holds gives back that line alone, the change's other lines kept.</summary>
    [Fact]
    public void Unstaging_a_line_of_a_staged_change_gives_back_that_line_alone()
    {
        byte[]? unstaged = PartialCommit.Unstaged(Encoding.UTF8.GetBytes("a\nb\nc\nd\n"), Encoding.UTF8.GetBytes("A\nB\nc\nd\n"), [new DiffHunk(1, 1, 1, 1)]);
        Assert.Equal("A\nb\nc\nd\n", unstaged is null ? null : Encoding.UTF8.GetString(unstaged));
    }
}

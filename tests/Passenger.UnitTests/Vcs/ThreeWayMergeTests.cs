using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>The three-way merge's changes as the merge window takes them, the file's last line break among them: a side
/// that adds or takes it away makes a change, and taking that change gives the result the side's ending.</summary>
public class ThreeWayMergeTests
{
    [Fact]
    public void The_merge_window_takes_a_change_to_the_last_line_break_as_any_other_and_a_conflict_where_both_sides_differ() => Cases.All(
        ("a change only one side made to the last line break is a change and taking it gives that sides ending", () => Cases.Each<(string, string, string, string)>(
        [
            ("a\nb", "a\nb\n", "a\nb", "a\nb\n"),
            ("a\nb\n", "a\nb", "a\nb\n", "a\nb"),
            ("a\r\nb", "a\r\nb\r\n", "a\r\nb", "a\r\nb\r\n"),
            ("a\nb", "a\n", "a\nb", "a\n"),
            ("a\nb\nc", "a\nb\nc\nd\n", "a\nb\nc", "a\nb\nc\nd\n"),
        ], row => A_change_only_one_side_made_to_the_last_line_break_is_a_change_and_taking_it_gives_that_sides_ending(row.Item1, row.Item2, row.Item3, row.Item4))),
        ("both sides adding the last break alike is no conflict and one adding it where the other changed the line is one", Both_sides_adding_the_last_break_alike_is_no_conflict_and_one_adding_it_where_the_other_changed_the_line_is_one));

    private void A_change_only_one_side_made_to_the_last_line_break_is_a_change_and_taking_it_gives_that_sides_ending(string common, string left, string right, string merged) =>
        Assert.Equal(merged, Merged(common, left, right));

    private void Both_sides_adding_the_last_break_alike_is_no_conflict_and_one_adding_it_where_the_other_changed_the_line_is_one()
    {
        Assert.DoesNotContain(Chunks("a\nb", "a\nb\n", "a\nb\n"), c => c.IsConflict);
        Assert.Contains(Chunks("a\nb", "a\nb\n", "a\nc"), c => c.IsConflict);
        Assert.Empty(Chunks("a\nb\n", "a\nb\n", "a\nb\n"));
    }

    private static IReadOnlyList<MergeChunk> Chunks(string common, string left, string right) =>
        ThreeWayMerge.Chunks(ThreeWayMerge.Compared(common), ThreeWayMerge.Compared(left), ThreeWayMerge.Compared(right));

    /// <summary>The base with every change only one side made taken, as Apply All Non-Conflicting Changes takes them (each
    /// from the side that made it, with that side's ending where it reaches the end); the right side wins a conflict.</summary>
    private static string Merged(string common, string left, string right)
    {
        string[] lefts = LineDiff.Lines(left);
        string[] rights = LineDiff.Lines(right);
        string lineBreak = common.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        TextSnapshot text = TextSnapshot.From(common);
        int shift = 0;
        foreach (MergeChunk chunk in Chunks(common, left, right))
        {
            bool fromLeft = chunk.LeftChanged && !chunk.IsConflict;
            string[] lines = fromLeft ? lefts[chunk.LeftStart..chunk.LeftEnd] : rights[chunk.RightStart..chunk.RightEnd];
            bool? breakAtEnd = chunk.BaseEnd < LineDiff.Lines(common).Length ? null : (fromLeft ? left : right).EndsWith('\n');
            text = text.Apply([ThreeWayMerge.ReplaceLines(text, chunk.BaseStart + shift, chunk.BaseEnd + shift, lines, lineBreak, breakAtEnd)]);
            shift += lines.Length - (chunk.BaseEnd - chunk.BaseStart);
        }
        return text.ToString();
    }
}

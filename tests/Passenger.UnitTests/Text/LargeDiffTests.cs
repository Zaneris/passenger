using System.Globalization;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Texts that differ by more edits than a comparison searches for (<see cref="LineDiff.Hunks(string[], string[])"/>)
/// still diff to each change, split where the searches got furthest, at a bounded cost.</summary>
public class LargeDiffTests
{
    [Fact]
    public void A_file_changed_on_one_line_in_five_and_short_of_another_diffs_to_each_change_past_the_edits_searched_for()
    {
        string[] before = Numbered(8160);
        string[] after = Edited(before);
        long visited = LineDiff.Visited;
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(before, after);
        visited = LineDiff.Visited - visited;
        Holds(before, after, hunks);
        int widest = hunks.Max(h => Math.Max(h.OldCount, h.NewCount));
        Assert.True(hunks.Count == 3264 && widest == 1,
            $"8,160 lines with 1,632 changed and 1,632 deleted, 4,896 edits, diffed to {hunks.Count} hunks, the widest {widest} lines, where each change is a hunk of its own");
        Assert.True(visited <= 8_000_000, $"Diffing 8,160 lines edited all over visited {visited:N0} points, more than two searches of 2,000 steps that never meet");
    }

    [Fact]
    public void Texts_with_no_line_alike_diff_to_one_change_within_the_comparisons_budget()
    {
        string[] before = Numbered(3000);
        string[] after = [.. before.Select(line => line + " rewritten")];
        long visited = LineDiff.Visited;
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(before, after, mostEdits: 64);
        visited = LineDiff.Visited - visited;
        Holds(before, after, hunks);
        Assert.True(visited <= (8 * 32 * 32) + 200, $"3,000 lines with none alike visited {visited:N0} points, where a comparison searching 64 edits stops near {8 * 32 * 32:N0}");
    }

    [Fact]
    public void Searches_that_never_meet_split_where_they_got_furthest_and_find_every_change()
    {
        string[] before = Numbered(150);
        string[] after = Edited(before);
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(before, after, mostEdits: 40);
        Holds(before, after, hunks);
        int widest = hunks.Max(h => Math.Max(h.OldCount, h.NewCount));
        Assert.True(hunks.Count == 60 && widest == 1,
            $"150 lines with 30 changed and 30 deleted, searched 40 edits at a time, diffed to {hunks.Count} hunks, the widest {widest} lines, where each change is a hunk of its own");
    }

    /// <summary>Lines each unlike any other.</summary>
    private static string[] Numbered(int count) => [.. Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture, $"line {i} of the file"))];

    /// <summary>The lines with every fifth from the second changed and every fifth from the fourth deleted.</summary>
    private static string[] Edited(string[] lines) =>
        [.. lines.Select((line, i) => (line, i)).Where(l => l.i % 5 != 3).Select(l => l.i % 5 == 1 ? l.line + " changed" : l.line)];

    /// <summary>The hunks are a diff of the texts: in order, apart, and the lines between them alike.</summary>
    private static void Holds(string[] before, string[] after, IReadOnlyList<DiffHunk> hunks)
    {
        int oldAt = 0;
        int newAt = 0;
        foreach (DiffHunk hunk in hunks.Append(new DiffHunk(before.Length, 0, after.Length, 0)))
        {
            Assert.True(hunk.OldStart - oldAt == hunk.NewStart - newAt && hunk.OldStart >= oldAt,
                $"The lines alike before the hunk at {hunk.OldStart}, {hunk.NewStart} number {hunk.OldStart - oldAt} before and {hunk.NewStart - newAt} after");
            for (int i = 0; i < hunk.OldStart - oldAt; i++)
            {
                Assert.True(before[oldAt + i] == after[newAt + i], $"Line {oldAt + i} left out of every hunk reads \"{before[oldAt + i]}\" before and \"{after[newAt + i]}\" after");
            }
            oldAt = hunk.OldStart + hunk.OldCount;
            newAt = hunk.NewStart + hunk.NewCount;
        }
    }
}

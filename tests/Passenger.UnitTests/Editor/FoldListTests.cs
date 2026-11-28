using System.Text;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>A document's fold regions and rows above lines kept in runs: the regions found again take what the person folded
/// by where each starts, making again only the runs that changes, so a file of thousands of regions copies none of the
/// others; and the rows above lines carried through edits stand where each edit puts them, an edit copying the runs it
/// reaches alone.</summary>
public class FoldListTests
{
    [Fact]
    public void Folds_and_rows_above_lines_kept_in_runs_remake_only_what_changed() => Cases.All(
        ("Folded at starts folds the regions starting there and opens the rest", Folded_at_starts_folds_the_regions_starting_there_and_opens_the_rest),
        ("Folded at reads each region where the edits since carried it", Folded_at_reads_each_region_where_the_edits_since_carried_it),
        ("Folding one region of six thousand four hundred copies one run of them", Folding_one_region_of_six_thousand_four_hundred_copies_one_run_of_them),
        ("Rows above lines carried through random edits stand where each edit puts them and an edit copies the runs it reaches", Rows_above_lines_carried_through_random_edits_stand_where_each_edit_puts_them_and_an_edit_copies_the_runs_it_reaches));

    private void Folded_at_starts_folds_the_regions_starting_there_and_opens_the_rest()
    {
        FoldList open = FoldList.Of(Regions(300, folded: [7, 8, 200]));
        int[] starts = [Start(7), Start(130), Start(299)];
        FoldList folded = open.FoldedAt(starts);
        for (int i = 0; i < folded.Count; i++)
        {
            bool expected = i is 7 or 130 or 299;
            Assert.True(folded[i].Folded == expected, $"Region {i} is {(folded[i].Folded ? "folded" : "open")}, folded at {string.Join(", ", starts)}");
            Assert.Equal(open[i] with { Folded = expected }, folded[i]);
        }
        Assert.Same(folded, folded.FoldedAt(starts));
        Assert.DoesNotContain(folded.FoldedAt([]), r => r.Folded);
    }

    private void Folded_at_reads_each_region_where_the_edits_since_carried_it()
    {
        FoldList carried = FoldList.Of(Regions(300, folded: [])).Through([new TextEdit(0, 0, "// one more line\n")]);
        int shift = "// one more line\n".Length;
        FoldList folded = carried.FoldedAt([Start(150) + shift]);
        Assert.Equal(150, Assert.Single(Enumerable.Range(0, folded.Count), i => folded[i].Folded));
        Assert.Equal(Start(150) + shift, folded[150].Start);
        Assert.Equal(carried, folded.FoldedAt([]));
    }

    private void Folding_one_region_of_six_thousand_four_hundred_copies_one_run_of_them()
    {
        FoldList open = FoldList.Of(Regions(6400, folded: []));
        int[] one = [Start(3200)];
        Assert.True(open.FoldedAt(one)[3200].Folded, "The region was not folded");
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        FoldList folded = open.FoldedAt(one);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(folded[3200].Folded, "The region was not folded");
        Assert.True(allocated < 8 * 1024,
            $"Folding one region of 6,400 allocated {allocated:N0} bytes, where its run of 64 and the list of 100 runs take about 6 KB: the other regions were copied");
    }

    /// <summary>Random edits (typing, line breaks, deletions within a line and across lines) to a file of rows above lines:
    /// after each, the list carried through it holds the rows a plain list moved row by row holds, by offset, word and
    /// count before every line's start; and an edit among twenty thousand rows allocates what one among two thousand does.</summary>
    private void Rows_above_lines_carried_through_random_edits_stand_where_each_edit_puts_them_and_an_edit_copies_the_runs_it_reaches()
    {
        Random random = new(1464);
        TextDocument document = new(Lines(3000));
        List<RowAbove> model = [.. Enumerable.Range(0, 3000).Where(i => i % 3 != 1).Select(i => Row(document.Snapshot, i))];
        RowAboveList list = RowAboveList.Of(model);
        document.ChangedFirst += (_, change) =>
        {
            list = list.Through(change);
            model = [.. model.Where(r => !TakenAway(change, r.Offset)).Select(r => r with { Offset = TextEditing.MapOffset(r.Offset, change.Edits, afterInsertions: true) })];
        };
        string[] typed = ["x", "\n", "    ", "int y;\n", ""];
        for (int step = 0; step < 200; step++)
        {
            TextSnapshot text = document.Snapshot;
            int at = random.Next(text.Length + 1);
            int length = random.Next(4) switch
            {
                0 => 0,
                1 => Math.Min(text.Length - at, random.Next(1, 4)),
                2 => Math.Min(text.Length - at, random.Next(10, 80)),
                _ => Math.Min(text.Length - at, random.Next(200, 900)),
            };
            document.Edit([new TextEdit(at, length, typed[random.Next(typed.Length)])], SelectionSet.Single(at), SelectionSet.Single(at));
            TextSnapshot now = document.Snapshot;
            Assert.True(list.Count == model.Count, $"Step {step} (seed 1464): {list.Count} rows above carried, where {model.Count} stand");
            int i = 0;
            foreach (RowAbove row in list)
            {
                Assert.True(row.Offset == model[i].Offset && row.Words[0].Text == model[i].Words[0].Text,
                    $"Step {step} (seed 1464): row {i} stands at {row.Offset} saying {row.Words[0].Text}, where it stands at {model[i].Offset} saying {model[i].Words[0].Text}");
                i++;
            }
            for (int line = random.Next(20); line < now.LineCount; line += 401)
            {
                int start = now.GetLineStart(line);
                Assert.True(list.CountBefore(start) == model.Count(r => r.Offset < start), $"Step {step} (seed 1464): {list.CountBefore(start)} rows above stand before line {line + 1}, where {model.Count(r => r.Offset < start)} do");
            }
        }
        long few = Edited(2_000);
        long many = Edited(20_000);
        Assert.True(many < few + 1024, $"An edit among 20,000 rows above lines allocated {many:N0} bytes, against {few:N0} among 2,000: the runs it did not reach were copied");

        static string Lines(int count)
        {
            StringBuilder text = new();
            for (int i = 0; i < count; i++)
            {
                text.Append("    int value").Append(i).Append(";\n");
            }
            return text.ToString();
        }

        static RowAbove Row(TextSnapshot text, int line) => new(text.GetLineStart(line) + 4, [new RowAboveWord($"{line} usages")]);

        static bool TakenAway(DocumentChangedEventArgs change, int offset) =>
            change.Edits.Any(e => e.Start <= offset && offset < e.End && change.Before.GetLineFromOffset(e.Start) != change.Before.GetLineFromOffset(e.End));

        // The least an edit in the middle of a file of rows above every line allocates carrying them, of three.
        static long Edited(int lines)
        {
            TextDocument document = new(Lines(lines));
            RowAboveList rows = RowAboveList.Of([.. Enumerable.Range(0, lines).Select(i => Row(document.Snapshot, i))]);
            long least = long.MaxValue;
            document.ChangedFirst += (_, change) =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                rows = rows.Through(change);
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            };
            int middle = document.Snapshot.GetLineStart(lines / 2) + 6;
            for (int i = 0; i < 3; i++)
            {
                document.Edit([new TextEdit(middle, 0, "x")], SelectionSet.Single(middle), SelectionSet.Single(middle + 1));
            }
            return least;
        }
    }

    /// <summary>Where region <paramref name="index"/> starts: forty characters past the one before, each 200 long.</summary>
    private static int Start(int index) => index * 40;

    private static FoldRegion[] Regions(int count, int[] folded) =>
        [.. Enumerable.Range(0, count).Select(i => new FoldRegion(Start(i), Start(i) + 200, "{...}", Folded: folded.Contains(i)))];
}

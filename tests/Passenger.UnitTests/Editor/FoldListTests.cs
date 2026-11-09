using Passenger.Editor;
using Passenger.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>A document's fold regions kept in runs: the regions found again take what the person folded by where each
/// starts, making again only the runs that changes, so a file of thousands of regions copies none of the others.</summary>
public class FoldListTests
{
    [Fact]
    public void Folded_at_starts_folds_the_regions_starting_there_and_opens_the_rest()
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

    [Fact]
    public void Folded_at_reads_each_region_where_the_edits_since_carried_it()
    {
        FoldList carried = FoldList.Of(Regions(300, folded: [])).Through([new TextEdit(0, 0, "// one more line\n")]);
        int shift = "// one more line\n".Length;
        FoldList folded = carried.FoldedAt([Start(150) + shift]);
        Assert.Equal(150, Assert.Single(Enumerable.Range(0, folded.Count), i => folded[i].Folded));
        Assert.Equal(Start(150) + shift, folded[150].Start);
        Assert.Equal(carried, folded.FoldedAt([]));
    }

    [Fact]
    public void Folding_one_region_of_six_thousand_four_hundred_copies_one_run_of_them()
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

    /// <summary>Where region <paramref name="index"/> starts: forty characters past the one before, each 200 long.</summary>
    private static int Start(int index) => index * 40;

    private static FoldRegion[] Regions(int count, int[] folded) =>
        [.. Enumerable.Range(0, count).Select(i => new FoldRegion(Start(i), Start(i) + 200, "{...}", Folded: folded.Contains(i)))];
}

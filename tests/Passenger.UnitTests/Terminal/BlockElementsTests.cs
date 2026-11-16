using Avalonia;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>The share of its cell each block element fills, as Unicode's names say: the full block all of it, the
/// eighths from the foot and from the left, the halves and the quadrants; the shades and any other character none, left to
/// the font; a run spans as one rectangle only where its element fills its cell's width.</summary>
public class BlockElementsTests
{
    [Theory]
    [InlineData(0x2588, "0 0 1 1")]
    [InlineData(0x2580, "0 0 1 0.5")]
    [InlineData(0x2584, "0 0.5 1 0.5")]
    [InlineData(0x2581, "0 0.875 1 0.125")]
    [InlineData(0x2587, "0 0.125 1 0.875")]
    [InlineData(0x2589, "0 0 0.875 1")]
    [InlineData(0x258C, "0 0 0.5 1")]
    [InlineData(0x258F, "0 0 0.125 1")]
    [InlineData(0x2590, "0.5 0 0.5 1")]
    [InlineData(0x2594, "0 0 1 0.125")]
    [InlineData(0x2595, "0.875 0 0.125 1")]
    [InlineData(0x2596, "0 0.5 0.5 0.5")]
    [InlineData(0x259A, "0 0 0.5 0.5|0.5 0.5 0.5 0.5")]
    [InlineData(0x259F, "0.5 0 0.5 0.5|0 0.5 0.5 0.5|0.5 0.5 0.5 0.5")]
    [InlineData(0x2591, "")]
    [InlineData(0x2593, "")]
    [InlineData('#', "")]
    [InlineData(0x2500, "")]
    public void A_block_element_fills_the_share_of_its_cell_its_name_says(int rune, string shares) =>
        Assert.Equal(shares, string.Join('|', BlockElements.Of(rune).ToArray().Select(Said)));

    [Theory]
    [InlineData(0x2588, true)]
    [InlineData(0x2580, true)]
    [InlineData(0x2582, true)]
    [InlineData(0x2594, true)]
    [InlineData(0x258C, false)]
    [InlineData(0x2590, false)]
    [InlineData(0x2599, false)]
    [InlineData(0x2592, false)]
    public void A_run_of_an_element_spans_as_one_only_where_it_fills_its_cells_width(int rune, bool spans) =>
        Assert.Equal(spans, BlockElements.Spans(rune));

    private static string Said(Rect share) => FormattableString.Invariant($"{share.X} {share.Y} {share.Width} {share.Height}");
}

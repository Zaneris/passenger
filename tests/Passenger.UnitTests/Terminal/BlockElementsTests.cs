using Avalonia;
using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>The share of its cell each block element fills, as Unicode's names say: the full block all of it, the
/// eighths from the foot and from the left, the halves and the quadrants; the shades and any other character none, left to
/// the font; a run spans as one rectangle only where its element fills its cell's width.</summary>
public class BlockElementsTests
{
    [Fact]
    public void A_block_element_fills_its_share_of_the_cell_and_a_run_spans_as_one_where_it_fills_its_width() => Cases.All(
        ("A block element fills the share of its cell its name says", A_block_element_fills_the_share_of_its_cell_its_name_says),
        ("A run of an element spans as one only where it fills its cells width", A_run_of_an_element_spans_as_one_only_where_it_fills_its_cells_width));

    private void A_block_element_fills_the_share_of_its_cell_its_name_says() => Cases.Each<(int rune, string shares)>(
    [
        (0x2588, "0 0 1 1"),
        (0x2581, "0 0.875 1 0.125"),
        (0x258F, "0 0 0.125 1"),
        (0x2595, "0.875 0 0.125 1"),
        (0x2591, ""),
        (0x2500, ""),
    ], row =>
    {
        (int rune, string shares) = row;
        
                Assert.Equal(shares, string.Join('|', BlockElements.Of(rune).ToArray().Select(Said)));
    });

    private void A_run_of_an_element_spans_as_one_only_where_it_fills_its_cells_width() => Cases.Each<(int rune, bool spans)>(
    [
        (0x2588, true),
        (0x2580, true),
        (0x2594, true),
        (0x258C, false),
        (0x2599, false),
        (0x2592, false),
    ], row =>
    {
        (int rune, bool spans) = row;
        
                Assert.Equal(spans, BlockElements.Spans(rune));
    });

    private static string Said(Rect share) => FormattableString.Invariant($"{share.X} {share.Y} {share.Width} {share.Height}");
}

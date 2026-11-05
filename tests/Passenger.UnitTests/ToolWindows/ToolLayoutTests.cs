using static Passenger.App.ToolWindows.ToolLayout;

namespace Passenger.UnitTests.ToolWindows;

/// <summary>How a tool window's bar shares out its width: parts wrap where the next would not fit, what a row leaves goes to
/// the parts that fill, and a window narrow and tall stands its two panes one over the other.</summary>
public sealed class ToolLayoutTests
{
    private const double Gap = 2;

    [Fact]
    public void Parts_that_fit_stay_in_one_row_and_the_first_that_does_not_starts_the_next()
    {
        Piece[] buttons = [new(28), new(28), new(28)];
        Assert.Equal([0], RowStarts(buttons, 88, Gap));
        Assert.Equal([0, 2], RowStarts(buttons, 87, Gap));
    }

    [Fact]
    public void A_part_wider_than_the_row_has_a_row_of_its_own_and_no_more_than_the_row()
    {
        Piece[] parts = [new(28), new(400), new(28)];
        Assert.Equal([0, 1, 2], RowStarts(parts, 200, Gap));
        Assert.Equal([200], Spread([parts[1]], 200, Gap));
    }

    [Fact]
    public void A_part_that_fills_wraps_at_the_least_it_asks()
    {
        Piece[] parts = [new(100), new(160, new Fills(160, 220))];
        Assert.Equal([0], RowStarts(parts, 262, Gap));
        Assert.Equal([0, 1], RowStarts(parts, 261, Gap));
    }

    [Fact]
    public void A_part_that_fills_takes_up_to_its_most_beside_others_and_the_whole_row_alone()
    {
        Fills search = new(160, 220);
        Assert.Equal([100, 220], Spread([new(100), new(160, search)], 500, Gap));
        Assert.Equal([264], Spread([new(160, search)], 264, Gap));
    }

    [Fact]
    public void What_a_row_leaves_goes_to_parts_with_a_most_before_the_first_with_none()
    {
        Fills words = new(120);
        Fills search = new(160, 220);
        Assert.Equal([100, 376, 220], Spread([new(100), new(120, words), new(160, search)], 700, Gap));
    }

    [Fact]
    public void A_row_with_nothing_to_fill_keeps_its_parts_widths()
    {
        Assert.Equal([28, 28], Spread([new(28), new(28)], 500, Gap));
    }

    [Theory]
    [InlineData(264, 800, true)]
    [InlineData(479, 800, true)]
    [InlineData(480, 800, false)]
    [InlineData(1100, 300, false)]
    [InlineData(283, 250, false)]
    public void Two_panes_stand_one_over_the_other_only_in_a_window_narrow_and_tall(double width, double height, bool stacked) =>
        Assert.Equal(stacked, Stacks(width, height));
}

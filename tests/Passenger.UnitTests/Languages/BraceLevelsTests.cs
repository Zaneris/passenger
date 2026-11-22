using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Languages;

/// <summary>Rainbow braces' levels: a depth cycles through exactly the three to six colours a theme gives.</summary>
public class BraceLevelsTests
{
    [Fact]
    public void A_depth_cycles_through_exactly_the_levels_a_theme_gives() => Cases.Each<(int, int[])>(
        [
            (3, new[] { 0, 1, 2, 0, 1, 2, 0 }),
            (6, new[] { 0, 1, 2, 3, 4, 5, 0 }),
        ], row => A_depth_cycles_through_exactly_the_levels_given(row.Item1, row.Item2));

    private void A_depth_cycles_through_exactly_the_levels_given(int levels, int[] cycle) =>
        Assert.Equal(cycle, Enumerable.Range(0, cycle.Length).Select(depth => BraceLevels.Of(depth, levels)));
}

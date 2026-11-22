using Passenger.Languages.CSharp;

namespace Passenger.UnitTests.Languages;

/// <summary>Rainbow braces' levels: a depth cycles through exactly the three to six colours a theme gives, and the levels a
/// theme gives count only the fourth to sixth given in turn.</summary>
public class BraceLevelsTests
{
    [Fact]
    public void A_depth_cycles_through_the_levels_a_theme_gives_held_to_three_to_six() => Cases.All(
        ("a depth cycles through exactly the levels given", () => Cases.Each<(int, int[])>(
        [
            (3, new[] { 0, 1, 2, 0, 1, 2, 0 }),
            (6, new[] { 0, 1, 2, 3, 4, 5, 0 }),
        ], row => A_depth_cycles_through_exactly_the_levels_given(row.Item1, row.Item2))),
        ("five levels bring the sixth depth round to the first and levels out of range are held to three to six", Five_levels_bring_the_sixth_depth_round_to_the_first_and_levels_out_of_range_are_held_to_three_to_six),
        ("the levels given count the fourth to sixth only in turn", The_levels_given_count_the_fourth_to_sixth_only_in_turn));

    private void A_depth_cycles_through_exactly_the_levels_given(int levels, int[] cycle) =>
        Assert.Equal(cycle, Enumerable.Range(0, cycle.Length).Select(depth => BraceLevels.Of(depth, levels)));

    private void Five_levels_bring_the_sixth_depth_round_to_the_first_and_levels_out_of_range_are_held_to_three_to_six()
    {
        Assert.Equal(0, BraceLevels.Of(5, 5));
        Assert.Equal(4, BraceLevels.Of(4, 5));
        Assert.Equal(BraceLevels.Of(7, 3), BraceLevels.Of(7, 1));
        Assert.Equal(BraceLevels.Of(7, 6), BraceLevels.Of(7, 9));
    }

    private void The_levels_given_count_the_fourth_to_sixth_only_in_turn()
    {
        Assert.Equal(3, BraceLevels.Given(_ => false));
        Assert.Equal(5, BraceLevels.Given(level => level <= 5));
        Assert.Equal(6, BraceLevels.Given(_ => true));
        Assert.Equal(3, BraceLevels.Given(level => level == 5));
    }
}

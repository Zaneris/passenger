using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Select All Occurrences as the carets it leaves (<see cref="Marked"/>), the selection it began from kept as
/// the primary.</summary>
public class SelectionCommandsTests
{
    [Theory]
    [InlineData("a«aa|", "a«aa|", 1)]
    [InlineData("«aa|a", "«aa|a", 0)]
    [InlineData("a«aa|aa", "a«aa|«aa|", 1)]
    [InlineData("aaa«aa|", "«aa|a«aa|", 3)]
    [InlineData("ab «ab| ab", "«ab| «ab| «ab|", 3)]
    [InlineData("foo f|oo", "«foo| «foo|", 4)]
    public void Select_All_Occurrences_keeps_the_selection_it_began_from_and_leaves_out_copies_overlapping_it(string before, string after, int primaryStart)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        SelectionSet all = SelectionCommands.SelectAllOccurrences(text, selections, out bool limited);
        Assert.False(limited);
        Assert.Equal(after, Marked.Show(text, all));
        Assert.True(primaryStart == all.Primary.Start, $"The primary selection moved to {all.Primary.Start}, so typing replaces other characters than those chosen.");
    }
}

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
        SelectionSet all = SelectionCommands.SelectAllOccurrences(text, selections, wholeWords: false, out bool limited);
        Assert.False(limited);
        Assert.Equal(after, Marked.Show(text, all));
        Assert.True(primaryStart == all.Primary.Start, $"The primary selection moved to {all.Primary.Start}, so typing replaces other characters than those chosen.");
    }

    [Fact]
    public void A_word_taken_from_a_caret_is_found_only_as_a_whole_word_by_Select_All_and_Select_Next()
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse("_value va|lue values");
        SelectionSet all = SelectionCommands.SelectAllOccurrences(text, caret, wholeWords: false, out _);
        Assert.True(Marked.Show(text, all) == "_value «value| values", $"Select All Occurrences from a caret in value chose {Marked.Show(text, all)}, so typing would rename _value and values too");
        SelectionSet word = SelectionCommands.SelectNextOccurrence(text, caret, wholeWords: false);
        SelectionSet next = SelectionCommands.SelectNextOccurrence(text, word, wholeWords: true);
        Assert.True(Marked.Show(text, next) == "_value «value| values", $"Select Next Occurrence from a caret in value chose {Marked.Show(text, next)}, so typing would rename _value and values too");
    }

    [Fact]
    public void Select_Next_from_a_caret_passes_over_the_word_inside_longer_names_to_its_next_whole_copy()
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse("va|lue _value values value");
        SelectionSet word = SelectionCommands.SelectNextOccurrence(text, caret, wholeWords: false);
        SelectionSet next = SelectionCommands.SelectNextOccurrence(text, word, wholeWords: true);
        Assert.Equal("«value| _value values «value|", Marked.Show(text, next));
    }

    [Fact]
    public void A_selection_made_by_hand_is_found_inside_longer_names_too()
    {
        (TextSnapshot text, SelectionSet selected) = Marked.Parse("_value «value| values");
        SelectionSet all = SelectionCommands.SelectAllOccurrences(text, selected, wholeWords: false, out _);
        Assert.Equal("_«value| «value| «value|s", Marked.Show(text, all));
        SelectionSet next = SelectionCommands.SelectNextOccurrence(text, selected, wholeWords: false);
        Assert.Equal("_value «value| «value|s", Marked.Show(text, next));
    }
}

using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A word chosen whole, as a double-click chooses it, and its other places in the text: every copy standing as a
/// whole word in its case (<see cref="WordCharacters"/>), and past the limit the run around the chosen one.</summary>
public class ChosenWordTests
{
    [Theory]
    [InlineData("«public| static", true)]
    [InlineData("x «value|;", true)]
    [InlineData("«café| au lait", true)]
    [InlineData("«publi|c", false)]
    [InlineData("_«value|s", false)]
    [InlineData("«cafe|́ au lait", false)]
    [InlineData("«public static|", false)]
    [InlineData("x «.|y", false)]
    [InlineData("«one\n|two", false)]
    public void A_range_is_a_whole_word_only_as_a_double_click_would_choose_it(string marked, bool whole)
    {
        (TextSnapshot text, SelectionSet chosen) = Marked.Parse(marked);
        Assert.True(TextNavigation.IsWholeWord(text, chosen.Primary.Range) == whole,
            $"{marked} was read as {(whole ? "no" : "a")} whole word, so a selection {(whole ? "a double-click makes lights nothing" : "of part of a word or of several lights a word's places")}");
    }

    [Fact]
    public void A_words_places_are_its_copies_standing_whole_in_its_case_the_chosen_one_among_them()
    {
        (TextSnapshot text, SelectionSet chosen) = Marked.Parse(
            "// The public count: publicly, Public and _public are others.\n«public| static int Count;\nstring said = \"public\";\npublić x;\n");

        IReadOnlyList<TextRange> places = TextSearch.PlacesOfWord(text, chosen.Primary.Range);

        Assert.Equal(["public", "public", "public"], places.Select(text.GetText));
        Assert.Equal([0, 1, 2], places.Select(p => text.GetLineFromOffset(p.Start)));
        Assert.Contains(chosen.Primary.Range, places);
    }

    [Fact]
    public void A_range_that_is_no_whole_word_has_no_places()
    {
        (TextSnapshot text, SelectionSet chosen) = Marked.Parse("int «coun|t = count + count;\n");
        Assert.Empty(TextSearch.PlacesOfWord(text, chosen.Primary.Range));
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(30, 25)]
    [InlineData(48, 40)]
    public void Past_the_limit_the_places_are_the_run_around_the_chosen_one(int chosen, int first)
    {
        string line = "word, sword and word\n";
        TextSnapshot text = TextSnapshot.From(string.Concat(Enumerable.Repeat(line, 25)));
        IReadOnlyList<TextRange> all = TextSearch.PlacesOfWord(text, new TextRange(0, 4), limit: int.MaxValue);
        Assert.Equal(50, all.Count);

        IReadOnlyList<TextRange> places = TextSearch.PlacesOfWord(text, all[chosen], limit: 10);

        Assert.True(places.SequenceEqual(all.Skip(first).Take(10)),
            $"Of 50 places, the 10 lit around the {chosen}th began at the {all.TakeWhile(p => p != places[0]).Count()}th, not the {first}th, so the places on screen beside it may go unlit");
    }
}

using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A word chosen whole, as a double-click chooses it, and its other places in the text: every copy standing as a
/// whole word in its case (<see cref="WordCharacters"/>), and past the limit the run around the chosen one.</summary>
public class ChosenWordTests
{
    [Fact]
    public void A_word_chosen_whole_finds_its_other_whole_copies_in_its_case() => Cases.All(
        ("A range is a whole word only as a double click would choose it", A_range_is_a_whole_word_only_as_a_double_click_would_choose_it),
        ("A words places are its copies standing whole in its case the chosen one among them", A_words_places_are_its_copies_standing_whole_in_its_case_the_chosen_one_among_them),
        ("A range that is no whole word has no places", A_range_that_is_no_whole_word_has_no_places),
        ("Past the limit the places are the run around the chosen one", Past_the_limit_the_places_are_the_run_around_the_chosen_one));

    private void A_range_is_a_whole_word_only_as_a_double_click_would_choose_it() => Cases.Each<(string marked, bool whole)>(
    [
        ("«public| static", true),
        ("«café| au lait", true),
        ("«publi|c", false),
        ("«cafe|́ au lait", false),
        ("«public static|", false),
        ("«one\n|two", false),
    ], row =>
    {
        (string marked, bool whole) = row;
        (TextSnapshot text, SelectionSet chosen) = Marked.Parse(marked);
        Assert.True(TextNavigation.IsWholeWord(text, chosen.Primary.Range) == whole,
            $"{marked} was read as {(whole ? "no" : "a")} whole word, so a selection {(whole ? "a double-click makes lights nothing" : "of part of a word or of several lights a word's places")}");
    });

    private void A_words_places_are_its_copies_standing_whole_in_its_case_the_chosen_one_among_them()
    {
        (TextSnapshot text, SelectionSet chosen) = Marked.Parse(
            "// The public count: publicly, Public and _public are others.\n«public| static int Count;\nstring said = \"public\";\npublić x;\n");

        IReadOnlyList<TextRange> places = TextSearch.PlacesOfWord(text, chosen.Primary.Range);

        Assert.Equal(["public", "public", "public"], places.Select(text.GetText));
        Assert.Equal([0, 1, 2], places.Select(p => text.GetLineFromOffset(p.Start)));
        Assert.Contains(chosen.Primary.Range, places);
    }

    private void A_range_that_is_no_whole_word_has_no_places()
    {
        (TextSnapshot text, SelectionSet chosen) = Marked.Parse("int «coun|t = count + count;\n");
        Assert.Empty(TextSearch.PlacesOfWord(text, chosen.Primary.Range));
    }

    private void Past_the_limit_the_places_are_the_run_around_the_chosen_one() => Cases.Each<(int chosen, int first)>(
    [
        (3, 0),
        (30, 25),
        (48, 40),
    ], row =>
    {
        (int chosen, int first) = row;
        string line = "word, sword and word\n";
        TextSnapshot text = TextSnapshot.From(string.Concat(Enumerable.Repeat(line, 25)));
        IReadOnlyList<TextRange> all = TextSearch.PlacesOfWord(text, new TextRange(0, 4), limit: int.MaxValue);
        Assert.Equal(50, all.Count);

        IReadOnlyList<TextRange> places = TextSearch.PlacesOfWord(text, all[chosen], limit: 10);

        Assert.True(places.SequenceEqual(all.Skip(first).Take(10)),
            $"Of 50 places, the 10 lit around the {chosen}th began at the {all.TakeWhile(p => p != places[0]).Count()}th, not the {first}th, so the places on screen beside it may go unlit");
    });
}

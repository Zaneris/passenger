using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>One rule for what words are made of (<see cref="WordCharacters"/>), read alike by the editor's word moves
/// and double-click, Select Next and Select All's whole words and Find's Words: a letter's marks are its word's, and a
/// mark on an emoji keeps it out of the word beside it.</summary>
public class WordCharactersTests
{
    /// <summary>An é written as e and a combining acute.</summary>
    private const string Cafe = "cafe\u0301";

    /// <summary>Hindi's namaskar, its ा a spacing mark (U+093E).</summary>
    private const string Namaskar = "नमस्कार";

    /// <summary>A heart and the selector asking for its emoji look, a nonspacing mark that is no letter's.</summary>
    private const string Heart = "\u2764\uFE0F";

    [Fact]
    public void Word_moves_and_a_double_click_take_a_letters_marks_with_it_and_an_emoji_alone() => Cases.All(
        ("A word move right steps over a letters marks and stops at an emojis end", A_word_move_right_steps_over_a_letters_marks_and_stops_at_an_emojis_end),
        ("A word move left steps back over a letters marks to its words start", A_word_move_left_steps_back_over_a_letters_marks_to_its_words_start),
        ("A double click takes a word with its letters marks and an emoji with its own", A_double_click_takes_a_word_with_its_letters_marks_and_an_emoji_with_its_own),
        ("Select Next and Select All by whole words pass over a word inside one whose letter carries a mark", Select_Next_and_Select_All_by_whole_words_pass_over_a_word_inside_one_whose_letter_carries_a_mark));

    private void A_word_move_right_steps_over_a_letters_marks_and_stops_at_an_emojis_end() => Cases.Each<(string before, WordStop stop, string after)>(
    [
        ($"|{Cafe} x", WordStop.End, $"{Cafe}| x"),
        ($"|{Cafe} x", WordStop.Start, $"{Cafe} |x"),
        ("|नमस्ते x", WordStop.End, "नमस्ते| x"),
        ($"|{Namaskar} x", WordStop.Start, $"{Namaskar} |x"),
        ($"|{Heart}abc", WordStop.End, $"{Heart}|abc"),
    ], row =>
    {
        (string before, WordStop stop, string after) = row;
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        int to = TextNavigation.NextWord(text, caret.Primary.Caret, stop);
        Assert.True(Marked.Show(text, SelectionSet.Single(to)) == after,
            $"A word move right from {before} went to {Marked.Show(text, SelectionSet.Single(to))}, so the caret stops inside a letter and its marks");
    });

    private void A_word_move_left_steps_back_over_a_letters_marks_to_its_words_start() => Cases.Each<(string before, string after)>(
    [
        ($"{Cafe} |x", $"|{Cafe} x"),
        ($"x {Namaskar}|", $"x |{Namaskar}"),
        ($"{Heart}abc|", $"{Heart}|abc"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        int to = TextNavigation.PreviousWord(text, caret.Primary.Caret);
        Assert.True(Marked.Show(text, SelectionSet.Single(to)) == after,
            $"A word move left from {before} went to {Marked.Show(text, SelectionSet.Single(to))}, so the caret stops inside a letter and its marks");
    });

    private void A_double_click_takes_a_word_with_its_letters_marks_and_an_emoji_with_its_own() => Cases.Each<(string before, string after)>(
    [
        ("ca|fe\u0301 x", $"«{Cafe}| x"),
        ($"x {Cafe}|", $"x «{Cafe}|"),
        ($"{Heart}a|bc", $"{Heart}«abc|"),
        ($"|{Heart}abc", $"«{Heart}|abc"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        TextRange word = TextNavigation.WordAt(text, caret.Primary.Caret);
        string shown = Marked.Show(text, SelectionSet.Single(new Selection(word.Start, word.End)));
        Assert.True(shown == after, $"A double-click in {before} chose {shown}, splitting a letter from its marks");
    });

    private void Select_Next_and_Select_All_by_whole_words_pass_over_a_word_inside_one_whose_letter_carries_a_mark()
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse($"ca|fe {Cafe} cafe");
        SelectionSet all = SelectionCommands.SelectAllOccurrences(text, caret, wholeWords: false, out _);
        Assert.True(Marked.Show(text, all) == $"«cafe| {Cafe} «cafe|",
            $"Select All Occurrences from a caret in cafe chose {Marked.Show(text, all)}, so typing would split the é of café from its accent");
        SelectionSet word = SelectionCommands.SelectNextOccurrence(text, caret, wholeWords: false);
        SelectionSet next = SelectionCommands.SelectNextOccurrence(text, word, wholeWords: true);
        Assert.True(Marked.Show(text, next) == $"«cafe| {Cafe} «cafe|",
            $"Select Next Occurrence from a caret in cafe chose {Marked.Show(text, next)}, so typing would split the é of café from its accent");
    }

    [Fact]
    public void Whole_word_finds_and_a_typed_quote_read_a_letters_marks_as_its_own() => Cases.All(
        ("Select Next by whole words finds a word right after an emoji", Select_Next_by_whole_words_finds_a_word_right_after_an_emoji),
        ("Finds Words takes a letters marks as its words and an emojis as no words", Finds_Words_takes_a_letters_marks_as_its_words_and_an_emojis_as_no_words),
        ("Finds Words reads the letter before a matchs marks wherever a long texts blocks part", Finds_Words_reads_the_letter_before_a_matchs_marks_wherever_a_long_texts_blocks_part),
        ("A quote typed after a word whose last letter carries a mark opens no pair", A_quote_typed_after_a_word_whose_last_letter_carries_a_mark_opens_no_pair));

    private void Select_Next_by_whole_words_finds_a_word_right_after_an_emoji()
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse($"a|bc {Heart}abc");
        SelectionSet word = SelectionCommands.SelectNextOccurrence(text, caret, wholeWords: false);
        SelectionSet next = SelectionCommands.SelectNextOccurrence(text, word, wholeWords: true);
        Assert.Equal($"«abc| {Heart}«abc|", Marked.Show(text, next));
    }

    private void Finds_Words_takes_a_letters_marks_as_its_words_and_an_emojis_as_no_words() => Cases.Each<(string query, string within, string found)>(
    [
        ("नमस्क", $"{Namaskar} नमस्क", "नमस्कार «नमस्क|"),
        ("cafe", $"{Cafe} cafe", $"{Cafe} «cafe|"),
        ("abc", $"{Heart}abc abc", $"{Heart}«abc| «abc|"),
        ("cafe\u0301", $"{Cafe}\u0301 {Cafe}", $"{Cafe}\u0301 «{Cafe}|"),
    ], row =>
    {
        (string query, string within, string found) = row;
        TextSnapshot text = TextSnapshot.From(within);
        SearchResult result = TextSearch.FindAll(text, new SearchQuery(query, WholeWords: true), TextSearch.AtOnce);
        string shown = Marked.Show(text, SelectionSet.Create([.. result.Matches.Select(m => new Selection(m.Start, m.End))], 0));
        Assert.True(shown == found, $"Find's Words for {query} found {shown}, so it splits a letter from its marks or misses a whole word");
    });

    private void Finds_Words_reads_the_letter_before_a_matchs_marks_wherever_a_long_texts_blocks_part()
    {
        for (int at = 16_300; at < 16_420; at++)
        {
            string within = new string(' ', at - 6) + "e" + new string('\u0301', 5) + "abc abc" + new string(' ', 400);
            SearchResult result = TextSearch.FindAll(TextSnapshot.From(within), new SearchQuery("abc", WholeWords: true), TextSearch.AtOnce);
            Assert.True(result.Matches.Count == 1 && result.Matches[0].Start == at + 4,
                $"Find's Words for abc after an e and its marks at {at} found {string.Join(", ", result.Matches)}, so a word is split where a search's blocks part");
        }
    }

    private void A_quote_typed_after_a_word_whose_last_letter_carries_a_mark_opens_no_pair() => Cases.Each<(string before, string after)>(
    [
        ("cafe\u0301|", "cafe\u0301'|"),
        ("\u2764\uFE0F|", "\u2764\uFE0F'|'"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Type(text, caret, "'")));
    });
}

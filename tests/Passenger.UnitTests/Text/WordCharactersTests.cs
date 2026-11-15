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

    [Theory]
    [InlineData($"|{Cafe} x", WordStop.End, $"{Cafe}| x")]
    [InlineData($"|{Cafe} x", WordStop.Start, $"{Cafe} |x")]
    [InlineData("|नमस्ते x", WordStop.End, "नमस्ते| x")]
    [InlineData($"|{Namaskar} x", WordStop.Start, $"{Namaskar} |x")]
    [InlineData($"|{Heart}abc", WordStop.End, $"{Heart}|abc")]
    public void A_word_move_right_steps_over_a_letters_marks_and_stops_at_an_emojis_end(string before, WordStop stop, string after)
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        int to = TextNavigation.NextWord(text, caret.Primary.Caret, stop);
        Assert.True(Marked.Show(text, SelectionSet.Single(to)) == after,
            $"A word move right from {before} went to {Marked.Show(text, SelectionSet.Single(to))}, so the caret stops inside a letter and its marks");
    }

    [Theory]
    [InlineData($"{Cafe} |x", $"|{Cafe} x")]
    [InlineData($"x {Namaskar}|", $"x |{Namaskar}")]
    [InlineData($"{Heart}abc|", $"{Heart}|abc")]
    public void A_word_move_left_steps_back_over_a_letters_marks_to_its_words_start(string before, string after)
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        int to = TextNavigation.PreviousWord(text, caret.Primary.Caret);
        Assert.True(Marked.Show(text, SelectionSet.Single(to)) == after,
            $"A word move left from {before} went to {Marked.Show(text, SelectionSet.Single(to))}, so the caret stops inside a letter and its marks");
    }

    [Theory]
    [InlineData("ca|fe\u0301 x", $"«{Cafe}| x")]
    [InlineData($"x {Cafe}|", $"x «{Cafe}|")]
    [InlineData($"{Heart}a|bc", $"{Heart}«abc|")]
    [InlineData($"|{Heart}abc", $"«{Heart}|abc")]
    public void A_double_click_takes_a_word_with_its_letters_marks_and_an_emoji_with_its_own(string before, string after)
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        TextRange word = TextNavigation.WordAt(text, caret.Primary.Caret);
        string shown = Marked.Show(text, SelectionSet.Single(new Selection(word.Start, word.End)));
        Assert.True(shown == after, $"A double-click in {before} chose {shown}, splitting a letter from its marks");
    }

    [Fact]
    public void Select_Next_and_Select_All_by_whole_words_pass_over_a_word_inside_one_whose_letter_carries_a_mark()
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
    public void Select_Next_by_whole_words_finds_a_word_right_after_an_emoji()
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse($"a|bc {Heart}abc");
        SelectionSet word = SelectionCommands.SelectNextOccurrence(text, caret, wholeWords: false);
        SelectionSet next = SelectionCommands.SelectNextOccurrence(text, word, wholeWords: true);
        Assert.Equal($"«abc| {Heart}«abc|", Marked.Show(text, next));
    }

    [Theory]
    [InlineData("नमस्क", $"{Namaskar} नमस्क", "नमस्कार «नमस्क|")]
    [InlineData("cafe", $"{Cafe} cafe", $"{Cafe} «cafe|")]
    [InlineData("abc", $"{Heart}abc abc", $"{Heart}«abc| «abc|")]
    [InlineData("cafe\u0301", $"{Cafe}\u0301 {Cafe}", $"{Cafe}\u0301 «{Cafe}|")]
    public void Finds_Words_takes_a_letters_marks_as_its_words_and_an_emojis_as_no_words(string query, string within, string found)
    {
        TextSnapshot text = TextSnapshot.From(within);
        SearchResult result = TextSearch.FindAll(text, new SearchQuery(query, WholeWords: true), TextSearch.AtOnce);
        string shown = Marked.Show(text, SelectionSet.Create([.. result.Matches.Select(m => new Selection(m.Start, m.End))], 0));
        Assert.True(shown == found, $"Find's Words for {query} found {shown}, so it splits a letter from its marks or misses a whole word");
    }

    [Fact]
    public void Finds_Words_reads_the_letter_before_a_matchs_marks_wherever_a_long_texts_blocks_part()
    {
        for (int at = 16_300; at < 16_420; at++)
        {
            string within = new string(' ', at - 6) + "e" + new string('\u0301', 5) + "abc abc" + new string(' ', 400);
            SearchResult result = TextSearch.FindAll(TextSnapshot.From(within), new SearchQuery("abc", WholeWords: true), TextSearch.AtOnce);
            Assert.True(result.Matches.Count == 1 && result.Matches[0].Start == at + 4,
                $"Find's Words for abc after an e and its marks at {at} found {string.Join(", ", result.Matches)}, so a word is split where a search's blocks part");
        }
    }

    [Theory]
    [InlineData("cafe\u0301|", "cafe\u0301'|")]
    [InlineData("\u2764\uFE0F|", "\u2764\uFE0F'|'")]
    public void A_quote_typed_after_a_word_whose_last_letter_carries_a_mark_opens_no_pair(string before, string after)
    {
        (TextSnapshot text, SelectionSet caret) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Type(text, caret, "'")));
    }
}

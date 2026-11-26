using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Cyclic Expand Word's round of words for a word begun at the caret (<see cref="WordExpansion"/>): the nearest
/// above first and on up, the other texts' next, then those below from the end back down, each once, longer than the part
/// typed and beginning with it in its case; none where no word is begun at the caret.</summary>
public class WordExpansionTests
{
    [Fact]
    public void A_word_begun_at_the_caret_is_offered_the_texts_words_it_begins_nearest_above_first() => Cases.Each<(string Text, string[] Elsewhere, string? Begun, string[] Words)>(
    [
        ("total token to| topmost today", [], "to", ["token", "total", "today", "topmost"]),
        ("Token token\nto|\ntoken tokens", ["tonic toast to"], "to", ["token", "tonic", "toast", "tokens"]),
        ("café caféine\ncaf|", [], "caf", ["caféine", "café"]),
        ("alpha |", [], null, []),
        ("count counter|s", [], "counter", []),
    ], row =>
    {
        int caret = row.Text.IndexOf('|', StringComparison.Ordinal);
        string text = row.Text.Remove(caret, 1);
        (int Start, string Begun, IReadOnlyList<string> Words)? found = WordExpansion.At(text, caret, row.Elsewhere);
        Assert.True(found?.Begun == row.Begun && (found?.Words ?? []).SequenceEqual(row.Words),
            $"In \"{row.Text}\" the word begun was \"{found?.Begun}\" with [{string.Join(", ", found?.Words ?? [])}], not \"{row.Begun}\" with [{string.Join(", ", row.Words)}]: Alt+/ would finish it otherwise");
    });
}

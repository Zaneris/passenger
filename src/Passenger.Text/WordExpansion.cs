namespace Passenger.Text;

/// <summary>Cyclic Expand Word's words: those a word begun just before the caret may be finished as, each once, longer than
/// the part typed, which it begins with as written. They stand in one round: the nearest above first and on up to the
/// text's start, then the other texts' words (the other open files'), then those below from the text's end back down to
/// the caret, so a step on from the word as typed reaches the nearest above and a step back the nearest below.</summary>
public static class WordExpansion
{
    /// <summary>Where the word before <paramref name="caret"/> begins, as typed, and its round of words; null where no word's
    /// character stands just before the caret.</summary>
    public static (int Start, string Begun, IReadOnlyList<string> Words)? At(string text, int caret, IEnumerable<string> elsewhere)
    {
        int start = caret;
        while (start > 0 && IsWord(text[start - 1]))
        {
            start--;
        }
        if (start == caret)
        {
            return null;
        }
        string begun = text[start..caret];
        int end = caret;
        while (end < text.Length && IsWord(text[end]))
        {
            end++;
        }
        List<(int Start, int End)> runs = Runs(text);
        HashSet<string> seen = new(StringComparer.Ordinal) { begun };
        List<string> words = [];
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].End <= start)
            {
                Take(text, runs[i]);
            }
        }
        foreach (string other in elsewhere)
        {
            foreach ((int Start, int End) run in Runs(other))
            {
                Take(other, run);
            }
        }
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].Start >= end)
            {
                Take(text, runs[i]);
            }
        }
        return (start, begun, words);

        void Take(string from, (int Start, int End) run)
        {
            if (run.End - run.Start > begun.Length && string.CompareOrdinal(from, run.Start, begun, 0, begun.Length) == 0
                && from[run.Start..run.End] is var word && seen.Add(word))
            {
                words.Add(word);
            }
        }
    }

    /// <summary>A word's character, its marks and joiners among them (<see cref="WordCharacters"/>).</summary>
    private static bool IsWord(char c) => WordCharacters.IsWord(c) || WordCharacters.Extends(c);

    /// <summary>Each run of a word's characters in a text, in order.</summary>
    private static List<(int Start, int End)> Runs(string text)
    {
        List<(int Start, int End)> runs = [];
        for (int at = 0; at < text.Length;)
        {
            if (!IsWord(text[at]))
            {
                at++;
                continue;
            }
            int from = at;
            while (at < text.Length && IsWord(text[at]))
            {
                at++;
            }
            runs.Add((from, at));
        }
        return runs;
    }
}

using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Completion's ranking as a name is typed: the same rows in the same order as scoring the whole list afresh at
/// every key, while a key that lengthens the name scores only what the last one matched, and a key costs the same however
/// long the list; in a list saying how well its items fit, the one that fits better first among those matched the same
/// way.</summary>
public class CompletionRankingTests
{
    private const int MostShown = 200;

    /// <summary>The letters names are made of: words' capitals, runs of them, separators, digits, and characters that match
    /// an ASCII letter ignoring case (the Kelvin sign, the dotless i, the long s, the dotted capital I).</summary>
    private const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.Kıſİé";

    private static readonly string[] Words =
    [
        "String", "Builder", "Str", "Get", "Type", "Or", "Default", "IP", "Address", "Code", "Editor", "Async", "Task", "Value",
        "_field", "count", "Kelvin", "Kelvin", "ıtem", "ſize", "İndex", "S", "s", "url", "URL", "Xml", "HTML",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Narrowing_key_by_key_shows_the_rows_in_the_order_scoring_the_whole_list_afresh_shows(int seed)
    {
        Random random = new(seed);
        for (int list = 0; list < 12; list++)
        {
            CompletionRanking.Entry[] entries = RandomList(random, random.Next(0, 3) == 0 ? random.Next(0, 40) : random.Next(200, 3000), prioritized: random.Next(3) == 0);
            int alike = random.Next(3) == 0 ? 100 : 1;
            int most = random.Next(4) == 0 ? random.Next(1, 20) : MostShown;
            string lead = random.Next(4) == 0 ? "." : string.Empty;
            CompletionRanking ranking = new(entries);
            string typed = lead;
            for (int key = 0; key < 60; key++)
            {
                typed = NextTyped(random, entries, typed, lead);
                string bare = typed.StartsWith(lead, StringComparison.Ordinal) ? typed[lead.Length..] : typed;
                int[] expected = Reference(entries, typed, bare, alike, most);
                int[] shown = ranking.Shown(typed, bare, alike, most);
                Assert.True(expected.SequenceEqual(shown),
                    $"Seed {seed}, list {list}, key {key}: '{typed}' showed [{string.Join(", ", shown.Take(8).Select(i => entries[i].Matched))}…] where the whole list "
                    + $"scored afresh shows [{string.Join(", ", expected.Take(8).Select(i => entries[i].Matched))}…]: narrowing would hide or reorder what the person types for");
            }
        }
    }

    [Fact]
    public void Of_the_names_the_letters_begin_the_one_that_fits_goes_first_after_the_name_typed_whole()
    {
        CompletionRanking.Entry[] entries =
        [
            Entry("abc"), Entry("ab"), Entry("aef", 600), Entry("Aim", 600), Entry("await"), Entry("aefLonger", 400), Entry("xa", 600),
            Entry("aDeprioritized", int.MinValue / 2), Entry("aPreselected", int.MaxValue / 2),
        ];
        CompletionRanking ranking = new(entries);
        string[] Shown(string typed) => [.. ranking.Shown(typed, typed, 1, MostShown).Select(i => entries[i].Matched)];
        Assert.Equal(["aPreselected", "aef", "aefLonger", "ab", "abc", "await", "aDeprioritized", "Aim"], Shown("a"));
        Assert.Equal(["ab", "abc"], Shown("ab"));
        Assert.Equal(["aPreselected", "Aim", "aef", "xa", "aefLonger", "ab", "abc", "await", "aDeprioritized"], Shown(""));

        CompletionRanking.Entry[] unsaid = [Entry("abc"), Entry("ab"), Entry("aef"), Entry("await")];
        CompletionRanking plain = new(unsaid);
        Assert.Equal(["ab", "abc", "aef", "await"], plain.Shown("a", "a", 1, MostShown).Select(i => unsaid[i].Matched));
    }

    [Fact]
    public void A_key_that_lengthens_the_name_scores_only_the_items_the_last_key_matched()
    {
        Random random = new(1560);
        CompletionRanking.Entry[] entries = RandomList(random, 5000);
        CompletionRanking ranking = new(entries);
        ranking.Shown("St", "St", 1, MostShown);
        int matchedSt = entries.Count(e => FuzzyMatch.Label("St", e.Matched) is not null);
        long before = ranking.Scored;
        ranking.Shown("Str", "Str", 1, MostShown);
        long scored = ranking.Scored - before;
        Assert.True(scored <= matchedSt,
            $"Typing 'r' after 'St' scored {scored} items where 'St' matched {matchedSt}: each key would score the whole list of {entries.Length}");
        before = ranking.Scored;
        ranking.Shown("Sx", "Sx", 1, MostShown);
        Assert.True(ranking.Scored - before > 0, "A key that is no lengthening of the name scored nothing: its rows would be the last key's");
    }

    [Fact]
    public void An_item_lacking_a_letter_typed_is_passed_over_unscored()
    {
        CompletionRanking.Entry[] entries = [.. Enumerable.Range(0, 1000).Select(i => Entry($"Item{i}")), Entry("Zebra")];
        CompletionRanking ranking = new(entries);
        long before = ranking.Scored;
        int[] shown = ranking.Shown("Zeb", "Zeb", 1, MostShown);
        Assert.Equal([1000], shown);
        Assert.True(ranking.Scored - before == 1, $"Typing 'Zeb' scored {ranking.Scored - before} items where one holds its letters: every item would be scored on every key");
    }

    [Fact]
    public void Ranking_a_key_allocates_the_same_for_fifty_thousand_items_as_for_five_hundred()
    {
        Random random = new(11);
        long small = KeyBytes(RandomList(random, 500, prefix: "Str"));
        long large = KeyBytes(RandomList(random, 50_000, prefix: "Str"));
        Assert.True(large == small,
            $"A key allocated {large:N0} bytes over 50,000 items and {small:N0} over 500: completion's garbage would grow with the list, not the rows shown");
    }

    /// <summary>What ranking a key allocates once the list has been ranked for a first key, every key keeping more than a
    /// list's rows.</summary>
    private static long KeyBytes(CompletionRanking.Entry[] entries)
    {
        CompletionRanking ranking = new(entries);
        ranking.Shown("St", "St", 1, MostShown);
        ranking.Shown("S", "S", 1, MostShown);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int[] shown = ranking.Shown("Str", "Str", 1, MostShown);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(MostShown, shown.Length);
        return allocated;
    }

    /// <summary>The ranking as the whole list scored afresh gives it: every item scored by <see cref="FuzzyMatch.Label"/>,
    /// the best kept; where any item says how well it fits, the way each was matched first, then how well it fits.</summary>
    private static int[] Reference(CompletionRanking.Entry[] entries, string typed, string bare, int alike, int most)
    {
        bool prioritized = entries.Any(e => e.Priority != 0);
        List<(int Way, int Score, int Index)> matches = [];
        for (int i = 0; i < entries.Length; i++)
        {
            string query = entries[i].ByTyped ? typed : bare;
            if (FuzzyMatch.Label(query, entries[i].Matched) is { } score)
            {
                matches.Add((prioritized ? FuzzyMatch.LabelWay(query, entries[i].Matched, score) : 0, score / alike, i));
            }
        }
        matches.Sort((x, y) =>
        {
            int byWay = y.Way.CompareTo(x.Way);
            if (byWay != 0)
            {
                return byWay;
            }
            int byPriority = entries[y.Index].Priority.CompareTo(entries[x.Index].Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }
            int byScore = y.Score.CompareTo(x.Score);
            if (byScore != 0)
            {
                return byScore;
            }
            int byPreselect = entries[y.Index].Preselected.CompareTo(entries[x.Index].Preselected);
            if (byPreselect != 0)
            {
                return byPreselect;
            }
            int bySort = string.CompareOrdinal(entries[x.Index].Sort, entries[y.Index].Sort);
            return bySort != 0 ? bySort : x.Index.CompareTo(y.Index);
        });
        return [.. matches.Take(most).Select(m => m.Index)];
    }

    /// <summary>What is typed after a key: mostly a letter some item holds, sometimes another character, a backspace, or
    /// the name begun again.</summary>
    private static string NextTyped(Random random, CompletionRanking.Entry[] entries, string typed, string lead)
    {
        int roll = random.Next(20);
        if (roll == 0)
        {
            return lead;
        }
        if (roll <= 3 && typed.Length > lead.Length)
        {
            return typed[..^1];
        }
        if (roll == 4 || entries.Length == 0)
        {
            return typed + Letters[random.Next(Letters.Length)];
        }
        string from = entries[random.Next(entries.Length)].Matched;
        return typed + from[random.Next(from.Length)];
    }

    /// <summary>Priorities as Roslyn's matching gives them: mostly none, then a symbol's kind's, a keyword's, preselected
    /// and set back.</summary>
    private static readonly int[] Priorities = [0, 0, 0, 0, 0, 0, 100, 200, 400, 500, 600, int.MaxValue / 2, int.MinValue / 2];

    private static CompletionRanking.Entry[] RandomList(Random random, int count, string prefix = "", bool prioritized = false)
    {
        CompletionRanking.Entry[] entries = new CompletionRanking.Entry[count];
        for (int i = 0; i < count; i++)
        {
            string label = prefix + string.Concat(Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(6) == 0
                ? new string(Letters[random.Next(Letters.Length)], 1)
                : Words[random.Next(Words.Length)]));
            bool filtered = random.Next(8) == 0;
            string matched = filtered ? "." + label : label;
            string sort = random.Next(3) switch
            {
                0 => label,
                1 => random.Next(5).ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
                _ => "!" + label[..Math.Min(2, label.Length)],
            };
            entries[i] = new CompletionRanking.Entry(matched, filtered, random.Next(30) == 0, sort, prioritized ? Priorities[random.Next(Priorities.Length)] : 0);
        }
        return entries;
    }

    private static CompletionRanking.Entry Entry(string label, int priority = 0) => new(label, false, false, label, priority);
}

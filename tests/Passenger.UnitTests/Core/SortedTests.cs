using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>The editor's sorted lists searched by halving: each search answers what a scan from the start answers, over
/// keys that repeat, in spans, lists and keys read at a place alike.</summary>
public class SortedTests
{
    [Fact]
    public void Every_search_finds_the_first_place_a_scan_from_the_start_finds()
    {
        Random random = new(1028);
        for (int run = 0; run < 200; run++)
        {
            int[] keys = new int[random.Next(0, 40)];
            for (int i = 1; i < keys.Length; i++)
            {
                keys[i] = keys[i - 1] + random.Next(0, 3);
            }
            (int Key, string Name)[] items = [.. keys.Select(k => (k, $"item {k}"))];
            List<(int Key, string Name)> list = [.. items];
            for (int value = -2; value <= (keys.Length > 0 ? keys[^1] : 0) + 2; value++)
            {
                int atOrAfter = Scan(keys, k => k >= value);
                int after = Scan(keys, k => k > value);
                string where = $"for {value} in [{string.Join(", ", keys)}]";
                Assert.True(atOrAfter == Sorted.FirstAtOrAfter(keys, value), $"The first value at or after {where} is wrong, so a caret or a hint there is passed over");
                Assert.True(after == Sorted.FirstAfter(keys, value), $"The first value after {where} is wrong, so a row or a run is taken for its neighbour's");
                Assert.True(atOrAfter == Sorted.FirstAtOrAfter(items, static i => i.Key, value), $"The first item at or after {where} in a span is wrong");
                Assert.True(after == Sorted.FirstAfter(items, static i => i.Key, value), $"The first item after {where} in a span is wrong");
                Assert.True(atOrAfter == Sorted.FirstAtOrAfter(list, static i => i.Key, value), $"The first item at or after {where} in a list is wrong");
                Assert.True(after == Sorted.FirstAfter(list, static i => i.Key, value), $"The first item after {where} in a list is wrong");
                Assert.True(atOrAfter == Sorted.FirstAtOrAfter(keys.Length, keys, static (all, at) => all[at], value), $"The first place at or after {where} by its key is wrong, so a comment on screen is passed over");
                Assert.True(after == Sorted.FirstAfter(keys.Length, keys, static (all, at) => all[at], value), $"The first place after {where} by its key is wrong");
            }
        }
    }

    [Fact]
    public void Nothing_to_search_answers_none_before_and_keys_of_another_kind_compare_as_their_own()
    {
        Assert.Equal(0, Sorted.FirstAtOrAfter<int>([], 5));
        Assert.Equal(0, Sorted.FirstAfter(new List<int>(), static k => k, 5));
        Int128[] wide = [0, Int128.MaxValue / 2, Int128.MaxValue];
        Assert.Equal(1, Sorted.FirstAfter<Int128>(wide, 0));
        Assert.Equal(2, Sorted.FirstAfter<Int128>(wide, Int128.MaxValue / 2));
        Assert.Equal(3, Sorted.FirstAfter<Int128>(wide, Int128.MaxValue));
    }

    private static int Scan(int[] keys, Func<int, bool> reached)
    {
        int at = 0;
        while (at < keys.Length && !reached(keys[at]))
        {
            at++;
        }
        return at;
    }
}

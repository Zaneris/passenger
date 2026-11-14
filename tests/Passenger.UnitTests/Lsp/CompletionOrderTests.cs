using System.Globalization;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Lsp;

/// <summary>A whole C# list's sort texts as Passenger's extension sends them: in a list of more than 10,000 items each
/// place is widened so the sort texts keep the server's order, each still ending as the server wrote it; any other list is
/// sent as the server wrote it.</summary>
public class CompletionOrderTests
{
    [Fact]
    public void A_list_of_more_than_ten_thousand_sorts_in_the_servers_order_once_widened_each_ending_as_the_server_wrote_it()
    {
        string[] written = ServersSortTexts(12_345);
        Assert.NotEqual(Enumerable.Range(0, written.Length), SortedPlaces(written));

        string[] widened = CompletionOrder.Widened(written)!;
        Assert.Equal(Enumerable.Range(0, written.Length), SortedPlaces(widened));
        for (int i = 0; i < written.Length; i++)
        {
            Assert.True(widened[i].Length == 5 + Own(i).Length && widened[i].EndsWith(Own(i), StringComparison.Ordinal),
                $"Item {i}'s sort text widened to {widened[i]} from the server's {written[i]}: resolving it reads the server's own at its end");
        }
    }

    [Fact]
    public void A_list_of_ten_thousand_or_fewer_is_sent_as_the_server_wrote_it()
    {
        Assert.Null(CompletionOrder.Widened(ServersSortTexts(10_000)));
        Assert.Null(CompletionOrder.Widened(ServersSortTexts(3)));
        Assert.Null(CompletionOrder.Widened([]));
    }

    [Fact]
    public void A_list_whose_sort_texts_do_not_begin_with_their_places_is_sent_as_the_server_wrote_it()
    {
        string[] wide = [.. Enumerable.Range(0, 10_001).Select(i => i.ToString("D5", CultureInfo.InvariantCulture) + Own(i))];
        Assert.Null(CompletionOrder.Widened(wide));
        string?[] unsorted = ServersSortTexts(10_001);
        unsorted[500] = null;
        Assert.Null(CompletionOrder.Widened(unsorted));
    }

    /// <summary>The sort texts the server writes for a list of <paramref name="count"/>: each item's place in at least four
    /// digits, then its own.</summary>
    private static string[] ServersSortTexts(int count) =>
        [.. Enumerable.Range(0, count).Select(i => i.ToString("D4", CultureInfo.InvariantCulture) + Own(i))];

    /// <summary>An item's own sort text, of a length that varies with its place.</summary>
    private static string Own(int place) => place % 3 == 0 ? string.Empty : "Name" + new string('z', place % 7);

    /// <summary>The places of sort texts, ordered as their sort texts compare.</summary>
    private static IEnumerable<int> SortedPlaces(string[] sortTexts) =>
        Enumerable.Range(0, sortTexts.Length).OrderBy(i => sortTexts[i], StringComparer.Ordinal);
}

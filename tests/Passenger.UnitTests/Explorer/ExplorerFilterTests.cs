using Passenger.App.Explorer;

namespace Passenger.UnitTests.Explorer;

/// <summary>Which files the Explorer's filter shows: those whose names hold its text, ignoring case and the folders above
/// them, those beginning with it first, at most so many, with how many matched in all.</summary>
public sealed class ExplorerFilterTests
{
    private static readonly string[] Files =
    [
        Path.Combine("src", "Orders", "Customer.cs"),
        Path.Combine("src", "Orders", "OrderLine.cs"),
        Path.Combine("src", "Reorder.cs"),
        Path.Combine("src", "order.json"),
        Path.Combine("README.md"),
    ];

    [Fact]
    public void Names_beginning_with_the_text_come_before_names_holding_it_and_a_folder_s_name_matches_nothing()
    {
        ExplorerFilter.Matches found = ExplorerFilter.Of(Files, "ORDER");
        Assert.Equal([Files[1], Files[3], Files[2]], found.Files);
        Assert.Equal(3, found.Total);
        Assert.Equal(0, found.More);
    }

    [Fact]
    public void Past_the_most_shown_the_rest_are_counted_those_beginning_with_the_text_kept_first()
    {
        ExplorerFilter.Matches found = ExplorerFilter.Of(Files, "order", most: 2);
        Assert.Equal([Files[1], Files[3]], found.Files);
        Assert.Equal(3, found.Total);
        Assert.Equal(1, found.More);
    }

    [Fact]
    public void A_name_holding_the_text_fills_what_names_beginning_with_it_leave()
    {
        ExplorerFilter.Matches found = ExplorerFilter.Of(Files, "r", most: 3);
        Assert.Equal([Files[2], Files[4], Files[0]], found.Files);
        Assert.Equal(5, found.Total);
    }
}

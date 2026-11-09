using Passenger.App.History;

namespace Passenger.UnitTests.History;

/// <summary>Files Local History does not know, come from another program, are read while few come together, never once more
/// than a hundred have come within two seconds of one another; and a file such a bulk arrival brought is not taken for
/// made when it is read later.</summary>
public sealed class BulkArrivalsTests
{
    private static readonly DateTime Start = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_hundred_files_at_once_are_read_and_one_more_is_a_bulk_arrival()
    {
        Assert.False(new BulkArrivals().Came(BulkArrivals.AtOnce, Start), "A hundred files come at once were taken for a bulk arrival, so none is read");
        Assert.True(new BulkArrivals().Came(BulkArrivals.AtOnce + 1, Start), "More than a hundred files come at once were each read");
    }

    [Fact]
    public void Files_coming_within_a_quiet_moment_of_others_count_with_them_and_a_bulk_arrival_lasts_while_they_come()
    {
        BulkArrivals arrivals = new();
        Assert.False(arrivals.Came(60, Start), "Sixty files come together were taken for a bulk arrival");
        Assert.Equal(40, arrivals.Room(Start + TimeSpan.FromSeconds(1)));
        Assert.True(arrivals.Came(50, Start + TimeSpan.FromSeconds(1)), "Fifty more a second after sixty were each read, though a hundred and ten came together");
        Assert.Equal(0, arrivals.Room(Start + TimeSpan.FromSeconds(2.5)));
        Assert.True(arrivals.Came(1, Start + TimeSpan.FromSeconds(2.5)), "A file come a moment into a bulk arrival was read");
        Assert.False(arrivals.Came(1, Start + TimeSpan.FromSeconds(2.5) + BulkArrivals.Quiet), "A file come once a bulk arrival had been quiet was passed over");
    }

    [Fact]
    public void Files_coming_after_a_quiet_moment_count_on_their_own()
    {
        BulkArrivals arrivals = new();
        Assert.False(arrivals.Came(60, Start), "Sixty files come together were taken for a bulk arrival");
        Assert.Equal(BulkArrivals.AtOnce, arrivals.Room(Start + BulkArrivals.Quiet));
        Assert.False(arrivals.Came(60, Start + BulkArrivals.Quiet), "Sixty files come after a quiet moment were counted with the sixty before");
    }

    [Fact]
    public void A_file_made_during_a_bulk_arrival_is_its_and_one_made_before_or_after_is_not()
    {
        BulkArrivals arrivals = new();
        Assert.True(arrivals.Came(500, Start), "Five hundred files come at once were each read");
        Assert.True(arrivals.Came(500, Start + TimeSpan.FromSeconds(1)), "Files still coming a second into a bulk arrival were read");
        Assert.True(arrivals.Brought(Start - TimeSpan.FromSeconds(1)), "A file the arrival brought, made a moment before it was first heard, is taken for made when it is read later");
        Assert.True(arrivals.Brought(Start + TimeSpan.FromSeconds(1)), "A file the arrival brought, made as it was last heard, is taken for made when it is read later");
        Assert.False(arrivals.Brought(Start - BulkArrivals.Lead - TimeSpan.FromSeconds(1)), "A file made well before the arrival is taken for one it brought, so never for made");
        Assert.False(arrivals.Brought(Start + TimeSpan.FromSeconds(5)), "A file made after the arrival is taken for one it brought, so never for made");
    }
}

using Passenger.App.Web;

namespace Passenger.UnitTests.Web;

/// <summary>A package.json's engines range read as npm reads it: an operator and its version as one comparator however they
/// are spaced, hyphen ranges with both ends in, and a version given in part standing for every version it names.</summary>
public class NpmRangeTests
{
    /// <summary>Node.js 22.1.0 meets a range that takes it and is turned away by one that leaves it out.</summary>
    [Fact]
    public void Nodejs_meets_a_range_as_npm_reads_it() => Cases.All(
        (nameof(Nodejs_22_1_0_meets_a_range_that_takes_it), Nodejs_22_1_0_meets_a_range_that_takes_it),
        (nameof(Nodejs_22_1_0_is_turned_away_by_a_range_that_leaves_it_out), Nodejs_22_1_0_is_turned_away_by_a_range_that_leaves_it_out));

    private void Nodejs_22_1_0_meets_a_range_that_takes_it() => Cases.Each<string>(
    [
        ">= 18",
        ">=18 <23",
        "18 - 22",
        "^20.19.0 || ^22.0.0 || >=24",
        "22.x",
        "~22.1.0",
        "",
    ], Nodejs_22_1_0_meets_a_range_that_takes_it);

    private void Nodejs_22_1_0_meets_a_range_that_takes_it(string range) =>
        Assert.True(NpmRange.Meets(new Version(22, 1, 0), range),
            $"Node.js 22.1.0 was turned away by engines \"{range}\", which npm takes it under, so the web language server needing it would not start");

    private void Nodejs_22_1_0_is_turned_away_by_a_range_that_leaves_it_out() => Cases.Each<string>(
    [
        ">= 23",
        "18 - 21",
        "^20.19.0 || ^22.13.0 || >=24",
        "~22.0.5",
        "<22.1",
        "^0.22.1",
    ], Nodejs_22_1_0_is_turned_away_by_a_range_that_leaves_it_out);

    private void Nodejs_22_1_0_is_turned_away_by_a_range_that_leaves_it_out(string range) =>
        Assert.False(NpmRange.Meets(new Version(22, 1, 0), range),
            $"Node.js 22.1.0 was taken for engines \"{range}\", which npm turns it away under, so a server needing a later one would start on it and fail");
}

using Passenger.App.Web;

namespace Passenger.UnitTests.Web;

/// <summary>A package.json's engines range read as npm reads it: an operator and its version as one comparator however they
/// are spaced, hyphen ranges with both ends in, and a version given in part standing for every version it names.</summary>
public class NpmRangeTests
{
    [Theory]
    [InlineData(">= 18")]
    [InlineData(">=18 <23")]
    [InlineData(">= 18 < 23")]
    [InlineData("18 - 22")]
    [InlineData("18.0.0 - 22.1.0")]
    [InlineData("^20.19.0 || ^22.0.0 || >=24")]
    [InlineData("^20.19.0 || ^ 22.1.0")]
    [InlineData("22")]
    [InlineData("22.x")]
    [InlineData("22.1")]
    [InlineData("~22.1.0")]
    [InlineData("<=22")]
    [InlineData(">21")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(">=18.0.0-0")]
    public void Nodejs_22_1_0_meets_a_range_that_takes_it(string range) =>
        Assert.True(NpmRange.Meets(new Version(22, 1, 0), range),
            $"Node.js 22.1.0 was turned away by engines \"{range}\", which npm takes it under, so the web language server needing it would not start");

    [Theory]
    [InlineData(">= 23")]
    [InlineData(">=18 <22")]
    [InlineData("18 - 21")]
    [InlineData("18 - 22.0")]
    [InlineData("^20.19.0 || ^22.13.0 || >=24")]
    [InlineData("21")]
    [InlineData("22.0")]
    [InlineData("~22.0.5")]
    [InlineData(">22")]
    [InlineData("<22.1")]
    [InlineData("<=22.0")]
    [InlineData("^0.22.1")]
    public void Nodejs_22_1_0_is_turned_away_by_a_range_that_leaves_it_out(string range) =>
        Assert.False(NpmRange.Meets(new Version(22, 1, 0), range),
            $"Node.js 22.1.0 was taken for engines \"{range}\", which npm turns it away under, so a server needing a later one would start on it and fail");
}

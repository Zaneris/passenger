using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>NuGet's version ranges as a project or an advisory writes them, read once for showing, comparing and auditing:
/// a bare version is that or later, a pin one version alone, an interval its ends, and a floating version its least.</summary>
public class VersionRangeTests
{
    [Fact]
    public void A_version_range_is_read_once_for_showing_comparing_and_auditing() => Cases.All(
        ("a bare version is that version or later", () =>
        {
            VersionRange range = VersionRange.Parse(" 8.0.0 ")!;
            Assert.True(range is { Bare: true, Floating: false, IsExact: false, Min: "8.0.0", MinIncluded: true, Max: null }, $"8.0.0 reads as {range}");
            Assert.True(range.Contains("8.0.0") && range.Contains("9.0.0") && !range.Contains("7.9.9"));
        }),
        ("a pin is one version alone whatever its spacing", () =>
        {
            Assert.True(VersionRange.Parse("[ 8.0.0 , 8.0 ]") is { IsExact: true, Min: "8.0.0" }, "A pin written as a closed interval of one version is not read as one");
            Assert.True(VersionRange.Parse("[8.0.0]")!.Contains("8.0") && !VersionRange.Parse("[8.0.0]")!.Contains("8.0.1"));
        }),
        ("an interval holds its bracketed ends and leaves out its parenthesised ones an empty end unbounded", () =>
        {
            VersionRange range = VersionRange.Parse("[1.0.0, 2.0.0)")!;
            Assert.True(range is { Bare: false, IsExact: false, Min: "1.0.0", Max: "2.0.0" }, $"[1.0.0, 2.0.0) reads as {range}");
            Assert.True(range.Contains("1.0.0") && range.Contains("1.9.9") && !range.Contains("2.0.0") && !range.Contains("0.9.0"));
            VersionRange below = VersionRange.Parse("(, 1.4.1)")!;
            Assert.True(below is { Min: null, Max: "1.4.1" } && below.Contains("0.0.1") && !below.Contains("1.4.1"));
        }),
        ("a floating version is read from where its star stands", () =>
        {
            Assert.True(VersionRange.Parse("8.*") is { Floating: true, Bare: false, Min: "8", IsExact: false });
            Assert.True(VersionRange.Parse("1.0.0-*") is { Floating: true, Min: "1.0.0-0" });
            Assert.True(VersionRange.Parse("1.0.0-beta.*")!.Contains("1.0.0-beta.3"));
        }),
        ("text that is no range reads as none", () =>
        {
            Assert.Null(VersionRange.Parse(""));
            Assert.Null(VersionRange.Parse("(1.0.0)"));
            Assert.Null(VersionRange.Parse("[1.0, 2.0, 3.0]"));
        }),
        ("an advisory affects a pinned version as it does that version but no other range or floating version", () =>
        {
            Advisory advisory = new(AdvisorySeverity.Moderate, "https://github.com/advisories/GHSA-test", "[1.0.0, 1.0.1)");
            Assert.True(advisory.Affects("1.0.0") && advisory.Affects("[1.0.0]"), "A pinned 1.0.0 is not audited as 1.0.0 is");
            Assert.False(advisory.Affects("1.0.1") || advisory.Affects("[1.0.1]"));
            Assert.False(advisory.Affects("[1.0.0, 2.0.0)") || advisory.Affects("1.*"), "A range or floating version, its version in force unknown, was taken for one");
        }));

}

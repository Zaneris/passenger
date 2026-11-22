using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>What fixes a vulnerable version a project gets only through other packages: a reference of its own at the least
/// version that no advisory affects, in the frameworks that get one, the project's whole only where it references the
/// package in none and every framework gets one.</summary>
public class TransitiveFixTests
{
    private const string Id = "Passenger.Vulnerable";

    private static readonly VulnerabilityData Known = new(new Dictionary<string, IReadOnlyList<Advisory>>
    {
        [Id] = [new Advisory(AdvisorySeverity.Moderate, "https://github.com/advisories/GHSA-one", "[1.0.0, 1.0.1)"),
            new Advisory(AdvisorySeverity.High, "https://github.com/advisories/GHSA-two", "[1.1.0, 1.1.2)")],
    }, []);

    private static readonly string[] Offered = ["1.2.0", "1.1.2", "1.1.1", "1.1.0", "1.0.1", "1.0.0"];

    [Fact]
    public void A_vulnerable_version_a_project_gets_through_others_is_fixed_by_a_reference_at_the_least_safe_version_where_it_is_got() => Cases.All(
        ("a framework getting a vulnerable version is fixed alone at the least version above it no advisory affects", () =>
        {
            TransitiveUse[] brought = [new("net10.0", "1.0.0", ["Passenger.Charts"])];
            TransitiveFix? fix = TransitiveFix.Of(Id, brought, [new InstalledVersion("net11.0", "1.2.0")], ["net11.0", "net10.0"], Known, Offered);
            Assert.True(fix is { Version: "1.0.1", Frameworks: ["net10.0"] } && fix.Where == " for net10.0",
                $"net10.0's 1.0.0 brought in, beside net11.0's own 1.2.0, is fixed as {fix?.Version}{fix?.Where}, not by 1.0.1 for net10.0 alone");
        }),
        ("two frameworks' vulnerable versions are fixed together at the newer of their fixes", () =>
        {
            TransitiveUse[] brought = [new("net9.0", "1.0.0", ["Passenger.Charts"]), new("net10.0", "1.1.0", ["Passenger.Charts"])];
            TransitiveFix? fix = TransitiveFix.Of(Id, brought, [new InstalledVersion("net11.0", "1.2.0")], ["net11.0", "net10.0", "net9.0"], Known, Offered);
            Assert.True(fix is { Version: "1.1.2", Frameworks: ["net9.0", "net10.0"] },
                $"1.0.0 for net9.0 and 1.1.0 for net10.0 are fixed as {fix?.Version}{fix?.Where}, not by 1.1.2, which fixes both, in those two");
        }),
        ("a project referencing it in none whose every framework is vulnerable takes one reference for its whole", () =>
        {
            TransitiveUse[] brought = [new("net10.0", "1.0.0", ["Passenger.Charts"]), new("net11.0", "1.0.0", ["Passenger.Charts"])];
            Assert.Equal([], TransitiveFix.Of(Id, brought, [], ["net11.0", "net10.0"], Known, Offered)?.Frameworks);
            Assert.Equal(["net10.0"], TransitiveFix.Of(Id, brought[..1], [], ["net11.0", "net10.0"], Known, Offered)?.Frameworks);
            Assert.Equal([], TransitiveFix.Of(Id, [new(null, "1.0.0", ["Passenger.Charts"])], [], ["net10.0"], Known, Offered)?.Frameworks);
        }),
        ("nothing fixes a safe version or one no offered version is without an advisory", () =>
        {
            Assert.Null(TransitiveFix.Of(Id, [new("net10.0", "1.0.1", ["Passenger.Charts"])], [], ["net10.0"], Known, Offered));
            Assert.Null(TransitiveFix.Of(Id, [new("net10.0", "1.0.0", ["Passenger.Charts"])], [], ["net10.0"], Known, ["1.1.0", "1.0.0"]));
        }),
        ("only the frameworks no reference stands for are left to what brings it in", () =>
        {
            TransitiveUse[] uses = [new("net10.0", "1.0.0", ["Passenger.Charts"]), new("net11.0", "1.0.0", ["Passenger.Charts"])];
            Assert.Equal(["net10.0"], TransitiveFix.Left(uses, [new InstalledVersion("NET11.0", "1.0.1")]).Select(u => u.Framework));
            Assert.Empty(TransitiveFix.Left(uses, [new InstalledVersion(null, "1.0.1")]));
            Assert.Empty(TransitiveFix.Left([new(null, "1.0.0", ["Passenger.Charts"])], [new InstalledVersion("net10.0", "1.0.1")]));
            Assert.Equal(2, TransitiveFix.Left(uses, []).Count);
        }));

}

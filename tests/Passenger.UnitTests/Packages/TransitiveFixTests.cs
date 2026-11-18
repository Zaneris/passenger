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
    public void A_framework_getting_a_vulnerable_version_is_fixed_alone_at_the_least_version_above_it_no_advisory_affects()
    {
        TransitiveUse[] brought = [new("net10.0", "1.0.0", ["Passenger.Charts"])];
        TransitiveFix? fix = TransitiveFix.Of(Id, brought, [new InstalledVersion("net11.0", "1.2.0")], ["net11.0", "net10.0"], Known, Offered);
        Assert.True(fix is { Version: "1.0.1", Frameworks: ["net10.0"] } && fix.Where == " for net10.0",
            $"net10.0's 1.0.0 brought in, beside net11.0's own 1.2.0, is fixed as {fix?.Version}{fix?.Where}, not by 1.0.1 for net10.0 alone");
    }

    [Fact]
    public void Two_frameworks_vulnerable_versions_are_fixed_together_at_the_newer_of_their_fixes()
    {
        TransitiveUse[] brought = [new("net9.0", "1.0.0", ["Passenger.Charts"]), new("net10.0", "1.1.0", ["Passenger.Charts"])];
        TransitiveFix? fix = TransitiveFix.Of(Id, brought, [new InstalledVersion("net11.0", "1.2.0")], ["net11.0", "net10.0", "net9.0"], Known, Offered);
        Assert.True(fix is { Version: "1.1.2", Frameworks: ["net9.0", "net10.0"] },
            $"1.0.0 for net9.0 and 1.1.0 for net10.0 are fixed as {fix?.Version}{fix?.Where}, not by 1.1.2, which fixes both, in those two");
    }

    [Fact]
    public void A_project_referencing_it_in_none_whose_every_framework_is_vulnerable_takes_one_reference_for_its_whole()
    {
        TransitiveUse[] brought = [new("net10.0", "1.0.0", ["Passenger.Charts"]), new("net11.0", "1.0.0", ["Passenger.Charts"])];
        Assert.Equal([], TransitiveFix.Of(Id, brought, [], ["net11.0", "net10.0"], Known, Offered)?.Frameworks);
        Assert.Equal(["net10.0"], TransitiveFix.Of(Id, brought[..1], [], ["net11.0", "net10.0"], Known, Offered)?.Frameworks);
        Assert.Equal([], TransitiveFix.Of(Id, [new(null, "1.0.0", ["Passenger.Charts"])], [], ["net10.0"], Known, Offered)?.Frameworks);
    }

    [Fact]
    public void Nothing_fixes_a_safe_version_or_one_no_offered_version_is_without_an_advisory()
    {
        Assert.Null(TransitiveFix.Of(Id, [new("net10.0", "1.0.1", ["Passenger.Charts"])], [], ["net10.0"], Known, Offered));
        Assert.Null(TransitiveFix.Of(Id, [new("net10.0", "1.0.0", ["Passenger.Charts"])], [], ["net10.0"], Known, ["1.1.0", "1.0.0"]));
    }

    [Fact]
    public void Only_the_frameworks_no_reference_stands_for_are_left_to_what_brings_it_in()
    {
        TransitiveUse[] uses = [new("net10.0", "1.0.0", ["Passenger.Charts"]), new("net11.0", "1.0.0", ["Passenger.Charts"])];
        Assert.Equal(["net10.0"], TransitiveFix.Left(uses, [new InstalledVersion("NET11.0", "1.0.1")]).Select(u => u.Framework));
        Assert.Empty(TransitiveFix.Left(uses, [new InstalledVersion(null, "1.0.1")]));
        Assert.Empty(TransitiveFix.Left([new(null, "1.0.0", ["Passenger.Charts"])], [new InstalledVersion("net10.0", "1.0.1")]));
        Assert.Equal(2, TransitiveFix.Left(uses, []).Count);
    }
}

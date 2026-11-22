using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>What the NuGet window offers on a project's line for the version chosen, against every framework's reference
/// of the package: Update where one would move up and none down, Downgrade where one would only move down, both where one
/// would move up and another down, nothing where each has it; a range read as the version its restore resolved, else its
/// least, and a pin kept a pin by a change.</summary>
public class InstalledVersionTests
{
    private static InstalledVersion[] Of(params string?[] versions) => [.. versions.Select((v, i) => new InstalledVersion($"net{10 + i}.0", v))];

    [Fact]
    public void The_NuGet_window_offers_an_update_a_downgrade_both_or_nothing_against_every_frameworks_reference() => Cases.All(
        ("a version above below or at a project's one reference is an update a downgrade or nothing", () =>
        {
            Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.0.1", Of("8.0.0")));
            Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("7.0.0", Of("8.0.0")));
            Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.0", Of("8.0.0")));
        }),
        ("a version one framework would move up to and none down is an update", () =>
        {
            Assert.Equal(PackageMove.Update, InstalledVersion.Move("9.0.0", Of("9.0.0", "8.0.0")));
            Assert.Empty(InstalledVersion.Lowered("9.0.0", Of("9.0.0", "8.0.0")));
        }),
        ("a version one framework would move up to and another down is both naming the one that goes down", () =>
        {
            InstalledVersion[] at = Of("2.0.0", "3.0.0");
            Assert.Equal(PackageMove.UpdateAndDowngrade, InstalledVersion.Move("2.1.0", at));
            Assert.Equal(["net11.0"], InstalledVersion.Lowered("2.1.0", at).Select(v => v.Framework));
        }),
        ("a version that moves one framework down and none up is a downgrade and one every framework has is nothing", () =>
        {
            Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("8.0.0", Of("9.0.0", "8.0.0")));
            Assert.Equal(PackageMove.None, InstalledVersion.Move("9.0.0", Of("9.0.0", "9.0.0")));
        }),
        ("a reference whose version central management does not give is never compared", () =>
        {
            Assert.Equal(PackageMove.None, InstalledVersion.Move("1.0.0", Of((string?)null)));
            Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("1.0.0", Of(null, "2.0.0")));
        }),
        ("a range is shown compared and audited as its restore resolved it else as its least", () =>
        {
            Assert.Equal("1.0.0", new InstalledVersion(null, "[1.0.0, 2.0.0)").Version);
            Assert.Equal("1.2.0", new InstalledVersion(null, "[1.0.0, 2.0.0)", "1.2.0").Version);
            Assert.Equal("8.0.11", new InstalledVersion(null, "8.*", "8.0.11").Version);
            Assert.True(new InstalledVersion(null, "8.*") is { Version: null, Shown: "8.*" }, "A floating version not restored was given a version in force");
            Assert.True(new InstalledVersion(null, "[1.0.0, 2.0.0)").IsRanged && new InstalledVersion(null, "8.*").IsRanged,
                "A range or floating version is not taken for one whose version in force its restore says");
            Assert.False(new InstalledVersion(null, "[8.0.0]").IsRanged || new InstalledVersion(null, "8.0.0").IsRanged || new InstalledVersion(null, null).IsRanged,
                "A pin, a bare version or none is taken for one whose version in force only its restore says");
        }),
        ("a pinned version is offered no change to itself and a downgrade below it", () =>
        {
            Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.0", [new InstalledVersion(null, "[8.0.0]")]));
            Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("7.0.0", [new InstalledVersion(null, "[8.0.0]")]));
        }),
        ("a range is compared at the version restored and a floating one not restored at its least", () =>
        {
            Assert.Equal(PackageMove.None, InstalledVersion.Move("1.0.0", [new InstalledVersion(null, "[1.0.0, 2.0.0)")]));
            Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("1.0.0", [new InstalledVersion(null, "[1.0.0, 2.0.0)", "1.5.0")]));
            Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.0.5", [new InstalledVersion(null, "8.*")]));
            Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.5", [new InstalledVersion(null, "8.*", "8.0.5")]));
        }),
        ("a change of a project that pins every framework writes a pin and of any other the version as given", () =>
        {
            Assert.Equal("[9.0.0]", InstalledVersion.Written("9.0.0", [new InstalledVersion(null, "[8.0.0]")]));
            Assert.Equal("[9.0.0]", InstalledVersion.Written("9.0.0", Of("[8.0.0]", "[8.0.1]")));
            Assert.Equal("9.0.0", InstalledVersion.Written("9.0.0", Of("[8.0.0]", "8.0.1")));
            Assert.Equal("[9.0.0, )", InstalledVersion.Written("[9.0.0, )", [new InstalledVersion(null, "[8.0.0]")]));
        }));

}

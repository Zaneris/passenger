using Passenger.Packages;

namespace Passenger.UnitTests.Packages;

/// <summary>What the NuGet window offers on a project's line for the version chosen, against every framework's reference
/// of the package: Update where any would move up, Downgrade where one would only move down, nothing where each has it.</summary>
public class InstalledVersionTests
{
    private static InstalledVersion[] Of(params string?[] versions) => [.. versions.Select((v, i) => new InstalledVersion($"net{10 + i}.0", v))];

    [Fact]
    public void A_version_above_below_or_at_a_projects_one_reference_is_an_update_a_downgrade_or_nothing()
    {
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.0.1", Of("8.0.0")));
        Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("7.0.0", Of("8.0.0")));
        Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.0", Of("8.0.0")));
    }

    [Fact]
    public void A_version_any_framework_would_move_up_to_is_an_update_though_another_would_move_down()
    {
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("9.0.0", Of("9.0.0", "8.0.0")));
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.5.0", Of("9.0.0", "8.0.0")));
    }

    [Fact]
    public void A_version_that_moves_one_framework_down_and_none_up_is_a_downgrade_and_one_every_framework_has_is_nothing()
    {
        Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("8.0.0", Of("9.0.0", "8.0.0")));
        Assert.Equal(PackageMove.None, InstalledVersion.Move("9.0.0", Of("9.0.0", "9.0.0")));
    }

    [Fact]
    public void A_reference_whose_version_central_management_does_not_give_is_never_compared()
    {
        Assert.Equal(PackageMove.None, InstalledVersion.Move("1.0.0", Of((string?)null)));
        Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("1.0.0", Of(null, "2.0.0")));
    }
}

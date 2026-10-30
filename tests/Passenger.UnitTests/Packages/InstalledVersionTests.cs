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
    public void A_version_above_below_or_at_a_projects_one_reference_is_an_update_a_downgrade_or_nothing()
    {
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.0.1", Of("8.0.0")));
        Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("7.0.0", Of("8.0.0")));
        Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.0", Of("8.0.0")));
    }

    [Fact]
    public void A_version_one_framework_would_move_up_to_and_none_down_is_an_update()
    {
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("9.0.0", Of("9.0.0", "8.0.0")));
        Assert.Empty(InstalledVersion.Lowered("9.0.0", Of("9.0.0", "8.0.0")));
    }

    /// <summary>A security update of one framework's vulnerable version that would take another framework's newer one down
    /// is never a plain Update: the change moves every framework's reference together.</summary>
    [Fact]
    public void A_version_one_framework_would_move_up_to_and_another_down_is_both_naming_the_one_that_goes_down()
    {
        InstalledVersion[] at = Of("2.0.0", "3.0.0");
        Assert.Equal(PackageMove.UpdateAndDowngrade, InstalledVersion.Move("2.1.0", at));
        Assert.Equal(["net11.0"], InstalledVersion.Lowered("2.1.0", at).Select(v => v.Framework));
        Assert.Equal(PackageMove.UpdateAndDowngrade, InstalledVersion.Move("8.5.0", Of("9.0.0", "8.0.0")));
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

    [Fact]
    public void A_range_is_shown_compared_and_audited_as_its_restore_resolved_it_else_as_its_least()
    {
        Assert.Equal("8.0.0", new InstalledVersion(null, "[8.0.0]").Version);
        Assert.Equal("1.0.0", new InstalledVersion(null, "[1.0.0, 2.0.0)").Version);
        Assert.Equal("1.2.0", new InstalledVersion(null, "[1.0.0, 2.0.0)", "1.2.0").Version);
        Assert.Equal("8.0.11", new InstalledVersion(null, "8.*", "8.0.11").Version);
        Assert.True(new InstalledVersion(null, "8.*") is { Version: null, Shown: "8.*" }, "A floating version not restored was given a version in force");
        Assert.True(new InstalledVersion(null, null) is { Version: null, Shown: null });
        Assert.True(new InstalledVersion(null, "[1.0.0, 2.0.0)").IsRanged && new InstalledVersion(null, "8.*").IsRanged,
            "A range or floating version is not taken for one whose version in force its restore says");
        Assert.False(new InstalledVersion(null, "[8.0.0]").IsRanged || new InstalledVersion(null, "8.0.0").IsRanged || new InstalledVersion(null, null).IsRanged,
            "A pin, a bare version or none is taken for one whose version in force only its restore says");
    }

    [Fact]
    public void A_pinned_version_is_offered_no_change_to_itself_and_a_downgrade_below_it()
    {
        Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.0", [new InstalledVersion(null, "[8.0.0]")]));
        Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("7.0.0", [new InstalledVersion(null, "[8.0.0]")]));
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.0.1", [new InstalledVersion(null, "[8.0.0]")]));
    }

    [Fact]
    public void A_range_is_compared_at_the_version_restored_and_a_floating_one_not_restored_at_its_least()
    {
        Assert.Equal(PackageMove.None, InstalledVersion.Move("1.0.0", [new InstalledVersion(null, "[1.0.0, 2.0.0)")]));
        Assert.Equal(PackageMove.Downgrade, InstalledVersion.Move("1.0.0", [new InstalledVersion(null, "[1.0.0, 2.0.0)", "1.5.0")]));
        Assert.Equal(PackageMove.Update, InstalledVersion.Move("8.0.5", [new InstalledVersion(null, "8.*")]));
        Assert.Equal(PackageMove.None, InstalledVersion.Move("8.0.5", [new InstalledVersion(null, "8.*", "8.0.5")]));
    }

    [Fact]
    public void A_change_of_a_project_that_pins_every_framework_writes_a_pin_and_of_any_other_the_version_as_given()
    {
        Assert.Equal("[9.0.0]", InstalledVersion.Written("9.0.0", [new InstalledVersion(null, "[8.0.0]")]));
        Assert.Equal("[9.0.0]", InstalledVersion.Written("9.0.0", Of("[8.0.0]", "[8.0.1]")));
        Assert.Equal("9.0.0", InstalledVersion.Written("9.0.0", Of("[8.0.0]", "8.0.1")));
        Assert.Equal("9.0.0", InstalledVersion.Written("9.0.0", [new InstalledVersion(null, "[1.0.0, 10.0.0)")]));
        Assert.Equal("9.0.0", InstalledVersion.Written("9.0.0", []));
        Assert.Equal("[9.0.0, )", InstalledVersion.Written("[9.0.0, )", [new InstalledVersion(null, "[8.0.0]")]));
    }
}

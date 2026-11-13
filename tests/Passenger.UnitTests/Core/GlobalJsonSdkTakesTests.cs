using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Which installed SDKs a global.json takes, as the .NET host's resolver matches them: the version asked, else one
/// at or above it as far as rollForward goes, prereleases by allowPrerelease, and any SDK where it asks none.</summary>
public sealed class GlobalJsonSdkTakesTests
{
    [Theory]
    [InlineData(null, "11.0.100", true)]
    [InlineData(null, "11.0.150", true)]
    [InlineData(null, "11.0.200", false)]
    [InlineData("disable", "11.0.100", true)]
    [InlineData("disable", "11.0.101", false)]
    [InlineData("patch", "11.0.105", true)]
    [InlineData("patch", "11.0.200", false)]
    [InlineData("patch", "11.0.99", false)]
    [InlineData("latestPatch", "11.0.199", true)]
    [InlineData("feature", "11.0.300", true)]
    [InlineData("latestFeature", "11.1.100", false)]
    [InlineData("minor", "11.2.100", true)]
    [InlineData("latestMinor", "12.0.100", false)]
    [InlineData("major", "12.0.100", true)]
    [InlineData("latestMajor", "10.0.400", false)]
    [InlineData("LatestFeature", "11.0.400", true)]
    [InlineData("sideways", "11.0.100", false)]
    public void A_version_asked_takes_itself_and_what_its_roll_forward_reaches_at_or_above_it(string? rollForward, string sdk, bool taken)
    {
        GlobalJsonSdk asked = new("/work/global.json", "11.0.100", rollForward, 2);
        Assert.True(asked.Takes(sdk) == taken, $"global.json asking 11.0.100 with rollForward {rollForward ?? "unsaid"} {(taken ? "did not take" : "took")} the SDK {sdk}");
    }

    [Fact]
    public void A_prerelease_is_taken_unless_allowPrerelease_is_false_and_the_version_asked_is_a_release()
    {
        GlobalJsonSdk release = new("/work/global.json", "11.0.100", "latestMajor", 2);
        Assert.True(release.Takes("12.0.100-rc.1.26401.1"), "A prerelease above the version asked was not taken where allowPrerelease is unsaid");
        Assert.False((release with { AllowPrerelease = false }).Takes("12.0.100-rc.1.26401.1"), "A prerelease was taken where allowPrerelease is false");
        Assert.False((release with { AllowPrerelease = false }).Takes("11.0.100-rc.2.26504.105"), "A prerelease below the version asked was taken");

        GlobalJsonSdk prerelease = new("/work/global.json", "11.0.100-rc.2.26504.105", "latestFeature", 2, AllowPrerelease: false);
        Assert.True(prerelease.Takes("11.0.100-rc.3.26522.148"), "A prerelease asked for did not take a later prerelease of its band, allowPrerelease false or not");
        Assert.True(prerelease.Takes("11.0.100"), "A prerelease asked for did not take its release");
    }

    [Fact]
    public void Asking_no_version_takes_any_SDK_but_a_prerelease_allowPrerelease_refuses_and_a_version_that_does_not_read_takes_none()
    {
        GlobalJsonSdk none = new("/work/global.json", null, null, 0);
        Assert.True(none.Takes("9.0.303") && none.Takes("11.0.100-rc.3.26522.148"), "global.json asking no version did not take every SDK");
        Assert.False((none with { AllowPrerelease = false }).Takes("11.0.100-rc.3.26522.148"), "global.json refusing prereleases took one");
        Assert.False((none with { RollForward = "disable" }).Takes("9.0.303"), "global.json disabling roll-forward with no version took an SDK the host takes none of");
        Assert.False((none with { Version = "eleven" }).Takes("11.0.100"), "global.json asking a version that does not read took an SDK");
        Assert.False(none.Takes("not-a-version"), "A folder named as no version was taken as an SDK");
    }
}

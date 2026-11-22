using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A property given on a dotnet or MSBuild command line: its value's separators, escape mark and quote written as
/// MSBuild's %XX escapes, everything else as given, in dotnet watch's switch or the rest's.</summary>
public sealed class PropertySwitchTests
{
    [Fact]
    public void A_property_switch_escapes_only_what_msbuilds_command_line_reads_otherwise() => Cases.Each<(string value, bool watched, string written)>(
    [
        ("Release", false, "-p:Configuration=Release"),
        ("Any CPU", true, "--property:Configuration=Any CPU"),
        ("/Users/Smith, John/Android;sdk", false, "-p:Configuration=/Users/Smith%2C John/Android%3Bsdk"),
        ("100% \"sure\" $(Not)@(Items)=x", false, "-p:Configuration=100%25 %22sure%22 $(Not)@(Items)=x"),
    ], row =>
    {
        (string value, bool watched, string written) = row;
        
                Assert.Equal(written, PropertySwitch.Of(MSBuildNames.Configuration, value, watched));
    });
}

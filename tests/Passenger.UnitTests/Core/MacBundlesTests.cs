using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A macOS app bundle found from a path alone: a folder whose name ends in .app in any letter case, a program its
/// Contents/MacOS folder's, and anything under its Contents inside its seal.</summary>
public class MacBundlesTests
{
    private static readonly string Applications = Path.Combine(Path.GetTempPath(), "Applications");

    [Fact]
    public void A_program_in_a_bundles_MacOS_folder_is_that_bundles_and_any_other_file_is_none()
    {
        string bundle = Path.Combine(Applications, "Godot_mono.APP");
        Assert.True(MacBundles.Of(Path.Combine(bundle, "Contents", "MacOS", "Godot")) == bundle,
            $"A program inside {bundle} was found in no bundle, so its Resources beside it would be missed");
        Assert.Null(MacBundles.Of(Path.Combine(bundle, "Contents", "Resources", "Godot")));
        Assert.Null(MacBundles.Of(Path.Combine(Applications, "Tools", "Contents", "MacOS", "Godot")));
        Assert.Null(MacBundles.Of(Path.Combine(bundle, "Contents", "MacOS", "Helpers", "godot-helper")));
        Assert.Null(MacBundles.Of(null));
    }

    [Fact]
    public void A_path_under_a_bundles_Contents_is_held_by_it_and_one_beside_it_is_not()
    {
        string bundle = Path.Combine(Applications, "Passenger.app");
        Assert.True(MacBundles.Holds(Path.Combine(bundle, "Contents", "MonoBundle", "roslyn")),
            "A folder inside the app was taken to be outside it, so a server writing its caches there would break the app's seal");
        Assert.True(MacBundles.Holds(Path.Combine(bundle, "Contents")));
        Assert.False(MacBundles.Holds(bundle));
        Assert.False(MacBundles.Holds(Path.Combine(Applications, "Passenger", "Contents", "roslyn")));
    }

    [Fact]
    public void A_bundles_programs_are_in_its_Contents_MacOS_folder()
    {
        string bundle = Path.Combine(Applications, "Passenger.app");
        Assert.Equal(bundle, MacBundles.Of(Path.Combine(MacBundles.Programs(bundle), "Passenger")));
        Assert.True(MacBundles.Named(bundle + Path.DirectorySeparatorChar) && !MacBundles.Named(Path.Combine(Applications, "Passenger.apps")));
    }
}

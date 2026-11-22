using Passenger.App;
using Passenger.Core;

namespace Passenger.UnitTests.Run;

/// <summary>What Passenger carries that is no code of its own (the debug relays, the web servers) is found beside the app
/// as every system's package lays it out, and in a macOS bundle's Resources, where a Mac's package moves it so Gatekeeper
/// takes the notarized app.</summary>
public sealed class CarriedFilesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void The_debug_relays_are_found_where_the_app_carries_them() => Cases.AllApart<CarriedFilesTests>(
        ("a bundle carries its debug relays in its Resources", test => test.A_bundle_carries_its_debug_relays_in_its_Resources()),
        ("an app carrying its relays beside it takes them there", test => test.An_app_carrying_its_relays_beside_it_takes_them_there()),
        ("an app carrying no relays names where they would be beside it", test => test.An_app_carrying_no_relays_names_where_they_would_be_beside_it()));

    private void A_bundle_carries_its_debug_relays_in_its_Resources()
    {
        string code = Path.Join(root, "Passenger.app", "Contents", "MacOS");
        string relays = Path.Join(root, "Passenger.app", "Contents", "Resources", "debugger", "relay");
        Directory.CreateDirectory(Path.Join(code, "debugger"));
        Directory.CreateDirectory(Path.Join(relays, "iossimulator-arm64"));

        Assert.Equal(relays, CarriedFiles.FolderIn(code, "debugger", "relay"));
    }

    private void An_app_carrying_its_relays_beside_it_takes_them_there()
    {
        string app = Path.Join(root, "app");
        string beside = Path.Join(app, "debugger", "relay");
        Directory.CreateDirectory(Path.Join(beside, "android-arm64"));
        Directory.CreateDirectory(Path.Join(root, "Resources", "debugger", "relay"));

        Assert.Equal(beside, CarriedFiles.FolderIn(app, "debugger", "relay"));
    }

    private void An_app_carrying_no_relays_names_where_they_would_be_beside_it()
    {
        string app = Path.Join(root, "app");
        Directory.CreateDirectory(app);

        Assert.Equal(Path.Join(app, "debugger", "relay"), CarriedFiles.FolderIn(app, "debugger", "relay"));
    }
}

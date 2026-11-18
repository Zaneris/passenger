namespace Passenger.App;

/// <summary>Files Passenger carries that are no code of the Mac app's own: beside the app, or in a macOS bundle its
/// Resources, which Gatekeeper reads as sealed data. The web servers go there because codesign takes a folder under
/// Contents/MacOS named with a dot (a package's .github) for a nested bundle; the debug relays because Gatekeeper refuses
/// a notarized app carrying another platform's Mach-O (an iOS simulator's) among its code.</summary>
public static class CarriedFiles
{
    private static readonly string Relays = FolderIn(AppContext.BaseDirectory, "debugger", "relay");

    /// <summary>The folder carried as <paramref name="parts"/> below the app.</summary>
    public static string Folder(params string[] parts) => FolderIn(AppContext.BaseDirectory, parts);

    /// <summary>The debug relay Passenger carries for <paramref name="runtime"/> (an app's runtime identifier), whether
    /// this Passenger carries it or not.</summary>
    public static string Relay(string runtime, string file) => Path.Combine(Relays, runtime, file);

    /// <summary>The folder carried as <paramref name="parts"/> below an app in <paramref name="appFolder"/>: there, unless
    /// only the macOS bundle's Resources beside it holds it.</summary>
    public static string FolderIn(string appFolder, params string[] parts)
    {
        string beside = Path.Combine([appFolder, .. parts]);
        string resources = Path.GetFullPath(Path.Combine([appFolder, "..", "Resources", .. parts]));
        return !Directory.Exists(beside) && Directory.Exists(resources) ? resources : beside;
    }
}

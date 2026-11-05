using Avalonia;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The window's typeface in force and the ones it may be (<see cref="WindowFontNames"/>): the Inter Passenger
/// carries, its hinted faces (<see cref="Carried"/>), the system's own UI font, or any installed family. The application's
/// resources hold the one in force under <see cref="Key"/>, which Fluent's windows and popups take as their font
/// (FluentKeys.axaml) and every control drawing words itself reads as the font it inherits; the installed families are
/// listed once for the process, off the UI thread.</summary>
public static class WindowFonts
{
    /// <summary>The application's resource holding the window's typeface in force.</summary>
    [IdentifierHome]
    public const string Key = "Passenger.WindowFont";

    /// <summary>Inter as Passenger carries it, the platform's default family behind it for what Inter lacks.</summary>
    public static readonly FontFamily Carried = new("avares://Passenger/Fonts#Inter, $Default");

    // Unheld: one list for every window, each asker holding its own wait for it (InstalledAsync).
    private static readonly Lazy<Task<IReadOnlyList<string>>> Found = new(() => BackgroundWork.Unheld(() => WindowFontNames.Choosable(Families())));

    /// <summary>The installed families a person may choose, by name in order, found on the thread pool once for the process,
    /// the caller's wait held as background work.</summary>
    public static Task<IReadOnlyList<string>> InstalledAsync() => BackgroundWork.Run(() => Found.Value);

    /// <summary>What a setting's name comes to here.</summary>
    public static WindowFontChoice Choice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return WindowFontNames.Of(name, [], string.Empty);
        }
        return WindowFontNames.Of(name, Families(), SystemFamily());
    }

    /// <summary>The family a setting's name gives: the carried Inter for none, the system's own, or an installed family;
    /// null for a name no installed family has (<see cref="WhyNot"/>).</summary>
    public static FontFamily? Named(string? name) => Choice(name) switch
    {
        { Kind: WindowFontKind.Carried } => Carried,
        { Kind: WindowFontKind.System or WindowFontKind.Installed, Family: string family } => new FontFamily(family),
        _ => null,
    };

    /// <summary>Why a name gives no typeface, as words after it; null when it gives one.</summary>
    public static string? WhyNot(string name) => Named(name) is null ? "is not a font installed on this computer" : null;

    /// <summary>How Settings names the system's own choice here: "System font (Segoe UI)".</summary>
    public static string SystemShown() => WindowFontNames.SystemShown(SystemFamily());

    /// <summary>Puts a family in force as the window's typeface, for every window and popup, with its own tracking where macOS
    /// would set it (<see cref="WindowTracking"/>).</summary>
    public static void Use(Application app, FontFamily family)
    {
        // Its tracking first, which what draws words takes as their face changes to it.
        WindowTracking.Follow(app, family);
        app.Resources[Key] = family;
    }

    /// <summary>The system's own UI family here; a name the font manager does not know gives its default's face, so the
    /// macOS family draws only where its face is another.</summary>
    private static string SystemFamily()
    {
        string platform = FontManager.Current.DefaultFontFamily.Name;
        bool draws = OperatingSystem.IsMacOS() && FontManager.Current.TryGetGlyphTypeface(new Typeface(WindowFontNames.MacSystem), out GlyphTypeface? face)
            && !face.FamilyName.Equals(platform, StringComparison.OrdinalIgnoreCase);
        return WindowFontNames.SystemFamily(draws, platform);
    }

    private static IReadOnlyList<string> Families() => [.. FontManager.Current.SystemFonts.Select(family => family.Name)];
}

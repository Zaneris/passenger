namespace Passenger.App.Welcome;

/// <summary>The first start's pictures of Passenger in its own theme and each it carries, dark and light, carried in
/// Assets/FirstStart and taken by <c>just first-start-shots</c>: what each file is named, how wide a tile draws one, and the
/// file beside them naming the version of Passenger they show, which the unit tests hold to the build's.</summary>
public static class FirstStartPictures
{
    /// <summary>The widest a tile draws a picture, in the window's units.</summary>
    public const double Widest = 468;

    /// <summary>The largest screen scale a picture drawn at its widest stays sharp on: it holds this many pixels a unit.</summary>
    public const double Sharpest = 3;

    /// <summary>The file naming the version of Passenger the pictures show.</summary>
    public const string VersionFile = "pictures.json";

    /// <summary>The file of a theme's picture, by the theme's name, in dark or light.</summary>
    public static string FileOf(string theme, bool dark) => $"{theme.ToLowerInvariant()}-{(dark ? "dark" : "light")}.webp";
}

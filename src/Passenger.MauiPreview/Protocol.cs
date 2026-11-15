using Passenger.Core;

// What Passenger asks its MAUI previewer, one definition for both ends (the app compiles this file too); the rest of the
// wire, the answers and how a line is read, is every previewer's (PreviewWire).
namespace Passenger.MauiPreview;

/// <summary>A XAML file to draw (<see cref="PreviewRequest"/>), with the size of the screen it is drawn on in
/// device-independent units, the platform and idiom OnPlatform and OnIdiom read (as MAUI names them: Android, iOS,
/// MacCatalyst, WinUI; Phone, Tablet, Desktop), and the theme AppThemeBinding reads (Light, Dark).</summary>
public sealed record MauiPreviewRequest : PreviewRequest
{
    public double Width { get; init; } = 390;

    public double Height { get; init; } = 844;

    public string Platform { get; init; } = MauiPreviewDevices.Android;

    public string Idiom { get; init; } = MauiPreviewDevices.Phone;

    public string Theme { get; init; } = MauiPreviewThemes.Light;
}

/// <summary>The platforms and idioms a preview is drawn as, by the names MAUI's DevicePlatform and DeviceIdiom give them.</summary>
public static class MauiPreviewDevices
{
    public const string Android = "Android";
    public const string IOS = "iOS";
    public const string MacCatalyst = "MacCatalyst";
    public const string Windows = "WinUI";

    public const string Phone = "Phone";
    public const string Tablet = "Tablet";
    public const string Desktop = "Desktop";
}

public static class MauiPreviewThemes
{
    public const string Light = "Light";
    public const string Dark = "Dark";
}

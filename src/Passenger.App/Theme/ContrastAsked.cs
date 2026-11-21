namespace Passenger.App;

/// <summary>Whether the system asks for high contrast, and in dark or light where it says (<see cref="Dark"/> null where
/// the system's own dark or light stands): each system's answers read into it by <see cref="OfWindows"/> and
/// <see cref="OfGnome"/>, macOS's by <see cref="Of(bool)"/>, and a stand-in's words by <see cref="Parse"/>.</summary>
public sealed record ContrastAsked(bool High, bool? Dark)
{
    /// <summary>No high contrast asked.</summary>
    public static readonly ContrastAsked None = new(false, null);

    public override string ToString() => !High ? "no high contrast" : Dark switch
    {
        true => "high contrast, dark",
        false => "high contrast, light",
        null => "high contrast",
    };

    /// <summary>High contrast where the system asks for it, in the system's own dark or light.</summary>
    public static ContrastAsked Of(bool high) => high ? new(true, null) : None;

    /// <summary>Reads a stand-in's words: <c>none</c>, <c>high</c>, <c>high-dark</c> or <c>high-light</c>; throws
    /// <see cref="FormatException"/> on any other.</summary>
    public static ContrastAsked Parse(string value) => value.Trim() switch
    {
        "none" => None,
        "high" => new(true, null),
        "high-dark" => new(true, true),
        "high-light" => new(true, false),
        _ => throw new FormatException($"takes none, high, high-dark or high-light, not '{value}'"),
    };

    /// <summary>Windows' answer: whether a contrast theme is on (SPI_GETHIGHCONTRAST's HCF_HIGHCONTRASTON), dark or
    /// light by the window colour it gives (GetSysColor's COLOR_WINDOW, 0x00BBGGRR): dark below middle grey.</summary>
    public static ContrastAsked OfWindows(bool on, uint window)
    {
        if (!on)
        {
            return None;
        }
        uint argb = 0xFF000000 | ((window & 0xFF) << 16) | (window & 0xFF00) | ((window >> 16) & 0xFF);
        return new(true, ThemeColours.Luminance(argb) < 0.18);
    }

    /// <summary>GNOME's answer from <c>gsettings get org.gnome.desktop.a11y.interface high-contrast</c>'s words, in the
    /// desktop's own dark or light; none for words it does not read as true.</summary>
    public static ContrastAsked OfGnome(string said) => Of(said.Trim() == "true");
}

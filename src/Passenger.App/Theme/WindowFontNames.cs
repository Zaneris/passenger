namespace Passenger.App;

/// <summary>What a setting of the window's typeface may be, as it names it.</summary>
public enum WindowFontKind
{
    /// <summary>The Inter Passenger carries: no name, or its own.</summary>
    Carried,

    /// <summary>The system's own UI font, whichever it is here.</summary>
    System,

    /// <summary>A family installed on this computer.</summary>
    Installed,

    /// <summary>A name no installed family has.</summary>
    Missing,
}

/// <summary>The family a setting of the window's typeface comes to, and what kind of choice it is.</summary>
public readonly record struct WindowFontChoice(WindowFontKind Kind, string Family);

/// <summary>The window's typeface a setting names, read as words alone: the Inter Passenger carries where it names none or
/// Inter, the system's own UI font by <see cref="SystemUi"/> (CSS's word for it, which no family takes), else the installed
/// family of that name, spelt as installed. The system's own is macOS's hidden UI family where it draws, else the family
/// the platform gives as its default (Segoe UI on Windows, fontconfig's answer on Linux).</summary>
public static class WindowFontNames
{
    /// <summary>The family Passenger carries.</summary>
    public const string Carried = "Inter";

    /// <summary>The setting's word for the system's own UI font.</summary>
    public const string SystemUi = "system-ui";

    /// <summary>macOS's own UI family (San Francisco), hidden behind a dot from the families its font list gives.</summary>
    public const string MacSystem = ".AppleSystemUIFont";

    /// <summary>What a setting's name comes to, among the installed families.</summary>
    public static WindowFontChoice Of(string? name, IEnumerable<string> installed, string system)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new(WindowFontKind.Carried, Carried);
        }
        string trimmed = name.Trim();
        if (trimmed.Equals(Carried, StringComparison.OrdinalIgnoreCase))
        {
            return new(WindowFontKind.Carried, Carried);
        }
        if (trimmed.Equals(SystemUi, StringComparison.OrdinalIgnoreCase))
        {
            return new(WindowFontKind.System, system);
        }
        return installed.FirstOrDefault(family => family.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) is { } found
            ? new(WindowFontKind.Installed, found)
            : new(WindowFontKind.Missing, trimmed);
    }

    /// <summary>The system's own UI family: macOS's where it draws (a family the platform's font manager finds by its
    /// name), else the platform's default.</summary>
    public static string SystemFamily(bool macSystemDraws, string platformDefault) => macSystemDraws ? MacSystem : platformDefault;

    /// <summary>The installed families a person may choose, by name in order: none the system hides (a dot first), and
    /// not the carried one's name, which is always the carried font.</summary>
    public static IReadOnlyList<string> Choosable(IEnumerable<string> installed) =>
        [.. installed.Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('.') && !name.Equals(Carried, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>How Settings names the system's own choice: with its family, unless the system hides that family's name.</summary>
    public static string SystemShown(string family) => family.StartsWith('.') ? "System font" : $"System font ({family})";
}

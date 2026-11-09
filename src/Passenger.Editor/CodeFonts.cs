using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Media;
using Passenger.Core;

namespace Passenger.Editor;

/// <summary>The code font in force and the fonts it may be: the one Passenger carries (<see cref="CodeEditor.CodeFont"/>)
/// or an installed family whose characters all take one width, as code's columns need. The application's resources hold
/// the one in force under <see cref="Key"/>, which every editor, the terminal and each control showing code follow
/// (<see cref="InCodeFont{T}"/>); the installed ones are found once for the process, off the UI thread, as telling one
/// apart loads it.</summary>
public static class CodeFonts
{
    /// <summary>The application's resource holding the code font in force.</summary>
    [IdentifierHome]
    public const string Key = "Passenger.CodeFont";

    /// <summary>Characters a proportional font draws at different widths, which a monospaced one must have.</summary>
    private const string Widths = "iMW0. ";

    private static readonly ConcurrentDictionary<string, bool> Monospaced = new(StringComparer.OrdinalIgnoreCase);

    // Unheld: one list for every window, each asker holding its own wait for it (InstalledAsync).
    private static readonly Lazy<Task<IReadOnlyList<string>>> Found = new(() => BackgroundWork.Unheld(Find));

    /// <summary>The name of the font Passenger carries.</summary>
    public static string Carried => CodeEditor.CodeFont.Name;

    /// <summary>The installed families whose characters all take one width, by name in order, the carried font's left out;
    /// found on the thread pool once for the process, the caller's wait held as background work.</summary>
    public static Task<IReadOnlyList<string>> InstalledAsync() => BackgroundWork.Run(() => Found.Value);

    /// <summary>The family a name gives: the carried font, or an installed family whose characters take one width; null for
    /// none, or a name that is neither (<see cref="WhyNot"/>).</summary>
    public static FontFamily? Named(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string trimmed = name.Trim();
        if (trimmed.Equals(Carried, StringComparison.OrdinalIgnoreCase))
        {
            return CodeEditor.CodeFont;
        }
        return Installed(trimmed) is { } family && IsMonospaced(family) ? FallbackFonts.Of(family.Name) : null;
    }

    /// <summary>Why a name gives no code font, as words after it; null when it gives one.</summary>
    public static string? WhyNot(string name) => Named(name) is not null ? null
        : Installed(name.Trim()) is null ? "is not a font installed on this computer"
        : "is not monospaced: code's columns need every character one width";

    /// <summary>Whether a family of the name is installed, monospaced or not.</summary>
    public static bool IsInstalled(string name) => Installed(name.Trim()) is not null;

    /// <summary>Puts a family in force as the code font, for every control following it.</summary>
    public static void Use(Application app, FontFamily family) => app.Resources[Key] = family;

    /// <summary>Has a control or a run of text show the code font in force, following each change.</summary>
    public static T InCodeFont<T>(this T element)
        where T : StyledElement
    {
        element.Bind(TextElement.FontFamilyProperty, element.GetResourceObservable(Key, Font));
        return element;
    }

    /// <summary>Has a control's own font property follow the code font in force, below any value it is given.</summary>
    public static void Follow(StyledElement element, StyledProperty<FontFamily> property) =>
        element.Bind(property, element.GetResourceObservable(Key, Font), BindingPriority.Style);

    private static object? Font(object? found) => found as FontFamily ?? CodeEditor.CodeFont;

    private static FontFamily? Installed(string name) =>
        FontManager.Current.SystemFonts.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> Find()
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamily family in FontManager.Current.SystemFonts)
        {
            // The system's own hidden families begin with a dot.
            if (!family.Name.StartsWith('.') && !family.Name.Equals(Carried, StringComparison.OrdinalIgnoreCase) && IsMonospaced(family))
            {
                names.Add(family.Name);
            }
        }
        return [.. names];
    }

    /// <summary>Whether a family draws its letters and digits one width: by its own word, else by measuring a few of them;
    /// one without them all (symbols, another script's) is not.</summary>
    private static bool IsMonospaced(FontFamily family) => Monospaced.GetOrAdd(family.Name, _ => Measured(family));

    private static bool Measured(FontFamily family)
    {
        if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? face))
        {
            return false;
        }
        Span<ushort> glyphs = stackalloc ushort[Widths.Length];
        for (int i = 0; i < Widths.Length; i++)
        {
            if (!face.CharacterToGlyphMap.TryGetGlyph(Widths[i], out glyphs[i]) || glyphs[i] == 0)
            {
                return false;
            }
        }
        if (face.Metrics.IsFixedPitch)
        {
            return true;
        }
        Span<ushort> advances = stackalloc ushort[Widths.Length];
        if (!face.TryGetHorizontalGlyphAdvances(glyphs, advances))
        {
            return false;
        }
        foreach (ushort advance in advances)
        {
            if (advance != advances[0])
            {
                return false;
            }
        }
        return true;
    }
}

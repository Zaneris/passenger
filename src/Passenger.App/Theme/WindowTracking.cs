using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.VisualTree;
using Passenger.Core;

namespace Passenger.App;

/// <summary>The window's typeface's own tracking where macOS's text drawing would set it, so words in San Francisco are as
/// tight as in the system's own apps: a face with a 'trak' table (<see cref="FontTracking"/>), on macOS, has the letter
/// spacing its table gives each size on every TextBlock and TextPresenter drawing words in it, at its size, as its size or
/// face changes; any other face, or any other system, keeps Avalonia's own. A UI-thread object.</summary>
public static class WindowTracking
{
    private static FontFamily? tracked;
    private static byte[]? table;
    private static int unitsPerEm;
    private static bool hooked;

    /// <summary>The window's typeface about to be put in force: its tracking set on what draws it from then, as each piece of
    /// words takes the face, what is shown now among them.</summary>
    public static void Follow(Application app, FontFamily family)
    {
        (tracked, table, unitsPerEm) = OperatingSystem.IsMacOS() && TableOf(family) is (byte[] read, int em) ? (family, read, em) : (null, null, 0);
        if (!hooked && tracked is not null)
        {
            hooked = true;
            TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>((block, _) => Track(block));
            TextBlock.FontFamilyProperty.Changed.AddClassHandler<TextBlock>((block, _) => Track(block));
            Avalonia.Controls.Documents.TextElement.FontSizeProperty.Changed.AddClassHandler<TextPresenter>((presenter, _) => Track(presenter));
            Avalonia.Controls.Documents.TextElement.FontFamilyProperty.Changed.AddClassHandler<TextPresenter>((presenter, _) => Track(presenter));
        }
        if (hooked && app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Visual drawn in desktop.Windows.SelectMany(w => w.GetVisualDescendants()))
            {
                Track(drawn);
            }
        }
    }

    /// <summary>The letter spacing the window's typeface in force gives words of a size: its table's, else none.</summary>
    public static double At(double size) => table is not null ? FontTracking.Of(table, unitsPerEm, size) ?? 0 : 0;

    private static void Track(Visual drawn)
    {
        switch (drawn)
        {
            case TextBlock block:
                block.SetValue(TextBlock.LetterSpacingProperty, Equals(block.FontFamily, tracked) ? At(block.FontSize) : 0, BindingPriority.Style);
                break;
            case TextPresenter presenter:
                presenter.SetValue(TextPresenter.LetterSpacingProperty, Equals(presenter.FontFamily, tracked) ? At(presenter.FontSize) : 0, BindingPriority.Style);
                break;
        }
    }

    /// <summary>A family's face's 'trak' table and its units to the em, or null where it has none.</summary>
    private static (byte[] Table, int UnitsPerEm)? TableOf(FontFamily family) =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? face)
        && face.PlatformTypeface.TryGetTable(OpenTypeTag.Parse("trak"), out ReadOnlyMemory<byte> trak) && trak.Length > 0
            ? (trak.ToArray(), face.Metrics.DesignEmHeight)
            : null;
}

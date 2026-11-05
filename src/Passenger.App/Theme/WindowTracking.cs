using System.Reflection;
using System.Runtime.InteropServices;
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

/// <summary>The window's typeface's own tracking and optical size where macOS's text drawing would set them, so words in San
/// Francisco are as wide as in the system's own apps: a face with a 'trak' table (<see cref="FontTracking"/>), on macOS, has
/// the letter spacing its table gives each size on every TextBlock and TextPresenter drawing words in it, at its size, as
/// its size or face changes, and a variable face is shaped at the optical size macOS gives the window's words and at its
/// own weight, never at its default instance's; any other face, or any other system, keeps Avalonia's own. A UI-thread
/// object.</summary>
public static partial class WindowTracking
{
    /// <summary>The size of the window's words whose optical size its faces take: one face draws every size, and San
    /// Francisco's least optical size, 17, is what macOS gives every size up to it.</summary>
    private const double WordsSize = 13;

    /// <summary>The weights of the window's words, whose upright faces are shaped as macOS draws them.</summary>
    private static readonly FontWeight[] Weights = [FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold, FontWeight.Bold];

    private static readonly HashSet<GlyphTypeface> varied = [];

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
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                Vary(family);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or TargetInvocationException
                or MemberAccessException)
            {
                // Its words keep their tracking, shaped at the face's default instance.
                IdeErrors.Write($"The window's typeface {family.Name} could not be set at macOS's optical size, so its words are set narrower than the system's own: {error.Message}");
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

    /// <summary>Each upright face of a variable family the window's words take, shaped at the optical size macOS gives them
    /// and at its own weight, as the system's own apps draw it: its shaper otherwise takes the font's default instance (San
    /// Francisco's is its display size, 28, a tenth narrower than its text at 13 px). Only the family's own faces are asked
    /// for and set, never one another family stands in with, nor one simulated or already set.</summary>
    private static void Vary(FontFamily family)
    {
        if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out GlyphTypeface? own)
            || !own.PlatformTypeface.TryGetTable(OpenTypeTag.Parse("fvar"), out _))
        {
            return;
        }
        foreach (FontWeight weight in Weights)
        {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out GlyphTypeface? face)
                || face.FontSimulations != FontSimulations.None || face.FamilyName != own.FamilyName || varied.Contains(face)
                || !face.PlatformTypeface.TryGetTable(OpenTypeTag.Parse("fvar"), out ReadOnlyMemory<byte> fvar))
            {
                continue;
            }
            varied.Add(face);
            List<Variation> set = [];
            foreach (FontAxis axis in FontAxes.Of(fvar.Span))
            {
                if (axis.Tag == FontAxes.OpticalSize)
                {
                    set.Add(new Variation(axis.Tag, (float)axis.Held(WordsSize)));
                }
                else if (axis.Tag == FontAxes.Weight)
                {
                    set.Add(new Variation(axis.Tag, (float)axis.Held((int)weight)));
                }
            }
            if (set.Count > 0 && ShaperFont(face) is { } font)
            {
                SetVariations(font, [.. set], (uint)set.Count);
            }
        }
    }

    /// <summary>The HarfBuzz font a face is shaped with, which Avalonia's shaper keeps on it and shows no way to vary; null
    /// where its shaper is another.</summary>
    private static IntPtr? ShaperFont(GlyphTypeface face) =>
        face.TextShaperTypeface.GetType().GetProperty("HBFont", BindingFlags.Public | BindingFlags.Instance)?.GetValue(face.TextShaperTypeface) is HarfBuzzSharp.Font font
            ? font.Handle
            : null;

    /// <summary>An axis's value as HarfBuzz takes one, its hb_variation_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Variation(uint Tag, float Value);

    [LibraryImport("libHarfBuzzSharp", EntryPoint = "hb_font_set_variations")]
    private static partial void SetVariations(IntPtr font, [In] Variation[] variations, uint count);
}

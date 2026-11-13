using Avalonia.Media;
using Avalonia.Media.Fonts;
using SkiaSharp;

namespace Passenger.App;

/// <summary>The platform's own font fallback (DirectWrite, CoreText, fontconfig) as Skia, which draws every window, asks it,
/// for <see cref="Passenger.Editor.FallbackFonts"/>: the face it names for a character, as the system's collection gives that
/// face, never a face of another family the collection would give for the name.</summary>
internal static class PlatformFallback
{
    /// <summary>The installed face the platform names for a character in a language (a BCP 47 tag), asked by a family at a
    /// style; null for none.</summary>
    public static GlyphTypeface? Named(int codepoint, FontCollectionKey style, string? familyName, string language)
    {
        using SKFontStyle asked = new((SKFontStyleWeight)(int)style.Weight, (SKFontStyleWidth)(int)style.Stretch, style.Style switch
        {
            FontStyle.Italic => SKFontStyleSlant.Italic,
            FontStyle.Oblique => SKFontStyleSlant.Oblique,
            _ => SKFontStyleSlant.Upright,
        });
        // Never disposed: Skia gives one object for a typeface, which the system's collection may hold for this face.
        if (SKFontManager.Default.MatchCharacter(string.IsNullOrEmpty(familyName) ? null : familyName, asked, [language], codepoint) is not { } face)
        {
            return null;
        }
        FontStyle slant = face.FontSlant switch
        {
            SKFontStyleSlant.Italic => FontStyle.Italic,
            SKFontStyleSlant.Oblique => FontStyle.Oblique,
            _ => FontStyle.Normal,
        };
        return FontManager.Current.SystemFonts.TryGetGlyphTypeface(face.FamilyName, slant, (FontWeight)face.FontWeight, (FontStretch)face.FontWidth, out GlyphTypeface? glyphs)
            && glyphs.PlatformTypeface.FamilyName.Equals(face.FamilyName, StringComparison.OrdinalIgnoreCase) ? glyphs : null;
    }
}

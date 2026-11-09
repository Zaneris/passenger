using SkiaSharp;

namespace Passenger.App;

/// <summary>An installed family's widths as the platform's face for its name has them, read through Skia, which draws every
/// window, from the face's own tables (post's fixed pitch, the advances), for <see cref="Passenger.Editor.CodeFonts"/>: the
/// face the system's collection would load for the name, never loaded into it, so telling every installed family apart
/// keeps none of them for the process.</summary>
internal static class PlatformWidths
{
    /// <summary>The 'post' table, whose isFixedPitch stands at byte 12.</summary>
    private const uint Post = 0x706F7374;

    /// <summary>Whether a family of the name has every character given and draws them one width, or says it is fixed pitch.</summary>
    public static bool Monospaced(string family, string characters)
    {
        // Never disposed: Skia gives one object for a typeface, which the system's collection may hold for this face.
        if (SKFontManager.Default.MatchFamily(family) is not { } face)
        {
            return false;
        }
        ushort[] glyphs = face.GetGlyphs(characters);
        if (glyphs.Length != characters.Length || Array.IndexOf(glyphs, (ushort)0) >= 0)
        {
            return false;
        }
        if (face.GetTableData(Post) is { Length: >= 16 } post && (post[12] | post[13] | post[14] | post[15]) != 0)
        {
            return true;
        }
        // At its own units per em with no hinting, an advance is the face's own, as its hmtx table gives it.
        using SKFont font = new(face, face.UnitsPerEm) { Hinting = SKFontHinting.None, LinearMetrics = true, Subpixel = true };
        float[] advances = font.GetGlyphWidths(glyphs);
        return Array.TrueForAll(advances, advance => advance == advances[0]);
    }
}

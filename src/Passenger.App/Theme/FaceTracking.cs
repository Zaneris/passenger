using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Each face Passenger draws in, tracked on macOS as the system's text drawing tracks a face it made by name, as its
/// own apps draw it. Its shaper, HarfBuzz, sets a face with 'trak' and 'STAT' tables at the tracking for 12 points at every
/// size, by a lone track too, which macOS never sets: as the face is made (<see cref="Set"/>, given every face
/// <see cref="Passenger.Editor.FallbackFonts"/> gives) its shaper font takes that much off each letter's advance, which it
/// adds back, so words, caret placement, hit testing and wrapping all take the face's own advances. What macOS sets at a size,
/// a normal track's, is the window's letter spacing (<see cref="SpacingAt"/>, <see cref="WindowTracking"/>). Thread-safe.</summary>
public static partial class FaceTracking
{
    /// <summary>The size in points HarfBuzz tracks every size at, told none.</summary>
    private const double HarfBuzzSize = 12;

    private static readonly OpenTypeTag Trak = OpenTypeTag.Parse("trak");
    private static readonly OpenTypeTag Stat = OpenTypeTag.Parse("STAT");
    private static readonly ConditionalWeakTable<GlyphTypeface, Tables> Faces = [];

    /// <summary>A face as it is made, its shaper's own tracking taken off on macOS; once for each face, a face with no
    /// tracking left as it is.</summary>
    public static void Set(GlyphTypeface face)
    {
        if (OperatingSystem.IsMacOS())
        {
            Faces.GetValue(face, Made);
        }
    }

    /// <summary>The letter spacing a face's words take at a size on macOS, its shaper's own tracking taken off: what macOS sets
    /// by its normal track, for a face with a 'STAT' table; else none.</summary>
    public static double SpacingAt(GlyphTypeface face, double size) =>
        OperatingSystem.IsMacOS() && Faces.GetValue(face, Made) is { Trak: { } trak } tables ? FontTracking.ByName(trak, tables.Styled, tables.UnitsPerEm, size) : 0;

    /// <summary>Whether a face's words take letter spacing on macOS: it has a 'STAT' table and a normal track.</summary>
    public static bool Spaces(GlyphTypeface face) =>
        OperatingSystem.IsMacOS() && Faces.GetValue(face, Made) is { Trak: { } trak, Styled: true } tables && FontTracking.Of(trak, tables.UnitsPerEm, HarfBuzzSize) is not null;

    /// <summary>The HarfBuzz font a face is shaped with, which Avalonia's shaper keeps on it and shows no way to set; null
    /// where its shaper is another.</summary>
    internal static IntPtr? ShaperFont(GlyphTypeface face) =>
        face.TextShaperTypeface.GetType().GetProperty("HBFont", BindingFlags.Public | BindingFlags.Instance)?.GetValue(face.TextShaperTypeface) is HarfBuzzSharp.Font font
            ? font.Handle
            : null;

    /// <summary>A face's tables, read once, its shaper's tracking taken off each advance its shaper gives (HarfBuzz's
    /// synthetic emboldening, which moves only the advances it shapes with; Skia draws the outlines).</summary>
    private static Tables Made(GlyphTypeface face)
    {
        byte[]? trak = face.PlatformTypeface.TryGetTable(Trak, out ReadOnlyMemory<byte> read) && read.Length > 0 ? read.ToArray() : null;
        bool styled = face.PlatformTypeface.TryGetTable(Stat, out ReadOnlyMemory<byte> stat) && stat.Length > 0;
        Tables tables = new(trak, styled, face.Metrics.DesignEmHeight);
        int shaped = trak is null ? 0 : FontTracking.ByShaper(trak, styled, HarfBuzzSize);
        if (shaped == 0 || tables.UnitsPerEm <= 0)
        {
            return tables;
        }
        try
        {
            if (ShaperFont(face) is IntPtr font)
            {
                SetSyntheticBold(font, -(float)shaped / tables.UnitsPerEm, 0, inPlace: 0);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or TargetInvocationException
            or MemberAccessException)
        {
            // Its words keep the shaper's tracking.
            IdeErrors.Write($"The typeface {face.FamilyName} keeps its shaper's tracking, so its words are set wider than the system's own: {error.Message}");
        }
        return tables;
    }

    /// <summary>A face's 'trak' table, whether it has a 'STAT' table, and its units to the em.</summary>
    private sealed record Tables(byte[]? Trak, bool Styled, int UnitsPerEm);

    [LibraryImport("libHarfBuzzSharp", EntryPoint = "hb_font_set_synthetic_bold")]
    private static partial void SetSyntheticBold(IntPtr font, float x, float y, int inPlace);
}

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace Passenger.App.Profiling;

/// <summary>A chart's words laid out once, to be drawn as they are frame after frame: on one line, in the culture of the
/// moment, trimmed to their room with an ellipsis.</summary>
internal static class Labels
{
    /// <summary>Words on one line, trimmed to the room given (none: as long as they are).</summary>
    public static TextLayout Of(string words, Typeface face, double size, IBrush brush, double? room) =>
        new(words, face, size, brush, textTrimming: room is null ? null : TextTrimming.CharacterEllipsis, maxWidth: room ?? double.PositiveInfinity, maxLines: 1,
            textStyleOverrides: [new ValueSpan<TextRunProperties>(0, words.Length, new GenericTextRunProperties(face, size, foregroundBrush: brush, cultureInfo: CultureInfo.CurrentCulture))]);

    /// <summary>A one-line layout's glyphs as one run, placed from the layout's origin as the layout draws them, so a
    /// trimmed label (its words and its ellipsis, two runs) is one draw; null where its runs differ in font, size or
    /// direction, for the layout to draw itself.</summary>
    public static GlyphRun? Run(TextLayout layout)
    {
        if (layout.TextLines is not [TextLine line])
        {
            return null;
        }
        ShapedTextRun? first = null;
        int glyphs = 0;
        int characters = 0;
        foreach (TextRun run in line.TextRuns)
        {
            if (run is not DrawableTextRun)
            {
                continue;
            }
            if (run is not ShapedTextRun { GlyphRun.IsLeftToRight: true, Properties.BaselineAlignment: BaselineAlignment.Baseline } shaped
                || (first is not null && (shaped.GlyphRun.GlyphTypeface != first.GlyphRun.GlyphTypeface
                    || shaped.GlyphRun.FontRenderingEmSize != first.GlyphRun.FontRenderingEmSize || shaped.Baseline != first.Baseline)))
            {
                return null;
            }
            first ??= shaped;
            glyphs += shaped.GlyphRun.GlyphInfos.Count;
            characters += shaped.Text.Length;
        }
        if (first is null)
        {
            return null;
        }
        GlyphInfo[] infos = new GlyphInfo[glyphs];
        char[] text = new char[characters];
        int glyph = 0;
        int character = 0;
        foreach (TextRun run in line.TextRuns)
        {
            if (run is not ShapedTextRun shaped)
            {
                continue;
            }
            IReadOnlyList<GlyphInfo> own = shaped.GlyphRun.GlyphInfos;
            // Clusters count from this run's place in the text the run is made with.
            int start = own.Count > 0 ? own[0].GlyphCluster : 0;
            for (int i = 0; i < own.Count; i++)
            {
                infos[glyph++] = new GlyphInfo(own[i].GlyphIndex, own[i].GlyphCluster - start + character, own[i].GlyphAdvance, own[i].GlyphOffset);
            }
            shaped.Text.Span.CopyTo(text.AsSpan(character));
            character += shaped.Text.Length;
        }
        return new GlyphRun(first.GlyphRun.GlyphTypeface, first.GlyphRun.FontRenderingEmSize, text, infos,
            new Point(line.Start, line.Baseline - first.Baseline + first.GlyphRun.BaselineOrigin.Y));
    }
}

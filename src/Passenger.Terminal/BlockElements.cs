using Avalonia;

namespace Passenger.Terminal;

/// <summary>The block elements (U+2580 to U+259F but the three shades) as the share of a cell each fills: a whole cell, a
/// half, an eighth from an edge, quadrants. Drawn as these rectangles, a bar or a chart of them meets the next cell and
/// row with no gap, as no font's glyph is made to fill a terminal's cell.</summary>
public static class BlockElements
{
    private const int First = 0x2580;
    private const int Last = 0x259F;

    private static readonly Rect UpperLeft = new(0, 0, 0.5, 0.5);
    private static readonly Rect UpperRight = new(0.5, 0, 0.5, 0.5);
    private static readonly Rect LowerLeft = new(0, 0.5, 0.5, 0.5);
    private static readonly Rect LowerRight = new(0.5, 0.5, 0.5, 0.5);

    /// <summary>Each element's rectangles, by its code point less <see cref="First"/>, in a cell one unit square; empty
    /// for the shades, which their font draws.</summary>
    private static readonly Rect[][] Shares =
    [
        [new(0, 0, 1, 0.5)],
        .. Enumerable.Range(1, 8).Select(eighths => (Rect[])[new(0, 1 - (eighths / 8.0), 1, eighths / 8.0)]),
        .. Enumerable.Range(1, 7).Select(eighths => (Rect[])[new(0, 0, (8 - eighths) / 8.0, 1)]),
        [new(0.5, 0, 0.5, 1)],
        [], [], [],
        [new(0, 0, 1, 1 / 8.0)],
        [new(7 / 8.0, 0, 1 / 8.0, 1)],
        [LowerLeft],
        [LowerRight],
        [UpperLeft],
        [UpperLeft, LowerLeft, LowerRight],
        [UpperLeft, LowerRight],
        [UpperLeft, UpperRight, LowerLeft],
        [UpperLeft, UpperRight, LowerRight],
        [UpperRight],
        [UpperRight, LowerLeft],
        [UpperRight, LowerLeft, LowerRight],
    ];

    /// <summary>The rectangles a code point fills of its cell, one unit square; empty for any but a block element.</summary>
    public static ReadOnlySpan<Rect> Of(int rune) => rune is >= First and <= Last ? Shares[rune - First] : [];

    /// <summary>Whether a run of the element fills its cells across their whole width, so the run is one rectangle.</summary>
    public static bool Spans(int rune) => Of(rune) is [{ X: 0, Width: 1 }];
}

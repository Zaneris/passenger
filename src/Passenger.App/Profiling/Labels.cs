using System.Globalization;
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
}

using System.Text;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Passenger.App;

/// <summary>How a menu item's words give way to a menu too narrow for them, every item's one rule (Shell.axaml): words
/// naming something in quotes (Run 'name' Until Failure) give way inside the name, which keeps its start and its end
/// either side of an ellipsis, so the words round it stay whole and items naming one long name stay told apart; other
/// words end in an ellipsis.</summary>
public sealed class MenuTrimming : TextTrimming
{
    private const string Ellipsis = "…";

    public static MenuTrimming InTheName { get; } = new();

    private MenuTrimming()
    {
    }

    public override TextCollapsingProperties CreateCollapsingProperties(TextCollapsingCreateInfo createInfo) =>
        new NameGivesWay(createInfo.Width, createInfo.TextRunProperties, createInfo.FlowDirection);

    /// <summary>A line's words cut to a width: inside the quoted name, half the name's room for its start and half for its
    /// end, where the line quotes one; else at the end.</summary>
    private sealed class NameGivesWay(double width, TextRunProperties properties, FlowDirection direction) : TextCollapsingProperties
    {
        public override double Width => width;

        public override TextRun Symbol { get; } = new TextCharacters(Ellipsis, properties);

        public override FlowDirection FlowDirection => direction;

        public override TextRun[]? Collapse(TextLine textLine)
        {
            string text = TextOf(textLine);
            int open = text.IndexOf('\'', StringComparison.Ordinal);
            int close = text.LastIndexOf('\'');
            if (open < 0 || close <= open + 1)
            {
                return new TextTrailingCharacterEllipsis(Ellipsis, width, properties, direction).Collapse(textLine);
            }
            double before = At(textLine, open + 1);
            double after = textLine.WidthIncludingTrailingWhitespace - At(textLine, close);
            double room = width - TextFormatter.CreateSymbol(Symbol, direction).Size.Width - before - after;
            double half = before + (Math.Max(0, room) / 2);
            int start = open + 1;
            while (start < close && At(textLine, start + 1) <= half)
            {
                start++;
            }
            // A character of two halves is kept whole or not at all.
            if (char.IsLowSurrogate(text[start]))
            {
                start--;
            }
            return new TextLeadingPrefixCharacterEllipsis(Ellipsis, start, width, properties, direction).Collapse(textLine);
        }

        /// <summary>How far into the line a character of it starts.</summary>
        private static double At(TextLine line, int index) => line.GetDistanceFromCharacterHit(new CharacterHit(line.FirstTextSourceIndex + index));

        private static string TextOf(TextLine line)
        {
            StringBuilder text = new();
            foreach (TextRun run in line.TextRuns)
            {
                text.Append(run.Text.Span);
            }
            return text.ToString();
        }
    }
}

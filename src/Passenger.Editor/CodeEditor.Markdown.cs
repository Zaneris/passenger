using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Passenger.Editor;

/// <summary>Markdown's document view uses the same buffer, selections and keys as its source editor. Its formatting
/// markers take no room in the text layout, while keeping their source columns for editing and navigation.</summary>
public sealed partial class CodeEditor
{
    private const char HiddenMarkdownMark = '\u200B';
    private const double LargestMarkdownHeading = 1.65;

    private readonly Dictionary<(TokenKind Kind, double Scale), TextRunProperties> markdownRuns = [];
    private bool presentsMarkdown;

    /// <summary>Whether the editor lays out Markdown syntax as document text.</summary>
    public bool PresentsMarkdown
    {
        get => presentsMarkdown;
        set
        {
            if (presentsMarkdown == value)
            {
                return;
            }
            KeepTopLine(() =>
            {
                presentsMarkdown = value;
                metrics = MeasureRows();
                RefreshStyle();
            });
            Reveal(selections.Primary.Caret);
        }
    }

    private Typeface MarkdownTypeface => new(TextElement.GetFontFamily(this));

    private Metrics MeasureRows()
    {
        Metrics code = Metrics.Measure(Typeface, FontSize, LineSpacing);
        if (!presentsMarkdown)
        {
            return code;
        }
        Metrics heading = Metrics.Measure(MarkdownTypeface, (FontSize + 1) * LargestMarkdownHeading, 1.08);
        return code with { LineHeight = Math.Max(code.LineHeight, heading.LineHeight) };
    }

    private TextRunProperties MarkdownRun(TokenKind kind, double scale)
    {
        if (!markdownRuns.TryGetValue((kind, scale), out TextRunProperties? run))
        {
            bool bold = scale > 1 || kind is TokenKind.Bold or TokenKind.Heading;
            Typeface face = new(kind == TokenKind.Code ? FontFamily : MarkdownTypeface.FontFamily,
                kind == TokenKind.Italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);
            IBrush foreground = kind is TokenKind.Heading or TokenKind.Bold or TokenKind.Italic ? palette.Text : palette.Syntax(kind);
            run = new GenericTextRunProperties(face, (FontSize + 1) * scale, foregroundBrush: foreground);
            markdownRuns.Add((kind, scale), run);
        }
        return run;
    }

    private static MarkdownLine ProjectMarkdown(string source, LineRun[] runs, bool startsLine)
    {
        double scale = 1;
        int leading = 0;
        char[]? drawn = null;
        int first = source.Length - source.AsSpan().TrimStart().Length;
        if (startsLine && runs.Length > 0 && runs[0] is { Kind: TokenKind.Keyword } marker && marker.Start == first)
        {
            int after = first + marker.Length;
            if (after < source.Length && char.IsWhiteSpace(source[after]))
            {
                ReadOnlySpan<char> marks = source.AsSpan(first, marker.Length);
                if (marker.Length <= 6 && marks.IndexOfAnyExcept('#') < 0)
                {
                    leading = source.Length - source.AsSpan(after).TrimStart().Length;
                    scale = marker.Length switch
                    {
                        1 => LargestMarkdownHeading,
                        2 => 1.4,
                        3 => 1.25,
                        _ => 1.1,
                    };
                    Hide(0, leading);
                    int end = source.AsSpan().TrimEnd().Length;
                    int closing = end;
                    while (closing > leading && source[closing - 1] == '#')
                    {
                        closing--;
                    }
                    if (closing < end && closing > leading && char.IsWhiteSpace(source[closing - 1]))
                    {
                        Hide(closing, end - closing);
                    }
                }
                else if (marker.Length == 1 && source[first] is '-' or '+' or '*')
                {
                    (drawn ??= source.ToCharArray())[first] = '•';
                }
            }
        }
        for (int i = 0; i < runs.Length; i++)
        {
            LineRun run = runs[i];
            if (run.Kind != TokenKind.Keyword || run.Length is < 1 or > 2 || run.Start < leading)
            {
                continue;
            }
            char mark = source[run.Start];
            if (mark is not ('*' or '_') || source.AsSpan(run.Start, run.Length).IndexOfAnyExcept(mark) >= 0)
            {
                continue;
            }
            TokenKind style = run.Length == 2 ? TokenKind.Bold : TokenKind.Italic;
            if ((i > 0 && runs[i - 1].Kind == style && runs[i - 1].Start + runs[i - 1].Length == run.Start)
                || (i + 1 < runs.Length && runs[i + 1].Kind == style && run.Start + run.Length == runs[i + 1].Start))
            {
                Hide(run.Start, run.Length);
                if (run.Start == leading)
                {
                    leading += run.Length;
                }
            }
        }
        return new MarkdownLine(drawn is null ? source : new string(drawn), scale, leading);

        void Hide(int from, int length) => (drawn ??= source.ToCharArray()).AsSpan(from, length).Fill(HiddenMarkdownMark);
    }

    private readonly record struct MarkdownLine(string? Text, double Scale, int Leading);
}

using Avalonia;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Ranges of the text offered to choose among (a line's calls to step into): each filled, the one chosen in a
/// stronger colour and boxed. Right and Left (Down and Up, Tab and Shift+Tab) move the choice, Enter or a click on one
/// takes it; Escape, a click elsewhere, an edit or any other key leaves without one.</summary>
public sealed partial class CodeEditor
{
    private ChoiceSession? choosing;

    /// <summary>Whether ranges are offered to choose among.</summary>
    public bool IsChoosing => choosing is not null;

    /// <summary>The ranges offered, in the order the keys move through them.</summary>
    public IReadOnlyList<TextRange> Choices => choosing?.Ranges ?? [];

    /// <summary>The range chosen so far, -1 with none offered.</summary>
    public int Chosen => choosing?.Current ?? -1;

    /// <summary>Offers ranges to choose among, the first chosen; <paramref name="taken"/> hears the one taken, or null when
    /// the choice is left.</summary>
    public void OfferChoices(IReadOnlyList<TextRange> ranges, Action<int?> taken)
    {
        EndChoices(null);
        if (ranges.Count == 0)
        {
            taken(null);
            return;
        }
        choosing = new ChoiceSession([.. ranges], taken);
        Reveal(ranges[0].Start);
        Redraw();
    }

    /// <summary>Leaves the choice without taking one.</summary>
    public void CancelChoices() => EndChoices(null);

    /// <summary>The keys of a choice; any other key leaves it (false, so the key does what it does).</summary>
    private bool RunChoiceKey(string action)
    {
        if (choosing is not { } session)
        {
            return false;
        }
        switch (action)
        {
            case EditorActions.Right or EditorActions.Down or EditorActions.Indent:
                MoveChoice(session, 1);
                return true;
            case EditorActions.Left or EditorActions.Up or EditorActions.Outdent:
                MoveChoice(session, -1);
                return true;
            case EditorActions.NewLine:
                EndChoices(session.Current);
                return true;
            case EditorActions.Escape:
                EndChoices(null);
                return true;
            default:
                EndChoices(null);
                return false;
        }
    }

    private void MoveChoice(ChoiceSession session, int step)
    {
        session.Current = (session.Current + step + session.Ranges.Count) % session.Ranges.Count;
        Reveal(session.Ranges[session.Current].Start);
        Redraw();
    }

    /// <summary>A left click while choosing: one on a range takes it, one elsewhere leaves the choice. True when taken.</summary>
    private bool ClickChoice(int offset)
    {
        if (choosing is not { } session)
        {
            return false;
        }
        int hit = session.Ranges.FindIndex(range => range.Start <= offset && offset <= range.End);
        EndChoices(hit >= 0 ? hit : null);
        return hit >= 0;
    }

    private void EndChoices(int? taken)
    {
        if (choosing is not { } session)
        {
            return;
        }
        choosing = null;
        Redraw();
        session.Taken(taken);
    }

    /// <summary>The ranges offered on a row: each filled, the chosen one in its own colour and boxed.</summary>
    private void DrawChoices(DrawingContext context, LineView view, Gap[] gaps, double left, double y)
    {
        if (choosing is not { } session)
        {
            return;
        }
        RowSpan span = view.Span;
        for (int i = 0; i < session.Ranges.Count; i++)
        {
            TextRange range = session.Ranges[i];
            if (range.End < span.Start || range.Start > span.End)
            {
                continue;
            }
            int from = Math.Max(range.Start, span.Start) - span.Start;
            int to = Math.Min(range.End, span.End) - span.Start;
            double x1 = left + XWith(view, gaps, from);
            double x2 = left + XWith(view, gaps, to, afterHints: false);
            if (x2 <= x1)
            {
                continue;
            }
            Rect box = new(x1 - 1, y + 1, x2 - x1 + 2, metrics.LineHeight - 2);
            if (i == session.Current)
            {
                context.DrawRectangle(palette.SmartStepChosen, new Pen(palette.SmartStepBorder, 1), box, 2, 2);
            }
            else
            {
                context.FillRectangle(palette.SmartStepTarget, box, 2);
            }
        }
    }

    private sealed class ChoiceSession(List<TextRange> ranges, Action<int?> taken)
    {
        public List<TextRange> Ranges { get; } = ranges;
        public Action<int?> Taken { get; } = taken;
        public int Current { get; set; }
    }
}
